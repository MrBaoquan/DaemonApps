using System;
using System.IO;
using System.Windows;
using CloudDisk;

namespace FileSharer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>部署引导：--install-s3-credentials 从 stdin 读取 accessKey/secretKey（两行），DPAPI 加密写入 ini 后退出</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args != null && Array.Exists(e.Args, a => a == "--install-s3-credentials"))
            {
                InstallS3CredentialsFromStdin();
                Shutdown(0);
                return;
            }
            base.OnStartup(e);
        }

        private void InstallS3CredentialsFromStdin()
        {
            try
            {
                // 部署：echo -e "accessKey\nsecretKey" | FileSharer.exe --install-s3-credentials
                // 密钥不经命令行参数（避免同机进程读取 cmdline），从 stdin 读取
                var accessKey = Console.ReadLine()?.Trim() ?? string.Empty;
                var secretKey = Console.ReadLine()?.Trim() ?? string.Empty;

                ConfigMgr.InstallS3Credentials(accessKey, secretKey);
                Console.WriteLine("S3 凭据已加密写入 ~/.cloud-disk.ini（后续运行无需再配置）");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"安装失败: {ex.Message}");
                Shutdown(1);
            }
        }
    }
}

