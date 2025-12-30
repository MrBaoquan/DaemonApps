#pragma once

#include <d3d11.h>
#include <dxgi.h>
#include <functional>

namespace LicHper {

// Present 回调函数类型
// 返回 true 表示已处理，跳过原始 Present
// 返回 false 表示继续执行原始 Present
using PresentCallback = std::function<void(IDXGISwapChain* pSwapChain)>;
using ResizeBuffersCallback = std::function<void(IDXGISwapChain* pSwapChain, UINT BufferCount, 
    UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags)>;

// DXGI Hook 管理器
// 用于 Hook IDXGISwapChain::Present 和 ResizeBuffers
class DXGIHook {
public:
    static DXGIHook& Instance();
    
    // 初始化 Hook（必须在目标程序已创建 D3D 设备后调用）
    bool Initialize();
    
    // 卸载 Hook
    void Shutdown();
    
    // 检查是否已初始化
    bool IsInitialized() const { return m_initialized; }
    
    // 设置回调
    void SetPresentCallback(PresentCallback callback) { m_presentCallback = callback; }
    void SetResizeBuffersCallback(ResizeBuffersCallback callback) { m_resizeCallback = callback; }
    
    // 清除回调（在渲染器销毁时调用）
    void ClearCallbacks() { m_presentCallback = nullptr; m_resizeCallback = nullptr; }
    
    // 获取当前 SwapChain 和 Device
    IDXGISwapChain* GetSwapChain() const { return m_pCapturedSwapChain; }
    ID3D11Device* GetDevice() const { return m_pCapturedDevice; }
    ID3D11DeviceContext* GetDeviceContext() const { return m_pCapturedContext; }
    
private:
    DXGIHook() = default;
    ~DXGIHook() = default;
    DXGIHook(const DXGIHook&) = delete;
    DXGIHook& operator=(const DXGIHook&) = delete;
    
    // Hook 函数
    static HRESULT WINAPI HookedPresent(IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags);
    static HRESULT WINAPI HookedResizeBuffers(IDXGISwapChain* pSwapChain, UINT BufferCount,
        UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags);
    
    // 获取 SwapChain VTable
    bool GetSwapChainVTable(void** pVTable);
    
    // 原始函数指针
    using PresentFn = HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT);
    using ResizeBuffersFn = HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
    
    static PresentFn s_originalPresent;
    static ResizeBuffersFn s_originalResizeBuffers;
    
    // 回调
    PresentCallback m_presentCallback;
    ResizeBuffersCallback m_resizeCallback;
    
    // 捕获的设备
    IDXGISwapChain* m_pCapturedSwapChain = nullptr;
    ID3D11Device* m_pCapturedDevice = nullptr;
    ID3D11DeviceContext* m_pCapturedContext = nullptr;
    
    bool m_initialized = false;
};

} // namespace LicHper
