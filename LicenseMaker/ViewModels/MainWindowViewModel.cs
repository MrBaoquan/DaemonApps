using ReactiveUI;
using System.Reactive;
using DNHper;
using System;

namespace LicenseMaker.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        public string Greeting => "Welcome to Avalonia!";

        public MainWindowViewModel()
        {
            this.OnNewLicense = ReactiveCommand.Create(() =>
            {

                // 检测机器码是否为6位数字
                if (this.MachineCode.Length != 6)
                {
                    return "机器码不正确";
                }

                // 检测许可时长是否为数字
                if (!int.TryParse(this.LicenseDuration.ToString(), out int duration))
                {
                    return "许可时长不正确";
                }


                var _expireDate = DateTime.Now.AddDays(duration).ToString("yyyy-MM-dd");
                this.LicenseDate ="许可证有效期至: "+ _expireDate;

                var _license = $"{this.MachineCode}@{_expireDate}";
                var _licenseEncrypted = AES.Encrypt(_license, "mrbaoquan");
                return _licenseEncrypted;
            });
        }

        // 机器码
        private string _machineCode = string.Empty;
        public string MachineCode
        {
            get => _machineCode;
            set => this.RaiseAndSetIfChanged(ref _machineCode, value);
        }

        // 许可时长
        private string _licenseDuration;
        public string LicenseDuration
        {
            get => _licenseDuration;
            set => this.RaiseAndSetIfChanged(ref _licenseDuration, value);
        }

        // 许可证到期时间
        private string _licenseDate;
        public string LicenseDate
        {
            get => _licenseDate;
            set => this.RaiseAndSetIfChanged(ref _licenseDate, value);
        }

        private bool licenseDateVisible = false;
        public bool LicenseDateVisible
        {
            get => licenseDateVisible;
            set => this.RaiseAndSetIfChanged(ref licenseDateVisible, value);
        }

        // 生成授权码
        public ReactiveCommand<Unit, string> OnNewLicense { get; protected set; }
    }
}