# DaemonApps Copilot Instructions

## Project Overview

Windows process orchestration and licensing suite with three main components:

-   **DaemonKit** - WPF process orchestration tool (C#, .NET 8, x64) - "运维管家"
-   **AuthAssistant** - Avalonia UI licensing client (C#, .NET 6, x64)
-   **LicHper** - Native watermark rendering DLL via DLL injection (C++, VS2022)
-   **DNHper** - Shared C# utilities library (Windows API wrappers, logging)

## Architecture

### DaemonKit Process Tree Model

Central data structure is hierarchical `ProcessItem` tree in [DaemonKit/Models/ProcessItem.cs](DaemonKit/Models/ProcessItem.cs):

```
Root Node (IsSuperRoot=true)
├── Level 2 Process Nodes (user-selectable)
│   ├── Level 3+ Child Processes (auto-included with parent)
│   └── ProcessMetaData (Name, Path, Args, WorkingDirectory)
└── ScheduleItems (tasks attached to each node)
```

**Key Model Classes:**

-   `ProcessItem` - ReactiveUI observable tree node with process metadata, scheduling, and lifecycle management
-   `ProcessItemWithLevel` - Wrapper for UI display with indent level
-   `ScheduleTaskConfig` - Task definitions (see Schedule System below)

**Data Flow:**

1. User configures tree via WPF TreeView in `Views/MainWindow.xaml`
2. MainViewModel serializes tree to `Applications/Demo/*.json` (default project)
3. `Core/ProcManager.cs` launches/monitors processes using `DNHper.WinAPI`
4. `Core/ScheduleTaskEngine.cs` executes tasks based on triggers

### Schedule Task System

Advanced task scheduler supporting 4 trigger types ([任务计划功能重构说明.md](任务计划功能重构说明.md)):

-   **Daily** - Execute at specific time (HH:mm:ss)
-   **OncePerDayAfterStart** - Once per day after first launch + delay
-   **EveryStartupAfterDelay** - Every app launch + delay
-   **IntervalAfterStartup** - Repeat every X seconds after launch

**Task Actions:** StartProcess, RestartProcessTree, KillProcess, ShutdownSystem, RestartSystem, TakeScreenshot, MouseClick, SwitchPowerMode

**Implementation:** See [DaemonKit/Core/ScheduleTaskEngine.cs](DaemonKit/Core/ScheduleTaskEngine.cs) lines 1-683

### LicHper DLL Watermark System

Injects into target processes to render watermarks:

```
RenderManager → IWatermarkRenderer
                    ├── HookRenderer (DXGI Hook for D3D11/D3D12)
                    └── OverlayRenderer (Transparent window fallback)
```

**Critical D3D12 Limitation:** Must inject **before** SwapChain creation to capture CommandQueue. Post-injection apps (UE5) fallback to Overlay mode automatically.

**Config:** INI at `%USERPROFILE%\.authrc.ini` - see [LicHper/测试配置示例.ini](LicHper/测试配置示例.ini)

## Build Commands

### Full Solution Build

```powershell
# From repo root
dotnet build DaemonApps.sln
```

### LicHper (C++ DLL)

```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper\LicHper.vcxproj' /p:Configuration=Release /p:Platform=x64 /m
```

**Post-build:** Auto-copies to `AuthAssistant/Costura64/` for Costura.Fody embedding.

### Platform Targets

-   DaemonKit: **x64** (primary), x86 supported
-   AuthAssistant: **x64 only** (depends on LicHper x64)
-   LicHper: **x64** (primary), Win32 build has known issues

See [BUILD_COMMANDS_REFERENCE.md](BUILD_COMMANDS_REFERENCE.md) for all component build commands.

## Code Conventions

### C# (.NET Projects)

-   **UI Pattern:** ReactiveUI MVVM - ViewModels inherit `ReactiveObject`
-   **Logging:** Use `DNHper.NLogger.Info/Warn/Error()` throughout
-   **Win32 APIs:** Always use `DNHper.WinAPI` wrappers, never direct P/Invoke
-   **Async:** Prefer `Observable.Timer` and reactive streams over `Task.Run`
-   **Chinese UI:** All user-facing strings in Chinese (labels, messages)
-   **Process Ops:** Use `WinAPI.OpenProcess()` for launching, `WinAPI.FindProcess()` for handles

**Example ViewModel Pattern:**

```csharp
public class MyViewModel : ReactiveObject
{
    private string _myProperty;
    public string MyProperty
    {
        get => _myProperty;
        set => this.RaiseAndSetIfChanged(ref _myProperty, value);
    }
}
```

### C++ (LicHper)

-   **Charset:** MultiByte (NOT Unicode) - use `PROCESSENTRY32` not `PROCESSENTRY32W`
-   **Standard:** C++20, `/utf-8` compiler flag for source encoding
-   **Namespace:** `LicHper`
-   **Logging:** `LOG_INFO`, `LOG_WARNING`, `LOG_ERROR` from `Logger.h`
-   **COM Safety:** Never `Release()` host's D3D devices, only resources we create
-   **ImGui:** Backends at `imgui/backends/` - only D3D11/D3D12 supported

## Key Patterns

### Process Tree Export/Import

Rules in [PROCESS_TREE_EXPORT_IMPORT_RULES.md](PROCESS_TREE_EXPORT_IMPORT_RULES.md):

-   **Export:** Only second-level nodes are user-selectable; children auto-included
-   **Virtual Root:** Export creates temp root with selected nodes as children
-   **Import:** Merges nodes preserving GUIDs for update detection

### DXGI Hook Sequence

1. Hook `IDXGIFactory::CreateSwapChainForHwnd` → capture D3D12 CommandQueue
2. Hook `IDXGISwapChain::Present` → render ImGui overlay before original call
3. Hook `IDXGISwapChain::ResizeBuffers` → recreate render targets
4. Error `DXGI_ERROR_DEVICE_REMOVED` = wrong CommandQueue (D3D12 only)

### Schedule Task Execution Context

[DaemonKit/Core/ScheduleTaskEngine.cs](DaemonKit/Core/ScheduleTaskEngine.cs) provides `ScheduleTaskContext` to actions:

```csharp
var context = new ScheduleTaskContext
{
    Config = taskConfig,
    ProcessNode = targetNode,
    PowerSavingVM = powerSavingViewModelProvider?.Invoke()
};
```

## Important Files

| Path                                    | Purpose                            |
| --------------------------------------- | ---------------------------------- |
| `DaemonKit/Models/ProcessItem.cs`       | Core tree model (1114 lines)       |
| `DaemonKit/Core/ScheduleTaskEngine.cs`  | Task scheduler engine (683 lines)  |
| `DaemonKit/Core/ProcManager.cs`         | Process lifecycle management       |
| `DaemonKit/ViewModels/MainViewModel.cs` | Primary UI logic (partial class)   |
| `DNHper/` (project)                     | Shared utilities (WinAPI, NLogger) |
| `LicHper/Rendering/RenderManager.cpp`   | Watermark mode selection           |
| `LicHper/Hooks/DXGIHook.cpp`            | DXGI hooking implementation        |
| `任务计划功能重构说明.md`               | Schedule system Chinese docs       |
| `PROCESS_TREE_EXPORT_IMPORT_RULES.md`   | Export/import algorithm            |

## Testing

-   **DaemonKit:** Run from VS, test process tree in `Applications/Demo/`
-   **LicHper:** Inject into D3D11/D3D12 app, check logs at injection directory
