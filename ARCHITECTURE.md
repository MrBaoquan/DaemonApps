# DaemonApps 架构文档

> **运维管家** — Windows 进程编排、设备协同与授权管理套件
>
> 最后更新: 2026-02-13

---

## 一、解决方案全景

DaemonApps 是一个多项目 Visual Studio 解决方案，围绕 **进程编排（DaemonKit）** 和 **软件授权（LicHper 系列）** 两大核心能力展开，辅以局域网设备协同、文件分发、亮度节能等运维自动化功能。

```
DaemonApps.sln
│
├─ DaemonKit .............. 主应用：WPF 进程编排工具（"运维管家"）
├─ DNHper ................. 共享工具库（Win32 API、日志、加密、序列化）
│
├─ AuthAssistant .......... Avalonia 授权管理客户端
├─ auth_ghost ............. WPF 授权验证壳（静默注入水印）
├─ LicHper ................ C++ 水印渲染 DLL（DXGI Hook / Overlay）
├─ LicHper_inject ......... C++ 注入代理 DLL（PE 导入表桥接）
├─ LicHper_Injector ....... C++ PE 修改器（写入导入表）
├─ LicenseMaker ........... Avalonia 离线授权码生成器
│
├─ FileSharer ............. WPF 文件共享 Web 服务（EmbedIO + QR + AI）
├─ CloudDisk .............. 阿里云 OSS 存储封装库
├─ ClickSimulator ......... 命令行鼠标自动点击工具
├─ QRCoder ................ QR 码生成（Avalonia，原型）
└─ UNICopy ................ 文件拷贝工具（Avalonia，原型）
```

### 技术栈总览

| 维度 | 技术选型 |
|------|---------|
| **UI 框架** | WPF (.NET 8) + Material Design；Avalonia (.NET 6) |
| **MVVM** | ReactiveUI + DynamicData |
| **响应式** | System.Reactive 6.0 |
| **消息传输** | NetMQ (ZeroMQ ROUTER/DEALER) |
| **设备发现** | UDP 广播 + Zeroconf (mDNS) |
| **序列化** | Newtonsoft.Json + MessagePack + XML |
| **日志** | NLog（通过 DNHper.NLogger 封装） |
| **图形渲染** | ImGui + DirectX 11/12 (C++) |
| **压缩** | SharpCompress (ZIP) |
| **云存储** | 阿里云 OSS SDK |

---

## 二、组件依赖关系

```
                          ┌──────────────┐
                          │    DNHper    │  netstandard2.0
                          │ (共享工具库)  │  NLog, WinAPI, Crypto, Rx
                          └──────┬───────┘
                   ┌─────────────┼────────────────┐
                   │             │                │
                   ▼             ▼                ▼
          ┌─────────────┐ ┌───────────┐  ┌──────────────┐
          │  DaemonKit  │ │ FileSharer│  │ LicenseMaker │
          │ WPF net8.0  │ │ WPF net8.0│  │ Avalonia 6.0 │
          └─────────────┘ └─────┬─────┘  └──────────────┘
                                │
                          ┌─────┴─────┐
                          │ CloudDisk │  netstandard2.1
                          │ (OSS SDK) │  阿里云 OSS
                          └───────────┘

     ┌─────────────────────── C++ 组件链 ──────────────────────────┐
     │                                                             │
     │  LicHper_Injector ──→ 修改 PE 导入表 ──→ 目标 EXE          │
     │       (EXE)               添加引用           │              │
     │                                              ▼              │
     │                                       LicHper_inject        │
     │                                        (代理 DLL)           │
     │                                              │              │
     │                                    DLL_PROCESS_ATTACH       │
     │                                              ▼              │
     │                                          LicHper            │
     │                                        (核心 DLL)           │
     │                                    Validate() → 水印渲染    │
     └─────────────────────────────────────────────────────────────┘
                          │
                   Costura.Fody 嵌入
                          │
              ┌───────────┴───────────┐
              │                       │
        AuthAssistant           auth_ghost
        (Avalonia 6.0)          (WPF 8.0)
        授权管理 UI             静默验证壳
```

---

## 三、DaemonKit — 核心架构

DaemonKit 是整个套件的主应用，采用 **ReactiveUI MVVM** 模式，以 `MainWindow` (code-behind 承担组合根角色) 为入口，协调所有子系统。

### 3.1 分层架构

```
┌────────────────────────────────────────────────────────────────┐
│                        Views 层                                │
│  MainWindow · DaemonTable · ProcessNodeForm · Settings ·       │
│  Schedule · ExportDialog · ImportDialog · TransferListWindow · │
│  ResourceLibraryWindow · BackupManager · PowerSavingWindow ... │
├────────────────────────────────────────────────────────────────┤
│                     ViewModels 层                              │
│  MainViewModel (partial) · DaemonPanelViewModel ·              │
│  SettingsViewModel · ScheduleViewModel · ResourceLibraryVM ·   │
│  TransferListViewModel · ExportDialogVM · ImportDialogVM ...   │
├────────────────────────────────────────────────────────────────┤
│                      Services 层                               │
│  P2PFileTransferService · TransferTaskManager ·                │
│  ExportImportService · DeviceDiscoveryService ·                │
│  NetworkBroadcastService · CrashDetectionService ·             │
│  PowerSavingService · IdleMonitorService                       │
├────────────────────────────────────────────────────────────────┤
│                       Core 层                                  │
│  ProcManager · ScheduleTaskEngine · DeviceControl              │
├────────────────────────────────────────────────────────────────┤
│                      Models 层                                 │
│  ProcessItem · ScheduleTaskConfig · FileTransferTask ·         │
│  MachineInfo · Command · AppSettings · ResourceFileItem ...    │
├────────────────────────────────────────────────────────────────┤
│                    Infrastructure                              │
│  DNHper (NLogger, WinAPI) · Utilities (AppPathes, ZipHelper)   │
└────────────────────────────────────────────────────────────────┘
```

### 3.2 启动流程

```
App.xaml.cs
  │  全局异常处理 (Dispatcher / AppDomain / TaskScheduler)
  │  ReactiveUI ViewLocator 注册
  │
  └─→ MainWindow 构造函数
       │  NLogger 初始化
       │  创建 P2PFileTransferService (TCP 7009)
       │  创建 TransferTaskManager
       │  创建 DaemonTable（注入 P2P + TaskManager）
       │  创建 PowerSavingService
       │  订阅 PackageProgressInfo MessageBus
       │
       └─→ WhenActivated
            │  DataContext = MainViewModel
            │  日志轮询 (200ms Observable.Timer)
            │  加载扩展工具栏 (loadExtensions)
            │  加载配置 (loadConfig)
            │    ├─ 反序列化进程树 XML → rootProcessNode
            │    ├─ 反序列化 AppSettings
            │    ├─ 反序列化 GlobalScheduleConfig
            │    ├─ 初始化 IdleMonitorService
            │    ├─ 初始化 NetworkBroadcastService (UDP 7007/7008)
            │    └─ 启动 CrashDetectionService
            │  渲染进程树 TreeView
            │
            └─→ Loaded 事件 → InitializeBackgroundServices
                 │  异步获取硬件信息
                 │  创建 ScheduleTaskEngine
                 │    ├─ ConfirmHandler (关机/重启倒计时确认)
                 │    └─ PowerSavingViewModelProvider
                 │  启动计划任务监控 (1秒 Timer)
                 └─ 完成
```

### 3.3 进程树模型 — ProcessItem

进程树是 DaemonKit 的**核心数据结构**，采用树形层级模型：

```
Root Node (IsSuperRoot=true, 不可见)
├── Level-2 节点（用户可选择的顶层进程组）
│   ├── Level-3 子进程（随父节点自动包含）
│   │   └── Level-4+ ...
│   ├── ProcessMetaData（名称、路径、参数、工作目录、窗口位置...）
│   └── ScheduleItems / ScheduleTaskConfigs（附属计划任务）
└── ... 更多 Level-2 节点
```

**ProcessItem 关键能力：**

| 能力 | 实现方式 |
|------|---------|
| **树结构** | `Children: ObservableCollection<ProcessItem>`，递归父子关系 |
| **进程启停** | `Start()` → `ProcManager.Daemon()` → 递归启动子节点 |
| **生命周期监控** | `Observable.Timer` 定时检测进程存活，自动重启（守护模式） |
| **勾选级联** | `IsChecked` 属性变化时向上（父）向下（子）级联传播 |
| **窗口编排** | `KeepTop`、`MoveWindow`、`ResizeWindow`、`PosX/Y/Width/Height` |
| **脚本支持** | `IsScript=true` 时调用 bat/ps1 而非 EXE |
| **延迟启动** | `Delay` 字段控制子节点逐个延迟启动 |
| **序列化** | XML 序列化持久化到 `Applications/{ProjectName}/` |

### 3.4 服务层详解

#### 3.4.1 P2P 文件传输（P2PFileTransferService）

基于 **NetMQ (ZeroMQ)** 的点对点文件传输引擎，支持断点续传、并发控制、暂停/恢复/取消。

```
发送端 (DealerSocket Client)              接收端 (RouterSocket Server)
         │                                          │
         │──── METADATA (JSON) ──────────────────→  │  HandleMetadataAsync
         │                                          │    创建 FileStream
         │  ←── RESUME_RESPONSE (JSON) ────────── │    SendResumeResponse
         │                                          │
         │──── DATA_CHUNK (Header+Binary) ───────→  │  HandleDataChunk (Rx管道)
         │──── DATA_CHUNK ───────────────────────→  │    零分配 Utf8JsonReader
         │     ...（256KB/块）                      │    ArrayPool 缓冲区
         │──── DATA_CHUNK (IsLastChunk=true) ────→  │
         │                                          │  CompleteReceiveAsync
         │  ←── TRANSFER_COMPLETE (JSON) ─────── │    MD5 校验（小文件）
         │                                          │
         │  ←── TRANSFER_CANCELLED ──────────────│  CancelTask（接收端取消时）
         │                                          │
```

**关键设计决策：**

- **接收管道**：Rx `Publish().RefCount()` → `GroupBy(taskId)` → 每组 `Concat()` 保序写盘
- **发送方式**：`Task.Run` 包裹全部阻塞 NetMQ 调用，避免阻塞 UI 线程
- **取消双向通知**：接收端取消时通过 RouterSocket 回送 `TRANSFER_CANCELLED` 给发送端
- **进度节流**：每任务 100ms 最多上报一次，减少 Subject/UI 开销
- **并发控制**：SemaphoreSlim（默认 4 路并发传输）

#### 3.4.2 传输任务管理（TransferTaskManager）

基于 **DynamicData SourceCache** 的响应式任务跟踪层，为 UI 提供实时分组、排序、统计。

```
SourceCache<TransferTaskItem, string>
    │
    ├─→ Filter(上传) → Sort(状态+时间) → Bind(UploadTasks)
    ├─→ Filter(下载) → Sort(状态+时间) → Bind(DownloadTasks)
    ├─→ Filter(进行中) → Bind(ActiveTasks)
    └─→ Filter(已完成) → Bind(CompletedTasks)
```

**速度计算**：滑动窗口算法（5 个采样点），平滑显示传输速率和 ETA。

#### 3.4.3 设备发现（DeviceDiscoveryService）

三种发现模式：`BroadcastOnly` / `ManualOnly` / `Hybrid`

```
┌─────────────────────────────────────────┐
│         DeviceDiscoveryService          │
│                                         │
│  UDP 广播接收 ─→ 解析 MachineInfo       │
│  手动 IP 列表 ─→ TCP 探测连通性         │
│  合并去重     ─→ SourceCache<MachineInfo>│
│                                         │
│  Rx 流：DeviceFound / DeviceLost /       │
│         DeviceUpdated                   │
└─────────────────────────────────────────┘
```

#### 3.4.4 网络广播（NetworkBroadcastService）

双职责服务：
1. **广播端**：每 3 秒通过 UDP 7007 广播本机 `MachineInfo`（硬件信息 + 状态）
2. **命令接收端**：监听 UDP 7008 接收远程控制命令 → `CommandReceived` (IObservable\<Command\>)

#### 3.4.5 计划任务引擎（ScheduleTaskEngine）

```
MainWindow (1秒 Timer)
    │
    └──→ ScheduleTaskEngine.CheckAndExecutePendingTasks()
              │
              ├── 收集全局任务 (GlobalScheduleConfig.Tasks)
              ├── 收集节点任务 (递归 ProcessItem.ScheduleTasks)
              │
              └── 逐任务检查触发条件
                    │
                    ├── Daily          → 时刻匹配 (HH:mm:ss)
                    ├── OncePerDayAfterStart → 首次启动 + 延迟，每天仅一次
                    ├── EveryStartupAfterDelay → 每次启动 + 延迟
                    └── IntervalAfterStartup → 启动后按间隔重复
                          │
                          └── 派发动作 ──→ StartProcess / RestartProcessTree /
                                          KillProcess / ShutdownSystem /
                                          RestartSystem / TakeScreenshot /
                                          MouseClick / EnterPowerSaving /
                                          ExitPowerSaving
```

#### 3.4.6 软件包导入导出（ExportImportService）

Docker-like 的配置打包方案，将进程树 + 程序文件打包为 `.dkp.zip`：

```
导出流程：
  选择节点 → 收集依赖文件 → 路径转相对路径 → ZIP 压缩
  ├── config.json (进程树结构)
  ├── programs/   (可执行文件 + 依赖)
  └── manifest.json (包元数据)

导入流程：
  读取 ZIP → 解析 manifest → 路径转换 → GUID 匹配合并/新增
  ├── 冲突检测 (同 GUID 节点更新 vs 新增)
  ├── 程序解压到目标目录
  └── 更新进程树
```

#### 3.4.7 节能系统（PowerSaving）

完整的显示器亮度管理子系统：

```
PowerSavingViewModel (UI: 亮度滑块、应用/恢复)
    └── PowerSavingManager (门面)
         └── BrightnessCoordinator (设备发现 + 驱动路由)
              ├── DdcCiBrightnessDriver (DDC/CI 协议)
              │     Win32 API: SetVCPFeature / GetVCPFeatureAndVCPFeatureReply
              │
              └── KsvLedBrightnessDriver (KSV LED 控制器)
                    ├── RS232 串口通信
                    └── TCP 网络通信
                          协议: 0x90(查询) / 0x91(设置) / 0x95(固化)
```

**显示器枚举**：4 层解析策略
1. Win32 `EnumDisplayMonitors` + `GetMonitorInfo`
2. `WindowsDisplayAPI` 库枚举
3. WMI `Win32_DesktopMonitor` 查询
4. Registry EDID 解析获取友好名称

### 3.5 联调面板（DaemonPanelViewModel）

DaemonKit 的远程设备管理中心，1900+ 行，是功能最密集的 ViewModel。

```
DaemonPanelViewModel
│
├── 设备管理
│   ├── DeviceDiscoveryService → SourceCache<MachineInfo>
│   ├── 分页浏览 (PageSize=50)
│   ├── 搜索过滤
│   └── 批量操作（关机/重启/部署）
│
├── 远程控制
│   ├── UDP 命令发送 (SendUdpCommandWithRetryAsync)
│   │   ├── SHUTDOWN / RESTART / BOOT
│   │   ├── RESTART_NODE_TREE / STOP
│   │   ├── EXPORT_PACKAGE (远程导出)
│   │   └── LIST_SHARED_FILES (文件列表)
│   └── 命令响应接收 (Rx Subject)
│
├── 文件传输
│   ├── SendFilesCommand → P2PFileTransferService
│   ├── BrowseFilesCommand → 远程文件浏览
│   ├── DownloadPackageCommand → 包分发
│   └── TransferTaskManager → UI 状态跟踪
│
└── 资源库
    └── ResourceLibraryViewModel
         ├── 渐进式设备扫描 (20 路并发)
         ├── 文件聚合 + 分类筛选
         ├── 批量下载
         └── 本地 MD5 校验恢复状态
```

### 3.6 网络端口规划

| 端口 | 协议 | 用途 | 组件 |
|------|------|------|------|
| **7007** | UDP | 设备广播（MachineInfo） | NetworkBroadcastService |
| **7008** | UDP | 远程控制命令 | NetworkBroadcastService |
| **7009** | TCP | P2P 文件传输 (NetMQ) | P2PFileTransferService |
| **7777** | UDP | 心跳检测 | DeviceDiscoveryService |
| **6699** | HTTP | 文件共享 Web 服务 | FileSharer (EmbedIO) |

### 3.7 数据持久化

```
{AppRoot}/
├── Applications/
│   └── {ProjectName}/         项目配置目录
│       ├── treeview.xml       进程树结构
│       ├── settings.xml       应用设置
│       └── schedule.xml       计划任务
├── Resources/
│   └── Configs/
│       ├── extension.xml      工具栏扩展定义
│       ├── HotkeyConfig.xml   快捷键配置
│       └── GlobalSchedule.xml 全局计划任务
├── SharedFiles/               共享文件目录
├── ReceivedFiles/             接收文件目录
├── Backups/                   配置备份
├── Hooks/
│   ├── Start/                 启动前钩子脚本
│   ├── Destroy/               退出前钩子脚本
│   └── Awake/                 唤醒钩子脚本
└── Logs/                      NLog 日志
```

---

## 四、LicHper — 授权与水印系统

### 4.1 授权验证流程

```
应用启动 → LicHper_inject.dll 自动加载 (PE 导入表)
    │
    └──→ LicHper.dll::Validate(processName)
              │
              ├── 读取 %USERPROFILE%\.authrc (AES 加密授权文件)
              ├── 解密 → 检查到期日/应用订阅
              │
              ├── [已授权] → 返回，无水印
              │
              └── [未授权] → RenderManager::Initialize()
                                │
                                ├── 检测目标进程图形 API
                                │   ├── 有 DXGI SwapChain?
                                │   │   ├── [是] → HookRenderer (DXGI Hook)
                                │   │   └── [否] → OverlayRenderer (透明窗口)
                                │   └── 读取水印配置 (.authrc.ini)
                                │
                                └── 启动渲染循环
                                    ├── ImGui 文字水印 (动画滚动)
                                    ├── ImGui 图片水印
                                    └── 授权输入窗口
```

### 4.2 渲染架构

```
RenderManager (单例, 策略选择)
    │
    ├── HookRenderer : IWatermarkRenderer
    │     DXGI Hook 模式（嵌入目标渲染管线）
    │     │
    │     ├── DXGIHook (单例)
    │     │   ├── Hook IDXGIFactory::CreateSwapChainForHwnd
    │     │   ├── Hook IDXGISwapChain::Present
    │     │   └── Hook IDXGISwapChain::ResizeBuffers
    │     │
    │     ├── D3D11 路径 → WatermarkRendererD3D11
    │     └── D3D12 路径 → WatermarkRendererD3D12
    │
    └── OverlayRenderer : IWatermarkRenderer
          透明窗口覆盖模式（独立渲染设备）
          │
          ├── 创建透明 Layered Window (WS_EX_TRANSPARENT)
          ├── 自建 D3D11 Device + SwapChain
          └── WatermarkRendererD3D11 渲染
```

**D3D12 关键约束**：必须在目标 SwapChain 创建**之前**注入，以通过 Hook `CreateSwapChainForHwnd` 捕获 `CommandQueue`。后注入（如 UE5）自动回退到 Overlay 模式。

### 4.3 PE 注入链

```
LicHper_Injector.exe <target.exe>
    │
    ├── 解析 PE 头 (DOS → NT → Section Headers)
    ├── 创建新节区 .rdata2
    ├── 写入 Import Descriptor → LicHper_inject.dll
    ├── 生成 IAT/INT 指向 dummy_export()
    ├── 更新 PE 头 (Import Directory, SectionCount, ImageSize)
    └── 重算 PE Checksum

目标 EXE 启动:
    PE Loader → 加载 LicHper_inject.dll
                  │ DllMain(DLL_PROCESS_ATTACH)
                  └── CreateThread → LicHper::Validate()
```

---

## 五、DNHper — 共享工具库

面向 `netstandard2.0`，所有 C# 项目的基础依赖。

```
DNHper/
├── Logger/
│   └── NLogger.cs .............. 静态日志封装（NLog + Rx Subject 流）
│
├── PlatformAPIs/WinAPI/
│   ├── WinAPI.cs ............... 基类 + 数据结构定义
│   ├── WinAPI.Process.cs ....... 进程管理: OpenProcess, FindProcess
│   ├── WinAPI.Window.cs ........ 窗口操作: FindWindow, EnumWindows...
│   ├── WinAPI.WindowHelper.cs .. 高级窗口: SetTopMost, ForceActivate...
│   ├── WinAPI.Audio.cs ......... 音量控制: IAudioEndpointVolume COM
│   ├── WinAPI.Cursor.cs ........ 光标操作: SetCursorPos, mouse_event
│   ├── WinAPI.Keyboard.cs ...... 键盘模拟: SendKey, PostMessage
│   ├── WinAPI.FileSystem.cs .... 文件系统: GetDiskSpace, WatchFolder...
│   ├── WinAPI.Shortcut.cs ...... 快捷方式: COM IShellLink
│   ├── WinAPI.SystemInfo.cs .... 系统信息: 显示器枚举, 电源状态
│   └── WinAPI.Interop.cs ....... 全部 P/Invoke 声明集中管理
│
├── Utils/
│   ├── AESCrypto.cs ............ AES 对称加密
│   ├── RSACrypto.cs ............ RSA 非对称加密
│   ├── Serializer.cs ........... XML + MessagePack 序列化
│   ├── Singleton.cs ............ 泛型单例 + 持久化单例
│   ├── NetworkUtils.cs ......... 本地 IP 获取
│   └── Extensions/ ............. 集合、对象、反射扩展方法
│
└── IO/
    └── DirectoryWatcher.cs ..... Rx FileSystemWatcher 封装
```

---

## 六、辅助组件

### 6.1 FileSharer — 文件共享服务

WPF + EmbedIO 嵌入式 HTTP 服务器，运行在端口 6699：

- **文件上传**：`POST /api/share` → 接收文件 → 推送阿里云 OSS → 生成 QR 码
- **AI 对话**：集成 DeepSeek API，流式 Markdown 渲染
- **依赖**：DNHper + CloudDisk (OSS)

### 6.2 ClickSimulator — 鼠标点击模拟

命令行工具，由 DaemonKit 计划任务的 `MouseClick` 动作调用：

```
ClickSimulator.exe --posX 100 --posY 200 --delay 5 --count 3 --interval 1000
```

使用 `System.Reactive Observable.Timer` 定时触发 Win32 `mouse_event`。

---

## 七、关键设计模式

### 7.1 ReactiveUI MVVM

所有 ViewModel 继承 `ReactiveObject`，使用 `RaiseAndSetIfChanged` 属性通知：

```csharp
public class MyViewModel : ReactiveObject
{
    private string _name;
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    // ReactiveCommand 自动管理 CanExecute / IsExecuting
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
}
```

### 7.2 DynamicData 响应式集合

核心集合操作模式：`SourceCache/SourceList` → `Connect()` → 操作链 → `Bind(out collection)`

```csharp
_sourceCache.Connect()
    .Filter(searchPredicate)
    .Sort(comparer)
    .ObserveOn(RxApp.MainThreadScheduler)
    .Bind(out _filteredItems)
    .Subscribe();
```

### 7.3 混合式 Rx / async-await

- **真正的流式场景**（事件、管道、重试）使用 `IObservable`
- **单值过程式操作**（文件 I/O、网络请求）使用 `async/await`
- **阻塞操作**（NetMQ socket）包裹在 `Task.Run()` 中避免阻塞 UI

### 7.4 火焰忘却模式

长时间操作不阻塞命令执行：

```csharp
SendFilesCommand = ReactiveCommand.Create<MachineInfo>(machine =>
{
    _ = Task.Run(async () => await _transferService.SendFilesAsync(machine, files));
});
```

### 7.5 MessageBus 跨层通信

`ReactiveUI.MessageBus` 用于松耦合的跨组件通信（如导入导出进度）：

```csharp
// 发布
MessageBus.Current.SendMessage(new PackageProgressInfo { ... });
// 订阅
MessageBus.Current.Listen<PackageProgressInfo>().Subscribe(OnProgress);
```

---

## 八、构建与部署

### 8.1 构建命令

```powershell
# 完整解决方案
dotnet build DaemonApps.sln

# DaemonKit 单独构建
dotnet build DaemonKit/DaemonKit.csproj

# LicHper (C++ DLL)
MSBuild LicHper/LicHper.vcxproj /p:Configuration=Release /p:Platform=x64 /m

# 发布单文件
dotnet publish DaemonKit/DaemonKit.csproj -c Release -r win-x64 --self-contained
```

### 8.2 平台约束

| 组件 | 平台 | 约束原因 |
|------|------|---------|
| DaemonKit | x64 (主), x86 | WPF + Win32 API |
| AuthAssistant | x64 only | 嵌入 LicHper x64 DLL |
| LicHper | x64 (主) | D3D11/12 + ImGui |
| LicHper_inject | x64 | 依赖 LicHper.lib |
| DNHper | Any CPU | netstandard2.0 |

### 8.3 Costura.Fody 嵌入

AuthAssistant 和 auth_ghost 通过 Costura.Fody 将 `LicHper.dll` 嵌入 .NET 程序集，运行时自动解压到临时目录。嵌入路径：`Costura64/LicHper.dll`。

---

## 九、线程模型

```
┌─────────────────────────────────────────────────────┐
│                   UI 线程 (STA)                      │
│  WPF Dispatcher · ViewModel 属性更新 · 命令触发      │
│  ObserveOn(RxApp.MainThreadScheduler) 确保线程安全   │
└──────────┬──────────────────────────────────────┬────┘
           │                                      │
    ┌──────▼──────┐                        ┌──────▼──────┐
    │  Rx 调度线程  │                        │ Task.Run 池  │
    │  Observable   │                        │  NetMQ 阻塞   │
    │  Timer/Interval│                        │  TCP 探测     │
    │  200ms 日志轮询│                        │  文件 Hash    │
    │  1s 计划任务   │                        │  ZIP 压缩     │
    │  500ms 进度刷新│                        │  串口通信     │
    └─────────────┘                        └─────────────┘
           │
    ┌──────▼──────┐
    │ NetMQ I/O 线程│
    │  RouterSocket  │
    │  接收循环       │
    │  Rx 管道处理    │
    └─────────────┘
```

---

## 十、扩展点

| 扩展点 | 机制 | 位置 |
|--------|------|------|
| **工具栏扩展** | `extension.xml` 定义菜单项 + bat/ps1 脚本 | `Resources/Configs/extension.xml` |
| **生命周期钩子** | `Hooks/Start/`、`Hooks/Destroy/`、`Hooks/Awake/` 目录放置脚本 | 启动前/退出前/唤醒时自动执行 |
| **亮度驱动** | 实现 `IBrightnessDriver` 接口 | `PowerSaving/Drivers/` |
| **计划任务动作** | 在 `ScheduleTaskEngine.ExecuteAction` 添加新 case | `Core/ScheduleTaskEngine.cs` |
| **设备发现模式** | `DiscoveryMode` 枚举扩展 | `Services/DeviceDiscoveryService.cs` |
