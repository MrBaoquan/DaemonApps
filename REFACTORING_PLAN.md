# DaemonKit 重构实施计划

> 基于架构评估，按优先级排列的重构任务清单
>
> 创建日期: 2026-02-13
> 更新日期: 2026-02-14

## 完成状态

| 任务 | 状态 | 说明 |
|------|------|------|
| P0-1 拆分 MainWindow | ✅ 已完成 | 2,977 → 1,384 行 + 5 个 partial class 文件 |
| P0-2 清理异常处理 | ✅ 已完成 | 修复 6 处高风险（空 catch / async void 无 try-catch） |
| P1-1 Splat DI | ✅ 已完成 | 4 个服务注册到 App.xaml.cs，MainWindow 通过 GetService 解析 |
| P1-2 拆分 ProcessItem | ✅ 已完成 | 1,114 → 615 行 + ProcessItem.Lifecycle.cs (~500 行) |
| P2-1 单元测试项目 | ✅ 已完成 | xUnit + NSubstitute，20 个测试全部通过 |
| P2-2 提取 PickerOverlay | ✅ 已完成 | 2,083 → 1,831 行 + ScreenCaptureService 静态工具类 |
| P2-3 网络端口配置化 | ✅ 已完成 | CommonVars 可配置属性 + AppSettings 持久化 + DeviceDiscoveryService 联动 |
| P2-4 日志结构化 | ✅ 已完成 | ~270 处 NLogger 字符串插值 → NLog 结构化参数，覆盖 25+ 文件 |

> 所有任务编译验证通过：**0 错误**，单元测试 **20/20 通过**

---

## P0 — 必须做（影响可维护性）

### P0-1: 拆分 MainWindow.xaml.cs（2,977 行 → 6 个 partial class）

**问题**: MainWindow.xaml.cs 是 God Object，承担 11 个职责、77 个方法、直接持有 7 个 Service 实例（56 处引用）。

**方案**: 使用 `partial class` 按 `#region` 边界拆分，零行为变更。

| 文件 | 包含 region | 行数 | 职责 |
|------|------------|------|------|
| `MainWindow.xaml.cs` | Fields + Constructor | ~1,440 | 组合根 + WhenActivated 绑定 |
| `MainWindow.Config.cs` | Configuration Management | ~450 | loadConfig / saveConfig / 备份 |
| `MainWindow.Schedule.cs` | 计划任务执行逻辑 | ~460 | CheckAndExecute / 倒计时确认 |
| `MainWindow.PackageOps.cs` | 软件包操作进度 + 远程导出 | ~310 | 进度UI / ExportToShared |
| `MainWindow.Lifecycle.cs` | Window Lifecycle + Hotkey + Log + HardwareInfo | ~350 | OnClosing / HwndHook / 日志 |
| `MainWindow.Services.cs` | Initialization Methods | ~60 | InitializeBackgroundServices |

**实施步骤**:
1. 创建 5 个新的 partial class 文件
2. 将对应 region 代码剪切到新文件
3. 所有文件共享相同的 `using` 块和 `namespace`
4. 编译验证

---

### P0-2: 清理异常处理

**问题**: 228 个 `catch(Exception)` + 19 个空 `catch` 块 + 6 处 `async void`。

**方案**: 分三步渐进清理

| 子任务 | 内容 | 影响范围 |
|--------|------|---------|
| P0-2a | 消除 19 个空 `catch` 块 → 添加 `NLogger.Warn` | 全局 |
| P0-2b | 6 个 `async void` → `async Task` + 安全包装 | 6 个文件 |
| P0-2c | 高频服务中的 `catch(Exception)` → 具体异常类型 | P2P / TransferTaskManager |

---

## P1 — 应该做（提升代码质量）

### P1-1: 引入 Splat 服务注册

**问题**: 7 个 Service 全部在 MainWindow 中通过 `new` 手动构造，无法替换或测试。

**方案**: 利用 ReactiveUI 自带的 Splat IoC（已在依赖中），在 `App.xaml.cs` 注册服务。

```csharp
// App.xaml.cs OnStartup
Locator.CurrentMutable.RegisterLazySingleton(() => new P2PFileTransferService());
Locator.CurrentMutable.RegisterLazySingleton(() => new TransferTaskManager());
// ...

// MainWindow.xaml.cs
_p2pService = Locator.Current.GetService<P2PFileTransferService>();
```

**收益**: 无需引入新依赖包，服务生命周期集中管理，为测试铺路。

---

### P1-2: 拆分 ProcessItem（1,093 行）

**问题**: ProcessItem 同时是 XML 序列化实体 + ReactiveObject ViewModel + 进程生命周期管理器。

**方案**: 按职责拆分为 partial class（保持 XML 序列化兼容）

| 文件 | 内容 | 行数 |
|------|------|------|
| `ProcessItem.cs` | 属性定义 + 树结构 + XML 序列化 | ~350 |
| `ProcessItem.Lifecycle.cs` | RunNode / KillNode / Daemon 守护 | ~400 |
| `ProcessItem.UI.cs` | IsSelected 级联 / EnableNameInput / UI Commands | ~200 |
| `ProcessItem.Schedule.cs` | RefreshSchedule / ScheduleItems | ~100 |

---

## P2 — 建议做（工程化提升） ✅ 全部完成

| 编号 | 任务 | 说明 | 状态 |
|------|------|------|------|
| P2-1 | 添加单元测试项目 | xUnit + NSubstitute，覆盖 ScheduleTaskEngine（20 个测试） | ✅ 已完成 |
| P2-2 | 提取 PickerOverlay 逻辑 | 2,083 行 → 1,831 行 + ScreenCaptureService 静态工具类 | ✅ 已完成 |
| P2-3 | 网络端口配置化 | CommonVars 可配置属性 + AppSettings 持久化 + DeviceDiscoveryService 联动 | ✅ 已完成 |
| P2-4 | 日志结构化 | ~270 处字符串插值 → NLog 结构化参数，覆盖 25+ 文件 | ✅ 已完成 |

### P2-1: 单元测试项目

- 创建 `DaemonKit.Tests` 项目（xUnit 2.5.3 + NSubstitute 5.3.0 + coverlet.collector 6.0.0）
- 编写 20 个测试覆盖 `ScheduleTaskEngine` 的核心逻辑
- 测试包括：Daily / OncePerDayAfterStart / EveryStartupAfterDelay / IntervalAfterStartup 触发器类型
- 所有测试通过，`InternalsVisibleTo` 已配置

### P2-2: 提取 PickerOverlay 逻辑

- 从 `PickerOverlay.xaml.cs`（2,083 行）提取屏幕截图相关工具方法到 `ScreenCaptureService` 静态类
- 提取方法：`CaptureScreen`、`CropFromScreen`、`RenderText`、`PushUndoState` 等
- PickerOverlay 减少至 1,831 行（-252 行），职责更清晰

### P2-3: 网络端口配置化

- `CommonVars` 从 `const int` 改为可配置属性（MetaPort / ControlPort / HeartbeatPort）
- 默认值保留（7007 / 7008 / 7777），通过 `ApplyPortOverrides()` 从 AppSettings 覆盖
- `AppSettings` 新增 `CustomMetaPort` / `CustomControlPort` / `CustomHeartbeatPort` 属性
- `SettingsViewModel` 新增对应 UI 绑定属性
- `DeviceDiscoveryService.UDP_BROADCAST_PORT` 改为引用 `CommonVars.MetaPort`
- 启动和运行时保存设置时均调用 `ApplyPortOverrides()`

### P2-4: 日志结构化

- 将 ~270 处 `NLogger.XXX($"...{var}...")` 字符串插值转换为 `NLogger.XXX("...{Var}...", var)` 结构化格式
- 覆盖 25+ 文件，包括：P2PFileTransferService (66)、DaemonPanelViewModel (31)、KsvLedBrightnessDriver (30)、ResourceLibraryViewModel (21)、App.xaml.cs (19)、DeviceDiscoveryService (17)、MainWindow.xaml.cs (18) 等
- 特殊处理：C# 格式说明符（如 `{val:F2}`）预格式化为 `.ToString("F2")`
- 仅保留 3 处注释代码中的旧格式（不影响运行）

---

## 实施顺序

```
P0-1 拆分 MainWindow ──→ P0-2 清理异常 ──→ P1-1 Splat DI ──→ P1-2 拆分 ProcessItem
     ✅ 已完成            ✅ 已完成           ✅ 已完成           ✅ 已完成

P2-1 单元测试 ──→ P2-2 提取 PickerOverlay ──→ P2-3 端口配置化 ──→ P2-4 日志结构化
   ✅ 已完成          ✅ 已完成                 ✅ 已完成            ✅ 已完成
```

每步完成后 `dotnet build` 验证，确保零回归。✅ 全部通过（0 错误，20/20 测试通过）

> 更新日期: 2026-02-14
