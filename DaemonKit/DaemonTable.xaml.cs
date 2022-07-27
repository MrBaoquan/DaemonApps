using System;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using DNHper;
using Newtonsoft.Json;
using ReactiveMarbles.ObservableEvents;
using ReactiveUI;

namespace DaemonKit {
    /// <summary>
    /// DaemonTable.xaml 的交互逻辑
    /// </summary>
    public partial class DaemonTable : ReactiveWindow<DaemonTableViewModel> {
        public DaemonTable () {
            InitializeComponent ();
            this.ViewModel = new DaemonTableViewModel ();

            this.WhenActivated ((disposables) => {
                this.DataContext = this.ViewModel;
                startBroadcast ();

                ViewModel.TryConnectCommand.Subscribe (_machineInfo => {
                    WinAPI.OpenProcess ("mstsc.exe", "/v " + _machineInfo.ID);
                });

                var _commandClient = new UdpClient (new IPEndPoint (IPAddress.Any, 0));
                ViewModel.TryRestartCommand.Subscribe (_machineInfo => {
                    var _commandBuffer = Encoding.UTF8.GetBytes (JsonConvert.SerializeObject (new Command { ID = Command.RESTART }));
                    _commandClient.Send (_commandBuffer, _commandBuffer.Length, new IPEndPoint (IPAddress.Parse (_machineInfo.ID), 7008));
                });

                ViewModel.TryShutdownCommand.Subscribe (_machineInfo => {
                    var _commandBuffer = Encoding.UTF8.GetBytes (JsonConvert.SerializeObject (new Command { ID = Command.SHUTDOWN }));
                    _commandClient.Send (_commandBuffer, _commandBuffer.Length, new IPEndPoint (IPAddress.Parse (_machineInfo.ID), 7008));
                });

                ViewModel.OpenSMBShareCommand.Subscribe (_machineInfo => {
                    var _dir = $@"\\{_machineInfo.ID}\Applications\";
                    WinAPI.OpenProcess ("explorer.exe", $@"\\{_machineInfo.ID}\Applications\");
                });

                ViewModel.TryRestartNodeTree.Subscribe (_machineInfo => {
                    var _commandBuffer = Encoding.UTF8.GetBytes (JsonConvert.SerializeObject (new Command { ID = Command.RESTART_NODE_TREE }));
                    _commandClient.Send (_commandBuffer, _commandBuffer.Length, new IPEndPoint (IPAddress.Parse (_machineInfo.ID), 7008));
                });

                this.Events ().Closed
                    .Subscribe (_ => {
                        _commandClient.Close ();
                        _commandClient.Dispose ();
                        if (broadcastTokenSource != null) broadcastTokenSource.Cancel ();
                    });
            });
        }

        private CancellationTokenSource broadcastTokenSource = new CancellationTokenSource ();
        public async void startBroadcast () {
            UdpClient udpClient = new UdpClient (new IPEndPoint (IPAddress.Any, 7007));
            udpClient.EnableBroadcast = true;
            while (!broadcastTokenSource.IsCancellationRequested) {
                try {
                    var result = await udpClient.ReceiveAsync ();
                    var data = Encoding.UTF8.GetString (result.Buffer);
                    var machineInfo = JsonConvert.DeserializeObject<MachineInfo> (data);
                    machineInfo.ID = result.RemoteEndPoint.Address.ToString ();
                    this.ViewModel.AddMachine (machineInfo);
                } catch (System.Exception e) {
                    NLogger.Error (e.Message);
                }
            }
        }

        protected override void OnClosing (CancelEventArgs e) {
            base.OnClosing (e);
            this.Hide ();
            e.Cancel = true;
        }
    }
}