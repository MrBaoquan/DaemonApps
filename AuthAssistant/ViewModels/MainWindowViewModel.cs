using Avalonia;
using Avalonia.Collections;
using DynamicData.Kernel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace AuthAssistant.ViewModels
{
    public static class LicHperInterface
    {
        public class UserInfo
        {
            public string error = string.Empty;

            // 用户名
            public string username { get; set; } = string.Empty;

            // 软件标识
            public string appid { get; set; } = string.Empty;

            // 过期时间
            public string expired_at = "1970-01-01 00:00:00";

            // 联系方式
            public string phone_number = string.Empty;
        }

        // 验证用户授权信息
        [DllImport(
            "LicHper.dll",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode
        )]
        [return: MarshalAs(UnmanagedType.BStr)]
        public static extern string Login([MarshalAs(UnmanagedType.BStr)] string userLicense);

        // 退出登录
        [DllImport(
            "LicHper.dll",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode
        )]
        public static extern int Logout();

        // 解析用户密钥
        public static UserInfo ParseLicense(string userLicense)
        {
            var json = Login(userLicense);
            JObject _data = JObject.Parse(json);
            var _userInfo = _data["value0"]!.ToString();
            return JsonConvert.DeserializeObject<UserInfo>(_userInfo)!;
        }

        // 获取授权信息
        [DllImport(
            "LicHper.dll",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode
        )]
        [return: MarshalAs(UnmanagedType.BStr)]
        public static extern string GetLicense();

        // 验证
        [DllImport(
            "LicHper.dll",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode
        )]
        public static extern int Validate(
            [MarshalAs(UnmanagedType.BStr)] string appid,
            int uiFlag = 0
        );

        // 续订
        [DllImport(
            "LicHper.dll",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode
        )]
        public static extern int Renew(
            [MarshalAs(UnmanagedType.BStr)] string appid,
            [MarshalAs(UnmanagedType.BStr)] string expiredAt
        );

        // 退订
        [DllImport(
            "LicHper.dll",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode
        )]
        public static extern int Unsubscribe([MarshalAs(UnmanagedType.BStr)] string appid);

        // 清空授权
        [DllImport(
            "LicHper.dll",
            CallingConvention = CallingConvention.StdCall,
            CharSet = CharSet.Unicode
        )]
        public static extern int ClearLicense();
    }

    public class License
    {
        public class KVPair
        {
            public string key = string.Empty;
            public LicenseInfo value;
        }

        public string serial_number { get; set; } = string.Empty;
        public List<KVPair> data = new List<KVPair>();
        public Dictionary<string, LicenseInfo> Data
        {
            get => data.ToDictionary(_ => _.key, _ => _.value);
        }
    }

    public class LicenseInfo
    {
        // 授权者
        public string username { get; set; } = string.Empty;

        // 过期时间
        public string updated_at { get; set; } = string.Empty;

        // 软件标识
        public string appid { get; set; } = string.Empty;

        // 软件过期时间
        public string expired_at { get; set; } = string.Empty;

        // 经过验证的最新的系统时间
        public string last_verified_at { get; set; } = string.Empty;

        public DateTime SystemTime
        {
            get =>
                DateTime.Parse(last_verified_at) < DateTime.Now
                    ? DateTime.Now
                    : DateTime.Parse(last_verified_at);
        }

        public string ExpiredDateString
        {
            get
            {
                var _expiredAt = DateTime.Parse(expired_at);
                if (!IsExpired)
                {
                    return $"{_expiredAt.ToString("yyyy-MM-dd")} (剩余: {(_expiredAt - SystemTime).Days} 天)";
                }
                return $"{_expiredAt.ToString("yyyy-MM-dd")}";
            }
        }

        public bool HasLicense
        {
            get => expired_at != "1970-01-01 00:00:00";
        }

        public bool NoLicense
        {
            get => !HasLicense;
        }

        public bool IsExpired
        {
            get => DateTime.Parse(expired_at) < SystemTime;
        }
    }

    public class MainWindowViewModel : ViewModelBase, IActivatableViewModel
    {
        private LicHperInterface.UserInfo userInfo = new LicHperInterface.UserInfo
        {
            username = "未登录"
        };
        public LicHperInterface.UserInfo UserInfo
        {
            get => userInfo;
            set => this.RaiseAndSetIfChanged(ref userInfo, value);
        }

        private readonly ObservableAsPropertyHelper<string> userName;
        public string UserName => userName.Value;

        // 登录相关
        public ReactiveCommand<Unit, bool> LoginCommand { get; }
        public ReactiveCommand<Unit, Unit> LoginFromFileCommand { get; }
        public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
        private string _userLicense = string.Empty;
        public string UserLicense
        {
            get => _userLicense;
            set => this.RaiseAndSetIfChanged(ref _userLicense, value);
        }

        private readonly ObservableAsPropertyHelper<bool> showLoginError;
        public bool ShowLoginError => showLoginError.Value;

        public bool Login(string userLicense)
        {
            var _userInfo = LicHperInterface.ParseLicense(userLicense);
            if (userLicense == string.Empty && _userInfo.error != string.Empty)
            {
                return false;
            }
            UserInfo = _userInfo;
            if (_userInfo.error != string.Empty)
            {
                UserInfo.username = "未登录";
                return false;
            }
            return true;
        }

        // 过期时间
        private DateTimeOffset _expiredAt = DateTime.Now;
        public DateTimeOffset ExpiredAt
        {
            get => _expiredAt;
            set => this.RaiseAndSetIfChanged(ref _expiredAt, value);
        }

        // 续订数量
        private int _renewCount = 0;
        public int RenewCount
        {
            get => _renewCount;
            set => this.RaiseAndSetIfChanged(ref _renewCount, value);
        }

        // 续订周期
        private int _renewCycle = 0; // 0: 天 , 1: 周 , 2: 月, 3: 年
        public int RenewCycle
        {
            get => _renewCycle;
            set => this.RaiseAndSetIfChanged(ref _renewCycle, value);
        }

        public AvaloniaList<LicenseInfo> LicenseInfos { get; set; } =
            new AvaloniaList<LicenseInfo>();

        public void LoadLicenseInfos()
        {
            var _license = LicHperInterface.GetLicense();
            _license = JObject.Parse(_license)["license"]!.ToString();

            License license = Newtonsoft.Json.JsonConvert.DeserializeObject<License>(_license)!;
            LicenseInfos.Clear();
            var _licenseInfos = license.Data.Values.ToList();
            LicenseInfos.AddRange(_licenseInfos);
        }

        // 生成许可证
        public ReactiveCommand<Unit, Unit> GenerateCommand { get; }

        public ReactiveCommand<Unit, Unit> ConfirmGenerateCommand { get; }

        public ReactiveCommand<LicenseInfo, LicenseInfo> RenewCommand { get; }

        // 已经登入
        private readonly ObservableAsPropertyHelper<bool> loggedIn;
        public bool LoggedIn => loggedIn.Value;

        // 重载许可证
        public ReactiveCommand<Unit, Unit> ReloadCommand { get; }

        // 确认命令
        public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }

        // 确认续期命令
        public ReactiveCommand<Unit, Unit> ConfirmRenewCommand { get; }

        // 退订命令
        public ReactiveCommand<LicenseInfo, LicenseInfo> UnsubscribeCommand { get; }

        // 清空许可证
        public ReactiveCommand<Unit, Unit> ClearLicenseCommand { get; }

        // 复制到剪切板命令
        public ReactiveCommand<Unit, Unit> CopyToClipboardCommand { get; }

        private readonly ObservableAsPropertyHelper<bool> noLicense;
        public bool NoLicense => noLicense.Value;

        public ViewModelActivator Activator { get; } = new ViewModelActivator();

        private LicenseInfo? _selectedLicenseInfo;
        public LicenseInfo? SelectedLicenseInfo
        {
            get => _selectedLicenseInfo;
            set => this.RaiseAndSetIfChanged(ref _selectedLicenseInfo, value);
        }

        private int renewPanelFormType = 0;

        // 0 生成   1 添加  2 续订
        public int RenewPanelFormType
        {
            get => renewPanelFormType;
            set => this.RaiseAndSetIfChanged(ref renewPanelFormType, value);
        }

        // 新增授权命令
        public ReactiveCommand<Unit, Unit> AddLicenseCommand { get; }

        private readonly ObservableAsPropertyHelper<bool> appIDEditable;
        public bool AppIDEditable => appIDEditable.Value;

        // 确认按钮是否可用
        private readonly ObservableAsPropertyHelper<bool> confirmButtonEnabled;
        public bool ConfirmButtonEnabled => confirmButtonEnabled.Value;

        // 软件标识
        private string _appID = string.Empty;
        public string AppID
        {
            get => _appID;
            set => this.RaiseAndSetIfChanged(ref _appID, value);
        }

        // 确认按钮文案
        private readonly ObservableAsPropertyHelper<string> confirmButtonText;
        public string ConfirmButtonText => confirmButtonText.Value;

        // 是否为生成许可证模式
        private readonly ObservableAsPropertyHelper<bool> isGenerateMode;
        public bool IsGenerateMode => isGenerateMode.Value;

        private string generatedLicense = string.Empty;
        public string GeneratedLicense
        {
            get => generatedLicense;
            set => this.RaiseAndSetIfChanged(ref generatedLicense, value);
        }

        public static string AESEncrypt(string plaintext, byte[] key)
        {
            using (AesCryptoServiceProvider aes = new AesCryptoServiceProvider())
            {
                aes.Key = key;
                aes.IV = key; // 使用相同的密钥作为初始化向量
                aes.Mode = CipherMode.CBC;

                // 创建加密器实例
                ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);

                byte[] encrypted = encryptor.TransformFinalBlock(
                    Encoding.UTF8.GetBytes(plaintext),
                    0,
                    Encoding.UTF8.GetBytes(plaintext).Length
                );

                // 返回 Base64 加密字符串
                return Convert.ToBase64String(encrypted);
            }
        }

        public static string AESDecrypt(string ciphertext, byte[] key)
        {
            using (AesCryptoServiceProvider aes = new AesCryptoServiceProvider())
            {
                aes.Key = key;
                aes.IV = key; // 使用相同的密钥作为初始化向量
                aes.Mode = CipherMode.CBC;

                // 创建解密器实例
                ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);

                byte[] decrypted = decryptor.TransformFinalBlock(
                    Convert.FromBase64String(ciphertext),
                    0,
                    Convert.FromBase64String(ciphertext).Length
                );

                // 返回解密后的字符串
                return Encoding.UTF8.GetString(decrypted);
            }
        }

        const string _key = "mrbaoquan1231231";

        public string GenerateLicenseKey(LicHperInterface.UserInfo userInfo)
        {
            JObject _data = new JObject();
            _data["value0"] = JObject.FromObject(userInfo);
            string userJson = _data.ToString();
            return AESEncrypt(userJson, Encoding.UTF8.GetBytes("mrbaoquan1231231"));
        }

        public string GenerateLicenseKey(string userName, string phone_number = "18888888888")
        {
            LicHperInterface.UserInfo userInfo = new LicHperInterface.UserInfo();
            userInfo.username = userName;
            userInfo.phone_number = phone_number;
            return GenerateLicenseKey(userInfo);
        }

        public string ExpiredAtString =>
            ExpiredAt.Date.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss");

        public MainWindowViewModel()
        {
            //var _json = GenerateLicenseKey(new LicHperInterface.UserInfo
            //{
            //    username = "马宝全"
            //});
            //Clipboard.SetText(_json);
            // var _string = AESDecrypt("8amG4zAZIDeUeMVtrRxOos9OpKbThmxhgqa4r7nUcJ5TAgw1SjKyBG4VY5v5PKQW1MJsnEgd0Wg8jWGvgorEMbEDM4n6eYsEotQKn/BQ0j8zNBUeTO0qf3JMga/tyjV7lNf6zDGiTFmrtFwvZMaIH6rRV3NDmCht5vhLuUdhejNeJQQG7C5aRBMcrDIIpN9oHsAQdCeRUAZGpzK5XP0jVg==", Encoding.UTF8.GetBytes("0123456789abcdef"));


            loggedIn = this.WhenAnyValue(x => x.UserInfo.username)
                .Select(x => x != "未登录")
                .ToProperty(this, x => x.LoggedIn);

            ReloadCommand = ReactiveCommand.Create(() =>
            {
                LoadLicenseInfos();
            });

            LoginCommand = ReactiveCommand.Create(() =>
            {
                return Login(UserLicense);
            });

            LoginFromFileCommand = ReactiveCommand.Create(() => { });

            LogoutCommand = ReactiveCommand.Create(() =>
            {
                UserInfo = new LicHperInterface.UserInfo { username = "未登录" };
                UserLicense = string.Empty;
            });

            // 续订命令
            RenewCommand = ReactiveCommand.Create<LicenseInfo, LicenseInfo>(info =>
            {
                RenewPanelFormType = 2;
                AppID = info.appid;
                ExpiredAt = DateTimeOffset.Parse(info.expired_at);
                SelectedLicenseInfo = info;
                return info;
            });

            AddLicenseCommand = ReactiveCommand.Create(() =>
            {
                SelectedLicenseInfo = null;
                RenewPanelFormType = 1;
                AppID = string.Empty;
                RenewCycle = 2;
                RenewCount = 1;
            });

            CopyToClipboardCommand = ReactiveCommand.Create(() =>
            {
                Clipboard.SetText(GeneratedLicense, TextDataFormat.UnicodeText);
            });

            isGenerateMode = this.WhenAnyValue(_ => _.RenewPanelFormType)
                .Select(_ => _ == 0)
                .ToProperty(this, _ => _.IsGenerateMode);

            userName = this.WhenAnyValue(_ => _.UserInfo)
                .Select(_ => UserInfo.username)
                .ToProperty(this, _ => _.UserName);

            showLoginError = this.WhenAnyValue(_ => _.UserInfo.error)
                .Select(_ =>
                {
                    return _ != string.Empty;
                })
                .ToProperty(this, _ => _.ShowLoginError);

            noLicense = this.WhenAnyValue(_ => _.LicenseInfos.Count)
                .Select(_ => _ == 0)
                .ToProperty(this, _ => _.NoLicense);

            GenerateCommand = ReactiveCommand.Create(() =>
            {
                GeneratedLicense = string.Empty;
                RenewPanelFormType = 0;
                AppID = string.Empty;
                RenewCycle = 2;
                RenewCount = 1;
            });

            ConfirmGenerateCommand = ReactiveCommand.Create(() =>
            {
                GeneratedLicense = GenerateLicenseKey(
                    new LicHperInterface.UserInfo
                    {
                        username = UserInfo.username,
                        appid = AppID,
                        expired_at = ExpiredAtString,
                    }
                );
            });

            appIDEditable = this.WhenAnyValue(_ => _.RenewPanelFormType)
                .Select(_ => _ != 2)
                .ToProperty(this, _ => _.AppIDEditable);

            this.WhenAnyValue(_ => _.SelectedLicenseInfo)
                .Subscribe(_ =>
                {
                    Console.WriteLine($"SelectedLicenseInfo: {SelectedLicenseInfo}");
                });

            confirmButtonEnabled = this.WhenAnyValue(
                    _ => _.AppID,
                    _ => _.ExpiredAt,
                    (appid, expiredAt) =>
                    {
                        return !string.IsNullOrEmpty(appid) && expiredAt > DateTimeOffset.Now;
                    }
                )
                .ToProperty(this, _ => _.ConfirmButtonEnabled);

            confirmButtonText = this.WhenAnyValue(_ => _.RenewPanelFormType)
                .Select(_ =>
                {
                    if (RenewPanelFormType == 0)
                    {
                        return "生成许可证";
                    }
                    else if (RenewPanelFormType == 1)
                    {
                        return "确认新增";
                    }
                    return "确认续订";
                })
                .ToProperty(this, _ => _.ConfirmButtonText);

            ConfirmRenewCommand = ReactiveCommand.Create(() =>
            {
                Console.WriteLine("Test");
            });

            ConfirmCommand = ReactiveCommand.Create(() =>
            {
                if (RenewPanelFormType == 0)
                { // 生成许可证
                    ConfirmGenerateCommand.Execute().Subscribe();
                }
                else if (RenewPanelFormType == 1 || renewPanelFormType == 2)
                { // 续订
                    ConfirmRenewCommand.Execute().Subscribe();
                }
            });

            UnsubscribeCommand = ReactiveCommand.Create<LicenseInfo, LicenseInfo>(info =>
            {
                SelectedLicenseInfo = info;
                return info;
            });

            ClearLicenseCommand = ReactiveCommand.Create(() => { });

            // 根据续订周期和数量自动计算过期时间
            bool _isAutoCalculate = false;
            this.WhenAnyValue(_ => _.RenewCount, _ => _.RenewCycle)
                .Subscribe(_ =>
                {
                    var _date = DateTimeOffset.Now;
                    switch (RenewCycle)
                    {
                        case 0:
                            _date = _date.AddDays(RenewCount);
                            break;
                        case 1:
                            _date = _date.AddDays(RenewCount * 7);
                            break;
                        case 2:
                            _date = _date.AddMonths(RenewCount);
                            break;
                        case 3:
                            _date = _date.AddYears(RenewCount);
                            break;
                    }

                    _isAutoCalculate = true;
                    ExpiredAt = _date;
                });

            // 根据过期时间反推续订周期和数量
            this.WhenAnyValue(_ => _.ExpiredAt)
                .Subscribe(_ =>
                {
                    if (_isAutoCalculate)
                    {
                        _isAutoCalculate = false;
                        return;
                    }
                    var _now = DateTimeOffset.Now;
                    var _diff = ExpiredAt - _now;
                    RenewCycle = 0;
                    RenewCount = (int)_diff.TotalDays + 1;
                });
        }
    }
}
