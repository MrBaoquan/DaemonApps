using DNHper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace FileSharer.Utils
{
    public class AppConfig
    {

        public int ServerPort { get; set; } = 6699;
        public string BaseFolder { get; set; } = "file_sharer";

        public string SearchText (string search)
        {
            return $"{BaseFolder}/{search}";
        }

        [XmlIgnore]
        public string HTTPServer = string.Empty;
        
        [XmlIgnore]
        public string ServerUrl => $"http://+:{ServerPort}";

        public string AssetUrl(string filename)
        {
            if(HTTPServer == string.Empty)
            {
                HTTPServer = $"http://{DNHper.Network.GetMainIPAddress()}:{AppConfig.Instance.ServerPort}";
            }
            return $"{HTTPServer}/assets/{filename}".ToForwardSlash();
        }

        private static AppConfig? _instance = null;
        [XmlIgnore]
        public static AppConfig Instance {
            get
            {
                if(_instance !=null) return _instance;
                if (File.Exists(Paths.AppConfigPath) == false)
                {
                    var _folder = Path.GetDirectoryName(Paths.AppConfigPath);
                    if (Directory.Exists(_folder) == false)
                    {
                        Directory.CreateDirectory(_folder);
                    }
                    _instance = new AppConfig();
                    Save();
                }
                else
                {
                    _instance = DNHper.USerialization.DeserializeXML<AppConfig>(Paths.AppConfigPath);
                }
                return _instance;
            }
        }
    
        public static void Save()
        {
            DNHper.USerialization.SerializeXML(Instance, Paths.AppConfigPath);
        }
    
    }
}
