using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSharer
{
    class Paths
    {
        // 程序根目录
        public static string? AppDir => Path.GetDirectoryName(Environment.ProcessPath);
        // 上传文件目录
        public static string UploadDir => Path.Combine(AppDir, "Assets");
        // 配置文件目录
        public static string ConfigDir => Path.Combine(AppDir, "Configs");

        public static string AppConfigPath = Path.Combine(ConfigDir, "AppConfig.xml");
    }
}
