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
            var canceled = false;

            await Task.Run(() =>
            {
                FileStream zipStream = null;
                int retryCount = 0;
                const int maxRetries = 3;
                Exception lastException = null;

                // 重试逻辑 - 处理文件被占用或访问失败的情况
                while (retryCount < maxRetries && zipStream == null)
                {
                    try
                    {
                        zipStream = new FileStream(
                            destinationZipPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.Read, // 允许其他进程读取
                            4096,
                            FileOptions.None
                        );
                    }
                    catch (IOException ex)
                    {
                        lastException = ex;
                        retryCount++;

                        if (retryCount >= maxRetries)
                        {
                            // 重试失败，抛出友好的错误消息
                            var errorMsg =
                                ex.HResult == unchecked((int)0x80070020)
                                    ? $"无法创建文件 '{destinationZipPath}'，文件被其他进程占用。请关闭可能正在使用该文件的程序（如压缩软件、文件管理器、防病毒软件等）后重试。"
                                    : $"无法创建文件 '{destinationZipPath}'：{ex.Message}";
                            throw new IOException(errorMsg, ex);
                        }

                        System.Threading.Thread.Sleep(500); // 等待 500ms 后重试
                    }
                }

                if (zipStream == null)
                {
                    throw new IOException(
                        $"无法创建文件 '{destinationZipPath}'，已尝试 {maxRetries} 次。",
                        lastException
                    );
                }

                using (zipStream)
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
                        if (cancellationToken.IsCancellationRequested)
                        {
                            canceled = true;
                            break;
                        }

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
            });

            if (canceled)
            {
                // 清理未完成的压缩文件并静默退出
                try
                {
                    if (File.Exists(destinationZipPath))
                    {
                        File.Delete(destinationZipPath);
                    }
                }
                catch
                { /* 忽略清理错误 */
                }

                return; // 正常返回，避免抛出取消异常
            }
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

            try
            {
                await Task.Run(
                    () =>
                    {
                        // 使用 FileShare.Read 允许其他进程读取
                        using (
                            var fileStream = new FileStream(
                                zipPath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                4096,
                                FileOptions.SequentialScan
                            )
                        )
                        using (var archive = SharpCompress.Archives.Zip.ZipArchive.Open(fileStream))
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
                                // Poll cancellation token and return early instead of throwing
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    NLog.LogManager.GetCurrentClassLogger().Info("解压操作被取消");
                                    return;
                                }

                                if (entry.IsDirectory)
                                    continue;

                                var destPath = Path.Combine(destinationDirectory, entry.Key);
                                var destDir = Path.GetDirectoryName(destPath);

                                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                                    Directory.CreateDirectory(destDir);

                                using (var entryStream = entry.OpenEntryStream())
                                using (var outputFileStream = File.Create(destPath, BufferSize))
                                {
                                    byte[] buffer = new byte[BufferSize];
                                    int bytesRead;
                                    while (
                                        (bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0
                                    )
                                    {
                                        outputFileStream.Write(buffer, 0, bytesRead);
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
            catch (OperationCanceledException)
            {
                // 清理未完成的解压目录（如果需要）
                // 注意：不删除整个 destinationDirectory，因为可能有其他文件
                throw; // 重新抛出，让调用方知道操作已取消
            }
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

            // 验证文件是否为ZIP格式
            try
            {
                using (var fs = File.OpenRead(zipPath))
                {
                    // 检查ZIP文件签名 (PK\x03\x04)
                    if (fs.Length < 4)
                        throw new InvalidDataException($"文件太小，不是有效的ZIP文件: {zipPath}");

                    byte[] signature = new byte[4];
                    fs.Read(signature, 0, 4);

                    // ZIP文件签名: 50 4B 03 04 (PK\x03\x04)
                    if (
                        signature[0] != 0x50
                        || signature[1] != 0x4B
                        || (signature[2] != 0x03 && signature[2] != 0x05 && signature[2] != 0x07)
                        || (signature[3] != 0x04 && signature[3] != 0x06 && signature[3] != 0x08)
                    )
                    {
                        throw new InvalidDataException($"该文件不是有效的ZIP格式文件: {zipPath}");
                    }
                }
            }
            catch (InvalidDataException)
            {
                throw; // 重新抛出验证异常
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"无法读取文件，请确保文件未被占用: {zipPath}", ex);
            }

            await Task.Run(() =>
            {
                try
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
                                    while (
                                        (bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0
                                    )
                                    {
                                        fileStream.Write(buffer, 0, bytesRead);
                                    }
                                }
                                return;
                            }
                        }
                        throw new FileNotFoundException($"Entry not found in ZIP: {entryPath}");
                    }
                }
                catch (SharpCompress.Common.ArchiveException ex)
                {
                    throw new InvalidDataException($"ZIP文件损坏或格式不正确: {ex.Message}", ex);
                }
            });
        }
    }
}
