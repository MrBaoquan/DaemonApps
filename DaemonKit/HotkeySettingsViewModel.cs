using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;

namespace DaemonKit
{
    public class HotkeySettingsViewModel : ReactiveObject
    {
        private bool enableGlobalHotKey = true;
        public bool EnableGlobalHotKey
        {
            get => enableGlobalHotKey;
            set => this.RaiseAndSetIfChanged(ref enableGlobalHotKey, value);
        }

        private bool enableToggleWindow = true;
        public bool EnableToggleWindow
        {
            get => enableToggleWindow;
            set => this.RaiseAndSetIfChanged(ref enableToggleWindow, value);
        }

        private bool enableStartTree = true;
        public bool EnableStartTree
        {
            get => enableStartTree;
            set => this.RaiseAndSetIfChanged(ref enableStartTree, value);
        }

        private bool enableStopTree = true;
        public bool EnableStopTree
        {
            get => enableStopTree;
            set => this.RaiseAndSetIfChanged(ref enableStopTree, value);
        }

        private bool enableDesktopOn = true;
        public bool EnableDesktopOn
        {
            get => enableDesktopOn;
            set => this.RaiseAndSetIfChanged(ref enableDesktopOn, value);
        }

        private bool enableDesktopOff = true;
        public bool EnableDesktopOff
        {
            get => enableDesktopOff;
            set => this.RaiseAndSetIfChanged(ref enableDesktopOff, value);
        }

        private bool enableScreenshot = true;
        public bool EnableScreenshot
        {
            get => enableScreenshot;
            set => this.RaiseAndSetIfChanged(ref enableScreenshot, value);
        }

        private bool enableScheduleToggle = true;
        public bool EnableScheduleToggle
        {
            get => enableScheduleToggle;
            set => this.RaiseAndSetIfChanged(ref enableScheduleToggle, value);
        }

        // ── 远程工具焦点挂起 ──

        /// <summary>进程名列表，前台窗口切换到这些进程时自动挂起全局快捷键</summary>
        public ObservableCollection<string> SuspendOnProcessNames { get; } =
            new ObservableCollection<string>();

        private string newProcessName = string.Empty;
        public string NewProcessName
        {
            get => newProcessName;
            set => this.RaiseAndSetIfChanged(ref newProcessName, value);
        }

        public ReactiveCommand<Unit, Unit> AddProcessName { get; }
        public ReactiveCommand<string, Unit> RemoveProcessName { get; }

        public HotkeySettingsViewModel()
        {
            var canAdd = this.WhenAnyValue(x => x.NewProcessName)
                .Select(n => !string.IsNullOrWhiteSpace(n));

            AddProcessName = ReactiveCommand.Create(
                () =>
                {
                    var name = NewProcessName.Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !SuspendOnProcessNames.Contains(name))
                        SuspendOnProcessNames.Add(name);
                    NewProcessName = string.Empty;
                },
                canAdd
            );

            RemoveProcessName = ReactiveCommand.Create<string>(name =>
            {
                SuspendOnProcessNames.Remove(name);
            });
        }

        public void LoadFrom(AppSettings settings)
        {
            EnableGlobalHotKey = settings.EnableGlobalHotKey;
            EnableToggleWindow = settings.EnableToggleWindow;
            EnableStartTree = settings.EnableStartTree;
            EnableStopTree = settings.EnableStopTree;
            EnableDesktopOn = settings.EnableDesktopOn;
            EnableDesktopOff = settings.EnableDesktopOff;
            EnableScreenshot = settings.EnableScreenshot;
            EnableScheduleToggle = settings.EnableScheduleToggleHotKey;
            SuspendOnProcessNames.Clear();
            var list = settings.SuspendHotkeyOnProcessNames;
            if (list != null)
                foreach (var n in list)
                    SuspendOnProcessNames.Add(n);
        }

        public void ApplyTo(AppSettings settings)
        {
            settings.EnableGlobalHotKey = EnableGlobalHotKey;
            settings.EnableToggleWindow = EnableToggleWindow;
            settings.EnableStartTree = EnableStartTree;
            settings.EnableStopTree = EnableStopTree;
            settings.EnableDesktopOn = EnableDesktopOn;
            settings.EnableDesktopOff = EnableDesktopOff;
            settings.EnableScreenshot = EnableScreenshot;
            settings.EnableScheduleToggleHotKey = EnableScheduleToggle;
            settings.SuspendHotkeyOnProcessNames = new System.Collections.Generic.List<string>(
                SuspendOnProcessNames
            );
        }
    }
}
