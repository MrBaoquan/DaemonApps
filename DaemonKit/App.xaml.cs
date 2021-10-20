using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using ReactiveUI;
using Splat;

namespace DaemonKit {
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application {
        public App () {
            Locator.CurrentMutable.RegisterViewsForViewModels (Assembly.GetCallingAssembly ());
        }

        protected override void OnStartup (StartupEventArgs e) {
            var _currentProcessFileName = Process.GetCurrentProcess ().MainModule.FileName;
            var _processName = Path.GetFileNameWithoutExtension (_currentProcessFileName);
            if (Process.GetProcessesByName (_processName).Count () > 1) {
                App.Current.Shutdown ();
                return;
            };
            base.OnStartup (e);
        }
    }
}