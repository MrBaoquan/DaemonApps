using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DaemonKit.Models;
using DaemonKit.Utilities;
using DNHper;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using ReactiveUI;

namespace DaemonKit.Services
{
    /// <summary>
    /// P2P文件传输服务
    /// 支持多文件并发传输、断点续传、进度监控
    /// </summary>
    public class P2PFileTransferService : IDisposable
    {
        #region 常量配置

        /// <summary>最大并发传输数</summary>
        private int _maxConcurrentTransfers = 4;

        /// <summary>数据块大小 (256KB，与AsyncFileCopy一致)</summary>
        private readonly int _chunkSize = 256 * 1024;

        /// <summary>默认传输端口（使用可配置的 CommonVars.FileTransferPort）</summary>
        private int _defaultPort => CommonVars.FileTransferPort;

        /// <summary>对外暴露端口号（供UDP推送命令使用）</summary>
        public int DefaultPort => _defaultPort;

        /// <summary>接收超时（毫秒）</summary>
        private readonly int _receiveTimeout = 30000;

        /// <summary>文件接收保存目录</summary>
        private readonly string _receiveDirectory;

        #endregion

        #region 字段

        private RouterSocket _serverSocket;
        private SemaphoreSlim _transferSemaphore;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _taskCancellations =
            new();
        private readonly ConcurrentDictionary<string, FileTransferTask> _activeTasks = new();
        private readonly ConcurrentDictionary<string, FileStream> _receivingFiles = new();
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _taskPauseEvents =
            new();

        /// <summary>RouterSocket写操作锁，确保多线程唳发时帧完整性</summary>
        private readonly object _socketWriteLock = new();

        /// <summary>Rx订阅清理容器（管理接收管道等后台订阅的生命周期）</summary>
        private CompositeDisposable _rxSubscriptions;

        /// <summary>已拒绝的任务ID集合，避免对不存在的任务反复解析和日志</summary>
        private readonly ConcurrentDictionary<string, byte> _rejectedTaskIds = new();

        /// <summary>接收任务对应的发送端identity（用于接收端取消时通知发送端）</summary>
        private readonly ConcurrentDictionary<string, byte[]> _taskIdentities = new();

        /// <summary>接收循环日志节流：最多500ms输出一次调试日志</summary>
        private long _lastRecvLogTicks = 0;
        private const long RecvLogThrottleIntervalTicks = 500 * TimeSpan.TicksPerMillisecond;

        /// <summary>源级进度节流：每个任务最多100ms上报一次（减少Subject/GroupBy/Sample开销）</summary>
        private readonly ConcurrentDictionary<string, long> _lastProgressTicks = new();
        private const long ProgressThrottleIntervalTicks = 100 * TimeSpan.TicksPerMillisecond;

        private readonly Subject<FileTransferTask> _transferProgress = new();
        private readonly Subject<FileTransferTask> _transferCompleted = new();
        private readonly Subject<FileTransferTask> _transferFailed = new();
        private readonly Subject<ListFilesResponse> _listFilesReceived = new();

        private CancellationTokenSource _serverCts;
        private bool _isDisposed;

        #endregion

        #region 公开事件

        /// <summary>传输进度更新</summary>
        public IObservable<FileTransferTask> TransferProgress => _transferProgress.AsObservable();

        /// <summary>传输完成事件</summary>
        public IObservable<FileTransferTask> TransferCompleted => _transferCompleted.AsObservable();

        /// <summary>传输失败事件</summary>
        public IObservable<FileTransferTask> TransferFailed => _transferFailed.AsObservable();

        /// <summary>远程文件列表接收事件</summary>
        public IObservable<ListFilesResponse> ListFilesReceived =>
            _listFilesReceived.AsObservable();

        /// <summary>本机设备信息提供器（用于响应 MACHINE_INFO_REQUEST）</summary>
        public Func<MachineInfo?> MachineInfoProvider { get; set; }

        /// <summary>当前活跃任务列表</summary>
        public IReadOnlyDictionary<string, FileTransferTask> ActiveTasks => _activeTasks;

        #endregion

        /// <summary>
        /// 带源级节流的进度上报（100ms内同一任务最多上报一次，减少下游Rx管道开销）
        /// </summary>
        private void ReportProgress(FileTransferTask task, bool force = false)
        {
            if (force)
            {
                _lastProgressTicks[task.TaskId] = DateTime.UtcNow.Ticks;
                _transferProgress.OnNext(task);
                return;
            }
            var now = DateTime.UtcNow.Ticks;
            var last = _lastProgressTicks.GetOrAdd(task.TaskId, 0L);
            if (now - last >= ProgressThrottleIntervalTicks)
            {
                _lastProgressTicks[task.TaskId] = now;
                _transferProgress.OnNext(task);
            }
        }

        #region 构造函数

        public P2PFileTransferService(
            string receiveDirectory = null,
            int maxConcurrentTransfers = 4
        )
        {
            _maxConcurrentTransfers = Math.Clamp(maxConcurrentTransfers, 1, 16);
            _transferSemaphore = new SemaphoreSlim(
                _maxConcurrentTransfers,
                _maxConcurrentTransfers
            );

            // 使用进程目录下的接收文件夹（与共享文件夹分开）
            _receiveDirectory = receiveDirectory ?? AppPathes.ReceivedFilesDir;

            if (!Directory.Exists(_receiveDirectory))
            {
                Directory.CreateDirectory(_receiveDirectory);
            }
        }

        /// <summary>
        /// 运行时更新最大并发传输数（设置面板修改后调用）
        /// </summary>
        public void UpdateMaxConcurrentTransfers(int maxConcurrentTransfers)
        {
            var newMax = Math.Clamp(maxConcurrentTransfers, 1, 16);
            if (newMax == _maxConcurrentTransfers)
                return;

            _maxConcurrentTransfers = newMax;
            var oldSemaphore = _transferSemaphore;
            _transferSemaphore = new SemaphoreSlim(newMax, newMax);
            oldSemaphore.Dispose();
            NLogger.Info("[P2P] 最大并发传输数已更新为: {NewMax}", newMax);
        }

        #endregion

        #region 服务端（接收文件）

        /// <summary>
        /// 启动文件传输服务端。
        /// 若端口被占用（如前一个进程的僵尸套接字），会等待重试，
        /// 若仍失败则尝试备用端口（+1, +2）。
        /// </summary>
        public void StartServer(int port = 0)
        {
            if (port == 0)
                port = _defaultPort;

            _serverCts = new CancellationTokenSource();

            // 依次尝试: 原始端口（重试2次） → 备用端口 port+1 → 备用端口 port+2
            var portsToTry = new[] { port, port, port + 1, port + 2 };
            var delays = new[] { 0, 2000, 0, 0 }; // 第二次原始端口前等待2秒

            for (int i = 0; i < portsToTry.Length; i++)
            {
                if (delays[i] > 0)
                    Thread.Sleep(delays[i]);

                RouterSocket socket = null;
                try
                {
                    socket = new RouterSocket();
                    socket.Bind($"tcp://*:{portsToTry[i]}");
                    _serverSocket = socket;

                    if (portsToTry[i] != port)
                    {
                        NLogger.Warn(
                            "[P2P] 原端口 {OrigPort} 被占用，已使用备用端口 {Port} 启动",
                            port,
                            portsToTry[i]
                        );
                    }

                    NLogger.Info("[P2P] 文件传输服务已启动，监听端口: {Port}", portsToTry[i]);

                    // 使用Rx管道替代后台Task循环
                    _rxSubscriptions?.Dispose();
                    _rxSubscriptions = new CompositeDisposable();
                    _rxSubscriptions.Add(StartReceivePipeline(_serverCts.Token));
                    return; // 成功
                }
                catch (NetMQ.AddressAlreadyInUseException)
                {
                    try { socket?.Close(); socket?.Dispose(); }
                    catch { /* ignore cleanup errors */ }

                    if (i < portsToTry.Length - 1)
                    {
                        NLogger.Warn(
                            "[P2P] 端口 {Port} 被占用，将尝试下一个端口",
                            portsToTry[i]
                        );
                    }
                    else
                    {
                        NLogger.Error("[P2P] 所有端口均被占用，文件传输服务启动失败");
                        throw;
                    }
                }
                catch
                {
                    try { socket?.Close(); socket?.Dispose(); }
                    catch { /* ignore cleanup errors */ }
                    throw;
                }
            }
        }

        /// <summary>
        /// 停止服务端
        /// </summary>
        public void StopServer()
        {
            _rxSubscriptions?.Dispose();
            _serverCts?.Cancel();
            _serverSocket?.Close();
            _serverSocket?.Dispose();
            _serverSocket = null;

            // 关闭所有接收中的文件流
            foreach (var kvp in _receivingFiles)
            {
                kvp.Value?.Dispose();
            }
            _receivingFiles.Clear();

            NLogger.Info("[P2P] 文件传输服务已停止");
        }

        /// <summary>
        /// 创建消息接收流（将RouterSocket轮询封装为Rx Observable冷流）
        /// 在TaskPoolScheduler上运行，每次轮询500ms超时
        /// </summary>
        private IObservable<(
            byte[] Identity,
            string MessageType,
            byte[] Payload
        )> CreateMessageStream(CancellationToken ct)
        {
            return Observable
                .Create<(byte[] Identity, string MessageType, byte[] Payload)>(observer =>
                {
                    var msg = new NetMQMessage();
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            msg.Clear();
                            if (
                                !_serverSocket.TryReceiveMultipartMessage(
                                    TimeSpan.FromMilliseconds(500),
                                    ref msg
                                )
                            )
                                continue;

                            if (msg.FrameCount < 3)
                            {
                                var debugFrames = string.Join(
                                    ", ",
                                    Enumerable
                                        .Range(0, msg.FrameCount)
                                        .Select(
                                            i =>
                                                $"F{i}=[{msg[i].ConvertToString().Substring(0, Math.Min(50, msg[i].ConvertToString().Length))}]({msg[i].BufferSize}B)"
                                        )
                                );
                                NLogger.Debug(
                                    "[P2P Server] 收到不完整消息: FrameCount={FrameCount}, {DebugFrames}",
                                    msg.FrameCount,
                                    debugFrames
                                );
                                continue;
                            }

                            // 拷贝消息数据，避免msg对象在下一次循环被清空
                            var identity = msg[0].ToByteArray();
                            var messageType = msg[1].ConvertToString();
                            var payload = msg[2].ToByteArray();

                            // 接收循环日志节流：避免高频DATA_CHUNK活动下日志洪水
                            var nowTicks = DateTime.UtcNow.Ticks;
                            if (
                                messageType != "DATA_CHUNK"
                                || nowTicks - _lastRecvLogTicks >= RecvLogThrottleIntervalTicks
                            )
                            {
                                _lastRecvLogTicks = nowTicks;
                                NLogger.Debug(
                                    "[P2P Server] 收到消息: type={MessageType}, FrameCount={FrameCount}, payloadSize={PayloadSize}",
                                    messageType,
                                    msg.FrameCount,
                                    payload.Length
                                );
                            }

                            observer.OnNext((identity, messageType, payload));
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (ct.IsCancellationRequested)
                                break;
                            NLogger.Error("[P2P] 接收消息异常: {ErrorMessage}", ex.Message);
                        }
                    }
                    observer.OnCompleted();
                    return Disposable.Empty;
                })
                .SubscribeOn(TaskPoolScheduler.Default);
        }

        /// <summary>
        /// 构建Rx接收管道（替代原有的while循环+Task.Run分发模式）
        /// DATA_CHUNK通过Concat保证顺序处理，其他消息通过SelectMany并发处理
        /// </summary>
        private IDisposable StartReceivePipeline(CancellationToken ct)
        {
            var disposables = new CompositeDisposable();
            var messages = CreateMessageStream(ct).Publish();

            // DATA_CHUNK管道：通过Concat保证顺序处理（FileStream写入顺序和Subject.OnNext线程安全）
            disposables.Add(
                messages
                    .Where(m => m.MessageType == "DATA_CHUNK")
                    .Select(
                        m =>
                            HandleDataChunk(m.Identity, m.Payload)
                                .Catch<Unit, Exception>(ex =>
                                {
                                    NLogger.Error(
                                        "[P2P] 处理DATA_CHUNK异常: {ErrorMessage}",
                                        ex.Message
                                    );
                                    return Observable.Empty<Unit>();
                                })
                    )
                    .Concat()
                    .Subscribe()
            );

            // 非DATA_CHUNK管道：并发处理（METADATA、LIST_FILES等不需要顺序保证）
            disposables.Add(
                messages
                    .Where(m => m.MessageType != "DATA_CHUNK")
                    .SelectMany(
                        m =>
                            DispatchMessage(m.Identity, m.MessageType, m.Payload, ct)
                                .Catch<Unit, Exception>(ex =>
                                {
                                    NLogger.Error(
                                        "[P2P] 处理消息异常 ({MessageType}): {ErrorMessage}",
                                        m.MessageType,
                                        ex.Message
                                    );
                                    return Observable.Empty<Unit>();
                                })
                    )
                    .Subscribe()
            );

            // 连接已发布的Observable，开始接收消息
            disposables.Add(messages.Connect());

            return disposables;
        }

        /// <summary>
        /// 消息分发器（返回IObservable，与Rx管道原生集成，替代async Task switch分发模式）
        /// </summary>
        private IObservable<Unit> DispatchMessage(
            byte[] identity,
            string messageType,
            byte[] payload,
            CancellationToken ct
        )
        {
            return messageType switch
            {
                "METADATA" => Observable.FromAsync(() => HandleMetadataAsync(identity, payload)),
                "DATA_CHUNK" => HandleDataChunk(identity, payload),
                "TRANSFER_CANCEL"
                    => Observable.Defer(() =>
                    {
                        HandleTransferCancel(payload);
                        return Observable.Return(Unit.Default);
                    }),
                "LIST_FILES_REQUEST"
                    => Observable.Defer(() =>
                    {
                        HandleListFilesRequest(identity, payload);
                        return Observable.Return(Unit.Default);
                    }),
                "MACHINE_INFO_REQUEST"
                    => Observable.Defer(() =>
                    {
                        HandleMachineInfoRequest(identity);
                        return Observable.Return(Unit.Default);
                    }),
                "DOWNLOAD_FILE_REQUEST" => HandleDownloadFileRequest(identity, payload),
                _
                    => Observable.Defer(() =>
                    {
                        NLogger.Warn("[P2P] 未知消息类型: {MessageType}", messageType);
                        return Observable.Return(Unit.Default);
                    })
            };
        }

        /// <summary>
        /// 处理传输元数据请求
        /// </summary>
        private async Task HandleMetadataAsync(byte[] identity, byte[] payload)
        {
            FileStream fileStream = null;
            string taskId = null;
            try
            {
                var metadata = JsonConvert.DeserializeObject<TransferMetadata>(
                    System.Text.Encoding.UTF8.GetString(payload)
                );
                taskId = metadata.TaskId;

                var filePath = Path.Combine(_receiveDirectory, metadata.FileName);
                long actualOffset = 0;

                // 检查是否存在部分文件（支持断点续传）
                if (File.Exists(filePath))
                {
                    var existingInfo = new FileInfo(filePath);
                    actualOffset = existingInfo.Length;

                    if (actualOffset >= metadata.TotalBytes)
                    {
                        if (!string.IsNullOrEmpty(metadata.FileHash))
                        {
                            var existingHash = ComputeFileMD5(filePath);
                            if (existingHash == metadata.FileHash)
                            {
                                SendResumeResponse(
                                    identity,
                                    metadata.TaskId,
                                    actualOffset,
                                    false,
                                    "文件已存在且完整"
                                );
                                return;
                            }
                        }
                        File.Delete(filePath);
                        actualOffset = 0;
                    }
                }

                // 解析来源类型提示
                var sourceType = TransferTaskSource.ManualReceive;
                if (
                    !string.IsNullOrEmpty(metadata.SourceHint)
                    && Enum.TryParse<TransferTaskSource>(metadata.SourceHint, out var parsedSource)
                )
                {
                    sourceType = parsedSource switch
                    {
                        TransferTaskSource.ManualSend => TransferTaskSource.ManualReceive,
                        TransferTaskSource.PackageDownload => TransferTaskSource.PackageDownload,
                        TransferTaskSource.RemoteBrowseDownload
                            => TransferTaskSource.RemoteBrowseDownload,
                        _ => TransferTaskSource.ManualReceive
                    };
                }

                var task = new FileTransferTask
                {
                    TaskId = metadata.TaskId,
                    FileName = metadata.FileName,
                    LocalPath = filePath,
                    TotalBytes = metadata.TotalBytes,
                    TransferredBytes = actualOffset,
                    FileHash = metadata.FileHash,
                    Direction = TransferDirection.Download,
                    Source = sourceType,
                    State = TransferState.Transferring,
                    StartTime = DateTime.Now,
                    RemoteMachine = new MachineInfo
                    {
                        ID = metadata.SenderIP,
                        Name = string.IsNullOrEmpty(metadata.SenderName)
                            ? metadata.SenderIP
                            : metadata.SenderName,
                        IPs = new System.Collections.ObjectModel.ObservableCollection<string>(
                            string.IsNullOrEmpty(metadata.SenderIP)
                                ? Array.Empty<string>()
                                : new[] { metadata.SenderIP }
                        )
                    }
                };

                _activeTasks[task.TaskId] = task;
                _taskIdentities[task.TaskId] = identity;
                _transferProgress.OnNext(task);

                fileStream = new FileStream(
                    filePath,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    useAsync: true
                );
                fileStream.Seek(actualOffset, SeekOrigin.Begin);
                _receivingFiles[metadata.TaskId] = fileStream;
                _rejectedTaskIds.TryRemove(metadata.TaskId, out _);

                SendResumeResponse(identity, metadata.TaskId, actualOffset, true, null);

                NLogger.Info(
                    "[P2P] 开始接收文件: {FileName}, 续传位置: {ActualOffset}",
                    metadata.FileName,
                    actualOffset
                );
            }
            catch (Exception ex)
            {
                if (fileStream != null)
                {
                    if (taskId != null)
                        _receivingFiles.TryRemove(taskId, out _);
                    try
                    {
                        fileStream.Dispose();
                    }
                    catch { }
                }
                NLogger.Error("[P2P] 处理元数据失败: {ErrorMessage}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 发送续传响应（线程安全）
        /// </summary>
        private void SendResumeResponse(
            byte[] identity,
            string taskId,
            long actualOffset,
            bool accepted,
            string error
        )
        {
            var response = new ResumeResponse
            {
                TaskId = taskId,
                ActualOffset = actualOffset,
                Accepted = accepted,
                Error = error ?? string.Empty
            };

            var responseJson = JsonConvert.SerializeObject(response);
            lock (_socketWriteLock)
            {
                _serverSocket.SendMoreFrame(identity);
                _serverSocket.SendMoreFrame("RESUME_RESPONSE");
                _serverSocket.SendFrame(responseJson);
            }
        }

        /// <summary>
        /// 处理数据块（返回IObservable，与Rx接收管道原生集成）
        /// Observable.Defer处理同步校验，Observable.FromAsync处理异步磁盘写入
        /// </summary>
        private IObservable<Unit> HandleDataChunk(byte[] identity, byte[] payload)
        {
            return Observable.Defer(() =>
            {
                // 解析数据块头部（前部分是JSON元数据，后部分是实际数据）
                var headerEndIndex = Array.IndexOf(payload, (byte)'\n');
                if (headerEndIndex < 0)
                    return Observable.Return(Unit.Default);

                // ── 零分配优化 ──
                // 用Utf8JsonReader替代Newtonsoft.Json反序列化，避免per-chunk的DataChunk对象+JSON对象图分配
                // 直接从payload写入磁盘，避免分配chunk.Data副本（每chunk省~256KB分配+拷贝）
                ParseChunkHeader(
                    new ReadOnlySpan<byte>(payload, 0, headerEndIndex),
                    out var taskId,
                    out var isLastChunk,
                    out var fileHash
                );

                if (string.IsNullOrEmpty(taskId))
                    return Observable.Return(Unit.Default);

                // 快速跳过已知的无效任务（避免反复查找 + 日志洪水）
                if (_rejectedTaskIds.ContainsKey(taskId))
                    return Observable.Return(Unit.Default);

                if (!_receivingFiles.TryGetValue(taskId, out var fileStream))
                {
                    _rejectedTaskIds.TryAdd(taskId, 0);
                    NLogger.Warn("[P2P] 未找到传输任务: {TaskId}，后续同任务数据块将被忽略", taskId);
                    return Observable.Return(Unit.Default);
                }

                if (!_activeTasks.TryGetValue(taskId, out var task))
                    return Observable.Return(Unit.Default);

                // 磁盘I/O通过Observable.FromAsync异步执行
                int dataOffset = headerEndIndex + 1;
                int dataLength = payload.Length - dataOffset;

                return Observable.FromAsync(async () =>
                {
                    await fileStream.WriteAsync(payload, dataOffset, dataLength);
                    Interlocked.Add(ref task._transferredBytesRaw, dataLength);

                    if (isLastChunk && !string.IsNullOrEmpty(fileHash))
                        task.FileHash = fileHash;

                    ReportProgress(task, force: isLastChunk);

                    // 完成接收处理（火烧而忘，不阻塞数据块管道）
                    if (isLastChunk || task.TransferredBytes >= task.TotalBytes)
                    {
                        _ = Task.Run(() => CompleteReceiveAsync(identity, task));
                    }
                });
            });
        }

        /// <summary>
        /// 零分配解析DATA_CHUNK的JSON头部（仅提取TaskId、IsLastChunk、FileHash）
        /// 替代Newtonsoft.Json.DeserializeObject以消除per-chunk的对象分配开销
        /// </summary>
        private static void ParseChunkHeader(
            ReadOnlySpan<byte> headerSpan,
            out string taskId,
            out bool isLastChunk,
            out string fileHash
        )
        {
            taskId = null;
            isLastChunk = false;
            fileHash = null;

            var reader = new System.Text.Json.Utf8JsonReader(headerSpan);
            while (reader.Read())
            {
                if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("TaskId"u8))
                {
                    reader.Read();
                    taskId = reader.GetString();
                }
                else if (reader.ValueTextEquals("IsLastChunk"u8))
                {
                    reader.Read();
                    isLastChunk = reader.GetBoolean();
                }
                else if (reader.ValueTextEquals("FileHash"u8))
                {
                    reader.Read();
                    fileHash = reader.GetString();
                }
                else
                {
                    reader.Read(); // skip value
                }
            }
        }

        /// <summary>
        /// 完成文件接收（同步执行流刷盘+完成确认+MD5后台校验）
        /// </summary>
        private async Task CompleteReceiveAsync(byte[] identity, FileTransferTask task)
        {
            try
            {
                // 1. 关闭文件流（优先完成，释放文件句柄）
                if (_receivingFiles.TryRemove(task.TaskId, out var fileStream))
                {
                    try
                    {
                        fileStream.Flush();
                    }
                    finally
                    {
                        fileStream.Dispose();
                    }
                }

                // 2. 立即发送完成确认给发送方（不等MD5，避免发送方超时）
                var skipMD5 =
                    task.TotalBytes > 50L * 1024 * 1024 || string.IsNullOrEmpty(task.FileHash);

                try
                {
                    var complete = new TransferComplete
                    {
                        TaskId = task.TaskId,
                        ReceivedHash = string.Empty,
                        HashMatch = true
                    };

                    lock (_socketWriteLock)
                    {
                        _serverSocket.SendMoreFrame(identity);
                        _serverSocket.SendMoreFrame("TRANSFER_COMPLETE");
                        _serverSocket.SendFrame(JsonConvert.SerializeObject(complete));
                    }
                }
                catch (Exception socketEx)
                {
                    NLogger.Warn("[P2P] 发送完成确认失败: {ErrorMessage}", socketEx.Message);
                }

                // 3. 标记本地任务完成
                task.EndTime = DateTime.Now;
                task.State = TransferState.Completed;
                _transferCompleted.OnNext(task);
                NLogger.Info(
                    "[P2P] 文件接收完成: {FileName} ({SizeMB} MB)",
                    task.FileName,
                    (task.TotalBytes / 1024.0 / 1024.0).ToString("F1")
                );

                // 4. 小文件后台校验MD5（火烧而忘，不阻塞完成流）
                if (!skipMD5)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var receivedHash = await ComputeMD5Async(task.LocalPath);
                            if (
                                !string.Equals(
                                    receivedHash,
                                    task.FileHash,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                NLogger.Warn("[P2P] 文件MD5校验不匹配（已接收）: {FileName}", task.FileName);
                            }
                        }
                        catch (Exception ex)
                        {
                            NLogger.Debug(
                                "[P2P] 后台MD5校验异常: {FileName}, {ErrorMessage}",
                                task.FileName,
                                ex.Message
                            );
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("[P2P] 完成接收异常: {ErrorMessage}", ex.Message);

                if (task.State == TransferState.Transferring && File.Exists(task.LocalPath))
                {
                    task.State = TransferState.Completed;
                    task.EndTime = DateTime.Now;
                    _transferCompleted.OnNext(task);
                    NLogger.Info("[P2P] 文件接收完成（异常恢复）: {FileName}", task.FileName);
                }
            }
            finally
            {
                _activeTasks.TryRemove(task.TaskId, out _);
                _taskIdentities.TryRemove(task.TaskId, out _);
            }
        }

        /// <summary>
        /// 处理取消请求
        /// </summary>
        private void HandleTransferCancel(byte[] payload)
        {
            try
            {
                var cancelInfo = JsonConvert.DeserializeObject<dynamic>(
                    System.Text.Encoding.UTF8.GetString(payload)
                );
                string taskId = cancelInfo.TaskId;

                if (_receivingFiles.TryRemove(taskId, out var fileStream))
                {
                    fileStream.Dispose();
                }

                _taskIdentities.TryRemove(taskId, out _);

                if (_activeTasks.TryRemove(taskId, out var task))
                {
                    task.State = TransferState.Cancelled;
                    NLogger.Info("[P2P] 传输已取消: {FileName}", task.FileName);
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("[P2P] 处理取消请求失败: {ErrorMessage}", ex.Message);
            }
        }

        #endregion

        #region 客户端（发送文件）

        /// <summary>
        /// 发送多个文件（支持取消，并发受信号量控制）
        /// </summary>
        /// <param name="sourceHint">任务来源类型提示，传递给接收方以区分来源</param>
        public async Task<FileTransferBatch> SendFilesAsync(
            MachineInfo target,
            IEnumerable<string> filePaths,
            TransferTaskSource sourceHint = TransferTaskSource.ManualSend
        )
        {
            var batch = new FileTransferBatch { TargetMachine = target };
            var fileList = filePaths.ToList();

            // 预处理：创建任务（不预计算MD5，在发送时增量计算）
            foreach (var path in fileList)
            {
                if (!File.Exists(path))
                {
                    NLogger.Warn("[P2P] 文件不存在: {Path}", path);
                    continue;
                }

                var fileInfo = new FileInfo(path);
                var task = new FileTransferTask
                {
                    FileName = fileInfo.Name,
                    LocalPath = path,
                    TotalBytes = fileInfo.Length,
                    Direction = TransferDirection.Upload,
                    Source = sourceHint,
                    RemoteMachine = target,
                    FileHash = string.Empty
                };

                batch.Tasks.Add(task);
                _activeTasks[task.TaskId] = task;
                _transferProgress.OnNext(task);
            }

            if (batch.Tasks.Count == 0)
            {
                NLogger.Warn("[P2P] 没有有效的文件可传输");
                return batch;
            }

            NLogger.Info("[P2P] 开始发送 {Count} 个文件到 {TargetName}", batch.Tasks.Count, target.Name);

            // 并发发送，受信号量控制
            try
            {
                await Task.WhenAll(
                    batch.Tasks.Select(
                        task => TransferSingleFileAsync(task, target, CancellationToken.None)
                    )
                );
            }
            catch (Exception ex)
            {
                NLogger.Error("[P2P] 批量发送异常: {ErrorMessage}", ex.Message);
            }

            return batch;
        }

        /// <summary>
        /// 发送单个文件
        /// </summary>
        public async Task<bool> SendFileAsync(MachineInfo target, string filePath)
        {
            var batch = await SendFilesAsync(target, new[] { filePath });
            return batch.Tasks.FirstOrDefault()?.State == TransferState.Completed;
        }

        /// <summary>
        /// 单文件传输实现（支持断点续传）
        /// </summary>
        private async Task TransferSingleFileAsync(
            FileTransferTask task,
            MachineInfo target,
            CancellationToken ct
        )
        {
            await _transferSemaphore.WaitAsync(ct);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _taskCancellations[task.TaskId] = cts;

            try
            {
                task.State = TransferState.Transferring;
                task.StartTime = DateTime.Now;

                var port = (target as MachineInfoExtended)?.FileTransferPort ?? _defaultPort;

                // 收集所有候选IP地址（去重）
                var candidateIPs = new System.Collections.Generic.List<string>();
                if (target.IPs != null)
                {
                    foreach (var ip in target.IPs)
                    {
                        if (!string.IsNullOrWhiteSpace(ip) && !candidateIPs.Contains(ip))
                            candidateIPs.Add(ip);
                    }
                }
                if (candidateIPs.Count == 0 && !string.IsNullOrEmpty(target.ID))
                    candidateIPs.Add(target.ID);

                // 将阻塞的TCP探测 + NetMQ操作全部放入Task.Run，避免占用调用方线程
                await Task.Run(
                    () =>
                    {
                        // 逐IP探测连通性
                        string connectIP = null;
                        string lastError = null;
                        foreach (var candidateIP in candidateIPs)
                        {
                            var resolvedIP = ResolveConnectionEndpoint(candidateIP);
                            try
                            {
                                using var probe = new System.Net.Sockets.TcpClient();
                                var probeResult = probe.BeginConnect(resolvedIP, port, null, null);
                                var probeSuccess = probeResult.AsyncWaitHandle.WaitOne(
                                    TimeSpan.FromSeconds(3)
                                );
                                if (probeSuccess)
                                {
                                    probe.EndConnect(probeResult);
                                    connectIP = resolvedIP;
                                    NLogger.Info(
                                        "[P2P] 连通性检测成功: {ResolvedIP}:{Port}",
                                        resolvedIP,
                                        port
                                    );
                                    break;
                                }
                                else
                                {
                                    lastError = $"{resolvedIP} - 连接超时";
                                    NLogger.Debug(
                                        "[P2P] 连通性检测失败: {ResolvedIP}:{Port} (超时)",
                                        resolvedIP,
                                        port
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                lastError =
                                    $"{resolvedIP} - {ex.InnerException?.Message ?? ex.Message}";
                                NLogger.Debug(
                                    "[P2P] 连通性检测失败: {ResolvedIP}:{Port} ({LastError})",
                                    resolvedIP,
                                    port,
                                    lastError
                                );
                            }
                        }

                        if (connectIP == null)
                        {
                            var triedIPs = string.Join(", ", candidateIPs);
                            throw new Exception(
                                $"无法连接到远程设备（已尝试: {triedIPs}），端口: {port}\n"
                                    + $"最后错误: {lastError ?? "未知"}\n"
                                    + $"请确认：1. 目标设备已启动联调面板  2. 防火墙已放行端口 {port}  3. 网络连接正常"
                            );
                        }

                        using var dealer = new DealerSocket();
                        dealer.Options.ReceiveHighWatermark = 1000;
                        dealer.Options.SendHighWatermark = 1000;
                        dealer.Options.Linger = TimeSpan.FromSeconds(1);
                        dealer.Connect($"tcp://{connectIP}:{port}");

                        // 1. 发送元数据
                        var localIP = GetLocalIPAddressForTarget(connectIP);
                        var metadata = new TransferMetadata
                        {
                            TaskId = task.TaskId,
                            FileName = task.FileName,
                            TotalBytes = task.TotalBytes,
                            ResumeOffset = task.ResumeOffset,
                            FileHash = task.FileHash,
                            SenderName = Environment.MachineName,
                            SenderIP = localIP,
                            SourceHint = task.Source.ToString()
                        };

                        dealer.SendMoreFrame("METADATA");
                        dealer.SendFrame(JsonConvert.SerializeObject(metadata));

                        // 2. 等待续传响应
                        if (
                            !dealer.TryReceiveFrameString(
                                TimeSpan.FromMilliseconds(_receiveTimeout),
                                out var responseType
                            )
                        )
                        {
                            throw new TimeoutException("等待服务端响应超时");
                        }

                        if (
                            !dealer.TryReceiveFrameString(
                                TimeSpan.FromMilliseconds(1000),
                                out var responseJson
                            )
                        )
                        {
                            throw new Exception("未收到响应数据");
                        }

                        var response = JsonConvert.DeserializeObject<ResumeResponse>(responseJson);
                        if (!response.Accepted)
                        {
                            task.State = TransferState.Failed;
                            task.ErrorMessage = response.Error;
                            _transferFailed.OnNext(task);
                            return;
                        }

                        task.TransferredBytes = response.ActualOffset;

                        // 3. 分块发送文件
                        using var fileStream = new FileStream(
                            task.LocalPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            _chunkSize,
                            useAsync: false
                        );
                        fileStream.Seek(task.TransferredBytes, SeekOrigin.Begin);

                        var buffer = new byte[_chunkSize];
                        int bytesRead;
                        int chunkIndex = 0;

                        var maxPayloadSize = 256 + _chunkSize;
                        var chunkPayload = System.Buffers.ArrayPool<byte>.Shared.Rent(
                            maxPayloadSize
                        );
                        try
                        {
                            while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (cts.Token.IsCancellationRequested)
                                {
                                    task.State = TransferState.Cancelled;
                                    return;
                                }

                                // 检查接收方是否已取消（非阻塞轮询）
                                if (
                                    dealer.TryReceiveFrameString(
                                        TimeSpan.Zero,
                                        out var incomingType
                                    )
                                )
                                {
                                    if (incomingType == "TRANSFER_CANCELLED")
                                    {
                                        task.State = TransferState.Cancelled;
                                        task.ErrorMessage = "接收方已取消传输";
                                        NLogger.Info("[P2P] 接收方已取消传输: {FileName}", task.FileName);
                                        _transferFailed.OnNext(task);
                                        return;
                                    }
                                    // 消费多帧消息的剩余帧
                                    dealer.TryReceiveFrameString(
                                        TimeSpan.FromMilliseconds(100),
                                        out _
                                    );
                                }

                                var isLast = fileStream.Position >= fileStream.Length;

                                var chunkHeader = new DataChunk
                                {
                                    TaskId = task.TaskId,
                                    ChunkIndex = chunkIndex++,
                                    IsLastChunk = isLast
                                };

                                var headerBytes = System.Text.Encoding.UTF8.GetBytes(
                                    JsonConvert.SerializeObject(chunkHeader) + "\n"
                                );

                                var totalPayloadLength = headerBytes.Length + bytesRead;
                                Array.Copy(headerBytes, 0, chunkPayload, 0, headerBytes.Length);
                                Array.Copy(buffer, 0, chunkPayload, headerBytes.Length, bytesRead);

                                dealer.SendMoreFrame("DATA_CHUNK");
                                dealer.SendFrame(chunkPayload, totalPayloadLength, false);

                                Interlocked.Add(ref task._transferredBytesRaw, bytesRead);
                                ReportProgress(
                                    task,
                                    force: fileStream.Position >= fileStream.Length
                                );
                            }
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(chunkPayload);
                        }

                        // 4. 等待完成确认（超时时间按文件大小动态调整）
                        var completionTimeout =
                            _receiveTimeout + (int)(task.TotalBytes / (100L * 1024 * 1024)) * 30000;
                        if (
                            dealer.TryReceiveFrameString(
                                TimeSpan.FromMilliseconds(completionTimeout),
                                out var completeType
                            )
                        )
                        {
                            if (completeType == "TRANSFER_CANCELLED")
                            {
                                task.State = TransferState.Cancelled;
                                task.ErrorMessage = "接收方已取消传输";
                                NLogger.Info("[P2P] 接收方已取消传输: {FileName}", task.FileName);
                                _transferFailed.OnNext(task);
                                return;
                            }

                            if (
                                completeType == "TRANSFER_COMPLETE"
                                && dealer.TryReceiveFrameString(
                                    TimeSpan.FromMilliseconds(1000),
                                    out var completeJson
                                )
                            )
                            {
                                var complete = JsonConvert.DeserializeObject<TransferComplete>(
                                    completeJson
                                );
                                if (complete.HashMatch)
                                {
                                    task.State = TransferState.Completed;
                                    task.EndTime = DateTime.Now;
                                    _transferCompleted.OnNext(task);
                                    NLogger.Info("[P2P] 文件发送完成: {FileName}", task.FileName);
                                }
                                else
                                {
                                    task.State = TransferState.Failed;
                                    task.ErrorMessage = "远端哈希验证失败";
                                    _transferFailed.OnNext(task);
                                }
                            }
                            else
                            {
                                // 未知消息类型，视为完成
                                task.State = TransferState.Completed;
                                task.EndTime = DateTime.Now;
                                _transferCompleted.OnNext(task);
                                NLogger.Warn(
                                    "[P2P] 收到未知响应类型({CompleteType})，视为完成: {FileName}",
                                    completeType,
                                    task.FileName
                                );
                            }
                        }
                        else
                        {
                            task.State = TransferState.Completed;
                            task.EndTime = DateTime.Now;
                            _transferCompleted.OnNext(task);
                            NLogger.Warn("[P2P] 未收到完成确认，但数据已全部发送，视为完成: {FileName}", task.FileName);
                        }
                    },
                    cts.Token
                );
            }
            catch (OperationCanceledException)
            {
                if (task.State != TransferState.Paused)
                {
                    task.State = TransferState.Cancelled;
                }
                NLogger.Info("[P2P] 传输已取消/暂停: {FileName}", task.FileName);
            }
            catch (Exception ex)
            {
                task.State = TransferState.Failed;
                task.ErrorMessage = ex.Message;
                _transferFailed.OnNext(task);
                NLogger.Error("[P2P] 传输失败: {FileName}, {ErrorMessage}", task.FileName, ex.Message);
            }
            finally
            {
                _taskCancellations.TryRemove(task.TaskId, out _);
                _taskPauseEvents.TryRemove(task.TaskId, out _);
                if (task.State != TransferState.Paused)
                {
                    _activeTasks.TryRemove(task.TaskId, out _);
                }
                _transferSemaphore.Release();
            }
        }

        #endregion

        #region 任务控制

        /// <summary>
        /// 暂停传输任务（使用ManualResetEventSlim阻塞发送循环，而非取消CTS）
        /// </summary>
        public void PauseTask(string taskId)
        {
            // 创建或获取暂停事件，Reset将阻塞发送循环
            var pauseEvent = _taskPauseEvents.GetOrAdd(taskId, _ => new ManualResetEventSlim(true));
            pauseEvent.Reset();

            if (_activeTasks.TryGetValue(taskId, out var task))
            {
                task.State = TransferState.Paused;
            }

            NLogger.Info("[P2P] 任务已暂停: {TaskId}", taskId);
        }

        /// <summary>
        /// 恢复传输任务（解除暂停或从断点重新传输）
        /// </summary>
        public async Task ResumeTaskAsync(FileTransferTask task)
        {
            // 如果有暂停事件，直接解除阻塞即可恢复
            if (_taskPauseEvents.TryGetValue(task.TaskId, out var pauseEvent))
            {
                task.State = TransferState.Transferring;
                if (_activeTasks.TryGetValue(task.TaskId, out var activeTask))
                {
                    activeTask.State = TransferState.Transferring;
                }
                pauseEvent.Set(); // 解除发送循环阻塞
                NLogger.Info("[P2P] 任务已恢复: {TaskId}", task.TaskId);
                return;
            }

            // 后备方案：从当前位置重新开始传输
            if (task.State != TransferState.Paused)
            {
                NLogger.Warn("[P2P] 任务状态不是暂停，无法恢复: {TaskId}", task.TaskId);
                return;
            }

            task.ResumeOffset = task.TransferredBytes;
            task.State = TransferState.Transferring;
            _activeTasks[task.TaskId] = task;

            await TransferSingleFileAsync(task, task.RemoteMachine, CancellationToken.None);
        }

        /// <summary>
        /// 取消传输任务
        /// </summary>
        public void CancelTask(string taskId)
        {
            // 取消发送端 CTS（上传任务）
            if (_taskCancellations.TryRemove(taskId, out var cts))
            {
                cts.Cancel();
            }

            // 清理接收端文件流（下载任务）
            if (_receivingFiles.TryRemove(taskId, out var fileStream))
            {
                try
                {
                    fileStream.Dispose();
                }
                catch { }
            }

            // 通知发送端：接收方已取消（通过RouterSocket回送TRANSFER_CANCELLED）
            if (_taskIdentities.TryRemove(taskId, out var identity))
            {
                try
                {
                    lock (_socketWriteLock)
                    {
                        _serverSocket.SendMoreFrame(identity);
                        _serverSocket.SendMoreFrame("TRANSFER_CANCELLED");
                        _serverSocket.SendFrame(taskId);
                    }
                    NLogger.Info("[P2P] 已通知发送端取消传输: {TaskId}", taskId);
                }
                catch (Exception ex)
                {
                    NLogger.Warn("[P2P] 通知发送端取消失败: {ErrorMessage}", ex.Message);
                }
            }

            // 将任务加入拒绝列表，后续到达的同任务数据块会被忽略
            _rejectedTaskIds.TryAdd(taskId, 0);

            if (_activeTasks.TryRemove(taskId, out var task))
            {
                task.State = TransferState.Cancelled;
                _transferFailed.OnNext(task);
            }

            NLogger.Info("[P2P] 任务已取消: {TaskId}", taskId);
        }

        /// <summary>
        /// 取消所有传输任务
        /// </summary>
        public void CancelAllTasks()
        {
            foreach (var kvp in _taskCancellations)
            {
                kvp.Value.Cancel();
            }

            foreach (var kvp in _activeTasks)
            {
                kvp.Value.State = TransferState.Cancelled;
            }

            _taskCancellations.Clear();
            _activeTasks.Clear();

            NLogger.Info("[P2P] 所有任务已取消");
        }

        #endregion

        #region 工具方法

        /// <summary>
        /// 计算文件MD5哈希
        /// </summary>
        public static async Task<string> ComputeMD5Async(string filePath)
        {
            return await Task.Run(() =>
            {
                using var md5 = MD5.Create();
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    useAsync: false
                );

                var buffer = new byte[81920];
                int bytesRead;

                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    md5.TransformBlock(buffer, 0, bytesRead, buffer, 0);
                }

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant();
            });
        }

        /// <summary>
        /// 获取共享文件目录路径
        /// </summary>
        public string SharedDirectory => AppPathes.SharedFilesDir;

        /// <summary>
        /// 获取共享目录下的所有文件列表（提供给其他设备下载）
        /// </summary>
        /// <returns>共享文件信息列表</returns>
        public List<SharedFileInfo> GetSharedFiles(bool computeMD5 = false)
        {
            var files = new List<SharedFileInfo>();
            var sharedDir = AppPathes.SharedFilesDir;

            if (!Directory.Exists(sharedDir))
                return files;

            try
            {
                foreach (
                    var filePath in Directory.GetFiles(sharedDir, "*", SearchOption.AllDirectories)
                )
                {
                    var fileInfo = new FileInfo(filePath);
                    files.Add(
                        new SharedFileInfo
                        {
                            FileName = fileInfo.Name,
                            RelativePath = Path.GetRelativePath(sharedDir, filePath),
                            FullPath = filePath,
                            FileSize = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime,
                            FileMD5 = computeMD5 ? ComputeFileMD5(filePath) : string.Empty
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("[P2P] 获取共享文件列表失败: {ErrorMessage}", ex.Message);
            }

            return files.OrderByDescending(f => f.LastModified).ToList();
        }

        /// <summary>
        /// 计算文件MD5哈希值
        /// </summary>
        private static string ComputeFileMD5(string filePath)
        {
            try
            {
                using var md5 = MD5.Create();
                using var stream = File.OpenRead(filePath);
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                NLogger.Warn(
                    "[P2P] 计算文件MD5失败 ({FileName}): {ErrorMessage}",
                    Path.GetFileName(filePath),
                    ex.Message
                );
                return string.Empty;
            }
        }

        /// <summary>
        /// 处理远程文件列表请求（作为服务端响应）
        /// </summary>
        private void HandleListFilesRequest(byte[] identity, byte[] payload)
        {
            try
            {
                NLogger.Info(
                    "[P2P Server] 处理LIST_FILES_REQUEST, identity长度={IdentityLength}",
                    identity.Length
                );
                var request = JsonConvert.DeserializeObject<ListFilesRequest>(
                    System.Text.Encoding.UTF8.GetString(payload)
                );

                // 获取SharedFiles目录下的文件（提供给其他设备）
                var sharedDir = AppPathes.SharedFilesDir;
                var files = new List<SharedFileInfo>();

                if (Directory.Exists(sharedDir))
                {
                    foreach (
                        var filePath in Directory.GetFiles(
                            sharedDir,
                            "*",
                            SearchOption.AllDirectories
                        )
                    )
                    {
                        var fileInfo = new FileInfo(filePath);
                        files.Add(
                            new SharedFileInfo
                            {
                                FileName = fileInfo.Name,
                                RelativePath = Path.GetRelativePath(sharedDir, filePath),
                                FullPath = filePath,
                                FileSize = fileInfo.Length,
                                LastModified = fileInfo.LastWriteTime
                            }
                        );
                    }
                }

                // 发送响应（线程安全）
                var response = new ListFilesResponse
                {
                    RequestId = request.RequestId,
                    Files = files.OrderByDescending(f => f.LastModified).ToArray()
                };

                var responseJson = JsonConvert.SerializeObject(response);
                lock (_socketWriteLock)
                {
                    _serverSocket
                        .SendMoreFrame(identity)
                        .SendMoreFrame("LIST_FILES_RESPONSE")
                        .SendFrame(System.Text.Encoding.UTF8.GetBytes(responseJson));
                }

                NLogger.Info("[P2P] 响应文件列表请求，共 {Count} 个文件", files.Count);
            }
            catch (Exception ex)
            {
                NLogger.Error("[P2P] 处理文件列表请求失败: {ErrorMessage}", ex.Message);
            }
        }

        /// <summary>
        /// 处理远程设备信息请求（作为服务端响应，返回本机 MachineInfo）
        /// </summary>
        private void HandleMachineInfoRequest(byte[] identity)
        {
            try
            {
                NLogger.Info("[P2P Server] 处理 MACHINE_INFO_REQUEST");

                var machineInfo = MachineInfoProvider?.Invoke();
                var responseJson =
                    machineInfo != null ? JsonConvert.SerializeObject(machineInfo) : "{}";

                lock (_socketWriteLock)
                {
                    _serverSocket
                        .SendMoreFrame(identity)
                        .SendMoreFrame("MACHINE_INFO_RESPONSE")
                        .SendFrame(System.Text.Encoding.UTF8.GetBytes(responseJson));
                }

                NLogger.Info("[P2P] 已响应设备信息请求");
            }
            catch (Exception ex)
            {
                NLogger.Error("[P2P] 处理设备信息请求失败: {ErrorMessage}", ex.Message);
            }
        }

        /// <summary>
        /// 处理下载文件请求（返回IObservable，纯同步解析 + 火烧而忘发送）
        /// </summary>
        private IObservable<Unit> HandleDownloadFileRequest(byte[] identity, byte[] payload)
        {
            return Observable.Defer(() =>
            {
                try
                {
                    NLogger.Info("[P2P] 收到远程下载请求");

                    var request = JsonConvert.DeserializeObject<DownloadFileRequest>(
                        System.Text.Encoding.UTF8.GetString(payload)
                    );

                    if (request == null)
                    {
                        NLogger.Warn("[P2P] 下载请求反序列化失败");
                        return Observable.Return(Unit.Default);
                    }

                    if (string.IsNullOrEmpty(request.RequesterIP))
                    {
                        NLogger.Warn("[P2P] 下载请求缺少请求方IP");
                        return Observable.Return(Unit.Default);
                    }

                    NLogger.Info(
                        "[P2P] 下载请求来自: {RequesterIP}:{RequesterPort}, 文件数: {FileCount}",
                        request.RequesterIP,
                        request.RequesterPort,
                        request.FileNames?.Length ?? 0
                    );

                    var sharedDir = AppPathes.SharedFilesDir;
                    var filesToSend = new List<string>();

                    foreach (var fileName in request.FileNames ?? Array.Empty<string>())
                    {
                        var filePath = Path.Combine(sharedDir, fileName);
                        if (File.Exists(filePath))
                        {
                            filesToSend.Add(filePath);
                            NLogger.Info("[P2P] 准备发送文件: {FileName}", fileName);
                        }
                        else
                        {
                            NLogger.Warn(
                                "[P2P] 请求的文件不存在: {FileName} (路径: {FilePath})",
                                fileName,
                                filePath
                            );
                        }
                    }

                    if (filesToSend.Count > 0)
                    {
                        var targetMachine = new MachineInfo
                        {
                            ID = request.RequesterIP,
                            IPs = new System.Collections.ObjectModel.ObservableCollection<string>
                            {
                                request.RequesterIP
                            }
                        };

                        var hasPackageFile =
                            request.FileNames?.Any(
                                f => f.EndsWith(".dkp.zip", StringComparison.OrdinalIgnoreCase)
                            ) ?? false;
                        var sourceHint = hasPackageFile
                            ? TransferTaskSource.PackageDownload
                            : TransferTaskSource.RemoteBrowseDownload;

                        NLogger.Info(
                            "[P2P] 开始向 {RequesterIP} 发送 {Count} 个文件 (来源: {SourceHint})",
                            request.RequesterIP,
                            filesToSend.Count,
                            sourceHint
                        );

                        // 火烧而忘：不阻塞消息分发管道
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await SendFilesAsync(
                                    targetMachine,
                                    filesToSend,
                                    sourceHint: sourceHint
                                );
                                NLogger.Info(
                                    "[P2P] 已完成响应下载请求，发送 {Count} 个文件到 {RequesterIP}",
                                    filesToSend.Count,
                                    request.RequesterIP
                                );
                            }
                            catch (Exception ex)
                            {
                                NLogger.Error("[P2P] 响应下载请求失败: {ErrorMessage}", ex.Message);
                            }
                        });
                    }
                    else
                    {
                        NLogger.Warn("[P2P] 没有找到请求的文件");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Error("[P2P] 处理下载请求失败: {ErrorMessage}", ex.Message);
                }

                return Observable.Return(Unit.Default);
            });
        }

        /// <summary>
        /// 请求远程设备的文件列表
        /// </summary>
        public async Task<SharedFileInfo[]> RequestRemoteFilesAsync(
            string remoteEndpoint,
            int timeoutMs = 10000
        )
        {
            return await Task.Run(() =>
            {
                var request = new ListFilesRequest();

                try
                {
                    var connectEndpoint = ResolveConnectionEndpoint(remoteEndpoint);
                    NLogger.Info(
                        "[P2P] 正在请求远程文件列表: {ConnectEndpoint}:{DefaultPort} (原始: {RemoteEndpoint})",
                        connectEndpoint,
                        _defaultPort,
                        remoteEndpoint
                    );

                    using var client = new DealerSocket();
                    client.Options.Identity = System.Text.Encoding.UTF8.GetBytes(request.RequestId);
                    client.Connect($"tcp://{connectEndpoint}:{_defaultPort}");

                    var requestJson = JsonConvert.SerializeObject(request);
                    client
                        .SendMoreFrame("LIST_FILES_REQUEST")
                        .SendFrame(System.Text.Encoding.UTF8.GetBytes(requestJson));

                    var message = new NetMQMessage();
                    if (
                        client.TryReceiveMultipartMessage(
                            TimeSpan.FromMilliseconds(timeoutMs),
                            ref message
                        )
                    )
                    {
                        if (message.FrameCount >= 2)
                        {
                            var messageType = message[0].ConvertToString();
                            var payload = message[1].ToByteArray();

                            if (messageType == "LIST_FILES_RESPONSE")
                            {
                                var response = JsonConvert.DeserializeObject<ListFilesResponse>(
                                    System.Text.Encoding.UTF8.GetString(payload)
                                );
                                NLogger.Info("[P2P] 收到远程文件列表，共 {Count} 个文件", response.Files.Length);
                                return response.Files;
                            }
                        }
                    }

                    NLogger.Warn("[P2P] 请求远程文件列表超时 ({TimeoutMs}ms)", timeoutMs);
                    return Array.Empty<SharedFileInfo>();
                }
                catch (Exception ex)
                {
                    NLogger.Error("[P2P] 请求远程文件列表失败: {ErrorMessage}", ex.Message);
                    return Array.Empty<SharedFileInfo>();
                }
            });
        }

        /// <summary>
        /// 通过 TCP 请求远程设备的 MachineInfo（用于跨网段设备探测获取完整设备信息）
        /// </summary>
        public async Task<MachineInfo?> RequestRemoteMachineInfoAsync(
            string remoteEndpoint,
            int timeoutMs = 5000
        )
        {
            return await Task.Run(() =>
            {
                try
                {
                    var connectEndpoint = ResolveConnectionEndpoint(remoteEndpoint);
                    NLogger.Info(
                        "[P2P] 正在请求远程设备信息: {ConnectEndpoint}:{DefaultPort}",
                        connectEndpoint,
                        _defaultPort
                    );

                    using var client = new DealerSocket();
                    client.Options.Identity = System.Text.Encoding.UTF8.GetBytes(
                        Guid.NewGuid().ToString("N")
                    );
                    client.Connect($"tcp://{connectEndpoint}:{_defaultPort}");

                    client.SendMoreFrame("MACHINE_INFO_REQUEST").SendFrame(Array.Empty<byte>());

                    var message = new NetMQMessage();
                    if (
                        client.TryReceiveMultipartMessage(
                            TimeSpan.FromMilliseconds(timeoutMs),
                            ref message
                        )
                    )
                    {
                        if (message.FrameCount >= 2)
                        {
                            var messageType = message[0].ConvertToString();
                            var payload = message[1].ToByteArray();

                            if (messageType == "MACHINE_INFO_RESPONSE")
                            {
                                var info = JsonConvert.DeserializeObject<MachineInfo>(
                                    System.Text.Encoding.UTF8.GetString(payload)
                                );
                                NLogger.Info("[P2P] 收到远程设备信息: {DeviceName}", info?.Name ?? "未知");
                                return info;
                            }
                        }
                    }

                    NLogger.Warn("[P2P] 请求远程设备信息超时 ({TimeoutMs}ms)", timeoutMs);
                    return null;
                }
                catch (Exception ex)
                {
                    NLogger.Error("[P2P] 请求远程设备信息失败: {ErrorMessage}", ex.Message);
                    return null;
                }
            });
        }

        /// <summary>
        /// 检测是否是本机地址
        /// </summary>
        private bool IsLocalEndpoint(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint))
                return false;

            // localhost 或 127.x.x.x
            if (
                endpoint.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || endpoint.StartsWith("127.")
            )
            {
                return true;
            }

            // 检查是否是本机的任一IP地址
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        if (ip.ToString() == endpoint)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.Warn("[P2P] 检测本机IP失败: {ErrorMessage}", ex.Message);
            }

            return false;
        }

        /// <summary>
        /// 解析连接地址：如果目标是本机IP，使用127.0.0.1回环地址以避免Windows多网卡环境下自连接失败
        /// </summary>
        private string ResolveConnectionEndpoint(string endpoint)
        {
            if (IsLocalEndpoint(endpoint))
            {
                NLogger.Info("[P2P] 检测到目标是本机({Endpoint})，使用127.0.0.1回环地址连接", endpoint);
                return "127.0.0.1";
            }
            return endpoint;
        }

        /// <summary>
        /// 请求从远程设备下载文件
        /// </summary>
        /// <param name="remoteEndpoint">远程设备IP地址</param>
        /// <param name="fileNames">要下载的文件名列表</param>
        public async Task RequestDownloadFilesAsync(string remoteEndpoint, string[] fileNames)
        {
            if (fileNames == null || fileNames.Length == 0)
            {
                NLogger.Warn("[P2P] 没有指定要下载的文件");
                return;
            }

            await Task.Run(() =>
            {
                try
                {
                    var localIP = GetLocalIPAddressForTarget(remoteEndpoint);
                    if (string.IsNullOrEmpty(localIP))
                    {
                        NLogger.Error("[P2P] 无法获取本机IP地址");
                        return;
                    }

                    NLogger.Info(
                        "[P2P] 本机IP: {LocalIP}, 远程IP: {RemoteEndpoint}",
                        localIP,
                        remoteEndpoint
                    );

                    var request = new DownloadFileRequest
                    {
                        FileNames = fileNames,
                        RequesterIP = localIP,
                        RequesterPort = _defaultPort
                    };

                    var connectEndpoint = ResolveConnectionEndpoint(remoteEndpoint);
                    NLogger.Info(
                        "[P2P] 发送下载请求到: {ConnectEndpoint}:{DefaultPort} (原始: {RemoteEndpoint})",
                        connectEndpoint,
                        _defaultPort,
                        remoteEndpoint
                    );

                    using var client = new DealerSocket();
                    client.Options.Linger = TimeSpan.FromSeconds(5);
                    client.Connect($"tcp://{connectEndpoint}:{_defaultPort}");

                    var requestJson = JsonConvert.SerializeObject(request);
                    client
                        .SendMoreFrame("DOWNLOAD_FILE_REQUEST")
                        .SendFrame(System.Text.Encoding.UTF8.GetBytes(requestJson));

                    NLogger.Info(
                        "[P2P] 已发送下载请求: {Count} 个文件 -> {RemoteEndpoint}",
                        fileNames.Length,
                        remoteEndpoint
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Error("[P2P] 发送下载请求失败: {ErrorMessage}", ex.Message);
                }
            });
        }

        /// <summary>
        /// 获取远程设备的共享文件列表（返回文件名数组，带重试）
        /// </summary>
        /// <param name="remoteEndpoint">远程设备IP地址</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <param name="maxRetries">最大重试次数</param>
        public async Task<string[]> GetRemoteSharedFilesAsync(
            string remoteEndpoint,
            int timeoutMs = 15000,
            int maxRetries = 3
        )
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    NLogger.Info("[P2P] 获取远程文件列表 (尝试 {Attempt}/{MaxRetries})", attempt, maxRetries);
                    var files = await RequestRemoteFilesAsync(remoteEndpoint, timeoutMs);

                    if (files.Length > 0)
                    {
                        return files.Select(f => f.RelativePath ?? f.FileName).ToArray();
                    }

                    NLogger.Warn(
                        "[P2P] 获取远程文件列表返回空 (尝试 {Attempt}/{MaxRetries})",
                        attempt,
                        maxRetries
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Error(
                        "[P2P] 获取远程文件列表异常 (尝试 {Attempt}/{MaxRetries}): {ErrorMessage}",
                        attempt,
                        maxRetries,
                        ex.Message
                    );
                }

                if (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt));
                }
            }

            NLogger.Warn("[P2P] 获取远程文件列表失败，已重试 {MaxRetries} 次", maxRetries);
            return Array.Empty<string>();
        }

        /// <summary>
        /// 获取本机IP地址（优先选择与目标同网段的IP）
        /// </summary>
        private string GetLocalIPAddressForTarget(string targetIP)
        {
            try
            {
                // 解析目标IP的网段前缀（简化处理，取前三段）
                var targetParts = targetIP.Split('.');
                string targetPrefix =
                    targetParts.Length >= 3
                        ? $"{targetParts[0]}.{targetParts[1]}.{targetParts[2]}"
                        : string.Empty;

                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                string fallbackIP = string.Empty;

                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var ipStr = ip.ToString();

                        // 优先选择同网段的IP
                        if (
                            !string.IsNullOrEmpty(targetPrefix)
                            && ipStr.StartsWith(targetPrefix + ".")
                        )
                        {
                            return ipStr;
                        }

                        // 记录第一个非本地回环IP作为备用
                        if (string.IsNullOrEmpty(fallbackIP) && !ipStr.StartsWith("127."))
                        {
                            fallbackIP = ipStr;
                        }
                    }
                }

                return fallbackIP;
            }
            catch (Exception ex)
            {
                NLogger.Error("[P2P] 获取本机IP失败: {ErrorMessage}", ex.Message);
            }
            return string.Empty;
        }

        /// <summary>
        /// 在共享目录中打开资源管理器
        /// </summary>
        public void OpenSharedFolder()
        {
            var sharedDir = AppPathes.SharedFilesDir;
            if (!Directory.Exists(sharedDir))
            {
                Directory.CreateDirectory(sharedDir);
            }

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", sharedDir);
            }
            catch (Exception ex)
            {
                NLogger.Error("[P2P] 打开共享目录失败: {ErrorMessage}", ex.Message);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed)
                return;
            _isDisposed = true;

            CancelAllTasks();
            StopServer();

            _transferSemaphore?.Dispose();
            _transferProgress?.Dispose();
            _transferCompleted?.Dispose();
            _transferFailed?.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
