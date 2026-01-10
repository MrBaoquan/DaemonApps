using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DaemonKit.Utilities
{
    /// <summary>
    /// 压缩进度信息
    /// </summary>
    public class CompressionProgress
    {
        public long ProcessedBytes { get; set; }
        public long TotalBytes { get; set; }
        public int FileCount { get; set; }
        public string CurrentFile { get; set; }
        public double Percentage => TotalBytes > 0 ? (double)ProcessedBytes / TotalBytes * 100 : 0;
    }

    /// <summary>
    /// 高性能压缩器，使用SharpCompress库提供压缩进度回调
    /// </summary>
    public class HighPerformanceCompressor
    {
        private const int BufferSize = 8 * 1024 * 1024; // 8MB buffer for optimal performance

        /// <summary>
        /// 压缩目录到ZIP文件（异步，带进度）
        /// </summary>
        public static async Task CompressDirectoryAsync(
            string sourceDirectory,
            string destinationZipPath,
            IProgress<CompressionProgress> progress = null,
            CancellationToken cancellationToken = default
        )
        {
            if (!Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException(
                    $"Source directory not found: {sourceDirectory}"
                );

            // 确保目标目录存在
            var destDir = Path.GetDirectoryName(destinationZipPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            // 计算总大小
            var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
            long totalBytes = 0;
            foreach (var file in files)
            {
                totalBytes += new FileInfo(file).Length;
            }

            long processedBytes = 0;

            await Task.Run(
                () =>
                {
                    using (var zipStream = File.Create(destinationZipPath))
                    using (
                        var writer = WriterFactory.Open(
                            zipStream,
                            ArchiveType.Zip,
                            new WriterOptions(CompressionType.Deflate)
                            {
                                LeaveStreamOpen = false,
                                ArchiveEncoding = new ArchiveEncoding
                                {
                                    Default = System.Text.Encoding.UTF8
                                }
                            }
                        )
                    )
                    {
                        for (int i = 0; i < files.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var file = files[i];
                            var relativePath = Path.GetRelativePath(sourceDirectory, file);

                            // 读取文件并添加到压缩包
                            using (var fileStream = File.OpenRead(file))
                            {
                                writer.Write(relativePath, fileStream);

                                processedBytes += new FileInfo(file).Length;

                                progress?.Report(
                                    new CompressionProgress
                                    {
                                        ProcessedBytes = processedBytes,
                                        TotalBytes = totalBytes,
                                        FileCount = i + 1,
                                        CurrentFile = relativePath
                                    }
                                );
                            }
                        }
                    }
                },
                cancellationToken
            );
        }

        /// <summary>
        /// 解压ZIP文件到目录（异步，带进度）
        /// </summary>
        public static async Task DecompressZipAsync(
            string zipPath,
            string destinationDirectory,
            IProgress<CompressionProgress> progress = null,
            CancellationToken cancellationToken = default
        )
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException($"ZIP file not found: {zipPath}");

            if (!Directory.Exists(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            await Task.Run(
                () =>
                {
                    using (var archive = SharpCompress.Archives.Zip.ZipArchive.Open(zipPath))
                    {
                        var entries = archive.Entries;
                        long totalBytes = 0;
                        int totalFiles = 0;

                        // 计算总大小
                        foreach (var entry in entries)
                        {
                            if (!entry.IsDirectory)
                            {
                                totalBytes += entry.Size;
                                totalFiles++;
                            }
                        }

                        long processedBytes = 0;
                        int processedFiles = 0;

                        foreach (var entry in entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (entry.IsDirectory)
                                continue;

                            var destPath = Path.Combine(destinationDirectory, entry.Key);
                            var destDir = Path.GetDirectoryName(destPath);

                            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                                Directory.CreateDirectory(destDir);

                            using (var entryStream = entry.OpenEntryStream())
                            using (var fileStream = File.Create(destPath, BufferSize))
                            {
                                byte[] buffer = new byte[BufferSize];
                                int bytesRead;
                                while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    fileStream.Write(buffer, 0, bytesRead);
                                }
                            }

                            processedBytes += entry.Size;
                            processedFiles++;

                            progress?.Report(
                                new CompressionProgress
                                {
                                    ProcessedBytes = processedBytes,
                                    TotalBytes = totalBytes,
                                    FileCount = processedFiles,
                                    CurrentFile = entry.Key
                                }
                            );
                        }
                    }
                },
                cancellationToken
            );
        }

        /// <summary>
        /// 读取ZIP文件中的条目列表（用于预览）
        /// </summary>
        public static string[] GetZipEntries(string zipPath)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException($"ZIP file not found: {zipPath}");

            using (var archive = SharpCompress.Archives.Zip.ZipArchive.Open(zipPath))
            {
                var entries = new System.Collections.Generic.List<string>();
                foreach (var entry in archive.Entries)
                {
                    if (!entry.IsDirectory)
                        entries.Add(entry.Key);
                }
                return entries.ToArray();
            }
        }

        /// <summary>
        /// 从ZIP文件中提取单个文件
        /// </summary>
        public static async Task ExtractFileAsync(
            string zipPath,
            string entryPath,
            string destinationPath
        )
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException($"ZIP file not found: {zipPath}");

            await Task.Run(() =>
            {
                using (var archive = SharpCompress.Archives.Zip.ZipArchive.Open(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (
                            entry.Key == entryPath
                            || entry.Key.Replace('\\', '/') == entryPath.Replace('\\', '/')
                        )
                        {
                            var destDir = Path.GetDirectoryName(destinationPath);
                            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                                Directory.CreateDirectory(destDir);

                            using (var entryStream = entry.OpenEntryStream())
                            using (var fileStream = File.Create(destinationPath, BufferSize))
                            {
                                byte[] buffer = new byte[BufferSize];
                                int bytesRead;
                                while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    fileStream.Write(buffer, 0, bytesRead);
                                }
                            }
                            return;
                        }
                    }
                    throw new FileNotFoundException($"Entry not found in ZIP: {entryPath}");
                }
            });
        }
    }
}
