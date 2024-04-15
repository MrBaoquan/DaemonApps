using System;
using Avalonia.Controls;
using System.Linq;
using ReactiveUI;
using Material.Dialog;
using Material.Dialog.Views;
using Avalonia.ReactiveUI;
using AuthAssistant.ViewModels;
using System.Reactive;
using System.Diagnostics;
using System.Reactive.Linq;
using Material.Dialog.Icons;
using System.Reactive.Threading.Tasks;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using Avalonia.Platform.Storage;

namespace AuthAssistant.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        MainWindowViewModel mainWindowViewModel => (DataContext as MainWindowViewModel)!;

        private void generateLicenses()
        {
            List<string> _users = new List<string>
            {
                "安达创展",
                "张坤",
                "马宝全",
                "朱景俊",
                "杜珣",
                "李伟",
                "胡彬斌"
            };
            // license directory
            var _licenseDir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "licenses");

            // create license directory if not exists
            if (!System.IO.Directory.Exists(_licenseDir))
                System.IO.Directory.CreateDirectory(_licenseDir);

            _users.ForEach(_user =>
            {
                var _licenseKey = mainWindowViewModel.GenerateLicenseKey(_user);
                var _licenseFile = System.IO.Path.Combine(_licenseDir, $"{_user}.lic");
                System.IO.File.WriteAllText(_licenseFile, _licenseKey, Encoding.UTF8);
            });
        }

        public MainWindow()
        {
            InitializeComponent();
            this.WhenActivated(disposables =>
            {
                // generateLicenses();

                mainWindowViewModel.LoginFromFileCommand.Subscribe(_ =>
                {
                    // 选择文件对话框
                    this.StorageProvider
                        .OpenFilePickerAsync(
                            new FilePickerOpenOptions()
                            {
                                Title = "选择许可证文件",
                                //You can add either custom or from the built-in file types. See "Defining custom file types" on how to create a custom one.
                                FileTypeFilter = new[]
                                {
                                    new FilePickerFileType("License Files")
                                    {
                                        Patterns = new[] { "*.lic" }
                                    }
                                }
                            }
                        )
                        .ToObservable()
                        .ObserveOn(RxApp.MainThreadScheduler)
                        .Subscribe(_files =>
                        {
                            var _selectedFile = _files.FirstOrDefault();
                            if (_selectedFile != null)
                            {
                                var _path = _selectedFile.TryGetLocalPath();
                                if (_path == null) return;
                                var _licenseKey = System.IO.File.ReadAllText(_path, Encoding.UTF8);
                                mainWindowViewModel.UserLicense = _licenseKey;
                                mainWindowViewModel.LoginCommand.Execute(Unit.Default).Subscribe();
                            }
                        });
                });

                Action reqLogin = () =>
                {
                    if (mainWindowViewModel.Login(string.Empty) == false)
                    {
                        var _loginPanel = new Login();
                        _loginPanel.DataContext = DataContext;
                        var _dialogWindow = DialogHelper.CreateCustomDialog(
                            new CustomDialogBuilderParams()
                            {
                                Content = _loginPanel,
                                StartupLocation = WindowStartupLocation.CenterOwner,
                                Borderless = true,
                                Width = 500,
                                WindowTitle = "许可证续期"
                            }
                        );

                        var _logged = false;
                        mainWindowViewModel.LoginCommand
                            .Where(_login => _login)
                            .FirstOrDefaultAsync()
                            .Subscribe(_login =>
                            {
                                if (_login)
                                {
                                    _logged = true;
                                    mainWindowViewModel.LoadLicenseInfos();
                                    // 关闭面板
                                    _dialogWindow.GetWindow().Close();
                                }
                            });

                        _dialogWindow
                            .ShowDialog(this)
                            .ToObservable()
                            .ObserveOn(RxApp.MainThreadScheduler)
                            .Subscribe(_ =>
                            {
                                if (!_logged)
                                    Close();
                            });
                    }
                    else
                    {
                        mainWindowViewModel.LoadLicenseInfos();
                    }
                };

                Func<
                    string,
                    Material.Dialog.Interfaces.IDialogWindow<DialogResult>
                > createRenewDialog = (title) =>
                {
                    var _renewPanel = new RenewPanel();
                    _renewPanel.DataContext = DataContext;
                    return DialogHelper.CreateCustomDialog(
                        new CustomDialogBuilderParams()
                        {
                            Content = _renewPanel,
                            StartupLocation = WindowStartupLocation.CenterOwner,
                            Borderless = false,
                            Width = 550,
                            WindowTitle = title
                        }
                    );
                };

                Action confirmRenew = async () =>
                {
                    var _dialog = createRenewDialog("许可证续期");
                    // 确认续费命令
                    mainWindowViewModel.ConfirmRenewCommand
                        .FirstOrDefaultAsync()
                        .Subscribe(_ =>
                        {
                            // 将过期时间设置成ExpiredAt日期的23:59:59
                            var _expiredAt = mainWindowViewModel.ExpiredAt.Date
                                .AddDays(1)
                                .AddSeconds(-1);

                            LicHperInterface.Renew(
                                mainWindowViewModel.AppID,
                                _expiredAt.ToString("yyyy-MM-dd HH:mm:ss")
                            );
                            mainWindowViewModel.LoadLicenseInfos();
                            _dialog.GetWindow().Close();
                        });

                    await _dialog.ShowDialog(this);
                };

                mainWindowViewModel.GenerateCommand.Subscribe(async _ =>
                {
                    var _dialog = createRenewDialog("生成许可证");
                    await _dialog.ShowDialog(this);
                });

                // 续费命令
                mainWindowViewModel.RenewCommand.Subscribe(_ => confirmRenew());

                // 添加许可证
                mainWindowViewModel.AddLicenseCommand.Subscribe(_ => confirmRenew());

                // 退订许可证
                mainWindowViewModel.UnsubscribeCommand.Subscribe(async _licenseInfo =>
                {
                    // 弹出确认对话框
                    var _result = await DialogHelper
                        .CreateAlertDialog(
                            new AlertDialogBuilderParams
                            {
                                ContentHeader = "操作确认",
                                SupportingText = "您确定要退订此许可证吗？",
                                StartupLocation = WindowStartupLocation.CenterOwner,
                                NegativeResult = new DialogResult("cancel"),
                                DialogHeaderIcon = DialogIconKind.Warning,
                                WindowTitle = "警告",
                                Width = 400,
                                DialogButtons = new[]
                                {
                                    new DialogButton { Content = "取消", Result = "cancel" },
                                    new DialogButton { Content = "确认", Result = "confirm" }
                                }
                            }
                        )
                        .ShowDialog(this);

                    if (_result.GetResult == "confirm")
                    {
                        LicHperInterface.Unsubscribe(_licenseInfo.appid);
                        mainWindowViewModel.LoadLicenseInfos();
                    }

                    Debug.WriteLine(_result.GetResult);
                });

                // 清空许可证
                mainWindowViewModel.ClearLicenseCommand.Subscribe(async _ =>
                {
                    // 弹出确认对话框
                    var _result = await DialogHelper
                        .CreateAlertDialog(
                            new AlertDialogBuilderParams
                            {
                                ContentHeader = "操作确认",
                                SupportingText = "您确定要清空所有许可证吗？",
                                StartupLocation = WindowStartupLocation.CenterOwner,
                                NegativeResult = new DialogResult("cancel"),
                                DialogHeaderIcon = DialogIconKind.Warning,
                                WindowTitle = "警告",
                                Width = 400,
                                DialogButtons = new[]
                                {
                                    new DialogButton { Content = "取消", Result = "cancel" },
                                    new DialogButton { Content = "确认", Result = "confirm" }
                                }
                            }
                        )
                        .ShowDialog(this);

                    if (_result.GetResult == "confirm")
                    {
                        LicHperInterface.ClearLicense();
                        mainWindowViewModel.LoadLicenseInfos();
                    }

                    Debug.WriteLine(_result.GetResult);
                });

                mainWindowViewModel.LogoutCommand.Subscribe(_ =>
                {
                    LicHperInterface.Logout();
                    // reqLogin();
                    Close();
                });

                reqLogin();
            });
        }
    }
}
