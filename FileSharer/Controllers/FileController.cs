using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        public string filename=> Path.GetFileName(file_url);
    }


    public class FileController : WebApiController
    {

        public static ReactiveCommand<ShareResponse, ShareResponse> OnNewQRCode { get; set; } = ReactiveCommand.Create<ShareResponse, ShareResponse>(response => response);


        [Route(HttpVerbs.Post, "/putobject")]
        public async Task PostData()
        {
            using (var reader = new System.IO.StreamReader(HttpContext.Request.InputStream))
            {
                var data = await reader.ReadToEndAsync();
                
                // 处理请求数据
                Console.WriteLine($"Received POST data: {data}");
                var response = new
                {
                    Message = "Received POST request",
                    Data = data
                };
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
                var parser = await MultipartFormDataParser.ParseAsync(HttpContext.Request.InputStream);

                // 获取文件
                var file = parser.Files.Where(_file => _file.Name == "file").FirstOrDefault();
                if (file == null)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                    await HttpContext.SendDataAsync(new APIResponse { msg="必须上传文件", code=10001});
                    return;
                }

                var fileName = file.FileName;
                var filePath = Path.Combine(Paths.UploadDir, fileName);
                var uploadDir = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(uploadDir))
                {
                    Directory.CreateDirectory(uploadDir);
                }

                using (var fileStream = new FileStream(filePath, FileMode.Create))
                {
                    await file.Data.CopyToAsync(fileStream);
                }

                var success = CloudDisk.FileSystem.PutObjectFromFile(filePath, out string fileUrl,"file_sharer");
                var qrFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_qrcode.png";
                QRCoderUtils.GenerateAndSaveQRCode(fileUrl, Path.Combine(Paths.UploadDir,qrFileName));
        
                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
        
                var data = new ShareResponse
                {
                    // 二维码地址
                    qrcode_url = AppConfig.Instance.AssetUrl(qrFileName),

                    // 文件地址
                    file_url = fileUrl,
                };
                OnNewQRCode.Execute(data).Subscribe();
                await HttpContext.SendDataAsync(new APIResponse { msg="分享成功", code=0, data=data});
            }
            catch (Exception ex)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
                await HttpContext.SendDataAsync(new APIResponse { code=10002, msg=ex.Message});
            }
        }

    }
}
