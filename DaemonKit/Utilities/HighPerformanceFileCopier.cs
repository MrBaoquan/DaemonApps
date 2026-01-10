using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Collections.Generic;
using System.Linq;

namespace DaemonKit.Utilities
{
    /// <summary>
    /// 文件复制进度信息
    /// </summary>
    public class FileCopyProgress
    {
        public long ProcessedBytes { get; set; }
        public long TotalBytes { get; set; }
        public int FileCount { get; set; }
        public string CurrentFile { get; set; }
        public double Percentage => TotalBytes > 0 ? (double)ProcessedBytes / TotalBytes * 100 : 0;
    }

    /// <summary>
    /// 高性能文件复制器，使用并行复制和大缓冲区提升性能
    /// </summary>
    public class HighPerformanceFileCopier
    {
        private const int BufferSize = 256 * 1024; // 256KB buffer
        private const int MaxDegreeOfParallelism = 4; // 最多4个并发复制任务

        /// <summary>
        /// 复制单个文件（异步，带进度）
        /// </summary>
        public static async Task CopyFileAsync(
            string sourceFile,
            string destinationFile,
            IProgress<FileCopyProgress> progress = null,
            CancellationToken cancellationToken = default
        )
        {
            if (!File.Exists(sourceFile))
                throw new FileNotFoundException($"Source file not found: {sourceFile}");

            var destDir = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            var fileInfo = new FileInfo(sourceFile);
            long totalBytes = fileInfo.Length;
            long processedBytes = 0;

            using (
                var sourceStream = new FileStream(
                    sourceFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    true
                )
            )
            using (
                var destStream = new FileStream(
                    destinationFile,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    true
                )
            )
            {
                byte[] buffer = new byte[BufferSize];
                int bytesRead;

                while (
                    (
                        bytesRead = await sourceStream.ReadAsync(
                            buffer,
                            0,
                            buffer.Length,
                            cancellationToken
                        )
                    ) > 0
                )
                {
                    await destStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    processedBytes += bytesRead;

                    progress?.Report(
                        new FileCopyProgress
                        {
                            ProcessedBytes = processedBytes,
                            TotalBytes = totalBytes,
                            FileCount = 1,
                            CurrentFile = Path.GetFileName(sourceFile)
                        }
                    );
                }
            }

            // 保留原文件的时间戳
            File.SetCreationTime(destinationFile, fileInfo.CreationTime);
            File.SetLastWriteTime(destinationFile, fileInfo.LastWriteTime);
        }

        /// <summary>
        /// 复制目录（异步，并行，带进度）
        /// </summary>
        public static async Task CopyDirectoryAsync(
            string sourceDirectory,
            string destinationDirectory,
            IProgress<FileCopyProgress> progress = null,
            CancellationToken cancellationToken = default
        )
        {
            if (!Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException(
                    $"Source directory not found: {sourceDirectory}"
                );

            if (!Directory.Exists(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            // 获取所有文件
            var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
            long totalBytes = files.Sum(f => new FileInfo(f).Length);
            long processedBytes = 0;
            int processedFiles = 0;
            object lockObj = new object();

            // 创建并行数据流块
            var copyBlock = new ActionBlock<string>(
                async sourceFile =>
                {
                    try
                    {
                        var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
                        var destFile = Path.Combine(destinationDirectory, relativePath);
                        var destDir = Path.GetDirectoryName(destFile);

                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        {
                            lock (lockObj)
                            {
                                if (!Directory.Exists(destDir))
                                    Directory.CreateDirectory(destDir);
                            }
                        }

                        var fileInfo = new FileInfo(sourceFile);
                        long fileSize = fileInfo.Length;

                        // 复制文件（不报告单个文件内的进度，避免过多更新）
                        using (
                            var sourceStream = new FileStream(
                                sourceFile,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                BufferSize,
                                true
                            )
                        )
                        using (
                            var destStream = new FileStream(
                                destFile,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None,
                                BufferSize,
                                true
                            )
                        )
                        {
                            await sourceStream.CopyToAsync(
                                destStream,
                                BufferSize,
                                cancellationToken
                            );
                        }

                        // 保留时间戳
                        File.SetCreationTime(destFile, fileInfo.CreationTime);
                        File.SetLastWriteTime(destFile, fileInfo.LastWriteTime);

                        // 更新进度
                        lock (lockObj)
                        {
                            processedBytes += fileSize;
                            processedFiles++;

                            progress?.Report(
                                new FileCopyProgress
                                {
                                    ProcessedBytes = processedBytes,
                                    TotalBytes = totalBytes,
                                    FileCount = processedFiles,
                                    CurrentFile = relativePath
                                }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        // 记录错误但继续处理其他文件
                        NLog.LogManager
                            .GetCurrentClassLogger()
                            .Error(ex, $"Failed to copy file: {sourceFile}");
                    }
                },
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                }
            );

            // 将文件加入队列
            foreach (var file in files)
            {
                await copyBlock.SendAsync(file, cancellationToken);
            }

            // 等待所有复制任务完成
            copyBlock.Complete();
            await copyBlock.Completion;
        }

        /// <summary>
        /// 复制多个文件（异步，并行，带进度）
        /// </summary>
        public static async Task CopyFilesAsync(
            IEnumerable<(string Source, string Destination)> filePairs,
            IProgress<FileCopyProgress> progress = null,
            CancellationToken cancellationToken = default
        )
        {
            var pairs = filePairs.ToArray();
            long totalBytes = pairs.Sum(
                p => File.Exists(p.Source) ? new FileInfo(p.Source).Length : 0
            );
            long processedBytes = 0;
            int processedFiles = 0;
            object lockObj = new object();

            var copyBlock = new ActionBlock<(string Source, string Destination)>(
                async pair =>
                {
                    try
                    {
                        if (!File.Exists(pair.Source))
                        {
                            NLog.LogManager
                                .GetCurrentClassLogger()
                                .Warn($"Source file not found: {pair.Source}");
                            return;
                        }

                        var destDir = Path.GetDirectoryName(pair.Destination);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        {
                            lock (lockObj)
                            {
                                if (!Directory.Exists(destDir))
                                    Directory.CreateDirectory(destDir);
                            }
                        }

                        var fileInfo = new FileInfo(pair.Source);
                        long fileSize = fileInfo.Length;

                        using (
                            var sourceStream = new FileStream(
                                pair.Source,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                BufferSize,
                                true
                            )
                        )
                        using (
                            var destStream = new FileStream(
                                pair.Destination,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None,
                                BufferSize,
                                true
                            )
                        )
                        {
                            await sourceStream.CopyToAsync(
                                destStream,
                                BufferSize,
                                cancellationToken
                            );
                        }

                        File.SetCreationTime(pair.Destination, fileInfo.CreationTime);
                        File.SetLastWriteTime(pair.Destination, fileInfo.LastWriteTime);

                        lock (lockObj)
                        {
                            processedBytes += fileSize;
                            processedFiles++;

                            progress?.Report(
                                new FileCopyProgress
                                {
                                    ProcessedBytes = processedBytes,
                                    TotalBytes = totalBytes,
                                    FileCount = processedFiles,
                                    CurrentFile = Path.GetFileName(pair.Source)
                                }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        NLog.LogManager
                            .GetCurrentClassLogger()
                            .Error(ex, $"Failed to copy file: {pair.Source}");
                    }
                },
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                }
            );

            foreach (var pair in pairs)
            {
                await copyBlock.SendAsync(pair, cancellationToken);
            }

            copyBlock.Complete();
            await copyBlock.Completion;
        }

        /// <summary>
        /// 计算目录大小
        /// </summary>
        public static long CalculateDirectorySize(string directory)
        {
            if (!Directory.Exists(directory))
                return 0;

            return Directory
                .GetFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }

        /// <summary>
        /// 计算多个文件的总大小
        /// </summary>
        public static long CalculateFilesSize(IEnumerable<string> files)
        {
            return files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        }
    }
}
