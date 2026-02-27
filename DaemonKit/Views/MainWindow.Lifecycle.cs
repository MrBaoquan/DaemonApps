using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using DaemonKit.Models;
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

        static readonly Hardware.Info.HardwareInfo hardwareInfo = new Hardware.Info.HardwareInfo();

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
                .Subscribe(_text =>
                {
                    MainWindow._uiCheckpoint = "fetchHwInfo_done";
                    UpdateHardwareInfoBox(_text);
                    MainWindow._uiCheckpoint = "idle";
                });
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
                    break;
                case WM_QUERYENDSESSION:
                    break;
                case WM_ENDSESSION:
                    break;
            }
            return IntPtr.Zero;
        }

        #endregion
    }
}
