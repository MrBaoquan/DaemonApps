#pragma once

#include "IWatermarkRenderer.h"
#include "WatermarkConfig.h"
#include "WatermarkRenderer.h"
#include "D3D12WatermarkRenderer.h"
#include "../Hooks/DXGIHook.h"
#include <d3d11.h>
#include <atomic>
#include <mutex>
#include <thread>
#include <memory>

namespace LicHper {

// DirectX Hook 渲染器
// 通过 Hook DXGI Present 在宿主进程的渲染流程中注入水印
class HookRenderer : public IWatermarkRenderer {
public:
    HookRenderer();
    virtual ~HookRenderer();
    
    // IWatermarkRenderer 接口实现
    bool Initialize(HWND hWndHost) override;
    void UpdateConfig(const WatermarkConfig& config) override;
    void RunRenderLoop() override;
    void Shutdown() override;
    RenderMode GetMode() const override { return RenderMode::Hook; }
    bool IsRunning() const override { return m_running; }
    void SetExitCallback(ExitCallback callback) override { m_exitCallback = callback; }
    void SetFallbackCallback(FallbackCallback callback) override { m_fallbackCallback = callback; }
    bool NeedsFallback() const override { return m_needsFallback; }
    
    // 检测宿主是否使用 DirectX
    static bool IsHostUsingDirectX();
    
private:
    // 配置
    WatermarkConfig m_config;
    std::mutex m_configMutex;
    
    // 状态
    std::atomic<bool> m_running{ false };
    std::atomic<bool> m_initialized{ false };
    std::atomic<bool> m_needsFallback{ false };  // 需要回退到 Overlay 模式
    ExitCallback m_exitCallback = nullptr;
    FallbackCallback m_fallbackCallback = nullptr;
    
    // 窗口
    HWND m_hwndHost = nullptr;
    HWND m_hwndTarget = nullptr;  // Hook 目标窗口
    
    // D3D11 共享水印渲染组件
    std::unique_ptr<WatermarkRenderer> m_watermarkRenderer;
    
    // D3D12 原生水印渲染器
    std::unique_ptr<D3D12WatermarkRenderer> m_d3d12Renderer;
    bool m_usingD3D12 = false;  // 是否使用 D3D12 渲染
    
    // ImGui 资源 (D3D11)
    ID3D11RenderTargetView* m_pRenderTargetView = nullptr;
    
    // 授权窗口状态
    bool m_showLicenseWindow = false;
    
    // 时间
    std::chrono::high_resolution_clock::time_point m_startTime;
    
    // 超时检查线程
    std::thread m_timeoutThread;
    
    // 回调方法
    void OnPresent(IDXGISwapChain* pSwapChain);
    void OnResizeBuffers(IDXGISwapChain* pSwapChain, UINT BufferCount,
        UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags);
    
    // 输入处理 - 使用 SetWindowLongPtr 直接替换窗口过程
    void ProcessInput();
    void InstallInputHook();
    void UninstallInputHook();
    static LRESULT CALLBACK WndProcHook(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam);
    static HookRenderer* s_instance;
    WNDPROC m_originalWndProc = nullptr;  // 原始窗口过程
    bool m_inputHookInstalled = false;
    
    // 初始化 ImGui（在 Hook 回调中首次调用）
    bool InitializeImGui(IDXGISwapChain* pSwapChain);
    bool InitializeD3D12Renderer(IDXGISwapChain* pSwapChain);
    void CleanupImGui();
    
    // 渲染方法
    void RenderWatermark();
    void RenderWatermarkD3D12(IDXGISwapChain* pSwapChain);
};

} // namespace LicHper
