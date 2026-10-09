using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using HttpMultipartParser;
using System.Diagnostics;
using ReactiveUI;
using System.Reactive;
using DNHper;
using FileSharer.Utils;

namespace FileSharer.Controllers
{
    public class APIResponse
    {
        public int code { get; set; } = 0;
        public object? data { get; set; } = null;
        public string msg { get; set; } = string.Empty;
    }

    public class ShareResponse
    {
        public string qrcode_url { get; set; } = string.Empty;
        public string file_url { get; set; } = string.Empty;

        public string filename => Path.GetFileName(file_url);
    }

    public class FileController : WebApiController
    {
        public static ReactiveCommand<ShareResponse, ShareResponse> OnNewQRCode { get; set; } =
            ReactiveCommand.Create<ShareResponse, ShareResponse>(response => response);

        // 同名文件写入/上传锁，防止并发请求写同一文件导致 IOException
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

        [Route(HttpVerbs.Post, "/putobject")]
        public async Task PostData()
        {
            using (var reader = new System.IO.StreamReader(HttpContext.Request.InputStream))
            {
                var data = await reader.ReadToEndAsync();

                // 处理请求数据
                Console.WriteLine($"Received POST data: {data}");
                var response = new { Message = "Received POST request", Data = data };
                await HttpContext.SendDataAsync(response);
            }
        }

        /// <summary>
        /// 应用场景
        /// 1. 仅内网分享
        /// 2. 内/外网都可以访问
        /// </summary>
        /// <returns></returns>
        [Route(HttpVerbs.Post, "/share")]
        public async Task ShareFile()
        {
            try
            {
                NLogger.Info("[FileController] 收到分享请求");
                var parser = await MultipartFormDataParser.ParseAsync(
                    HttpContext.Request.InputStream
                );

                // 获取文件
                var file = parser.Files.Where(_file => _file.Name == "file").FirstOrDefault();
                if (file == null)
                {
                    NLogger.Warn("[FileController] 请求中未包含文件");
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                    await HttpContext.SendDataAsync(
                        new APIResponse { msg = "必须上传文件", code = 10001 }
                    );
                    return;
                }

                var fileName = file.FileName;
                NLogger.Info(
                    $"[FileController] 接收文件: name={fileName}, contentType={file.ContentType}, size={file.Data.Length}"
                );

                var filePath = Path.Combine(Paths.UploadDir, fileName);
                var uploadDir = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(uploadDir))
                {
                    Directory.CreateDirectory(uploadDir);
                    NLogger.Info($"[FileController] 创建上传目录: {uploadDir}");
                }

                // 同名文件加锁，防止并发请求写同一文件
                var fileLock = _fileLocks.GetOrAdd(fileName, _ => new SemaphoreSlim(1, 1));
                await fileLock.WaitAsync();
                try
                {
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.Data.CopyToAsync(fileStream);
                    }
                    NLogger.Info($"[FileController] 文件已保存到本地: {filePath}");

                    NLogger.Info("[FileController] 开始上传到 OSS");
                    var success = CloudDisk.FileSystem.PutObjectFromFile(
                        filePath,
                        out string fileUrl,
                        "file_sharer"
                    );
                    if (!success)
                    {
                        NLogger.Error($"[FileController] OSS上传失败: {fileUrl}");
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                        await HttpContext.SendDataAsync(
                            new APIResponse { msg = fileUrl, code = 10002 }
                        );
                        return;
                    }

                    NLogger.Info($"[FileController] OSS上传成功, 生成二维码: url={fileUrl}");
                    var qrFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_qrcode.png";
                    QRCoderUtils.GenerateAndSaveQRCode(
                        fileUrl,
                        Path.Combine(Paths.UploadDir, qrFileName)
                    );
                    NLogger.Info($"[FileController] 二维码已生成: {qrFileName}");

                    HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;

                    var data = new ShareResponse
                    {
                        // 二维码地址
                        qrcode_url = AppConfig.Instance.AssetUrl(qrFileName),
                        // 文件地址
                        file_url = fileUrl,
                    };
                    OnNewQRCode.Execute(data).Subscribe();
                    NLogger.Info($"[FileController] 分享成功: fileUrl={fileUrl}");
                    await HttpContext.SendDataAsync(
                        new APIResponse
                        {
                            msg = "分享成功",
                            code = 0,
                            data = data
                        }
                    );
                }
                finally
                {
                    fileLock.Release();
                }
            }
            catch (Exception ex)
            {
                NLogger.Error($"[FileController] 分享处理异常: {ex}");
                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                await HttpContext.SendDataAsync(new APIResponse { code = 10002, msg = ex.Message });
            }
        }
    }
}
