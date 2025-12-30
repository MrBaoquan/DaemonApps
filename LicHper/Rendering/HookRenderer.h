#pragma once

#include "IWatermarkRenderer.h"
#include "WatermarkConfig.h"
#include "../Hooks/DXGIHook.h"
#include <d3d11.h>
#include <atomic>
#include <mutex>
#include <thread>

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
    
    // 检测宿主是否使用 DirectX
    static bool IsHostUsingDirectX();
    
private:
    // 配置
    WatermarkConfig m_config;
    std::mutex m_configMutex;
    
    // 状态
    std::atomic<bool> m_running{ false };
    std::atomic<bool> m_initialized{ false };
    std::atomic<bool> m_imguiInitialized{ false };
    ExitCallback m_exitCallback = nullptr;
    
    // 窗口
    HWND m_hwndHost = nullptr;
    
    // ImGui 资源
    ID3D11RenderTargetView* m_pRenderTargetView = nullptr;
    
    // 水印纹理
    ID3D11ShaderResourceView* m_pWatermarkTexture = nullptr;
    int m_watermarkWidth = 0;
    int m_watermarkHeight = 0;
    bool m_hasWatermarkImage = false;
    
    // 字体
    ImFont* m_font = nullptr;
    ImFont* m_titleFont = nullptr;
    
    // 动画状态
    ImVec2 m_titlePosition{ 0, 0 };
    ImVec2 m_titleVelocity{ 1, 1 };
    
    // 时间
    std::chrono::high_resolution_clock::time_point m_startTime;
    
    // 超时检查线程
    std::thread m_timeoutThread;
    
    // 回调方法
    void OnPresent(IDXGISwapChain* pSwapChain);
    void OnResizeBuffers(IDXGISwapChain* pSwapChain, UINT BufferCount,
        UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags);
    
    // 初始化 ImGui（在 Hook 回调中首次调用）
    bool InitializeImGui(IDXGISwapChain* pSwapChain);
    void CleanupImGui();
    
    // 加载水印纹理
    bool LoadWatermarkTexture(ID3D11Device* pDevice);
    
    // 渲染方法
    void RenderWatermark();
    void RenderWatermarkImage(float windowWidth, float windowHeight);
    void RenderWatermarkText(const std::string& text, float windowWidth, float windowHeight);
    
    // 工具方法
    std::string ProcessWatermarkText();
    std::string FormatCountdown(int seconds);
};

} // namespace LicHper
