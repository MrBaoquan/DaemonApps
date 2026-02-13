# 联调面板 (DaemonPanel) 架构文档

## 概述

联调面板是 DaemonKit 的 P2P 设备协调模块，用于局域网内多台设备的发现、文件传输和远程控制。

## 核心功能

### 1. 设备发现（新架构）

使用 `DeviceDiscoveryService` 支持三种发现模式：

| 模式 | 说明 | 适用场景 |
|------|------|----------|
| `BroadcastOnly` | 仅UDP广播 | 同子网设备 |
| `ManualOnly` | 仅手动配置 | 跨路由器设备 |
| `Hybrid` | 混合模式 | 推荐使用 |

**特性：**
- 支持手动添加跨路由器设备IP
- TCP探测确认设备在线状态
- 配置持久化 (`Resources/device_discovery.json`)
- 响应式编程 (RX) 实现

### 2. 远程控制
- **控制端口**：UDP 7008，发送 `Command` 命令
- **支持命令**：
  - `SHUTDOWN (1001)` - 远程关机
  - `RESTART (1002)` - 远程重启
  - `BOOT (1003)` - 远程启动
  - `RESTART_NODE_TREE (1004)` - 重启进程树
  - `STOP (1005)` - 停止进程树
  - `EXPORT_PACKAGE (1006)` - 导出进程包
  - `EXPORT_PACKAGE_COMPLETED (1007)` - 导出完成通知

### 3. 文件传输
- **传输端口**：TCP 7009 (NetMQ)
- **功能**：
  - 发送本地文件到远程设备
  - 浏览远程共享文件
  - 下载远程文件
- **共享目录**：`Resources/SharedFiles`
- **接收目录**：`Resources/ReceivedFiles`

### 4. 进程包管理
- **下载远程进程包**：请求远程设备导出配置+程序，然后下载
- **批量下载**：一键从所有在线设备下载进程包
- **任务管理**：取消、重试、清除已完成任务
- **备份管理**：本地备份/恢复进程包

## 架构设计（已实现）

### 类图

```
DaemonPanelViewModel
├── P2PFileTransferService (文件传输服务)
├── SourceCache<MachineInfo> (设备缓存)
├── SourceCache<FileTransferTask> (传输任务缓存)
├── SourceCache<RemotePackageTask> (远程包任务缓存) ✅
├── ConcurrentDictionary<TaskCompletionSource> (导出完成等待)
└── Commands
    ├── SendFilesCommand
    ├── BrowseFilesCommand
    ├── DownloadPackageCommand ✅ (非阻塞)
    ├── BatchDownloadPackagesCommand ✅ (批量下载)
    ├── CancelPackageTaskCommand ✅
    ├── RetryPackageTaskCommand ✅
    ├── ClearCompletedPackageTasksCommand ✅
    └── ShutdownCommand / RestartCommand
```

### 远程进程包下载流程（已实现）

```
1. 用户点击"下载进程包"
   ↓
2. 创建 RemotePackageTask 任务对象，加入 _packageTaskCache
   ↓
3. 任务在界面"远程包下载任务"区域显示状态
   ↓
4. 后台异步执行 ExecuteRemotePackageTaskAsync():
   a. State=RequestingExport, 发送 EXPORT_PACKAGE 命令
   b. State=Exporting, 等待 EXPORT_PACKAGE_COMPLETED 通知 (120s超时)
   c. State=ExportCompleted, 获取远程文件列表
   d. State=Downloading, 添加 .dkpkg 到传输队列
   e. State=Completed, 任务完成
   ↓
5. 用户可随时：取消、重试、清除任务
```

### 批量下载流程（已实现）

```
1. 点击"批量下载"按钮
   ↓
2. 确认对话框显示在线设备数量
   ↓
3. 为每个在线设备创建 RemotePackageTask
   ↓
4. 所有任务并行执行
   ↓
5. 在任务列表中统一显示进度
```

## 数据模型（已实现）

### RemotePackageTask

位置：`DaemonKit/Models/RemotePackageTask.cs`

```csharp
public class RemotePackageTask : ReactiveObject
{
    public string TaskId { get; }
    public string MachineName { get; }
    public string MachineIP { get; }
    public RemotePackageState State { get; set; }
    public double Progress { get; set; }
    public string StatusText { get; set; }
    public DateTime CreatedTime { get; }
    public string? PackageFileName { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LocalFilePath { get; set; }
    
    // 计算属性
    public string StateText { get; }
    public bool IsCompleted { get; }
    public bool IsFailed { get; }
    public bool IsInProgress { get; }
}

public enum RemotePackageState
{
    Pending,            // 等待中
    RequestingExport,   // 正在请求导出
    Exporting,          // 远程正在导出
    ExportCompleted,    // 导出完成
    Downloading,        // 正在下载
    Completed,          // 完成
    Failed,             // 失败
    Cancelled           // 已取消
}
```

## 关键改进（已完成）

### 1. 非阻塞设计 ✅
- 移除等待窗口，改用任务队列
- 所有操作异步执行，UI 始终响应
- `DownloadPackageCommand` 立即返回，任务后台执行

### 2. 任务可视化 ✅
- 远程包任务在 DaemonTable 界面单独显示
- 状态实时更新（请求中/导出中/下载中/完成/失败）
- 进度条和状态图标

### 3. 批量操作 ✅
- `BatchDownloadPackagesCommand` 批量下载
- 为所有在线设备创建任务
- 并行执行

### 4. 错误恢复 ✅
- 120秒超时保护
- 失败任务显示错误信息
- 支持重试 (`RetryPackageTaskCommand`)
- 支持取消 (`CancelPackageTaskCommand`)

### 5. 事件驱动通信 ✅
- 使用 `TaskCompletionSource` 等待远程导出完成
- `EXPORT_PACKAGE_COMPLETED` 事件通知
- 不再依赖固定延迟

## 文件结构

```
DaemonKit/
├── Views/
│   ├── DaemonTable.xaml          # 联调面板主界面（含远程包任务区域）
│   ├── TransferListWindow.xaml   # 传输列表窗口
│   └── RemoteFileBrowser.xaml    # 远程文件浏览器
├── ViewModels/
│   └── DaemonPanelViewModel.cs   # 联调面板 ViewModel（核心逻辑）
├── Models/
│   ├── MachineInfo.cs            # 设备信息
│   ├── FileTransferTask.cs       # 文件传输任务
│   ├── RemotePackageTask.cs      # 远程包任务 ✅
│   └── Types.cs                  # 命令定义
└── Services/
    ├── P2PFileTransferService.cs # P2P 文件传输服务
    └── NetworkBroadcastService.cs # 网络广播服务
```

## UI 布局

DaemonTable.xaml 界面结构：

```
┌─────────────────────────────────────────────────────┐
│ Row 0: 工具栏（搜索/过滤/刷新）                       │
├─────────────────────────────────────────────────────┤
│ Row 1: 设备列表 DataGrid                             │
│   - 设备名/IP/硬件信息/操作按钮                       │
├─────────────────────────────────────────────────────┤
│ Row 2: 分页控件                                      │
├─────────────────────────────────────────────────────┤
│ Row 3: 远程包下载任务（可折叠）                       │  ✅ 新增
│   - 批量下载/清除已完成                               │
│   - 任务列表：设备名/IP/状态/进度/操作                 │
├─────────────────────────────────────────────────────┤
│ Row 4: 文件传输状态栏                                │
└─────────────────────────────────────────────────────┘
```

## 后续扩展方向

1. **下载进度追踪**：当前任务在发送下载请求后立即标记完成，实际应等待文件传输真正完成
2. **导出进度显示**：远程设备导出时显示实际进度（需要新的事件类型）
3. **任务持久化**：重启后恢复未完成的任务
4. **选择性批量下载**：支持勾选设备后批量下载
5. **下载历史**：记录所有下载过的进程包
6. **集成 DeviceDiscoveryService**：将新的设备发现服务集成到 DaemonPanelViewModel

## 已知技术限制

1. **P2P通信**：使用 NetMQ DEALER-ROUTER 模式，请求和响应必须在同一连接
2. **文件传输异步**：`RequestDownloadFilesAsync` 只发送请求，实际传输由服务端推送
3. **跨路由器发现**：需要手动配置设备IP，无法自动发现

## 服务架构

### DeviceDiscoveryService（新增）

位置：`DaemonKit/Services/DeviceDiscoveryService.cs`

**职责：**
- 设备发现（UDP广播 + 手动配置）
- 设备在线状态管理
- 跨路由器TCP探测

**响应式接口：**
```csharp
// 设备发现/离线事件流
IObservable<MachineInfoExtended> DeviceDiscovered
IObservable<string> DeviceOffline

// 手动设备管理
void AddManualDevice(string ipAddress)
void RemoveManualDevice(string ipAddress)
Task<bool> ProbeDeviceAsync(string ipAddress)
```

**配置文件：** `Resources/device_discovery.json`
```json
{
  "Mode": "Hybrid",
  "ManualDevices": ["192.168.1.100", "10.0.0.50"]
}
```

### P2PFileTransferService

**改进：**
- `RequestRemoteFilesAsync` 不再抛异常，失败返回空数组
- 同机检测：自动识别本机IP，返回本地文件列表
- 超时友好处理，记录日志而非崩溃

## 文件结构

```
DaemonKit/
├── Views/
│   ├── DaemonTable.xaml          # 联调面板主界面（含远程包任务区域）
│   ├── TransferListWindow.xaml   # 传输列表窗口
│   └── RemoteFileBrowserWindow.xaml # 远程文件浏览器
├── ViewModels/
│   ├── DaemonPanelViewModel.cs   # 联调面板 ViewModel（核心逻辑）
│   └── RemoteFileBrowserViewModel.cs # 远程文件浏览器 ViewModel
├── Models/
│   ├── MachineInfo.cs            # 设备信息
│   ├── MachineInfoExtended       # 扩展设备信息（含手动添加标记）
│   ├── FileTransferTask.cs       # 文件传输任务
│   ├── RemotePackageTask.cs      # 远程包任务 ✅
│   └── Types.cs                  # 命令定义
└── Services/
    ├── P2PFileTransferService.cs # P2P 文件传输服务
    ├── DeviceDiscoveryService.cs # 设备发现服务 ✅ 新增
    └── NetworkBroadcastService.cs # 网络广播服务
