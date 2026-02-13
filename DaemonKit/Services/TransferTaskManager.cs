using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DaemonKit.Models;
using DaemonKit.Utilities;
using DNHper;
using DynamicData;
using DynamicData.Binding;
using Newtonsoft.Json;
using ReactiveUI;

namespace DaemonKit.Services
{
    /// <summary>
    /// 传输任务管理器 - 统一管理所有文件传输任务的进度、速度、分类
    /// 使用RX响应式编程实现实时速度计算和UI刷新
    /// </summary>
    public class TransferTaskManager : ReactiveObject, IDisposable
    {
        #region 常量

        /// <summary>速度采样间隔（毫秒）</summary>
        private const int SpeedSampleIntervalMs = 500;

        /// <summary>速度滑动窗口大小（保留最近N个采样点）</summary>
        private const int SpeedWindowSize = 10;

        /// <summary>已完成任务自动清理延迟（秒）</summary>
        private const int CompletedAutoRemoveDelaySec = 300;

        /// <summary>已完成任务最大保留数</summary>
        private const int MaxCompletedTasks = 100;

        #endregion

        #region 数据源

        /// <summary>所有传输任务的数据源缓存（Key = TaskId）</summary>
        private readonly SourceCache<TransferTaskItem, string> _taskCache;

        /// <summary>速度采样历史 [TaskId → 采样队列]</summary>
        private readonly ConcurrentDictionary<string, Queue<SpeedSample>> _speedSamples = new();

        #endregion

        #region 公开的Observable集合

        /// <summary>正在传输的任务列表（Transferring + Pending + Paused）</summary>
        private readonly ReadOnlyObservableCollection<TransferTaskItem> _activeTasks;
        public ReadOnlyObservableCollection<TransferTaskItem> ActiveTasks => _activeTasks;

        /// <summary>已完成的任务列表（Completed + Failed + Cancelled）</summary>
        private readonly ReadOnlyObservableCollection<TransferTaskItem> _completedTasks;
        public ReadOnlyObservableCollection<TransferTaskItem> CompletedTasks => _completedTasks;

        /// <summary>所有任务（供状态栏汇总用）</summary>
        private readonly ReadOnlyObservableCollection<TransferTaskItem> _allTasks;
        public ReadOnlyObservableCollection<TransferTaskItem> AllTasks => _allTasks;

        /// <summary>正在上传的任务列表</summary>
        private readonly ReadOnlyObservableCollection<TransferTaskItem> _activeUploadTasks;
        public ReadOnlyObservableCollection<TransferTaskItem> ActiveUploadTasks =>
            _activeUploadTasks;

        /// <summary>正在下载的任务列表</summary>
        private readonly ReadOnlyObservableCollection<TransferTaskItem> _activeDownloadTasks;
        public ReadOnlyObservableCollection<TransferTaskItem> ActiveDownloadTasks =>
            _activeDownloadTasks;

        #endregion

        #region 统计属性（响应式）

        private int _activeCount;

        /// <summary>活跃任务数</summary>
        public int ActiveCount
        {
            get => _activeCount;
            private set => this.RaiseAndSetIfChanged(ref _activeCount, value);
        }

        private int _completedCount;

        /// <summary>已完成任务数</summary>
        public int CompletedCount
        {
            get => _completedCount;
            private set => this.RaiseAndSetIfChanged(ref _completedCount, value);
        }

        private double _totalProgress;

        /// <summary>总进度 (0-100)</summary>
        public double TotalProgress
        {
            get => _totalProgress;
            private set => this.RaiseAndSetIfChanged(ref _totalProgress, value);
        }

        private string _totalSpeedDisplay = "";

        /// <summary>总速度显示</summary>
        public string TotalSpeedDisplay
        {
            get => _totalSpeedDisplay;
            private set => this.RaiseAndSetIfChanged(ref _totalSpeedDisplay, value);
        }

        private int _uploadCount;

        /// <summary>正在上传的任务数</summary>
        public int UploadCount
        {
            get => _uploadCount;
            private set => this.RaiseAndSetIfChanged(ref _uploadCount, value);
        }

        private int _downloadCount;

        /// <summary>正在下载的任务数</summary>
        public int DownloadCount
        {
            get => _downloadCount;
            private set => this.RaiseAndSetIfChanged(ref _downloadCount, value);
        }

        #endregion

        #region 事件

        /// <summary>任务状态变更事件</summary>
        private readonly Subject<TransferTaskItem> _taskStateChanged = new();
        public IObservable<TransferTaskItem> TaskStateChanged => _taskStateChanged.AsObservable();

        /// <summary>所有任务完成事件</summary>
        private readonly Subject<bool> _allTasksCompleted = new();
        public IObservable<bool> AllTasksCompleted => _allTasksCompleted.AsObservable();

        #endregion

        private readonly CompositeDisposable _disposables = new();

        #region 构造函数

        public TransferTaskManager()
        {
            _taskCache = new SourceCache<TransferTaskItem, string>(t => t.TaskId);

            // 1. 活跃任务管道：只显示传输中、等待中、暂停的任务
            var activeSubscription = _taskCache
                .Connect()
                .AutoRefresh(t => t.State)
                .Filter(
                    t =>
                        t.State == TransferState.Transferring
                        || t.State == TransferState.Pending
                        || t.State == TransferState.Paused
                )
                .Sort(
                    SortExpressionComparer<TransferTaskItem>
                        .Ascending(t => GetStateSortOrder(t.State))
                        .ThenByDescending(t => t.StartTime)
                )
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _activeTasks)
                .Subscribe();

            // 2. 已完成任务管道
            var completedSubscription = _taskCache
                .Connect()
                .AutoRefresh(t => t.State)
                .Filter(
                    t =>
                        t.State == TransferState.Completed
                        || t.State == TransferState.Failed
                        || t.State == TransferState.Cancelled
                )
                .Sort(
                    SortExpressionComparer<TransferTaskItem>.Descending(
                        t => t.EndTime ?? DateTime.MinValue
                    )
                )
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _completedTasks)
                .Subscribe();

            // 3. 所有任务管道
            var allSubscription = _taskCache
                .Connect()
                .Sort(SortExpressionComparer<TransferTaskItem>.Descending(t => t.StartTime))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _allTasks)
                .Subscribe();

            // 4. 上传任务管道
            var uploadSubscription = _taskCache
                .Connect()
                .AutoRefresh(t => t.State)
                .Filter(
                    t =>
                        (
                            t.State == TransferState.Transferring
                            || t.State == TransferState.Pending
                            || t.State == TransferState.Paused
                        )
                        && t.Direction == TransferDirection.Upload
                )
                .Sort(
                    SortExpressionComparer<TransferTaskItem>
                        .Ascending(t => GetStateSortOrder(t.State))
                        .ThenByDescending(t => t.StartTime)
                )
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _activeUploadTasks)
                .Subscribe();

            // 5. 下载任务管道
            var downloadSubscription = _taskCache
                .Connect()
                .AutoRefresh(t => t.State)
                .Filter(
                    t =>
                        (
                            t.State == TransferState.Transferring
                            || t.State == TransferState.Pending
                            || t.State == TransferState.Paused
                        )
                        && t.Direction == TransferDirection.Download
                )
                .Sort(
                    SortExpressionComparer<TransferTaskItem>
                        .Ascending(t => GetStateSortOrder(t.State))
                        .ThenByDescending(t => t.StartTime)
                )
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _activeDownloadTasks)
                .Subscribe();

            // 6. 速度采样定时器 - 每500ms对所有活跃任务采样并计算瞬时速度
            var speedTimer = Observable
                .Interval(TimeSpan.FromMilliseconds(SpeedSampleIntervalMs))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => UpdateSpeedForAllTasks());

            // 7. 统计属性（ActiveCount/CompletedCount/TotalProgress/TotalSpeedDisplay）
            //    全部由速度定时器在 UpdateSpeedForAllTasks() 中统一计算，
            //    消除 AutoRefresh 管道在每次 PropertyChanged 时的级联开销

            _disposables.Add(activeSubscription);
            _disposables.Add(completedSubscription);
            _disposables.Add(allSubscription);
            _disposables.Add(uploadSubscription);
            _disposables.Add(downloadSubscription);
            _disposables.Add(speedTimer);
        }

        #endregion

        #region 排序辅助

        /// <summary>
        /// 获取任务状态的排序权重（数值越小越靠前）
        /// 传输中 > 等待中 > 已暂停
        /// </summary>
        private static int GetStateSortOrder(TransferState state) =>
            state switch
            {
                TransferState.Transferring => 0,
                TransferState.Pending => 1,
                TransferState.Paused => 2,
                _ => 3
            };

        #endregion

        #region 公开方法

        /// <summary>
        /// 从P2P服务的FileTransferTask创建或更新跟踪项
        /// </summary>
        public TransferTaskItem TrackTask(FileTransferTask serviceTask)
        {
            var existing = _taskCache.Lookup(serviceTask.TaskId);
            if (existing.HasValue)
            {
                var item = existing.Value;
                item.UpdateFromServiceTask(serviceTask);
                // 不再对已存在项调用 AddOrUpdate：
                // 1. 项已在缓存中，AutoRefresh 通过 PropertyChanged 自动驱动管道刷新
                // 2. AddOrUpdate 会触发全部 6+ 条 Connect 管道重算（Filter/Sort/ToCollection），
                //    以 5次/秒 的频率造成大量冗余 UI 线程调度，抢占 Input 优先级导致界面卡死
                return item;
            }

            // 检查是否存在同名的Pending占位符任务（批量下载时预创建的等待中任务）
            // 如果存在，用实际任务替换占位符，避免传输列表中出现重复条目
            var placeholder = _taskCache.Items.FirstOrDefault(
                t =>
                    t.State == TransferState.Pending
                    && string.Equals(
                        t.FileName,
                        serviceTask.FileName,
                        System.StringComparison.OrdinalIgnoreCase
                    )
            );
            if (placeholder != null)
            {
                _taskCache.RemoveKey(placeholder.TaskId);
                _speedSamples.TryRemove(placeholder.TaskId, out _);
            }

            var newItem = TransferTaskItem.FromServiceTask(serviceTask);
            _taskCache.AddOrUpdate(newItem);

            // 初始化速度采样
            _speedSamples[newItem.TaskId] = new Queue<SpeedSample>();

            // 通知状态变更（让资源库能更新TransferTaskId映射）
            _taskStateChanged.OnNext(newItem);

            return newItem;
        }

        /// <summary>
        /// 更新任务进度（由P2P服务在每个chunk传输后调用）
        /// </summary>
        public void UpdateProgress(string taskId, long transferredBytes)
        {
            var existing = _taskCache.Lookup(taskId);
            if (!existing.HasValue)
                return;

            var item = existing.Value;
            item.TransferredBytes = transferredBytes;
            // 不在这里触发AddOrUpdate以减少UI刷新频率
            // 速度计算由定时器统一处理
        }

        /// <summary>
        /// 标记任务完成
        /// </summary>
        public void CompleteTask(string taskId, bool success, string errorMessage = null)
        {
            var existing = _taskCache.Lookup(taskId);
            if (!existing.HasValue)
                return;

            var item = existing.Value;
            item.EndTime = DateTime.Now;

            if (success)
            {
                item.State = TransferState.Completed;
                item.TransferredBytes = item.TotalBytes;
            }
            else
            {
                item.State = TransferState.Failed;
                item.ErrorMessage = errorMessage ?? "传输失败";
            }

            // 清理速度采样
            _speedSamples.TryRemove(taskId, out _);

            // 不再调用 AddOrUpdate：State 变更已通过 AutoRefresh 驱动所有管道刷新
            // AddOrUpdate 会触发全部管道完整重算，造成不必要的 UI 线程负载
            _taskStateChanged.OnNext(item);

            // 检查是否所有活跃任务都已完成
            if (_activeTasks.Count == 0)
            {
                _allTasksCompleted.OnNext(true);
            }
        }

        /// <summary>
        /// 暂停任务（更新状态并通知订阅者）
        /// </summary>
        public void PauseTask(string taskId)
        {
            var existing = _taskCache.Lookup(taskId);
            if (!existing.HasValue)
                return;

            var item = existing.Value;
            item.State = TransferState.Paused;
            _taskStateChanged.OnNext(item);
        }

        /// <summary>
        /// 恢复任务（更新状态并通知订阅者）
        /// </summary>
        public void ResumeTask(string taskId)
        {
            var existing = _taskCache.Lookup(taskId);
            if (!existing.HasValue)
                return;

            var item = existing.Value;
            item.State = TransferState.Transferring;
            _taskStateChanged.OnNext(item);
        }

        /// <summary>
        /// 取消任务
        /// </summary>
        public void CancelTask(string taskId)
        {
            var existing = _taskCache.Lookup(taskId);
            if (!existing.HasValue)
                return;

            var item = existing.Value;
            item.State = TransferState.Cancelled;
            item.EndTime = DateTime.Now;
            _speedSamples.TryRemove(taskId, out _);
            // 不再调用 AddOrUpdate：State 变更已通过 AutoRefresh 驱动管道刷新
            _taskStateChanged.OnNext(item);
        }

        /// <summary>
        /// 清除所有已完成/失败/取消的任务
        /// </summary>
        public void ClearCompleted()
        {
            var toRemove = _taskCache.Items
                .Where(
                    t =>
                        t.State == TransferState.Completed
                        || t.State == TransferState.Failed
                        || t.State == TransferState.Cancelled
                )
                .Select(t => t.TaskId)
                .ToList();

            _taskCache.RemoveKeys(toRemove);
        }

        /// <summary>
        /// 移除单个已完成任务
        /// </summary>
        public void RemoveTask(string taskId)
        {
            _taskCache.RemoveKey(taskId);
            _speedSamples.TryRemove(taskId, out _);
        }

        /// <summary>
        /// 根据TaskId查找关联的TransferTaskItem
        /// </summary>
        public TransferTaskItem FindTask(string taskId)
        {
            var existing = _taskCache.Lookup(taskId);
            return existing.HasValue ? existing.Value : null;
        }

        /// <summary>
        /// 根据文件名查找关联的TransferTaskItem（返回最近添加的匹配项）
        /// </summary>
        public TransferTaskItem FindTaskByFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;
            return _taskCache.Items.FirstOrDefault(
                t => string.Equals(t.FileName, fileName, System.StringComparison.OrdinalIgnoreCase)
            );
        }

        /// <summary>
        /// 创建占位符任务（用于远程备份等准备阶段，在传输面板中显示进度反馈）
        /// </summary>
        public TransferTaskItem CreatePlaceholderTask(
            string taskId,
            string fileName,
            TransferDirection direction,
            TransferTaskSource source,
            string remoteDeviceName,
            string remoteDeviceIP,
            string statusMessage = null
        )
        {
            var item = new TransferTaskItem
            {
                TaskId = taskId,
                FileName = fileName,
                Direction = direction,
                Source = source,
                RemoteDeviceName = remoteDeviceName,
                RemoteDeviceIP = remoteDeviceIP,
                State = TransferState.Transferring,
                TotalBytes = 0,
                StatusMessageOverride = statusMessage ?? "准备中..."
            };
            _taskCache.AddOrUpdate(item);
            _speedSamples[item.TaskId] = new Queue<SpeedSample>();
            return item;
        }

        /// <summary>
        /// 更新任务的状态文本覆盖（用于占位符任务显示自定义状态）
        /// </summary>
        public void UpdateTaskStatus(
            string taskId,
            string statusMessage,
            TransferState? state = null
        )
        {
            var existing = _taskCache.Lookup(taskId);
            if (!existing.HasValue)
                return;

            var item = existing.Value;
            item.StatusMessageOverride = statusMessage;
            if (state.HasValue)
                item.State = state.Value;
            _taskCache.AddOrUpdate(item);
        }

        #endregion

        #region 速度计算

        /// <summary>
        /// 对所有活跃任务更新速度（由定时器调用）
        /// </summary>
        /// <summary>清理计数器：每60次tick（30秒）才执行一次清理</summary>
        private int _cleanupCounter;

        private void UpdateSpeedForAllTasks()
        {
            var now = DateTime.Now;

            // 自动清理过期已完成任务（降频到每30秒一次，避免每500ms全表扫描）
            if (++_cleanupCounter >= 60)
            {
                _cleanupCounter = 0;
                CleanupCompletedTasks(now);
            }

            // ── 统一计算统计属性 ──
            ActiveCount = _activeTasks.Count;
            CompletedCount = _completedTasks.Count;
            UploadCount = _activeUploadTasks.Count;
            DownloadCount = _activeDownloadTasks.Count;

            // 使用foreach避免LINQ ToList()分配
            long totalBytes = 0,
                transferred = 0;
            double totalSpeed = 0;
            foreach (var t in _activeTasks)
            {
                if (t.State == TransferState.Transferring || t.State == TransferState.Pending)
                {
                    totalBytes += t.TotalBytes;
                    transferred += t.TransferredBytes;
                }
                if (t.State == TransferState.Transferring)
                {
                    totalSpeed += t.SpeedBytesPerSecond;
                }
            }
            TotalProgress = totalBytes > 0 ? (double)transferred / totalBytes * 100 : 0;
            TotalSpeedDisplay = FormatSpeed(totalSpeed);

            // ── 逐任务计算瞬时速度 ──
            foreach (var task in _activeTasks)
            {
                if (task.State != TransferState.Transferring)
                    continue;

                // 记录采样点
                if (_speedSamples.TryGetValue(task.TaskId, out var samples))
                {
                    samples.Enqueue(new SpeedSample(now, task.TransferredBytes));

                    // 保持滑动窗口大小
                    while (samples.Count > SpeedWindowSize)
                    {
                        samples.Dequeue();
                    }

                    // 计算瞬时速度（基于窗口首尾差值）
                    if (samples.Count >= 2)
                    {
                        var oldest = samples.Peek();
                        var newest = new SpeedSample(now, task.TransferredBytes);
                        var timeDiff = (newest.Timestamp - oldest.Timestamp).TotalSeconds;
                        var bytesDiff = newest.Bytes - oldest.Bytes;

                        if (timeDiff > 0 && bytesDiff >= 0)
                        {
                            task.SpeedBytesPerSecond = bytesDiff / timeDiff;
                        }
                    }

                    // 计算ETA
                    if (task.SpeedBytesPerSecond > 0)
                    {
                        var remaining = task.TotalBytes - task.TransferredBytes;
                        var etaSeconds = remaining / task.SpeedBytesPerSecond;
                        task.EstimatedRemaining = TimeSpan.FromSeconds(Math.Min(etaSeconds, 86400));
                    }
                    else
                    {
                        task.EstimatedRemaining = TimeSpan.MaxValue;
                    }

                    // 刷新显示属性
                    task.RaiseProgressChanged();
                }
            }
        }

        #endregion

        #region 工具方法

        /// <summary>
        /// 自动清理过期已完成任务
        /// </summary>
        private void CleanupCompletedTasks(DateTime now)
        {
            var completedItems = _taskCache.Items
                .Where(t => t.IsFinished)
                .OrderByDescending(t => t.EndTime)
                .ToList();

            if (completedItems.Count == 0)
                return;

            var toRemove = new List<string>();

            // 移除超过保留时间的已完成任务
            foreach (var item in completedItems)
            {
                if (
                    item.EndTime.HasValue
                    && (now - item.EndTime.Value).TotalSeconds > CompletedAutoRemoveDelaySec
                )
                {
                    toRemove.Add(item.TaskId);
                }
            }

            // 超过最大保留数的已完成任务也移除
            if (completedItems.Count > MaxCompletedTasks)
            {
                var excess = completedItems
                    .Skip(MaxCompletedTasks)
                    .Select(t => t.TaskId)
                    .Where(id => !toRemove.Contains(id));
                toRemove.AddRange(excess);
            }

            if (toRemove.Count > 0)
            {
                _taskCache.RemoveKeys(toRemove);
                foreach (var id in toRemove)
                    _speedSamples.TryRemove(id, out _);
            }
        }

        /// <summary>格式化速度显示</summary>
        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
                return "0 B/s";
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            int order = 0;
            double speed = bytesPerSecond;
            while (speed >= 1024 && order < units.Length - 1)
            {
                order++;
                speed /= 1024;
            }
            return $"{speed:0.##} {units[order]}";
        }

        /// <summary>格式化字节数</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double b = bytes;
            while (b >= 1024 && order < units.Length - 1)
            {
                order++;
                b /= 1024;
            }
            return $"{b:0.##} {units[order]}";
        }

        /// <summary>格式化剩余时间</summary>
        public static string FormatETA(TimeSpan eta)
        {
            if (eta == TimeSpan.MaxValue || eta.TotalSeconds < 0)
                return "--";
            if (eta.TotalHours >= 1)
                return $"{(int)eta.TotalHours}时{eta.Minutes:D2}分";
            if (eta.TotalMinutes >= 1)
                return $"{(int)eta.TotalMinutes}分{eta.Seconds:D2}秒";
            return $"{(int)eta.TotalSeconds}秒";
        }

        #endregion

        #region 历史持久化

        /// <summary>传输历史文件路径</summary>
        private static string HistoryFilePath => AppPathes.TransferHistoryPath;

        /// <summary>
        /// 保存已完成的传输任务到本地JSON文件
        /// </summary>
        public void SaveHistory()
        {
            try
            {
                var completedItems = _taskCache.Items
                    .Where(t => t.IsFinished)
                    .OrderByDescending(t => t.EndTime)
                    .Take(MaxCompletedTasks)
                    .Select(
                        t =>
                            new TransferHistoryEntry
                            {
                                TaskId = t.TaskId,
                                FileName = t.FileName,
                                LocalPath = t.LocalPath,
                                RemoteDeviceName = t.RemoteDeviceName,
                                RemoteDeviceIP = t.RemoteDeviceIP,
                                TotalBytes = t.TotalBytes,
                                TransferredBytes = t.TransferredBytes,
                                Direction = t.Direction,
                                State = t.State,
                                Source = t.Source,
                                StartTime = t.StartTime,
                                EndTime = t.EndTime,
                                ErrorMessage = t.ErrorMessage,
                            }
                    )
                    .ToList();

                var dir = Path.GetDirectoryName(HistoryFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(completedItems, Formatting.Indented);
                File.WriteAllText(HistoryFilePath, json);
                NLogger.Info($"[传输] 已保存 {completedItems.Count} 条传输历史");
            }
            catch (Exception ex)
            {
                NLogger.Error($"[传输] 保存传输历史失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从本地JSON文件加载传输历史（在构造函数之后调用）
        /// </summary>
        public void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryFilePath))
                    return;

                var json = File.ReadAllText(HistoryFilePath);
                var entries = JsonConvert.DeserializeObject<List<TransferHistoryEntry>>(json);
                if (entries == null || entries.Count == 0)
                    return;

                var items = entries
                    .Select(
                        e =>
                            new TransferTaskItem
                            {
                                TaskId = e.TaskId,
                                FileName = e.FileName,
                                LocalPath = e.LocalPath,
                                RemoteDeviceName = e.RemoteDeviceName,
                                RemoteDeviceIP = e.RemoteDeviceIP,
                                TotalBytes = e.TotalBytes,
                                TransferredBytes = e.TransferredBytes,
                                Direction = e.Direction,
                                State = e.State,
                                Source = e.Source,
                                StartTime = e.StartTime,
                                EndTime = e.EndTime,
                                ErrorMessage = e.ErrorMessage,
                            }
                    )
                    .ToList();

                _taskCache.AddOrUpdate(items);
                NLogger.Info($"[传输] 已恢复 {items.Count} 条传输历史");
            }
            catch (Exception ex)
            {
                NLogger.Error($"[传输] 加载传输历史失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 传输历史序列化DTO
        /// </summary>
        private class TransferHistoryEntry
        {
            public string TaskId { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string LocalPath { get; set; } = string.Empty;
            public string RemoteDeviceName { get; set; } = string.Empty;
            public string RemoteDeviceIP { get; set; } = string.Empty;
            public long TotalBytes { get; set; }
            public long TransferredBytes { get; set; }
            public TransferDirection Direction { get; set; }
            public TransferState State { get; set; }
            public TransferTaskSource Source { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _disposables.Dispose();
            _taskStateChanged.Dispose();
            _allTasksCompleted.Dispose();
            _taskCache.Dispose();
        }

        #endregion

        #region 内部类型

        /// <summary>速度采样点</summary>
        private readonly struct SpeedSample
        {
            public DateTime Timestamp { get; }
            public long Bytes { get; }

            public SpeedSample(DateTime timestamp, long bytes)
            {
                Timestamp = timestamp;
                Bytes = bytes;
            }
        }

        #endregion
    }
}
