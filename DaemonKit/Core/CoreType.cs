using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DaemonKit.Core {
    public class OpenProcssArgs {
        public string Path = string.Empty;
        public string Arguments = string.Empty;
    }
    public class CoreType {
        public static string ModuleDir {
            get {
                return Path.GetDirectoryName (Process.GetCurrentProcess ().MainModule.FileName);
            }
        }

        public static OpenProcssArgs EditConfigFile = new OpenProcssArgs { Path = "notepad.exe", Arguments = Path.Combine (ModuleDir, "config.xml") };

    }
}