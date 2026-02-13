using System;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.ViewModels;
using DNHper;
using Newtonsoft.Json;
using ReactiveMarbles.ObservableEvents;
using ReactiveUI;

namespace DaemonKit
{
    /// <summary>
    /// DaemonTable.xaml 的交互逻辑
    /// P2P设备发现与文件传输面板
    /// </summary>
    public partial class DaemonTable : ReactiveWindow<DaemonPanelViewModel>
    {
        private readonly P2PFileTransferService _p2pService;
        private CancellationTokenSource _broadcastTokenSource = new CancellationTokenSource();
        private IDisposable? _offlineCheckTimer;

        /// <summary>
        /// ReactiveUI需要无参构造函数进行类型注册，实际使用时请使用有参构造函数
        /// </summary>
        public DaemonTable()
        {
            InitializeComponent();
        }

        public DaemonTable(P2PFileTransferService p2pService, TransferTaskManager taskManager)
        {
            InitializeComponent();

            // 使用外部注入的P2P服务和任务管理器（应用级生命周期）
            _p2pService = p2pService;
            this.ViewModel = new DaemonPanelViewModel(_p2pService, taskManager);

            this.DataContext = this.ViewModel;

            // 立即启动设备发现（无需等待面板显示，资源库等功能可直接使用设备列表）
            StartBroadcastListener();

            // 立即启动离线检测定时器（每5秒检查一次）
            _offlineCheckTimer = Observable
                .Interval(TimeSpan.FromSeconds(5))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ViewModel?.UpdateDeviceStatus());

            this.WhenActivated(disposables =>
            {
                // 订阅传输列表自动弹出事件（下载时自动显示，类似浏览器下载行为）
                if (this.ViewModel != null)
                {
                    this.ViewModel.ShowTransferListRequested += OnShowTransferListRequested;
                    System.Reactive.Disposables.Disposable
                        .Create(() =>
                        {
                            this.ViewModel.ShowTransferListRequested -= OnShowTransferListRequested;
                        })
                        .DisposeWith(disposables);
                }
            });
        }

        /// <summary>
        /// 启动UDP广播监听，用于设备发现
        /// </summary>
        private async void StartBroadcastListener()
        {
            NLogger.Info($"[设备发现] 开始监听广播，端口: {CommonVars.MetaPort}");

            try
            {
                using var udpClient = new UdpClient(
                    new IPEndPoint(IPAddress.Any, CommonVars.MetaPort)
                );
                udpClient.EnableBroadcast = true;

                while (!_broadcastTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync();
                        var data = Encoding.UTF8.GetString(result.Buffer);
                        var machineInfo = JsonConvert.DeserializeObject<MachineInfo>(data);

                        if (machineInfo != null)
                        {
                            machineInfo.ID = result.RemoteEndPoint.Address.ToString();

                            // 如已存在则就地更新（保留IsSelected等UI状态），否则创建扩展模型
                            this.ViewModel?.AddOrUpdateMachine(
                                MachineInfoExtended.FromMachineInfo(machineInfo)
                            );
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        NLogger.Error($"广播接收错误: {e.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.Error($"启动广播监听失败: {ex.Message}");
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            this.Hide();
            e.Cancel = true;
        }

        /// <summary>
        /// 处理传输列表自动弹出请求，委托给MainWindow的单例传输列表窗口
        /// </summary>
        /// <param name="tabIndex">Tab索引：0=上传, 1=下载</param>
        private void OnShowTransferListRequested(int tabIndex)
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.ShowTransferListWindow(tabIndex);
            }
        }
    }
}
