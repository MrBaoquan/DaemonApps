using FileSharer.ViewModels;
using QRCoder;
using ReactiveUI;
using System.Drawing.Imaging;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using DNHper;
using EmbedIO;
using EmbedIO.WebApi;
using FileSharer.Controllers;
using EmbedIO.Files;
using System.Reactive.Linq;
using FileSharer.Utils;

namespace FileSharer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : ReactiveWindow<MainViewModel>
    {
        public static MainWindow Instance { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;
            this.WhenActivated(d =>
            {
                NLogger.LogFileDir = "Logs";
                NLogger.Initialize();
                ViewModel = new MainViewModel();
                DataContext = ViewModel;

                var _lastContent = string.Empty;
                ViewModel.LogContentCommand.Subscribe(_ =>
                {
                    if(_lastContent!=_)
                        this.logBox.ScrollToEnd();
                    _lastContent = _;
                });
                StartWebServer();
                ViewModel.QRFileName = "暂无数据";
                GenerateQRCode("暂无数据");


                FileController.OnNewQRCode.SubscribeOn(RxApp.MainThreadScheduler).Subscribe(response =>
                {
                    NLogger.Info($"Shared new file: {response.file_url}");
                    ViewModel.QRFileName = response.filename;
                    DisplayQRCode(response.qrcode_url);
                });
            });
        }

        
        public void DisplayQRCode(string filePath)
        {
            BitmapImage bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.EndInit();
            QRCodeImage.Source = bitmapImage;
        }

        private void GenerateQRCode(string text)
        {
            QRCodeGenerator qrGenerator = new QRCodeGenerator();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            QRCode qrCode = new QRCode(qrCodeData);
            Bitmap qrCodeImage = qrCode.GetGraphic(20);

            using (MemoryStream memory = new MemoryStream())
            {
                qrCodeImage.Save(memory, ImageFormat.Png);
                memory.Position = 0;
                BitmapImage bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                QRCodeImage.Source = bitmapImage;
            }
        }

        private WebServer _webServer;
        private void StartWebServer()
        {
            _webServer = new WebServer(o => o
                .WithUrlPrefix(AppConfig.Instance.ServerUrl)
                .WithMode(HttpListenerMode.EmbedIO))
                .WithWebApi("/api", m => m
                    .WithController<FileController>())
                .WithLocalSessionManager()
                .WithModule(new FileModule("/assets", new FileSystemProvider(Paths.UploadDir,true)));
            
            _webServer.StateChanged += (s, e) =>NLogger.Info($"File Sharer Server - {e.NewState}");

            _webServer.RunAsync();
            Console.WriteLine("EmbedIO WebServer is running on http://localhost:6699");
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _webServer.Dispose();
        }
    }

   
}