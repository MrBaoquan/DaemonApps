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
    }

    public class SettingsViewModel : ReactiveObject {
        public SettingsViewModel () {
            Confirm = ReactiveCommand.Create (() => {
                return new AppSettings {
                StartUp = StartUp,
                ShortCut = ShortCut
                };
            });
            Cancel = ReactiveCommand.Create (() => { });
        }

        public void SyncSettings (AppSettings settings) {
            StartUp = settings.StartUp;
            ShortCut = settings.ShortCut;
        }

        private bool startUP = true;
        public bool StartUp { get => startUP; set => this.RaiseAndSetIfChanged (ref startUP, value); }
        private bool shortcut = true;
        public bool ShortCut { get => shortcut; set => this.RaiseAndSetIfChanged (ref shortcut, value); }
        public ReactiveCommand<Unit, AppSettings> Confirm { get; protected set; }
        public ReactiveCommand<Unit, Unit> Cancel { get; protected set; }
    }
}