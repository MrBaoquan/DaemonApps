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
        }
    }
}
