#pragma once

#include "IWatermarkRenderer.h"
#include "WatermarkConfig.h"
#include <d3d11.h>
#include <string>
#include <vector>
#include <chrono>

namespace LicHper {

// 透明窗口覆盖渲染器
// 创建一个独立的透明窗口覆盖在宿主窗口上方
class OverlayRenderer : public IWatermarkRenderer {
public:
    OverlayRenderer();
    virtual ~OverlayRenderer();
    
    // IWatermarkRenderer 接口实现
    bool Initialize(HWND hWndHost) override;
    void UpdateConfig(const WatermarkConfig& config) override;
    void RunRenderLoop() override;
    void Shutdown() override;
    RenderMode GetMode() const override { return RenderMode::Overlay; }
    bool IsRunning() const override { return m_running; }
    void SetExitCallback(ExitCallback callback) override { m_exitCallback = callback; }
    
    // 获取 D3D 设备（供外部使用）
    ID3D11Device* GetDevice() const { return m_pd3dDevice; }
    ID3D11DeviceContext* GetDeviceContext() const { return m_pd3dDeviceContext; }
    
private:
    // DirectX 资源
    ID3D11Device* m_pd3dDevice = nullptr;
    ID3D11DeviceContext* m_pd3dDeviceContext = nullptr;
    IDXGISwapChain* m_pSwapChain = nullptr;
    ID3D11RenderTargetView* m_mainRenderTargetView = nullptr;
    
    // 水印图片纹理
    ID3D11ShaderResourceView* m_pWatermarkTexture = nullptr;
    int m_watermarkWidth = 0;
    int m_watermarkHeight = 0;
    bool m_hasWatermarkImage = false;
    
    // 窗口
    HWND m_hwnd = nullptr;
    HWND m_hwndHost = nullptr;
    WNDCLASSEXW m_wc = {};
    
    // 配置
    WatermarkConfig m_config;
    
    // 状态
    bool m_running = false;
    bool m_initialized = false;
    ExitCallback m_exitCallback = nullptr;
    
    // 动画状态
    ImVec2 m_titlePosition = ImVec2(0, 0);
    ImVec2 m_titleVelocity = ImVec2(1, 1);
    
    // 字体
    ImFont* m_font = nullptr;
    ImFont* m_titleFont = nullptr;
    
    // 时间
    std::chrono::high_resolution_clock::time_point m_startTime;
    
    // 私有方法
    bool CreateOverlayWindow();
    bool CreateDeviceD3D();
    void CleanupDeviceD3D();
    void CreateRenderTarget();
    void CleanupRenderTarget();
    bool LoadWatermarkTexture();
    void SetupImGui();
    void CleanupImGui();
    
    // 渲染方法
    void RenderFrame();
    void RenderWatermarkImage(float windowWidth, float windowHeight);
    void RenderWatermarkText(const std::string& text, float windowWidth, float windowHeight);
    void RenderLicenseWindow();
    
    // 工具方法
    std::string FormatCountdown(int remainSeconds);
    std::string ProcessWatermarkText();
    
    // 窗口过程
    static LRESULT WINAPI WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);
    static OverlayRenderer* s_instance;
};

} // namespace LicHper
