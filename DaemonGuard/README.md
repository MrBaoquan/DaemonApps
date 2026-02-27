# DaemonGuard - DaemonKit 专用进程守护服务

## 概述

DaemonGuard 是一个 Windows 服务，专门用于监控和守护 DaemonKit.exe 进程。当 DaemonKit 意外崩溃或被终止时，守护服务会自动在用户桌面会话中重新启动它。

### 核心特性

- **Session 0 隔离处理** — Windows 服务运行在 Session 0，通过 `WTSQueryUserToken` + `CreateProcessAsUser` 在用户桌面会话中启动 GUI 进程
- **智能重启策略** — 可配置检测间隔、重启延迟、最大连续重启次数和冷却时间，防止无限重启循环
- **SCM 故障恢复** — 安装时自动配置 Windows 服务控制管理器的故障恢复策略（前三次失败自动重启服务）
- **自动集成** — DaemonKit 启动时自动检测并尝试启动守护服务
- **无额外依赖** — DaemonKit 集成端通过 `sc.exe` 命令交互，不引入任何新 NuGet 包

## 架构

```
┌─────────────────────────────────┐
│  Windows SCM (服务控制管理器)      │
│  故障策略: 3次重启/10s间隔         │
└────────────┬────────────────────┘
             │ 管理
┌────────────▼────────────────────┐
│  DaemonGuard.exe (Session 0)    │
│  ├── GuardWorker (检测循环)       │
│  │   └── 每5s检测DaemonKit进程   │
│  └── ProcessLauncher (启动器)     │
│      └── CreateProcessAsUser     │
│          → 用户桌面会话           │
└────────────┬────────────────────┘
             │ 启动/守护
┌────────────▼────────────────────┐
│  DaemonKit.exe (Session 1+)     │
│  └── GuardServiceHelper          │
│      └── 启动时确保服务运行       │
└─────────────────────────────────┘
```

## 安装与使用

### 前置条件

- .NET 8.0 运行时
- 管理员权限（安装/卸载服务需要）

### 安装服务

```powershell
# 基本安装（自动检测同级目录的 DaemonKit）
DaemonGuard.exe --install

# 指定 DaemonKit.exe 路径
DaemonGuard.exe --install "C:\Programs\DaemonKit\DaemonKit.exe"
```

安装后服务自动配置为**开机自启动**，并设置 SCM 故障恢复策略。

### 管理服务

```powershell
# 启动
net start DaemonGuard

# 停止
net stop DaemonGuard

# 查看状态
DaemonGuard.exe --status

# 卸载
DaemonGuard.exe --uninstall
```

### 调试模式

不安装为服务，直接以控制台模式运行（用于开发调试）：

```powershell
DaemonGuard.exe
```

## 配置

通过 `appsettings.json` 配置守护参数：

```json
{
  "Guard": {
    "TargetExePath": "C:\\Programs\\DaemonKit\\DaemonKit.exe",
    "Arguments": null,
    "WorkingDirectory": null,
    "CheckIntervalSeconds": 5,
    "RestartDelaySeconds": 3,
    "MaxConsecutiveRestarts": 5,
    "CooldownSeconds": 60,
    "TargetProcessName": "DaemonKit"
  }
}
```

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `TargetExePath` | 同级目录下 `DaemonKit\DaemonKit.exe` | DaemonKit 可执行文件完整路径 |
| `Arguments` | null | 启动参数 |
| `WorkingDirectory` | exe 所在目录 | 工作目录 |
| `CheckIntervalSeconds` | 5 | 进程存活检测间隔（秒） |
| `RestartDelaySeconds` | 3 | 发现进程不存在后的重启延迟（秒） |
| `MaxConsecutiveRestarts` | 5 | 最大连续重启次数 |
| `CooldownSeconds` | 60 | 达到最大重启次数后的冷却时间（秒） |
| `TargetProcessName` | DaemonKit | 进程检测名称（不含 .exe） |

## 守护逻辑

### 检测循环

```
服务启动 → 等待10s（系统启动余量）
    │
    ▼
    ┌──→ 检测 DaemonKit 进程
    │    │
    │    ├── 存活 → 重置连续重启计数 → 等待 CheckInterval → ↑
    │    │
    │    └── 不存在
    │         │
    │         ├── 连续重启 ≥ 上限？
    │         │   ├── 是 + 冷却未到 → 跳过 → 等待 → ↑
    │         │   └── 是 + 冷却已过 → 重置计数
    │         │
    │         ├── 无用户登录？ → 跳过 → 等待 → ↑
    │         ├── exe 不存在？ → 记录错误 → 等待 → ↑
    │         │
    │         └── 延迟 RestartDelay 秒
    │              │
    │              ├── 延迟期间进程已启动 → 取消 → ↑
    │              └── 在用户会话中启动 → 等待 → ↑
    │
    └──────────────────────────────────────
```

### Session 0 进程启动流程

1. `WTSGetActiveConsoleSessionId()` — 获取活跃的控制台会话 ID
2. `WTSQueryUserToken()` — 获取该会话的用户令牌
3. `DuplicateTokenEx()` — 复制为主令牌
4. `CreateEnvironmentBlock()` — 创建用户环境变量块
5. `CreateProcessAsUser()` — 在 `winsta0\default` 桌面创建进程

## DaemonKit 集成

DaemonKit 在 `App.xaml.cs` 的 `OnStartup` 中自动调用 `GuardServiceHelper.EnsureGuardServiceRunning()`：

- 如果服务未安装 → 仅记录日志，不影响启动
- 如果服务已停止 → 尝试通过 `sc.exe start` 启动
- 如果服务已运行 → 无操作

### GuardServiceHelper API

```csharp
// 检查服务是否已安装
bool installed = GuardServiceHelper.IsServiceInstalled();

// 检查服务是否运行中
bool running = GuardServiceHelper.IsServiceRunning();

// 尝试启动服务
bool success = GuardServiceHelper.TryStartService();

// 获取状态文本（"运行中" / "已停止" / "未安装"）
string status = GuardServiceHelper.GetServiceStatusText();
```

## 编译

```powershell
# 编译
dotnet build DaemonGuard\DaemonGuard.csproj

# 发布单文件
dotnet publish DaemonGuard\DaemonGuard.csproj -c Release
```

## 文件清单

| 文件 | 说明 |
|------|------|
| `DaemonGuard.csproj` | 项目文件（.NET 8.0 Worker Service） |
| `Program.cs` | 入口点，处理命令行参数和服务宿主 |
| `GuardWorker.cs` | 核心守护工作线程（BackgroundService） |
| `GuardOptions.cs` | 配置选项模型 |
| `ProcessLauncher.cs` | Session 0 隔离进程启动器（Win32 P/Invoke） |
| `ServiceInstaller.cs` | 服务安装/卸载工具（sc.exe 封装） |
| `appsettings.json` | 默认配置文件 |

## 注意事项

1. **安装/卸载需要管理员权限** — 以管理员身份运行命令提示符
2. **RDP 远程桌面** — `WTSGetActiveConsoleSessionId` 返回的是物理控制台会话，RDP 会话可能不是活跃控制台
3. **多用户场景** — 仅在物理控制台会话中启动 DaemonKit
4. **日志** — 服务运行日志写入 Windows 事件日志（应用程序 → DaemonGuard），控制台模式写入标准输出
