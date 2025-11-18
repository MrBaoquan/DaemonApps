using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Windows;
using DNHper;
using DynamicData;
using FileSharer.Controllers;
using FileSharer.Utils;
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
        public string FileName => Key.Replace(AppConfig.Instance.BaseFolder+"/", string.Empty);
        public string Url=>CloudDisk.ConfigMgr.Instance.GetOSSObjectUrl(Key);

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
        public string LogContent { get => logContent; set => this.RaiseAndSetIfChanged(ref logContent, value); }

        // 聊天文本
        private string inputContent = "";
        public string InputContent { get => inputContent; set => this.RaiseAndSetIfChanged(ref inputContent, value); }

        private string markdownContent = "我是测试内容";
        public string MarkdownContent { get => markdownContent; set => this.RaiseAndSetIfChanged(ref markdownContent, value); }

        private string qrFileName = string.Empty;
        public string QRFileName { get => qrFileName; set => this.RaiseAndSetIfChanged(ref qrFileName, value); }

        public string MainIPAddress => DNHper.Network.GetMainIPAddress();
        public string ServerText => $"http://{MainIPAddress}:6699";

        public ReactiveCommand<Unit, string> LogContentCommand { get; }
        public ReactiveCommand<OSSObjectMetaData, OSSObjectMetaData> OnCopyUrlCommand { get; private set; }

        // 发送按钮命令
        public ReactiveCommand<Unit, Unit> SendCommand { get; private set; }

        private string searchText = string.Empty;
        public string SearchText { get => searchText; set => this.RaiseAndSetIfChanged(ref searchText, value); }

        public ReactiveCommand<Unit,string> SearchCommand { get; private set; }

        private SourceList<OSSObjectMetaData> _ossObjects = new SourceList<OSSObjectMetaData>();
        private ReadOnlyObservableCollection<OSSObjectMetaData> _items;
        public ReadOnlyObservableCollection<OSSObjectMetaData> Items => _items;

        public void Search()
        {
            var _ret = CloudDisk.FileSystem.ListObjects(AppConfig.Instance.SearchText(SearchText));
            _ossObjects.Clear();
            _ossObjects.AddRange(_ret.Select(_x => new OSSObjectMetaData { Key = _x.Key, Size = _x.Size, LastModified=_x.LastModified }));
        }

        public MainViewModel() {
            LogContentCommand = ReactiveCommand.Create(() => logContent);
            SearchCommand = ReactiveCommand.Create(() => searchText);
            OnCopyUrlCommand = ReactiveCommand.Create<OSSObjectMetaData, OSSObjectMetaData>(x => x);
            
            ChatController.Init();
            SendCommand = ReactiveCommand.Create(() => Unit.Default);

            SendCommand.Subscribe(x =>
            {
                Debug.WriteLine("点击发送按钮");
                ChatController.Chat(InputContent).SubscribeOn(RxApp.MainThreadScheduler).Subscribe(_ =>
                {
                    MarkdownContent = _;
                });
            });

            OnCopyUrlCommand.Subscribe(_ =>
            {
                Clipboard.SetText(_.Url);
                MessageBox.Show("文件链接已复制到剪贴板");
            });

            _ossObjects.Connect()
                           .ObserveOn(RxApp.MainThreadScheduler)
                           .Bind(out _items)
                           .DisposeMany()
                           .Subscribe();

            SearchCommand.Subscribe(_searchText =>
            {
                Search();
            });

            Search();


            Observable.Interval(TimeSpan.FromMilliseconds(200))
                .SubscribeOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    LogContent = DNHper.NLogger.FetchMessage().Aggregate(
                                string.Empty,
                                (_current, _next) => _current + _next + "\r\n");
                    LogContentCommand.Execute().Subscribe();
                });
        } 
    }
}
