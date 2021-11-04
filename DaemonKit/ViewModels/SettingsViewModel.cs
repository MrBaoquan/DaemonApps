using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Core;
using Microsoft.Win32;
using ReactiveUI;

namespace DaemonKit {

    public class AppSettings {
        public bool StartUp { get; set; }
        public bool ShortCut { get; set; }
        public int DelayDaemon { get; set; }
        public int DaemonInterval { get; set; }
        public int ErrorCount { get; set; }
    }

    public class SettingsViewModel : ReactiveObject {
        public SettingsViewModel () {
            Confirm = ReactiveCommand.Create (() => {
                return new AppSettings {
                StartUp = StartUp,
                ShortCut = ShortCut,
                DelayDaemon = DelayDaemon,
                DaemonInterval = DaemonInterval,
                ErrorCount = ErrorCount
                };
            });
            Cancel = ReactiveCommand.Create (() => { });
        }

        public void SyncSettings (AppSettings settings) {
            StartUp = settings.StartUp;
            ShortCut = settings.ShortCut;
            DelayDaemon = settings.DelayDaemon;
            DaemonInterval = settings.DaemonInterval;
            ErrorCount = settings.ErrorCount;
        }

        private bool startUP = true;
        public bool StartUp { get => startUP; set => this.RaiseAndSetIfChanged (ref startUP, value); }
        private bool shortcut = true;
        public bool ShortCut { get => shortcut; set => this.RaiseAndSetIfChanged (ref shortcut, value); }

        private int delayDaemon = 5000;
        public int DelayDaemon { get => delayDaemon; set => this.RaiseAndSetIfChanged(ref delayDaemon, Math.Max(value,100)); }

        private int daemonInterval = 500;
        public int DaemonInterval { get => daemonInterval; set => this.RaiseAndSetIfChanged(ref daemonInterval, Math.Max(value, 100)); }

        private int errorCount = 1;
        public int ErrorCount { get => errorCount; set => this.RaiseAndSetIfChanged(ref errorCount, value); }


        public ReactiveCommand<Unit, AppSettings> Confirm { get; protected set; }
        public ReactiveCommand<Unit, Unit> Cancel { get; protected set; }
    }
}