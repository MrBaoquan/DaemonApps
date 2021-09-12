using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace DaemonKit.Core {
    public class AppConfig {
        public string MainProcess = @"C:\Windows\System32\notepad.exe";
        public string BackupDir = @"D:\Daemonkit\Backups";
        public string Arguments = string.Empty;
        public bool KeepTop = true;
        public bool RunAs = true;
        public bool AutoStart = true;
        public bool KillIfHung = false;
        public bool GlobalShortcut = true;
        public float DelayTime = 15.0f;
        public float IntervalTime = 30.0f;
    }

    public class Extension {
        [XmlAttribute]
        public string Name = string.Empty;
        [XmlAttribute]
        public string Path = string.Empty;
        [XmlAttribute]
        public string Args = string.Empty;

        [XmlAttribute]
        public bool RunAs = true;
    }

    public class ExtensionConfig {
        public string Name = "快速访问";
        public List<Extension> Extensions = new List<Extension> () { };
    }
}