using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using DNHper;
using DynamicData;
using FileSharer.Controllers;
using FileSharer.Utils;
using Newtonsoft.Json.Linq;
using ReactiveUI;

namespace FileSharer.ViewModels
{
    public class OSSObjectMetaData
    {
        public static string FormatBytes(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;

            if (bytes >= GB)
            {
                return $"{(double)bytes / GB:F2} GB";
            }
            else if (bytes >= MB)
            {
                return $"{(double)bytes / MB:F2} MB";
            }
            else if (bytes >= KB)
            {
                return $"{(double)bytes / KB:F2} KB";
            }
            else
            {
                return $"{bytes} bytes";
            }
        }

        public string Key { get; set; } = string.Empty;
        public string FileName => Key.Replace(AppConfig.Instance.BaseFolder + "/", string.Empty);
        public string Url => CloudDisk.ConfigMgr.Instance.GetOSSObjectUrl(Key);

        public long Size = 0;
        public string SizeText => FormatBytes(Size);

        // 更新时间
        public DateTime LastModified { get; set; } = DateTime.Now;
        public string LastModifiedText
        {
            get
            {
                // 获取目标时区
                TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); // 替换为目标时区的 ID

                // 转换为目标时区的时间
                DateTimeOffset localTime = TimeZoneInfo.ConvertTime(LastModified, timeZone);

                return localTime.ToString("yyyy/MM/dd HH:mm:ss");
            }
        }
    }

    public class MainViewModel : ReactiveObject
    {
        public string logContent = "";
        public string LogContent
        {
            get => logContent;
            set => this.RaiseAndSetIfChanged(ref logContent, value);
        }

        // 聊天文本
        private string inputContent = "";
        public string InputContent
        {
            get => inputContent;
            set => this.RaiseAndSetIfChanged(ref inputContent, value);
        }

        private string markdownContent = "我是测试内容";
        public string MarkdownContent
        {
            get => markdownContent;
            set => this.RaiseAndSetIfChanged(ref markdownContent, value);
        }

        private string qrFileName = string.Empty;
        public string QRFileName
        {
            get => qrFileName;
            set => this.RaiseAndSetIfChanged(ref qrFileName, value);
        }

        public string MainIPAddress => DNHper.Network.GetMainIPAddress();
        public string ServerText => $"http://{MainIPAddress}:6699";

        public ReactiveCommand<Unit, string> LogContentCommand { get; }
        public ReactiveCommand<OSSObjectMetaData, OSSObjectMetaData> OnCopyUrlCommand
        {
            get;
            private set;
        }

        // 发送按钮命令
        public ReactiveCommand<Unit, Unit> SendCommand { get; private set; }

        private string searchText = string.Empty;
        public string SearchText
        {
            get => searchText;
            set => this.RaiseAndSetIfChanged(ref searchText, value);
        }

        public ReactiveCommand<Unit, string> SearchCommand { get; private set; }

        // 接口测试命令：走真实链路调用 /api/share
        public ReactiveCommand<Unit, Unit> TestShareCommand { get; private set; }

        // 测试结果回调：在主线程显示下载到的二维码图片
        public Action<BitmapImage>? OnTestShareQRCode { get; set; }

        private bool _isTestingShare = false;
        public bool IsTestingShare
        {
            get => _isTestingShare;
            set => this.RaiseAndSetIfChanged(ref _isTestingShare, value);
        }

        // ===== S3 存储凭据设置（自动默认 + 主动轮换） =====
        private string _s3AccessKey = string.Empty;
        public string S3AccessKey
        {
            get => _s3AccessKey;
            set => this.RaiseAndSetIfChanged(ref _s3AccessKey, value);
        }

        private string _s3SecretKey = string.Empty;
        public string S3SecretKey
        {
            get => _s3SecretKey;
            set => this.RaiseAndSetIfChanged(ref _s3SecretKey, value);
        }

        private string _s3StatusText = string.Empty;
        public string S3StatusText
        {
            get => _s3StatusText;
            set => this.RaiseAndSetIfChanged(ref _s3StatusText, value);
        }

        public string S3EndpointText => CloudDisk.ConfigMgr.Instance.S3Endpoint;
        public string S3BucketText => CloudDisk.ConfigMgr.Instance.S3Bucket;
        public string S3StorageTypeText => CloudDisk.ConfigMgr.Instance.StorageType;

        public ReactiveCommand<Unit, Unit> RotateS3Command { get; private set; }
        public ReactiveCommand<Unit, Unit> GenerateSecretCommand { get; private set; }

        private void RefreshS3Status()
        {
            var cfg = CloudDisk.ConfigMgr.Instance;
            var sk = cfg.S3SecretKey;
            S3StatusText = string.IsNullOrEmpty(sk)
                ? "未配置 S3 凭据"
                : $"已配置（密文存储），AccessKey: {cfg.S3AccessKey}，SecretKey: {(sk.Length > 0 ? "****" + sk.Substring(sk.Length - 4) : "")}";
        }

        private SourceList<OSSObjectMetaData> _ossObjects = new SourceList<OSSObjectMetaData>();
        private ReadOnlyObservableCollection<OSSObjectMetaData> _items;
        public ReadOnlyObservableCollection<OSSObjectMetaData> Items => _items;

        public void Search()
        {
            var _ret = CloudDisk.FileSystem.ListObjects(AppConfig.Instance.SearchText(SearchText));
            _ossObjects.Clear();
            _ossObjects.AddRange(
                _ret.Select(
                    _x =>
                        new OSSObjectMetaData
                        {
                            Key = _x.Key,
                            Size = _x.Size,
                            LastModified = _x.LastModified
                        }
                )
            );
        }

        public MainViewModel()
        {
            LogContentCommand = ReactiveCommand.Create(() => logContent);
            SearchCommand = ReactiveCommand.Create(() => searchText);
            OnCopyUrlCommand = ReactiveCommand.Create<OSSObjectMetaData, OSSObjectMetaData>(x => x);

            // S3 凭据轮换 / 生成随机
            RotateS3Command = ReactiveCommand.Create(RotateS3);
            GenerateSecretCommand = ReactiveCommand.Create(GenerateSecret);

            // 初始化显示当前 S3 配置（自动默认密钥已在 ConfigMgr 构造时生成）
            var cfg = CloudDisk.ConfigMgr.Instance;
            S3AccessKey = cfg.S3AccessKey;
            S3SecretKey = cfg.S3SecretKey;
            RefreshS3Status();

            ChatController.Init();
            SendCommand = ReactiveCommand.Create(() => Unit.Default);

            SendCommand.Subscribe(x =>
            {
                Debug.WriteLine("点击发送按钮");
                ChatController
                    .Chat(InputContent)
                    .SubscribeOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        MarkdownContent = _;
                    });
            });

            OnCopyUrlCommand.Subscribe(_ =>
            {
                Clipboard.SetText(_.Url);
                MessageBox.Show("文件链接已复制到剪贴板");
            });

            _ossObjects
                .Connect()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _items)
                .DisposeMany()
                .Subscribe();

            SearchCommand.Subscribe(_searchText =>
            {
                Search();
            });

            Search();

            // 接口测试命令：构造一个测试文件，通过 HTTP 调用本机 /api/share 走真实链路
            TestShareCommand = ReactiveCommand.Create(
                () => { },
                this.WhenAnyValue(x => x.IsTestingShare).Select(b => !b)
            );

            TestShareCommand.Subscribe(async _ =>
            {
                await RunShareApiTestAsync();
            });

            Observable
                .Interval(TimeSpan.FromMilliseconds(200))
                .SubscribeOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    LogContent = DNHper.NLogger
                        .FetchMessage()
                        .Aggregate(string.Empty, (_current, _next) => _current + _next + "\r\n");
                    LogContentCommand.Execute().Subscribe();
                });
        }

        // 真实链路调用 /api/share 接口（严格按 Unity upm-filesharer FileUploader 的方式）
        private async Task RunShareApiTestAsync()
        {
            IsTestingShare = true;
            NLogger.Info("[TestShare] === 开始执行 /api/share 接口测试 (按 Unity FileUploader 方式) ===");
            try
            {
                // 1. 构造测试 PNG 文件（模拟 Unity Texture2D.EncodeToPNG()）
                byte[] fileBytes;
                using (var bmp = new Bitmap(64, 64))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(0x00, 0x71, 0xcb));
                    g.DrawString("TEST", new Font("Arial", 10), Brushes.White, 8, 22);
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        fileBytes = ms.ToArray();
                    }
                }
                var fileName = $"share_test_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                NLogger.Info(
                    $"[TestShare] 构造测试 PNG: fileName={fileName}, size={fileBytes.Length} bytes"
                );

                // 2. 构造完整 URL（Unity 插件 httpServer 参数即完整 URL）
                var httpServer =
                    $"http://{DNHper.Network.GetMainIPAddress()}:{AppConfig.Instance.ServerPort}";
                var url = $"{httpServer}/api/share";
                NLogger.Info($"[TestShare] POST {url}");

                // 3. 严格按 FileUploader.shareFile 实现：MultipartFormDataContent + 字段名 "file"
                //    Unity 插件将 byteContent.Headers.ContentType 设为 "multipart/form-data"
                string responseContent;
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                using (var content = new MultipartFormDataContent())
                {
                    var byteContent = new ByteArrayContent(fileBytes);
                    byteContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
                        "multipart/form-data"
                    );
                    content.Add(byteContent, "file", fileName);

                    var response = await client.PostAsync(url, content);
                    // Unity 插件调用 EnsureSuccessStatusCode
                    response.EnsureSuccessStatusCode();
                    responseContent = await response.Content.ReadAsStringAsync();
                    NLogger.Info(
                        $"[TestShare] 响应: HTTP {response.StatusCode}, body={responseContent}"
                    );
                }

                // 4. 解析 JSON 取 data.qrcode_url（与 Unity 插件一致）
                var _response = JObject.Parse(responseContent);
                var _qrCodeUrl = _response["data"]?["qrcode_url"];
                if (_qrCodeUrl == null)
                {
                    NLogger.Error($"[TestShare] 响应中未找到 data.qrcode_url，接口可能失败: {responseContent}");
                    return;
                }
                var qrUrl = _qrCodeUrl.Value<string>();
                NLogger.Info($"[TestShare] 解析 qrcode_url: {qrUrl}");

                // 5. 下载二维码图片（对应 Unity DownloadImageAsync → Texture2D）
                using (var imgClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var imgBytes = await imgClient.GetByteArrayAsync(qrUrl);
                    NLogger.Info($"[TestShare] 二维码图片下载成功: size={imgBytes.Length} bytes");

                    var bmpImage = new BitmapImage();
                    bmpImage.BeginInit();
                    bmpImage.CacheOption = BitmapCacheOption.OnLoad;
                    bmpImage.StreamSource = new MemoryStream(imgBytes);
                    bmpImage.EndInit();
                    bmpImage.Freeze();

                    // 主线程显示
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        OnTestShareQRCode?.Invoke(bmpImage);
                    });
                    NLogger.Info("[TestShare] 二维码已在 UI 显示，测试通过");
                }
            }
            catch (Exception ex)
            {
                NLogger.Error($"[TestShare] 测试失败: {ex}");
            }
            finally
            {
                IsTestingShare = false;
                NLogger.Info("[TestShare] === 接口测试结束 ===");
            }
        }

        /// <summary>轮换 S3 凭据：用户填写新 accessKey/secretKey 后保存（DPAPI 加密替换 ini）</summary>
        private void RotateS3()
        {
            try
            {
                var ak = S3AccessKey?.Trim() ?? string.Empty;
                var sk = S3SecretKey?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(ak) || string.IsNullOrEmpty(sk))
                {
                    System.Windows.MessageBox.Show("请填写 AccessKey 与 SecretKey", "S3 凭据轮换", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                CloudDisk.ConfigMgr.RotateS3Credentials(ak, sk);
                RefreshS3Status();
                System.Windows.MessageBox.Show(
                    "凭据已加密保存，下次上传生效。\n\n请确保 MinIO 侧 filesharer 账号密码已同步（运行 minio-init.sh 或 mc admin user）。",
                    "S3 凭据轮换",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                NLogger.Info("[S3] 凭据已轮换保存");
            }
            catch (Exception ex)
            {
                NLogger.Error($"[S3] 轮换失败: {ex}");
                System.Windows.MessageBox.Show($"轮换失败: {ex.Message}", "S3 凭据轮换", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>生成随机 SecretKey 填入（便于与 MinIO 侧同步后轮换）</summary>
        private void GenerateSecret()
        {
            S3SecretKey = CloudDisk.ConfigMgr.GenerateRandomSecret(24);
            System.Windows.MessageBox.Show(
                "已生成随机 SecretKey（仅显示于此）。\n请先在 MinIO 侧将 filesharer 密码同步为该值，再点击「保存轮换」。",
                "生成随机密钥",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }
    }
}
