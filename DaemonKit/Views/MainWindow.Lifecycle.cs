using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using DaemonKit.Core;
using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.Utilities;
using DNHper;
using ReactiveUI;

namespace DaemonKit
{
    public partial class MainWindow
    {
        #region Log Display with Color Coding

        private string _lastLogContent = string.Empty;

        /// <summary>
        /// 更新日志显示，根据警告级别添加颜色
        /// </summary>
        private void UpdateLogBox(System.Collections.Generic.List<string> messages)
        {
            if (messages == null || messages.Count == 0)
                return;

            var newContent = string.Join("\r\n", messages);
            if (newContent == _lastLogContent)
                return;

            _lastLogContent = newContent;

            var document = new System.Windows.Documents.FlowDocument();
            var paragraph = new System.Windows.Documents.Paragraph();

            foreach (var message in messages)
            {
                var run = new System.Windows.Documents.Run(message + "\r\n");

                // 根据日志级别设置颜色 (NLog格式: [Info], [Warn], [Error], [Debug])
                if (message.Contains("[Error]") || message.Contains("[Fatal]"))
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)); // #F44336 红色
                    run.FontWeight = FontWeights.Medium;
                }
                else if (message.Contains("[Warn]"))
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)); // #FF9800 橙色
                }
                else if (message.Contains("[Info]"))
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61)); // #616161 深灰
                }
                else if (message.Contains("[Debug]") || message.Contains("[Trace]"))
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)); // #9E9E9E 浅灰
                }
                else
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61)); // 默认颜色
                }

                paragraph.Inlines.Add(run);
            }
            document.Blocks.Add(paragraph);
            logBox.Document = document;
            logBox.ScrollToEnd();
        }

        #endregion

        #region Hardware Info

        // 硬件信息富文本样式
        private static readonly SolidColorBrush HardwareLabelBrush = new SolidColorBrush(
            Color.FromRgb(0x42, 0x42, 0x42)
        );
        private static readonly SolidColorBrush HardwareValueBrush = new SolidColorBrush(
            Color.FromRgb(0x37, 0x47, 0x4F)
        );
        private static readonly SolidColorBrush HardwareSecondaryBrush = new SolidColorBrush(
            Color.FromRgb(0x75, 0x75, 0x75)
        );

        static MainWindow()
        {
            // 冻结画刷以提高性能
            HardwareLabelBrush.Freeze();
            HardwareValueBrush.Freeze();
            HardwareSecondaryBrush.Freeze();
        }

        /// <summary>
        /// 拉取硬件信息
        /// </summary>
        private void FetchHardwareInfo()
        {
            MainWindow._uiCheckpoint = "fetchHwInfo";
            UpdateHardwareInfoBox("⏳ 硬件信息读取中...");

            Utils
                .FetchHardwareInfo()
                .ObserveOn(RxApp.MainThreadScheduler) // 确保回调在 UI 线程执行，避免跨线程访问 WPF 控件
                .Subscribe(
                    _text =>
                    {
                        MainWindow._uiCheckpoint = "fetchHwInfo_done";
                        UpdateHardwareInfoBox(_text);
                        MainWindow._uiCheckpoint = "idle";
                    },
                    ex =>
                    {
                        NLogger.Warn("[硬件信息] 获取异常（已忽略）: {Message}", ex.Message);
                        MainWindow._uiCheckpoint = "idle";
                    }
                );
            MainWindow._uiCheckpoint = "idle";
        }

        /// <summary>
        /// 更新硬件信息显示（富文本格式）
        /// </summary>
        private void UpdateHardwareInfoBox(string text)
        {
            var document = new System.Windows.Documents.FlowDocument();
            var paragraph = new System.Windows.Documents.Paragraph();
            paragraph.LineHeight = 1.8;
            paragraph.Margin = new Thickness(0);

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    paragraph.Inlines.Add(new System.Windows.Documents.LineBreak());
                    continue;
                }

                // 检查是否是标签行（以冒号结尾，且冒号后没有内容或只有空格）
                if (
                    line.EndsWith(":")
                    || (
                        line.Contains(":")
                        && line.Substring(line.IndexOf(':') + 1).Trim().Length == 0
                    )
                )
                {
                    // 标签行 - 深灰色，粗体，14号字
                    var labelRun = new System.Windows.Documents.Run(line + "\r\n")
                    {
                        Foreground = HardwareLabelBrush,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 14
                    };
                    paragraph.Inlines.Add(labelRun);
                }
                else
                {
                    // 值行 - 蓝色，13号字
                    var valueRun = new System.Windows.Documents.Run(line + "\r\n")
                    {
                        Foreground = HardwareValueBrush,
                        FontSize = 13
                    };
                    paragraph.Inlines.Add(valueRun);
                }
            }

            document.Blocks.Add(paragraph);
            hardwareInfoBox.Document = document;
        }

        #endregion

        #region Window Lifecycle & Hotkey Handling

        // ── WinEvent Hook P/Invoke（前台窗口变更监听）──
        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime
        );

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags
        );

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        // 字段：保持委托引用防止 GC 回收；保存 hook 句柄；挂起状态标志
        private WinEventDelegate? _winEventProc;
        private IntPtr _winEventHook = IntPtr.Zero;
        private bool _isHotkeysSuspended = false;

        /// <summary>
        /// 注册前台窗口变更监听。在 loadConfig 完成（快捷键已注册）后调用。
        /// </summary>
        internal void RegisterForegroundHook()
        {
            if (_winEventHook != IntPtr.Zero)
                return; // 已注册，跳过

            _winEventProc = OnForegroundWindowChanged; // 保持强引用
            _winEventHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT
            );

            if (_winEventHook == IntPtr.Zero)
                NLogger.Warn("[快捷键] SetWinEventHook 注册失败，焦点挂起功能不可用");
            else
                NLogger.Info("[快捷键] 前台窗口监听已注册");
        }

        /// <summary>
        /// 注销前台窗口变更监听（窗口关闭时调用）。
        /// </summary>
        private void UnregisterForegroundHook()
        {
            if (_winEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
                NLogger.Info("[快捷键] 前台窗口监听已注销");
            }
        }

        /// <summary>
        /// 前台窗口切换回调：根据进程名决定是否挂起/恢复全局快捷键。
        /// 注意：该回调在 UI 线程上被调用（WINEVENT_OUTOFCONTEXT）。
        /// </summary>
        private void OnForegroundWindowChanged(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime
        )
        {
            if (AppSettings == null || !AppSettings.EnableGlobalHotKey)
                return;

            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                    return;

                string processName;
                try
                {
                    processName = Process.GetProcessById((int)pid).ProcessName;
                }
                catch
                {
                    return; // 进程已退出，忽略
                }

                var suspendList = AppSettings.SuspendHotkeyOnProcessNames;
                bool shouldSuspend =
                    suspendList != null
                    && suspendList.Any(
                        n =>
                            string.Equals(n.Trim(), processName, StringComparison.OrdinalIgnoreCase)
                    );

                if (shouldSuspend && !_isHotkeysSuspended)
                {
                    _isHotkeysSuspended = true;
                    Utils.UnRegisterHotKey(this);
                    NLogger.Info("[快捷键] 前台切换到 {ProcessName}，全局快捷键已挂起", processName);
                }
                else if (!shouldSuspend && _isHotkeysSuspended)
                {
                    _isHotkeysSuspended = false;
                    Utils.RegisterHotKey(this, AppSettings);
                    NLogger.Info("[快捷键] 前台切换到 {ProcessName}，全局快捷键已恢复", processName);
                }
            }
            catch (Exception ex)
            {
                NLogger.Warn("[快捷键] 前台切换处理异常: {Message}", ex.Message);
            }
        }

        private HwndSource _source;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            _source = HwndSource.FromHwnd(helper.Handle);
            _source.AddHook(HwndHook);
            // 快捷键注册移至loadConfig之后，确保AppSettings已加载
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            ViewModel.HideWindow.Execute().Subscribe();
            e.Cancel = true;
            base.OnClosing(e);
        }

        /// <summary>
        /// 真正退出时调用（由 ViewModel.Quit 触发 Application.Shutdown 前调用）
        /// </summary>
        internal void CleanupHooks()
        {
            UnregisterForegroundHook();
        }

        private IntPtr HwndHook(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled
        )
        {
            const int WM_HOTKEY = 0x0312;
            const int WM_QUERYENDSESSION = 0x0011;
            const int WM_ENDSESSION = 0x0016;
            switch (msg)
            {
                case WM_HOTKEY:
                    var hotkeyId = wParam.ToInt32();

                    if (hotkeyId == 88)
                    {
                        handled = true;
                        ViewModel.Quit.Execute().Subscribe();
                    }
                    if (hotkeyId == 99)
                    {
                        handled = true;
                        if (
                            this.Visibility == Visibility.Hidden
                            || this.WindowState == System.Windows.WindowState.Minimized
                        )
                        {
                            ViewModel.ShowWindow.Execute().Subscribe();
                        }
                    }
                    else if (hotkeyId == HOTKEY_ID)
                    {
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableScreenshot)
                        {
                            break;
                        }
                        // Alt+X 快捷键被按下，触发截图
                        handled = true;
                        Observable
                            .Return(System.Reactive.Unit.Default)
                            .ObserveOn(RxApp.MainThreadScheduler)
                            .Subscribe(_ => TriggerScreenshot());
                    }
                    else if (hotkeyId == 9001)
                    { //Alt+C
                        if (!AppSettings.EnableGlobalHotKey)
                        {
                            break;
                        }
                        // Alt+C 快捷键被按下，触发拾色
                        handled = true;
                        Observable
                            .Return(System.Reactive.Unit.Default)
                            .ObserveOn(RxApp.MainThreadScheduler)
                            .Subscribe(_ => ViewModel.PickColor.Execute().Subscribe());
                    }
                    else if (hotkeyId == 100)
                    { //Ctrl+D
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableToggleWindow)
                        {
                            break;
                        }
                        handled = true;
                        if (
                            this.Visibility == Visibility.Hidden
                            || this.WindowState == System.Windows.WindowState.Minimized
                        )
                        {
                            ViewModel.ShowWindow.Execute().Subscribe();
                        }
                        else
                        {
                            ViewModel.HideWindow.Execute().Subscribe();
                        }
                    }
                    else if (hotkeyId == 101)
                    { //Ctrl+R
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableStartTree)
                        {
                            break;
                        }
                        handled = true;
                        ViewModel.RunNodeTree.Execute().Subscribe();
                    }
                    else if (hotkeyId == 102)
                    { //Ctrl+W
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableStopTree)
                        {
                            break;
                        }
                        handled = true;
                        ViewModel.KillNodeTree.Execute().Subscribe();
                    }
                    else if (hotkeyId == 103)
                    {
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableDesktopOn)
                        {
                            break;
                        }
                        handled = true;
                        ViewModel.RunProcess.Execute(ViewModel.OpenFileExplorer_args).Subscribe();
                    }
                    else if (hotkeyId == 104)
                    {
                        if (!AppSettings.EnableGlobalHotKey || !AppSettings.EnableDesktopOff)
                        {
                            break;
                        }
                        handled = true;
                        ViewModel.RunProcess.Execute(ViewModel.KillFileExplorer_args).Subscribe();
                    }
                    else if (hotkeyId == 105)
                    {
                        if (
                            !AppSettings.EnableGlobalHotKey
                            || !AppSettings.EnableScheduleToggleHotKey
                        )
                        {
                            break;
                        }

                        handled = true;
                        GlobalSchedule.ScheduleTasksEnabled = !GlobalSchedule.ScheduleTasksEnabled;
                        saveConfig();
                        NLogger.Info(
                            $"全局计划任务已{(GlobalSchedule.ScheduleTasksEnabled ? "启用" : "禁用")}（快捷键切换）"
                        );
                    }
                    else if (hotkeyId == 106)
                    {
                        // Ctrl+Shift+T 紧急恢复 — 纯运行时控制，不写配置文件
                        handled = true;
                        ExecuteEmergencyRecovery();
                    }
                    else if (hotkeyId == 107)
                    {
                        // Ctrl+Shift+D 编排调试模式 — 纯运行时控制
                        handled = true;
                        EnterDebugMode();
                    }
                    else if (hotkeyId == 108)
                    {
                        // Ctrl+Shift+R 守护运行模式 — 纯运行时控制
                        handled = true;
                        EnterDaemonMode();
                    }
                    break;
                case WM_QUERYENDSESSION:
                    break;
                case WM_ENDSESSION:
                    break;
            }
            return IntPtr.Zero;
        }

        #endregion

        #region Emergency Recovery (Ctrl+Shift+T)

        /// <summary>
        /// 紧急恢复：纯运行时控制，不涉及任何配置文件的保存/修改。
        /// 执行顺序：
        ///   1. 停止计划任务（静默写字段，不触发 saveConfig）
        ///   2. 停止崩溃检测服务
        ///   3. 终止进程树
        ///   4. 退出省电模式（仅运行时状态）
        ///   5. 恢复桌面（启动 explorer.exe）
        ///   6. 托盘气泡通知用户
        /// </summary>
        private void ExecuteEmergencyRecovery()
        {
            NLogger.Warn("[紧急恢复] Ctrl+Shift+T 触发，开始执行紧急恢复序列...");

            try
            {
                // ① 静默停止计划任务（不触发 WhenAnyValue → saveConfig）
                if (GlobalSchedule != null && GlobalSchedule.ScheduleTasksEnabled)
                {
                    GlobalSchedule.SetEnabledSilently(false);
                    NLogger.Info("[紧急恢复] 计划任务已静默停止（运行时）");
                }

                // ② 停止崩溃检测服务
                if (_crashDetectionService != null)
                {
                    _crashDetectionService.Stop();
                    NLogger.Info("[紧急恢复] 崩溃检测服务已停止");
                }

                // ③ 终止进程树
                if (rootProcessNode != null)
                {
                    rootProcessNode.KillNode();
                    NLogger.Info("[紧急恢复] 进程树已终止");
                }

                // ④ 退出省电模式（运行时状态恢复，不保存配置）
                try
                {
                    var vm = _powerSavingService?.ViewModel;
                    if (vm != null && vm.IsPowerSavingMode)
                    {
                        _powerSavingService!.RestoreNormalAsync();
                        NLogger.Info("[紧急恢复] 省电模式已退出");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[紧急恢复] 退出省电模式异常（已忽略）: {Message}", ex.Message);
                }

                // ⑤ 恢复桌面（启动 explorer.exe）
                try
                {
                    WinAPI.OpenProcess(@"c:\windows\explorer.exe", "", true, false);
                    NLogger.Info("[紧急恢复] 桌面 explorer.exe 已恢复");
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[紧急恢复] 恢复桌面异常（已忽略）: {Message}", ex.Message);
                }

                // ⑥ 托盘气泡通知
                try
                {
                    TrayIcon?.ShowNotification(
                        "紧急恢复",
                        "已执行紧急恢复：计划任务已暂停、进程树已终止、桌面已恢复。\n如需恢复计划任务请使用 Alt+S。",
                        H.NotifyIcon.Core.NotificationIcon.Warning
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[紧急恢复] 托盘通知异常（已忽略）: {Message}", ex.Message);
                }

                NLogger.Warn("[紧急恢复] 紧急恢复序列执行完毕");
            }
            catch (Exception ex)
            {
                NLogger.Error("[紧急恢复] 执行异常: {Message}", ex.Message);
            }
        }

        #endregion

        #region Debug Mode (Ctrl+Shift+D)

        /// <summary>
        /// 进入编排调试模式：纯运行时控制，不涉及任何配置文件的保存/修改。
        /// 执行顺序：
        ///   1. 停止计划任务（静默）
        ///   2. 停止崩溃检测服务
        ///   3. 终止进程树
        ///   4. 启用触摸屏（如果之前被禁用）
        ///   5. 退出省电模式
        ///   6. 恢复桌面（如果 explorer 未运行）
        ///   7. 托盘气泡通知用户
        /// </summary>
        private void EnterDebugMode()
        {
            NLogger.Warn("[调试模式] Ctrl+Shift+D 触发，进入编排调试模式...");

            try
            {
                // ① 静默停止计划任务
                if (GlobalSchedule != null && GlobalSchedule.ScheduleTasksEnabled)
                {
                    GlobalSchedule.SetEnabledSilently(false);
                    NLogger.Info("[调试模式] 计划任务已静默停止");
                }

                // ② 停止崩溃检测服务
                if (_crashDetectionService != null)
                {
                    _crashDetectionService.Stop();
                    NLogger.Info("[调试模式] 崩溃检测服务已停止");
                }

                // ③ 终止进程树
                if (rootProcessNode != null)
                {
                    rootProcessNode.KillNode();
                    NLogger.Info("[调试模式] 进程树已终止");
                }

                // ④ 启用触摸屏（恢复调试可操作性）
                try
                {
                    if (DeviceManager.SetTouchScreenEnabled(true))
                    {
                        NLogger.Info("[调试模式] 触摸屏已启用");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[调试模式] 启用触摸屏异常（已忽略）: {Message}", ex.Message);
                }

                // ⑤ 退出省电模式
                try
                {
                    var vm = _powerSavingService?.ViewModel;
                    if (vm != null && vm.IsPowerSavingMode)
                    {
                        _powerSavingService!.RestoreNormalAsync();
                        NLogger.Info("[调试模式] 省电模式已退出");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[调试模式] 退出省电模式异常（已忽略）: {Message}", ex.Message);
                }

                // ⑥ 恢复桌面（仅当 explorer 未运行时）
                try
                {
                    var explorerProcs = System.Diagnostics.Process.GetProcessesByName("explorer");
                    if (explorerProcs.Length == 0)
                    {
                        WinAPI.OpenProcess(@"c:\windows\explorer.exe", "", true, false);
                        NLogger.Info("[调试模式] 桌面 explorer.exe 已恢复");
                    }
                    else
                    {
                        NLogger.Info("[调试模式] 桌面 explorer.exe 已在运行，跳过");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[调试模式] 恢复桌面异常（已忽略）: {Message}", ex.Message);
                }

                // ⑦ 托盘气泡通知
                try
                {
                    TrayIcon?.ShowNotification(
                        "编排调试模式",
                        "已进入调试模式：进程树已终止、触摸屏已启用、桌面已恢复。\n如需恢复守护模式请使用 Ctrl+Shift+R。",
                        H.NotifyIcon.Core.NotificationIcon.Info
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[调试模式] 托盘通知异常（已忽略）: {Message}", ex.Message);
                }

                NLogger.Warn("[调试模式] 编排调试模式已就绪");
            }
            catch (Exception ex)
            {
                NLogger.Error("[调试模式] 执行异常: {Message}", ex.Message);
            }
        }

        #endregion

        #region Daemon Mode (Ctrl+Shift+R)

        /// <summary>
        /// 进入守护运行模式：纯运行时控制，不涉及任何配置文件的保存/修改。
        /// 执行顺序：
        ///   1. 启用触摸屏禁用（如果配置中 DisableTouchScreen 为 true）
        ///   2. 杀桌面（如果配置中 EnableDesktopOff 为 true）
        ///   3. 启动进程树
        ///   4. 静默启用计划任务
        ///   5. 重启崩溃检测（如果配置了崩溃窗口标题）
        ///   6. 托盘气泡通知用户
        /// </summary>
        private void EnterDaemonMode()
        {
            NLogger.Warn("[守护模式] Ctrl+Shift+R 触发，进入守护运行模式...");

            try
            {
                // ① 禁用触摸屏（如果配置项指定需要禁用）
                try
                {
                    if (AppSettings != null && AppSettings.DisableTouchScreen)
                    {
                        if (DeviceManager.SetTouchScreenEnabled(false))
                        {
                            NLogger.Info("[守护模式] 触摸屏已禁用");
                        }
                    }
                    else
                    {
                        NLogger.Info("[守护模式] 配置未要求禁用触摸屏，跳过");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[守护模式] 禁用触摸屏异常（已忽略）: {Message}", ex.Message);
                }

                // ② 杀桌面（如果配置中启用了桌面关闭功能）
                try
                {
                    if (AppSettings != null && AppSettings.EnableDesktopOff)
                    {
                        ViewModel.RunProcess.Execute(ViewModel.KillFileExplorer_args).Subscribe();
                        NLogger.Info("[守护模式] 桌面 explorer.exe 已终止");
                    }
                    else
                    {
                        NLogger.Info("[守护模式] 配置未启用桌面关闭，跳过");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[守护模式] 终止桌面异常（已忽略）: {Message}", ex.Message);
                }

                // ③ 启动进程树
                if (rootProcessNode != null)
                {
                    rootProcessNode.RunNode();
                    NLogger.Info("[守护模式] 进程树已启动");
                }

                // ④ 静默启用计划任务
                if (GlobalSchedule != null && !GlobalSchedule.ScheduleTasksEnabled)
                {
                    GlobalSchedule.SetEnabledSilently(true);
                    NLogger.Info("[守护模式] 计划任务已静默启用");
                }

                // ⑤ 重启崩溃检测（如果配置了崩溃窗口标题）
                try
                {
                    if (
                        AppSettings != null
                        && !string.IsNullOrWhiteSpace(AppSettings.CrashWindows)
                        && rootProcessNode != null
                    )
                    {
                        _crashDetectionService?.Stop();
                        _crashDetectionService = new CrashDetectionService();
                        _crashDetectionService.Start(rootProcessNode, AppSettings.CrashWindows);
                        NLogger.Info("[守护模式] 崩溃检测服务已启动");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[守护模式] 启动崩溃检测异常（已忽略）: {Message}", ex.Message);
                }

                // ⑥ 托盘气泡通知
                try
                {
                    TrayIcon?.ShowNotification(
                        "守护运行模式",
                        "已进入守护模式：进程树已启动、计划任务已启用。\n如需调试请使用 Ctrl+Shift+D。",
                        H.NotifyIcon.Core.NotificationIcon.Info
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[守护模式] 托盘通知异常（已忽略）: {Message}", ex.Message);
                }

                NLogger.Warn("[守护模式] 守护运行模式已就绪");
            }
            catch (Exception ex)
            {
                NLogger.Error("[守护模式] 执行异常: {Message}", ex.Message);
            }
        }

        #endregion
    }
}
