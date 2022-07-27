using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;

namespace DaemonKit {

    public class MachineInfo {
        public string ID = string.Empty;
        public string Name { get; set; }
        public ObservableCollection<string> GPUs { get; set; }
        public ObservableCollection<string> CPUs { get; set; }
        public ObservableCollection<string> IPs { get; set; }
        public ObservableCollection<string> Memories { get; set; }
    }

    public class DaemonTableViewModel : ReactiveObject {
        public ObservableCollection<MachineInfo> Machines { get; set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryConnectCommand { get; protected set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryShutdownCommand { get; protected set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryRestartCommand { get; protected set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryBootCommand { get; protected set; }
        public ReactiveCommand<MachineInfo, MachineInfo> OpenSMBShareCommand { get; protected set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryRestartNodeTree { get; protected set; }
        public DaemonTableViewModel () {
            Machines = new ObservableCollection<MachineInfo> ();
            TryConnectCommand = ReactiveCommand.Create<MachineInfo, MachineInfo> (_machine => {
                _machine.Name = "连接中...";
                return _machine;
            });
            TryShutdownCommand = ReactiveCommand.Create<MachineInfo, MachineInfo> (_machine => {
                _machine.Name = "关机中...";
                return _machine;
            });
            TryRestartCommand = ReactiveCommand.Create<MachineInfo, MachineInfo> (_machine => {
                _machine.Name = "重启中...";
                return _machine;
            });
            TryBootCommand = ReactiveCommand.Create<MachineInfo, MachineInfo> (_machine => {
                _machine.Name = "开机中...";
                return _machine;
            });

            OpenSMBShareCommand = ReactiveCommand.Create<MachineInfo, MachineInfo> (_machine => {
                return _machine;
            });

            TryRestartNodeTree = ReactiveCommand.Create<MachineInfo, MachineInfo> (_machine => {
                return _machine;
            });
        }

        public void AddMachine (MachineInfo machine) {
            var _machineInfo = Machines.Where (m => m.ID == machine.ID).FirstOrDefault ();
            if (_machineInfo == default (MachineInfo)) {
                Machines.Add (machine);
            } else {
                _machineInfo.Name = machine.Name;
                _machineInfo.CPUs = machine.CPUs;
                _machineInfo.GPUs = machine.GPUs;
                _machineInfo.IPs = machine.IPs;
                _machineInfo.Memories = machine.Memories;
            }
        }

    }

}