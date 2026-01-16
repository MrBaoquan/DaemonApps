using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using AuthAssistant.ViewModels;
using System;
using System.IO;

namespace AuthAssistant.Views
{
    public partial class IssueLicenseDialog : Window
    {
        public IssueLicenseViewModel ViewModel => (IssueLicenseViewModel)DataContext!;
        public LicenseFile? IssuedLicense { get; private set; }

        public IssueLicenseDialog()
        {
            InitializeComponent();
            DataContext = new IssueLicenseViewModel();
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            // 验证输入
            if (string.IsNullOrWhiteSpace(ViewModel.Username))
            {
                await MessageBox.Show(this, "请输入用户名");
                return;
            }

            if (string.IsNullOrWhiteSpace(ViewModel.PhoneNumber))
            {
                await MessageBox.Show(this, "请输入联系电话");
                return;
            }

            if (ViewModel.ExpiredAt <= DateTimeOffset.Now)
            {
                await MessageBox.Show(this, "有效期必须大于当前时间");
                return;
            }

            // 创建许可证文件
            IssuedLicense = new LicenseFile
            {
                Username = ViewModel.Username,
                PhoneNumber = ViewModel.PhoneNumber,
                ExpiredAt = ViewModel.ExpiredAt.DateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                IsSuperAdmin = ViewModel.IsSuperAdmin,
                IssuedBy = "SuperAdmin", // 实际应该从主窗口获取当前管理员名称
                IssuedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                LicenseID = Guid.NewGuid().ToString()
            };

            // 保存文件对话框
            var file = await this.StorageProvider.SaveFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerSaveOptions()
                {
                    Title = "保存许可证文件",
                    DefaultExtension = "lic",
                    SuggestedFileName = $"{ViewModel.Username}.lic",
                    FileTypeChoices = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("许可证文件")
                        {
                            Patterns = new[] { "*.lic" }
                        }
                    }
                }
            );

            if (file != null)
            {
                var result = file.Path.LocalPath;
                if (!string.IsNullOrEmpty(result))
                {
                    try
                    {
                        var mainViewModel = new MainWindowViewModel();
                        var encryptedContent = mainViewModel.GenerateLicenseFile(IssuedLicense);
                        File.WriteAllText(result, encryptedContent);
                        await MessageBox.Show(this, $"许可证文件已保存到：\n{result}");
                        Close(true);
                    }
                    catch (Exception ex)
                    {
                        await MessageBox.Show(this, $"保存失败：{ex.Message}");
                    }
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close(false);
        }
    }

    // 简单的消息框辅助类
    public static class MessageBox
    {
        public static async System.Threading.Tasks.Task Show(Window owner, string message)
        {
            var dialog = new Window
            {
                Width = 300,
                Height = 150,
                Title = "提示",
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var stackPanel = new StackPanel { Margin = new Thickness(20) };
            stackPanel.Children.Add(
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 20)
                }
            );

            var button = new Button
            {
                Content = "确定",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Width = 100
            };
            button.Click += (s, e) => dialog.Close();
            stackPanel.Children.Add(button);

            dialog.Content = stackPanel;
            await dialog.ShowDialog(owner);
        }
    }
}
