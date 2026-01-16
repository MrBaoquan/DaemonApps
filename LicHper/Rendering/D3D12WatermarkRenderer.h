#pragma once

#include <d3d12.h>
#include <dxgi1_4.h>
#include <chrono>
#include <mutex>
#include <string>
#include <vector>
#include <functional>
#include <memory>

// ImGui 前置声明
struct ImFont;
typedef unsigned short ImWchar;

namespace LicHper {

struct WatermarkConfig;
class ImGuiWatermarkCore;

// D3D12 原生水印渲染器
// 使用 ImGui D3D12 后端直接在 D3D12 SwapChain 上渲染
class D3D12WatermarkRenderer {
public:
    D3D12WatermarkRenderer();
    ~D3D12WatermarkRenderer();
    
    // 初始化 D3D12 渲染资源
    bool Initialize(IDXGISwapChain* pSwapChain, ID3D12Device* pDevice, ID3D12CommandQueue* pCommandQueue, HWND hWnd);
    
    // 清理资源
    void Shutdown();
    
    // 检查是否已初始化
    bool IsInitialized() const { return m_initialized; }
    
    // 更新配置
    void UpdateConfig(const WatermarkConfig& config);
    
    // 设置开始时间（用于计时）
    void SetStartTime(std::chrono::high_resolution_clock::time_point startTime);
    
    // 渲染水印（在 Present 之前调用）
    void Render(IDXGISwapChain* pSwapChain);
    
    // 处理窗口大小变化
    void OnResize(UINT width, UINT height);
    
    // 显示/隐藏授权窗口
    void ShowLicenseWindow(bool show) { m_showLicenseWindow = show; }
    bool IsLicenseWindowVisible() const { return m_showLicenseWindow; }
    
    // 设置退出回调
    void SetExitCallback(std::function<void()> callback) { m_exitCallback = callback; }
    
private:
    // 创建 D3D12 资源
    bool CreateRenderTargets(IDXGISwapChain* pSwapChain);
    bool CreateCommandObjects();
    bool CreateDescriptorHeaps();
    
    // 清理资源
    void CleanupRenderTargets();
    
    // 等待 GPU 完成
    void WaitForGpu();
    
    // D3D12 设备和命令队列（从 DXGIHook 获取，不要 Release）
    ID3D12Device* m_pDevice = nullptr;
    ID3D12CommandQueue* m_pCommandQueue = nullptr;
    
    // 我们创建的资源（需要 Release）
    ID3D12CommandAllocator* m_pCommandAllocators[4] = {};
    ID3D12GraphicsCommandList* m_pCommandList = nullptr;
    ID3D12DescriptorHeap* m_pRtvHeap = nullptr;
    ID3D12DescriptorHeap* m_pSrvHeap = nullptr;
    ID3D12Fence* m_pFence = nullptr;
    HANDLE m_fenceEvent = nullptr;
    UINT64 m_fenceValues[4] = {};
    UINT64 m_currentFenceValue = 0;
    
    // 后台缓冲区
    static const int MAX_BACK_BUFFERS = 4;
    ID3D12Resource* m_pRenderTargets[MAX_BACK_BUFFERS] = {};
    D3D12_CPU_DESCRIPTOR_HANDLE m_rtvHandles[MAX_BACK_BUFFERS] = {};
    UINT m_backBufferCount = 0;
    UINT m_rtvDescriptorSize = 0;
    
    // 状态
    bool m_initialized = false;
    bool m_imguiInitialized = false;
    HWND m_hWnd = nullptr;
    DXGI_FORMAT m_rtvFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
    
    // 配置
    WatermarkConfig* m_pConfig = nullptr;
    std::mutex m_configMutex;
    
    // 共享水印渲染核心
    std::unique_ptr<ImGuiWatermarkCore> m_watermarkCore;
    
    // 时间
    std::chrono::high_resolution_clock::time_point m_startTime;
    
    // 授权窗口（默认隐藏，与 D3D11 模式保持一致）
    bool m_showLicenseWindow = false;
    std::function<void()> m_exitCallback;
    
    // 字体
    ImFont* m_font = nullptr;       // UI 字体（18px）
    ImFont* m_titleFont = nullptr;  // 水印字体（可变大小）
    std::vector<ImWchar> m_watermarkGlyphRanges;
};

} // namespace LicHper
