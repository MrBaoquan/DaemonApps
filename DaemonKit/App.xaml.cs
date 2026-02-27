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
using DaemonKit.Models;
using DaemonKit.PowerSaving;
using DaemonKit.Services;
using DaemonKit.Utilities;
using DNHper;
using ReactiveUI;
using Splat;

namespace DaemonKit
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // 添加全局异常处理
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            Locator.CurrentMutable.RegisterViewsForViewModels(Assembly.GetCallingAssembly());

            // 注册服务层（Splat DI）
            Locator.CurrentMutable.RegisterLazySingleton(() => new P2PFileTransferService());
            Locator.CurrentMutable.RegisterLazySingleton(() => new TransferTaskManager());
            Locator.CurrentMutable.RegisterLazySingleton(() => new PowerSavingService());
            Locator.CurrentMutable.RegisterLazySingleton(() => new NetworkBroadcastService());
        }

        private void App_DispatcherUnhandledException(
            object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e
        )
        {
            try
            {
                NLogger.Error("UI线程未处理异常: {ExceptionType}", e.Exception.GetType().Name);
                NLogger.Error("消息: {Message}", e.Exception.Message);
                NLogger.Error("堆栈: {StackTrace}", e.Exception.StackTrace);
                if (e.Exception.InnerException != null)
                {
                    NLogger.Error("内部异常: {InnerMessage}", e.Exception.InnerException.Message);
                }
            }
            catch { }

            e.Handled = false; // 让程序崩溃以便调试
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var exception = e.ExceptionObject as Exception;
                NLogger.Error("应用程序域未处理异常: {ExceptionType}", exception?.GetType().Name);
                NLogger.Error("消息: {Message}", exception?.Message);
                NLogger.Error("堆栈: {StackTrace}", exception?.StackTrace);
                NLogger.Error("IsTerminating: {IsTerminating}", e.IsTerminating);
            }
            catch { }
        }

        private void TaskScheduler_UnobservedTaskException(
            object sender,
            UnobservedTaskExceptionEventArgs e
        )
        {
            try
            {
                NLogger.Error("Task未观察异常: {ExceptionType}", e.Exception.GetType().Name);
                NLogger.Error("消息: {Message}", e.Exception.Message);
                e.SetObserved();
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            NLogger.Info("开始 OnStartup");

            try
            {
                var _curProcess = Process.GetCurrentProcess();
                var _currentProcessFileName = _curProcess.MainModule.FileName;
                var _processName = Path.GetFileNameWithoutExtension(_currentProcessFileName);
                var _processes = Process.GetProcessesByName(_processName);
                if (_processes.Count() > 1)
                {
                    var _anotherApp = _processes.FirstOrDefault(
                        _process => _process.Id != _curProcess.Id
                    );

                    if (_anotherApp.MainModule.FileName == _curProcess.MainModule.FileName)
                    {
                        WinAPI.SendMessage(
                            _anotherApp.MainWindowHandle,
                            0x0312,
                            99,
                            new System.Text.StringBuilder("0")
                        ); // 发送消息显示窗口
                        Shutdown();
                        return;
                    }
                    else
                    {
                        _anotherApp.Kill();
                        //if (_anotherApp.MainWindowHandle == IntPtr.Zero) {
                        //    Shutdown ();
                        //    return;
                        //}
                        //WinAPI.SendMessage (_anotherApp.MainWindowHandle, 0x0312, 88, new System.Text.StringBuilder ("0")); // 发送消息退出程序
                    }
                }

                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                NLogger.Error("OnStartup 异常: {ExceptionType}", ex.GetType().Name);
                NLogger.Error("消息: {Message}", ex.Message);
                NLogger.Error("堆栈: {StackTrace}", ex.StackTrace);
                if (ex.InnerException != null)
                {
                    NLogger.Error("内部异常: {InnerMessage}", ex.InnerException.Message);
                    NLogger.Error("内部堆栈: {InnerStackTrace}", ex.InnerException.StackTrace);
                }
                throw;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 正常退出时主动停止守护服务，避免 DaemonGuard 误重启。
            // 崩溃或被 taskkill 终止时 OnExit 不会触发，因此 DaemonGuard 仍会正常重启。
            // 下次启动时 SyncGuardService(true) 会自动重新启动守护服务。
            StopGuardServiceOnExit();
            base.OnExit(e);
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

        /// <summary>
        /// 正常退出时停止守护服务。
        /// 比哨兵文件方案更简洁可靠：无文件残留、无竞态风险。
        /// 下次启动时 SyncSettings → SyncGuardService(true) 会自动重新启动服务。
        /// </summary>
        private static void StopGuardServiceOnExit()
        {
            try
            {
                if (GuardServiceHelper.IsServiceRunning())
                {
                    GuardServiceHelper.StopService();
                    NLogger.Info("正常退出，已停止守护服务");
                }
            }
            catch (Exception ex)
            {
                NLogger.Warn("退出时停止守护服务失败: {Message}", ex.Message);
            }
        }

        public static async Task executeProgramsBeforeExit()
        {
            if (!Directory.Exists(AppPathes.DestroyHooksDir))
                return;
            var _files = Directory.GetFiles(
                AppPathes.DestroyHooksDir,
                "*.*",
                SearchOption.TopDirectoryOnly
            );
            await _files
                .Where(_path => _path.EndsWith(".bat") || _path.EndsWith(".cmd"))
                .Select(
                    _file =>
                        Observable
                            .Start(() =>
                            {
                                try
                                {
                                    NLogger.Info(
                                        "[退出钩子] 执行: {File}",
                                        _file
                                    );
                                    Process _process = new Process();
                                    _process.StartInfo.FileName = _file;
                                    _process.StartInfo.Verb = "runas";
                                    _process.Start();
                                    _process.WaitForExit();
                                    NLogger.Info(
                                        "[退出钩子] 完成: {File}",
                                        _file
                                    );
                                }
                                catch (System.Exception e)
                                {
                                    NLogger.Error("[退出钩子] 执行失败: {ErrorMessage}", e.Message);
                                }
                            })
                            .ObserveOn(RxApp.MainThreadScheduler)
                )
                .Zip()
                .ObserveOn(RxApp.MainThreadScheduler);
        }
    }
}
