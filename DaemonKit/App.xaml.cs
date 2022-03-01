using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DNHper;
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

        //
        // 摘要:
        //     Raises the System.Windows.Application.SessionEnding event.
        //
        // 参数:
        //   e:
        //     A System.Windows.SessionEndingCancelEventArgs that contains the event data.
        //protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        //{
        //    NLogger.Info($"E.Cancel = True {e.ReasonSessionEnding}");
        //    e.Cancel = true;
        //}
        public static async Task executeProgramsBeforeExit () {
            NLogger.Info ($"executeBE tid:{Thread.CurrentThread.ManagedThreadId}");
            if (!Directory.Exists (AppPathes.DestroyHooksDir)) return;
            var _files = Directory.GetFiles (AppPathes.DestroyHooksDir, "*.*", SearchOption.TopDirectoryOnly);
            await _files
                .Where (_path => _path.EndsWith (".bat") || _path.EndsWith (".cmd"))
                .Select (_file => Observable.Start (() => {
                    try {
                        NLogger.Info ($"execute script {_file} , {Thread.CurrentThread.ManagedThreadId}");
                        Process _process = new Process ();
                        _process.StartInfo.FileName = _file;
                        _process.StartInfo.Verb = "runas";
                        _process.Start ();
                        _process.WaitForExit ();
                        NLogger.Info ($"execute script {_file} , {Thread.CurrentThread.ManagedThreadId} completed");
                    } catch (System.Exception e) {
                        NLogger.Info ($"error {e.Message}");
                    }
                }).ObserveOn (RxApp.MainThreadScheduler)).Zip ().ObserveOn (RxApp.MainThreadScheduler);
            NLogger.Info ("executed all");
        }
    }
}