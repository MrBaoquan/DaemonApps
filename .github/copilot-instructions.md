# DaemonApps Copilot Instructions

## Project Overview

A Windows application suite for process management and software licensing, consisting of:

-   **DaemonKit** - WPF process orchestration tool (C#, .NET 9, x86)
-   **AuthAssistant** - Avalonia UI licensing client (C#, .NET 6, x64)
-   **LicHper** - Native DLL for watermark rendering via DLL injection (C++, VS2022)

## Architecture

### LicHper DLL (Core Focus)

Injects into target processes to render watermarks using two modes:

```
RenderManager → IWatermarkRenderer
                    ├── HookRenderer (DXGI Hook for D3D11 apps)
                    └── OverlayRenderer (Transparent window fallback)
```

**Key Components:**

-   `Hooks/DXGIHook.cpp` - Hooks `IDXGISwapChain::Present` via MinHook
-   `Rendering/WatermarkRenderer.cpp` - Shared ImGui rendering logic
-   `Rendering/D3D12WatermarkRenderer.cpp` - D3D12 native rendering (requires original CommandQueue)

**D3D12 Limitation:** Cannot render via Hook mode if DLL is injected after SwapChain creation (UE5 apps). Falls back to Overlay mode automatically.

### Configuration

INI files at `%USERPROFILE%\.authrc.ini` - see `LicHper/测试配置示例.ini` for format.

## Build Commands

### LicHper (C++ DLL)

```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' `
  'LicHper\LicHper.vcxproj' /p:Configuration=Debug /p:Platform=x64 /m /v:minimal
```

Post-build copies DLL to `AuthAssistant/Costura64/` for embedding.

### .NET Projects

```bash
dotnet build AuthAssistant/AuthAssistant.csproj
dotnet build DaemonKit/DaemonKit.csproj
```

## Code Conventions

### C++ (LicHper)

-   **Charset:** MultiByte (not Unicode) - use `PROCESSENTRY32` not `PROCESSENTRY32W`
-   **C++ Standard:** C++20, `/utf-8` flag for source encoding
-   **Namespace:** `LicHper`
-   **Logging:** Use `LOG_INFO`, `LOG_WARNING`, `LOG_ERROR` macros from `Logger.h`
-   **COM Objects:** Don't Release host's devices, only our created resources
-   **ImGui:** Backends at `imgui/backends/` - D3D11 and D3D12 supported

### C# Projects

-   **UI Framework:** Avalonia 11.x for AuthAssistant, WPF for DaemonKit
-   **Embedding:** Costura.Fody embeds LicHper.dll into AuthAssistant

## Key Patterns

### Renderer Mode Detection

```cpp
// RenderManager::DetectBestMode()
// D3D11/D3D12 detection: Check if d3d11.dll/dxgi.dll loaded
// D3D12 apps without captured CommandQueue → fallback to Overlay
```

### DXGI Hook Flow

1. Hook `CreateSwapChainForHwnd` to capture D3D12 CommandQueue (if injected early)
2. Hook `Present` and `ResizeBuffers` on SwapChain VTable
3. Render ImGui overlay before calling original Present

### Error Handling

GPU crashes (`DXGI_ERROR_DEVICE_REMOVED`) indicate CommandQueue mismatch - D3D12 SwapChains are bound to their creation CommandQueue.

## Important Files

| Path                                  | Purpose                     |
| ------------------------------------- | --------------------------- |
| `LicHper/Rendering/RenderManager.cpp` | Entry point, mode selection |
| `LicHper/Hooks/DXGIHook.cpp`          | DXGI hooking implementation |
| `LicHper/Rendering/WatermarkConfig.h` | Config structure definition |
| `AuthAssistant/ViewModels/`           | MVVM ViewModels             |
| `DaemonKit/Core/`                     | Process management logic    |

## Testing

Inject LicHper.dll into a DirectX application. Check logs at injection location for diagnostics.
