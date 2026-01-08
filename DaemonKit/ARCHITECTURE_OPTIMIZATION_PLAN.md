# DaemonKit 架构优化建议

## 📊 当前代码状况分析

### MainWindow.xaml.cs 现状
- **总行数**: 2078 行（重构后）
- **职责过多**: UI交互 + 网络通信 + 硬件信息 + 配置管理 + 扩展菜单
- **问题**: 仍然是一个"上帝类"（God Class），承担了过多职责

---

## 🔍 可继续优化的模块

### 1. 网络通信模块 (高优先级 ⭐⭐⭐⭐⭐)

#### 问题
- UDP 广播逻辑（设备信息、心跳、命令接收）直接写在 MainWindow 构造函数
- 三个 Observable.Timer 独立运行，难以统一管理
- 网络相关代码约 **300+ 行**，与 UI 逻辑混杂

#### 建议抽取服务
**NetworkBroadcastService.cs**
```csharp
public class NetworkBroadcastService : IDisposable
{
    // 职责：
    // 1. UDP 广播设备信息 (MachineInfo)
    // 2. 接收远程控制命令 (Command)
    // 3. 接收心跳命令 (Heartbeat)
    
    public IObservable<Command> CommandStream { get; }
    public void StartBroadcast();
    public void StopBroadcast();
}
```

**受益**:
- ✅ MainWindow 减少 ~300 行
- ✅ 网络逻辑可独立测试
- ✅ 易于扩展新的网络协议（如 TCP、WebSocket）

---

### 2. 崩溃检测模块 (高优先级 ⭐⭐⭐⭐)

#### 问题
- 崩溃检测逻辑使用 Observable.Interval 每 200ms 轮询
- 代码约 **50+ 行**，与进程管理逻辑耦合
- 检测频率和策略硬编码，难以调整

#### 建议抽取服务
**CrashDetectionService.cs**
```csharp
public class CrashDetectionService : IDisposable
{
    // 职责：
    // 1. 监控进程崩溃（200ms 轮询）
    // 2. 自动重启崩溃进程
    // 3. 记录崩溃日志
    
    public void StartMonitoring(ProcessItem rootNode);
    public void StopMonitoring();
    public IObservable<ProcessItem> CrashDetected { get; }
}
```

**受益**:
- ✅ 进程监控逻辑与 UI 解耦
- ✅ 可配置检测频率和重启策略
- ✅ 崩溃事件可订阅，便于添加报警功能

---

### 3. 硬件信息管理 (中优先级 ⭐⭐⭐)

#### 问题
- `FetchHardwareInfo()` 和 `UpdateHardwareInfoBox()` 约 **100 行**
- 富文本渲染逻辑与业务逻辑混杂
- 颜色刷子（Brush）在 MainWindow 静态构造函数中初始化

#### 建议抽取服务
**HardwareInfoService.cs**
```csharp
public class HardwareInfoService
{
    // 职责：
    // 1. 异步获取硬件信息（CPU、GPU、内存、网络）
    // 2. 格式化硬件信息为结构化数据
    
    public Task<HardwareInfoData> FetchHardwareInfoAsync();
}
```

**UI 层单独处理**:
```csharp
// ViewModels/HardwareInfoViewModel.cs
public class HardwareInfoViewModel : ReactiveObject
{
    public FlowDocument FormatHardwareInfo(HardwareInfoData data);
}
```

**受益**:
- ✅ 数据获取与 UI 渲染分离
- ✅ 可为不同 UI（WPF、Avalonia）提供统一数据源
- ✅ 易于单元测试

---

### 4. 配置管理优化 (中优先级 ⭐⭐⭐)

#### 问题
- `loadConfig()`, `saveConfig()`, `SaveAppSettingsOnly()` 约 **300+ 行**
- 配置加载时机混乱（构造函数、Window_Loaded）
- 保存逻辑有重试机制但缺乏统一错误处理

#### 建议抽取服务
**ConfigurationService.cs**
```csharp
public class ConfigurationService
{
    // 职责：
    // 1. 加载/保存 AppSettings
    // 2. 加载/保存 GlobalScheduleConfig
    // 3. 统一重试逻辑和错误处理
    
    public Task<AppSettings> LoadAppSettingsAsync();
    public Task SaveAppSettingsAsync(AppSettings settings);
    public Task<GlobalScheduleConfig> LoadScheduleConfigAsync();
    public Task SaveScheduleConfigAsync(GlobalScheduleConfig config);
}
```

**受益**:
- ✅ 配置加载/保存集中管理
- ✅ 易于添加配置迁移（版本升级）
- ✅ 支持热重载配置

---

### 5. 扩展菜单管理 (低优先级 ⭐⭐)

#### 问题
- `loadExtensions()` 约 **100 行**，创建菜单项逻辑复杂
- 扩展启动逻辑（WinAPI.OpenProcess）与 UI 混杂

#### 建议抽取服务
**ExtensionService.cs**
```csharp
public class ExtensionService
{
    // 职责：
    // 1. 加载扩展配置（ExtensionConfig.xml）
    // 2. 启动扩展程序
    // 3. 管理扩展分组（System、Tool）
    
    public Task<ExtensionConfig> LoadExtensionsAsync();
    public void LaunchExtension(Extension extension);
}
```

**UI 层**:
```csharp
// ViewModels/ExtensionMenuViewModel.cs
public class ExtensionMenuViewModel
{
    public ObservableCollection<MenuItem> SystemMenuItems { get; }
    public ObservableCollection<MenuItem> ToolMenuItems { get; }
}
```

**受益**:
- ✅ 菜单生成逻辑可复用（如托盘菜单）
- ✅ 扩展管理可独立演进

---

## 📂 文件层级结构优化

### 当前结构问题
```
DaemonKit/
├── Core/               # 混杂：业务逻辑 + 服务 + 工具类
│   ├── PowerSavingService.cs    # ✅ 服务
│   ├── IdleMonitorService.cs    # ✅ 服务
│   ├── ScheduleTaskEngine.cs    # ✅ 核心业务
│   ├── ProcessItem.cs           # ❌ 应在 Models
│   ├── Utils.cs                 # ❌ 应在 Utilities
│   └── AppConfig.cs             # ❌ 应在 Models
├── PowerSaving/        # ✅ 独立功能模块
├── Converters/         # ✅ UI 转换器
└── ViewModels/         # ✅ MVVM ViewModel
```

### 建议优化结构
```
DaemonKit/
├── Models/                  # 数据模型
│   ├── AppSettings.cs
│   ├── ProcessItem.cs
│   ├── GlobalScheduleConfig.cs
│   ├── MachineInfo.cs
│   └── HardwareInfoData.cs
│
├── Services/                # 业务服务层
│   ├── PowerSavingService.cs
│   ├── IdleMonitorService.cs
│   ├── NetworkBroadcastService.cs    # 新增
│   ├── CrashDetectionService.cs      # 新增
│   ├── HardwareInfoService.cs        # 新增
│   ├── ConfigurationService.cs       # 新增
│   └── ExtensionService.cs           # 新增
│
├── Core/                    # 核心业务逻辑
│   ├── ScheduleTaskEngine.cs
│   ├── ProcManager.cs
│   └── DeviceManager.cs
│
├── Utilities/               # 工具类
│   ├── Utils.cs
│   ├── WinAPI.cs
│   └── HardwareInfo.cs
│
├── ViewModels/              # MVVM ViewModel
│   ├── MainViewModel.cs
│   ├── HardwareInfoViewModel.cs     # 新增
│   └── ExtensionMenuViewModel.cs    # 新增
│
├── Views/                   # UI 窗口和控件（可选重构）
│   ├── MainWindow.xaml[.cs]
│   ├── Settings.xaml[.cs]
│   ├── Schedule.xaml[.cs]
│   └── ... (其他窗口)
│
├── PowerSaving/             # 省电模式模块
├── Converters/              # UI 转换器
└── Hooks/                   # 钩子功能
```

### 迁移步骤
1. ✅ **阶段 0**: 创建 `Services/` 文件夹（已有服务移入）
2. 🔜 **阶段 1**: 创建 `Models/` 文件夹，移动数据类
3. 🔜 **阶段 2**: 创建 `Utilities/` 文件夹，移动工具类
4. 🔜 **阶段 3**: 抽取网络通信服务
5. 🔜 **阶段 4**: 抽取崩溃检测服务
6. 🔜 **阶段 5**: 抽取配置管理服务

---

## 🎯 MainWindow 最终目标

### 职责清单（应该保留）
- ✅ UI 事件订阅（按钮点击、菜单选择）
- ✅ 窗口生命周期管理（Loaded、Closing）
- ✅ ReactiveUI 命令绑定
- ✅ 服务层调用和协调

### 应该移除的职责
- ❌ 网络通信逻辑（UDP 广播、命令接收）
- ❌ 硬件信息获取和格式化
- ❌ 配置文件读写
- ❌ 崩溃检测轮询
- ❌ 扩展菜单生成

### 预期效果
**当前**: 2078 行  
**目标**: **1200-1500 行** （减少 ~30-40%）

---

## 📋 优化优先级排序

| 优先级 | 模块 | 预计减少行数 | 复杂度 | 收益 |
|--------|------|-------------|--------|------|
| P0 ⭐⭐⭐⭐⭐ | NetworkBroadcastService | ~300 行 | 中 | 高 |
| P1 ⭐⭐⭐⭐ | CrashDetectionService | ~50 行 | 低 | 高 |
| P2 ⭐⭐⭐ | ConfigurationService | ~300 行 | 中 | 中 |
| P3 ⭐⭐⭐ | HardwareInfoService | ~100 行 | 低 | 中 |
| P4 ⭐⭐ | ExtensionService | ~100 行 | 低 | 低 |

**总计可减少**: ~850 行  
**最终 MainWindow**: ~1220 行

---

## 🚀 实施建议

### 短期（本次重构）
1. ✅ **已完成** - PowerSavingService, IdleMonitorService
2. 🔜 **创建文件夹结构** - Services/, Models/, Utilities/
3. 🔜 **抽取 NetworkBroadcastService** - 影响最大，优先处理
4. 🔜 **抽取 CrashDetectionService** - 简单快速，立即见效

### 中期（后续优化）
5. 🔜 抽取 ConfigurationService
6. 🔜 抽取 HardwareInfoService
7. 🔜 统一服务生命周期管理（ServiceContainer）

### 长期（架构演进）
8. 🔜 引入依赖注入容器（Microsoft.Extensions.DependencyInjection）
9. 🔜 实现接口抽象（INetworkService, IConfigurationService）
10. 🔜 事件总线模式（EventAggregator）

---

## 💡 设计原则

### SOLID 原则
- **S (单一职责)**: 每个服务只做一件事
- **O (开闭原则)**: 服务易于扩展，无需修改现有代码
- **L (里氏替换)**: 接口抽象后可替换实现
- **I (接口隔离)**: 服务接口精简，不强制依赖不需要的方法
- **D (依赖倒置)**: MainWindow 依赖抽象接口，非具体实现

### 分层架构
```
┌────────────────────────────────────┐
│   UI Layer (MainWindow, Views)    │
├────────────────────────────────────┤
│   ViewModel Layer (ReactiveUI)    │
├────────────────────────────────────┤
│   Service Layer (Business Logic)  │  ← 当前重构重点
├────────────────────────────────────┤
│   Core Layer (Domain Logic)       │
├────────────────────────────────────┤
│   Utilities (Helper Classes)      │
└────────────────────────────────────┘
```

---

## 📝 命名规范

### 服务命名
- 后缀 `Service`: `NetworkBroadcastService`, `CrashDetectionService`
- 单一职责命名: `HardwareInfoService` (不是 `HardwareService`)

### 文件夹命名
- 复数形式: `Services/`, `Models/`, `Utilities/`
- 功能模块: `PowerSaving/`, `Hooks/`, `Applications/`

### 接口命名
- 前缀 `I`: `INetworkService`, `IConfigurationService`
- 描述能力: `IDisposable`, `IObservable<T>`

---

## ⚠️ 注意事项

### 兼容性
- ✅ 保持向后兼容，不改变用户配置格式
- ✅ 服务抽取不影响现有功能
- ✅ 渐进式重构，每次提交可独立编译

### 测试策略
- 🧪 每个服务单独单元测试
- 🧪 集成测试验证服务间交互
- 🧪 UI 测试确保功能完整性

### 性能考虑
- ⚡ 服务懒加载（Lazy Initialization）
- ⚡ 避免过度抽象导致性能损失
- ⚡ 保持 Observable 订阅的高效性

---

## 📄 相关文档
- [REFACTORING_SUMMARY.md](./REFACTORING_SUMMARY.md) - 已完成的重构总结
- [任务计划重构说明.md](../任务计划重构说明.md)
- [命令控制使用文档.md](../命令控制使用文档.md)
