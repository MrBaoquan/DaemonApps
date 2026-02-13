using System;
using System.IO;

namespace DaemonKit.Models
{
    /// <summary>
    /// 备份文件信息
    /// </summary>
    public class BackupInfo
    {
        /// <summary>
        /// 文件名
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// 完整路径
        /// </summary>
        public string FullPath { get; set; }

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// 文件大小（显示文本）
        /// </summary>
        public string FileSizeText
        {
            get
            {
                if (FileSize < 1024)
                    return $"{FileSize} B";
                if (FileSize < 1024 * 1024)
                    return $"{FileSize / 1024.0:F1} KB";
                if (FileSize < 1024 * 1024 * 1024)
                    return $"{FileSize / (1024.0 * 1024.0):F1} MB";
                return $"{FileSize / (1024.0 * 1024.0 * 1024.0):F2} GB";
            }
        }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedTime { get; set; }

        /// <summary>
        /// 描述（从元数据中读取）
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 从文件路径创建 BackupInfo
        /// </summary>
        public static BackupInfo FromFile(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            return new BackupInfo
            {
                FileName = fileInfo.Name,
                FullPath = filePath,
                FileSize = fileInfo.Length,
                CreatedTime = fileInfo.CreationTime,
                Description = string.Empty
            };
        }
    }
}
