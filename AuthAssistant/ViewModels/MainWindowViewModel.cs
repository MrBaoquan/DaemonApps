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
    // 超级管理员许可证文件
    public class LicenseFile
    {
        // 用户名
        public string Username { get; set; } = string.Empty;

        // 联系方式
        public string PhoneNumber { get; set; } = string.Empty;

        // 过期时间
        public string ExpiredAt { get; set; } = "1970-01-01 00:00:00";

        // 是否为超级管理员
        public bool IsSuperAdmin { get; set; } = false;

        // 颁发者
        public string IssuedBy { get; set; } = "System";

        // 颁发时间
        public string IssuedAt { get; set; } = string.Empty;

        // 许可证ID
        public string LicenseID { get; set; } = string.Empty;
    }

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

            // 检查 "data" 字段是否存在并不为空
            if (_data["data"] == null)
            {
                throw new InvalidOperationException($"响应中缺少 'data' 字段。响应内容: {json}");
            }

            var _userInfo = _data["data"]!.ToString();
            var result = JsonConvert.DeserializeObject<UserInfo>(_userInfo);

            if (result == null)
            {
                throw new InvalidOperationException($"无法解析用户信息。JSON 内容: {_userInfo}");
            }

            // 检查错误信息 - 如果有错误，返回对象让上层处理
            if (!string.IsNullOrEmpty(result.error))
            {
                System.Diagnostics.Debug.WriteLine($"许可证验证返回错误: {result.error}");
            }

            return result;
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
            get =>
                data.GroupBy(_ => _.key)
                    .Select(g => g.First())
                    .ToDictionary(_ => _.key, _ => _.value);
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
        // 超级管理员密码（实际项目中应该加密存储）
        private const string SUPER_ADMIN_PASSWORD = "SuperAdmin@2024";

        // 当前许可证文件
        private LicenseFile? currentLicense = null;
        public LicenseFile? CurrentLicense
        {
            get => currentLicense;
            set => this.RaiseAndSetIfChanged(ref currentLicense, value);
        }

        // 是否为超级管理员
        public bool IsSuperAdmin => CurrentLicense?.IsSuperAdmin ?? false;

        // 许可证文件路径
        private static readonly string LicenseFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".authassistant.lic"
        );

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

        // 管理员登录相关
        private bool _isAdminLogin = false;
        public bool IsAdminLogin
        {
            get => _isAdminLogin;
            set => this.RaiseAndSetIfChanged(ref _isAdminLogin, value);
        }

        private string _adminPassword = string.Empty;
        public string AdminPassword
        {
            get => _adminPassword;
            set => this.RaiseAndSetIfChanged(ref _adminPassword, value);
        }

        private bool _showAdminLoginError = false;
        public bool ShowAdminLoginError
        {
            get => _showAdminLoginError;
            set => this.RaiseAndSetIfChanged(ref _showAdminLoginError, value);
        }

        public bool Login(string userLicense)
        {
            // 管理员登录
            if (IsAdminLogin)
            {
                if (SuperAdminLogin(AdminPassword))
                {
                    // 创建超级管理员许可证文件
                    var license = new LicenseFile
                    {
                        Username = "SuperAdmin",
                        PhoneNumber = "00000000000",
                        ExpiredAt = DateTime.Now.AddYears(100).ToString("yyyy-MM-dd HH:mm:ss"),
                        IsSuperAdmin = true,
                        IssuedBy = "System",
                        IssuedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        LicenseID = Guid.NewGuid().ToString()
                    };
                    SaveLicenseFileToLocal(license);
                    UserInfo = new LicHperInterface.UserInfo { username = "SuperAdmin" };

                    // 生成超级管理员许可证并在LicHper中验证
                    var superAdminLicense = GenerateLicenseKey(
                        new LicHperInterface.UserInfo
                        {
                            username = "SuperAdmin",
                            appid = "SuperAdmin",
                            expired_at = DateTime.Now.AddYears(100).ToString("yyyy-MM-dd HH:mm:ss"),
                            phone_number = "00000000000"
                        }
                    );

                    // 在LicHper中验证超级管理员许可证，使其在内存中被记录
                    LicHperInterface.ParseLicense(superAdminLicense);

                    ShowAdminLoginError = false;
                    return true;
                }
                else
                {
                    ShowAdminLoginError = true;
                    return false;
                }
            }

            // 普通用户登录
            // 首先尝试解析为超级管理员颁发的许可证文件格式
            var parsedLicenseFile = ParseLicenseFile(userLicense);
            if (parsedLicenseFile != null)
            {
                // 这是超级管理员颁发的许可证文件
                SaveLicenseFileToLocal(parsedLicenseFile);

                // 生成对应的UserInfo格式许可证并在LicHper中验证
                var standardLicenseKey = GenerateLicenseKeyFromFile(parsedLicenseFile);
                var userInfoFromFile = LicHperInterface.ParseLicense(standardLicenseKey);

                // 注册到UserInfo
                UserInfo = userInfoFromFile;
                if (userInfoFromFile.error != string.Empty)
                {
                    UserInfo = new LicHperInterface.UserInfo { username = "未登录" };
                    return false;
                }
                return true;
            }

            // 否则按正常的 UserInfo 格式处理
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
            try
            {
                var _license = LicHperInterface.GetLicense();
                if (string.IsNullOrEmpty(_license))
                {
                    LicenseInfos.Clear();
                    return;
                }

                var _licenseObj = JObject.Parse(_license);
                if (_licenseObj == null || _licenseObj["license"] == null)
                {
                    LicenseInfos.Clear();
                    return;
                }

                _license = _licenseObj["license"]!.ToString();
                License? license = Newtonsoft.Json.JsonConvert.DeserializeObject<License>(_license);
                if (license == null)
                {
                    LicenseInfos.Clear();
                    return;
                }

                LicenseInfos.Clear();
                var _licenseInfos = license.Data.Values.ToList();
                LicenseInfos.AddRange(_licenseInfos);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载许可证信息失败: {ex.Message}");
                LicenseInfos.Clear();
            }
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

        // 关闭对话框回调
        public Action? CloseDialogCallback { get; set; }

        // 超级管理员登录命令
        public ReactiveCommand<Unit, Unit> SuperAdminLoginCommand { get; }

        // 颁发许可证文件命令
        public ReactiveCommand<Unit, Unit> IssueLicenseCommand { get; }

        // 导入许可证文件命令
        public ReactiveCommand<Unit, Unit> ImportLicenseCommand { get; }

        // 导出许可证文件命令
        public ReactiveCommand<Unit, Unit> ExportLicenseCommand { get; }

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

        // 是否生成超级授权码（只有超级管理员可见）
        private bool _isSuperLicense = false;
        public bool IsSuperLicense
        {
            get => _isSuperLicense;
            set => this.RaiseAndSetIfChanged(ref _isSuperLicense, value);
        }

        // 确认按钮文案
        private readonly ObservableAsPropertyHelper<string> confirmButtonText;
        public string ConfirmButtonText => confirmButtonText.Value;

        // 是否为生成许可证模式
        private readonly ObservableAsPropertyHelper<bool> isGenerateMode;
        public bool IsGenerateMode => isGenerateMode.Value;

        // 是否能显示超级授权码选项
        private readonly ObservableAsPropertyHelper<bool> canShowSuperLicenseOption;
        public bool CanShowSuperLicenseOption => canShowSuperLicenseOption.Value;

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

        // 生成许可证文件内容（加密的JSON）
        public string GenerateLicenseFile(LicenseFile licenseFile)
        {
            string json = JsonConvert.SerializeObject(licenseFile);
            return AESEncrypt(json, Encoding.UTF8.GetBytes(_key));
        }

        // 解析许可证文件内容
        public LicenseFile? ParseLicenseFile(string encryptedContent)
        {
            try
            {
                string json = AESDecrypt(encryptedContent, Encoding.UTF8.GetBytes(_key));
                return JsonConvert.DeserializeObject<LicenseFile>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"解析许可证文件失败: {ex.Message}");
                return null;
            }
        }

        // 保存许可证文件到本地
        public void SaveLicenseFileToLocal(LicenseFile licenseFile)
        {
            string encrypted = GenerateLicenseFile(licenseFile);
            System.IO.File.WriteAllText(LicenseFilePath, encrypted);
            CurrentLicense = licenseFile;
            this.RaisePropertyChanged(nameof(IsSuperAdmin));
        }

        // 从本地加载许可证文件
        public bool LoadLicenseFileFromLocal()
        {
            if (!System.IO.File.Exists(LicenseFilePath))
            {
                return false;
            }

            string encrypted = System.IO.File.ReadAllText(LicenseFilePath);
            var license = ParseLicenseFile(encrypted);
            if (license == null)
            {
                return false;
            }

            // 检查是否过期
            var expiredAt = DateTime.Parse(license.ExpiredAt);
            if (expiredAt < DateTime.Now)
            {
                return false;
            }

            CurrentLicense = license;
            this.RaisePropertyChanged(nameof(IsSuperAdmin));
            return true;
        }

        // 超级管理员登录
        public bool SuperAdminLogin(string password)
        {
            return password == SUPER_ADMIN_PASSWORD;
        }

        // 颁发许可证文件
        public LicenseFile IssueLicenseFile(
            string username,
            string phoneNumber,
            string expiredAt,
            bool isSuperAdmin = false
        )
        {
            if (!IsSuperAdmin && isSuperAdmin)
            {
                throw new InvalidOperationException("只有超级管理员才能颁发超级管理员许可证");
            }

            var license = new LicenseFile
            {
                Username = username,
                PhoneNumber = phoneNumber,
                ExpiredAt = expiredAt,
                IsSuperAdmin = isSuperAdmin,
                IssuedBy = CurrentLicense?.Username ?? "System",
                IssuedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                LicenseID = Guid.NewGuid().ToString()
            };

            return license;
        }

        public string GenerateLicenseKey(LicHperInterface.UserInfo userInfo)
        {
            JObject _data = new JObject();
            _data["data"] = JObject.FromObject(userInfo);
            string userJson = _data.ToString();
            return AESEncrypt(userJson, Encoding.UTF8.GetBytes("mrbaoquan1231231"));
        }

        // 从许可证文件生成标准格式的许可证密钥（用于与LicHper交互）
        public string GenerateLicenseKeyFromFile(LicenseFile licenseFile)
        {
            var userInfo = new LicHperInterface.UserInfo
            {
                username = licenseFile.Username,
                appid = licenseFile.Username, // 使用用户名作为appid
                expired_at = licenseFile.ExpiredAt,
                phone_number = licenseFile.PhoneNumber
            };
            return GenerateLicenseKey(userInfo);
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
                CurrentLicense = null;
                this.RaisePropertyChanged(nameof(IsSuperAdmin));
                AdminPassword = string.Empty;
                IsAdminLogin = false;
            });

            // 续订命令
            RenewCommand = ReactiveCommand.Create<LicenseInfo, LicenseInfo>(info =>
            {
                RenewPanelFormType = 2;
                AppID = info.appid;
                ExpiredAt = DateTimeOffset.Parse(info.expired_at);
                SelectedLicenseInfo = info;
                IsSuperLicense = false; // 续订时重置，只有生成模式下才能使用超级授权码
                return info;
            });

            AddLicenseCommand = ReactiveCommand.Create(() =>
            {
                SelectedLicenseInfo = null;
                RenewPanelFormType = 1;
                AppID = string.Empty;
                RenewCycle = 2;
                RenewCount = 1;
                IsSuperLicense = false; // 新增许可证时重置，只有生成模式下才能使用超级授权码
            });

            CopyToClipboardCommand = ReactiveCommand.Create(() =>
            {
                Clipboard.SetText(GeneratedLicense, TextDataFormat.UnicodeText);
            });

            SuperAdminLoginCommand = ReactiveCommand.Create(() => {
                // UI 层会弹出密码输入对话框，这里只是占位
            });

            IssueLicenseCommand = ReactiveCommand.Create(() => {
                // UI 层会弹出许可证颁发对话框，这里只是占位
            });

            ImportLicenseCommand = ReactiveCommand.Create(() => {
                // UI 层会打开文件选择对话框，这里只是占位
            });

            ExportLicenseCommand = ReactiveCommand.Create(() => {
                // UI 层会打开文件保存对话框，这里只是占位
            });

            isGenerateMode = this.WhenAnyValue(_ => _.RenewPanelFormType)
                .Select(_ => _ == 0)
                .ToProperty(this, _ => _.IsGenerateMode);

            // 是否能显示超级授权码选项（只有超级管理员在生成模式下可见）
            canShowSuperLicenseOption = this.WhenAnyValue(
                    _ => _.IsSuperAdmin,
                    _ => _.IsGenerateMode
                )
                .Select(x => x.Item1 && x.Item2)
                .ToProperty(this, _ => _.CanShowSuperLicenseOption);

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
                IsSuperLicense = false; // 重置超级授权码选项
                RenewCycle = 2;
                RenewCount = 1;
            });

            ConfirmGenerateCommand = ReactiveCommand.Create(() =>
            {
                GeneratedLicense = GenerateLicenseKey(
                    new LicHperInterface.UserInfo
                    {
                        username = UserInfo.username,
                        appid = IsSuperLicense ? "*" : AppID, // 超级授权码使用 "*" 作为 AppID
                        expired_at = ExpiredAtString,
                    }
                );
            });

            appIDEditable = this.WhenAnyValue(_ => _.RenewPanelFormType, _ => _.IsSuperLicense)
                .Select(x => x.Item1 != 2 && !x.Item2) // 续订模式(2)或超级授权码时禁用
                .ToProperty(this, _ => _.AppIDEditable);

            this.WhenAnyValue(_ => _.SelectedLicenseInfo)
                .Subscribe(_ =>
                {
                    Console.WriteLine($"SelectedLicenseInfo: {SelectedLicenseInfo}");
                });

            confirmButtonEnabled = this.WhenAnyValue(
                    _ => _.AppID,
                    _ => _.ExpiredAt,
                    _ => _.IsSuperLicense,
                    (appid, expiredAt, isSuperLicense) =>
                    {
                        // 超级授权码不需要AppID，只需要过期时间有效
                        if (isSuperLicense)
                        {
                            return expiredAt > DateTimeOffset.Now;
                        }
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
                try
                {
                    // 将过期时间设置成ExpiredAt日期的23:59:59
                    var _expiredAt = ExpiredAt.Date.AddDays(1).AddSeconds(-1);

                    if (RenewPanelFormType == 1)
                    {
                        // 添加许可证
                        LicHperInterface.Renew(AppID, _expiredAt.ToString("yyyy-MM-dd HH:mm:ss"));
                    }
                    else if (RenewPanelFormType == 2)
                    {
                        // 续订
                        LicHperInterface.Renew(AppID, _expiredAt.ToString("yyyy-MM-dd HH:mm:ss"));
                    }

                    LoadLicenseInfos();
                    CloseDialogCallback?.Invoke();
                    CloseDialogCallback?.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"续订失败: {ex.Message}");
                }
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

            // 启动时尝试加载本地许可证文件
            LoadLicenseFileFromLocal();
        }
    }
}
