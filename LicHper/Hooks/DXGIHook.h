#pragma once

#include <d3d11.h>
#include <d3d12.h>
#include <dxgi.h>
#include <dxgi1_4.h>
#include <functional>
#include <vector>
#include <mutex>

namespace LicHper {

// Present 回调函数类型
using PresentCallback = std::function<void(IDXGISwapChain* pSwapChain)>;
using ResizeBuffersCallback = std::function<void(IDXGISwapChain* pSwapChain, UINT BufferCount, 
    UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags)>;

// DXGI Hook 管理器
// 用于 Hook IDXGISwapChain::Present 和 ResizeBuffers
// 支持 D3D11 和 D3D12（原生）
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
    
    // 获取当前 SwapChain
    IDXGISwapChain* GetSwapChain() const { return m_pCapturedSwapChain; }
    
    // D3D11 相关
    ID3D11Device* GetD3D11Device() const { return m_pD3D11Device; }
    ID3D11DeviceContext* GetD3D11DeviceContext() const { return m_pD3D11Context; }
    
    // D3D12 相关
    ID3D12Device* GetD3D12Device() const { return m_pD3D12Device; }
    ID3D12CommandQueue* GetD3D12CommandQueue() const { return m_pD3D12CommandQueue; }
    
    // 兼容旧接口
    ID3D11Device* GetDevice() const { return m_pD3D11Device; }
    ID3D11DeviceContext* GetDeviceContext() const { return m_pD3D11Context; }
    
    // 检查是否使用 D3D12
    bool IsD3D12() const { return m_isD3D12; }
    
    // 获取当前后台缓冲区索引（D3D12 模式）
    UINT GetCurrentBackBufferIndex() const { return m_currentBackBufferIndex; }
    
private:
    DXGIHook() = default;
    ~DXGIHook() = default;
    DXGIHook(const DXGIHook&) = delete;
    DXGIHook& operator=(const DXGIHook&) = delete;
    
    // Hook 函数
    static HRESULT WINAPI HookedPresent(IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags);
    static HRESULT WINAPI HookedResizeBuffers(IDXGISwapChain* pSwapChain, UINT BufferCount,
        UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags);
    
    // DXGI Factory Hook 用于捕获 D3D12 命令队列
    static HRESULT WINAPI HookedCreateSwapChainForHwnd(
        IDXGIFactory2* pFactory,
        IUnknown* pDevice,
        HWND hWnd,
        const DXGI_SWAP_CHAIN_DESC1* pDesc,
        const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* pFullscreenDesc,
        IDXGIOutput* pRestrictToOutput,
        IDXGISwapChain1** ppSwapChain);
    
    // 获取 SwapChain VTable
    bool GetSwapChainVTable(void** pVTable);
    
    // 初始化设备（从 SwapChain 获取）
    bool InitializeDevice(IDXGISwapChain* pSwapChain);
    
    // Hook DXGI Factory
    bool HookDXGIFactory();
    
    // 原始函数指针
    using PresentFn = HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT);
    using ResizeBuffersFn = HRESULT(WINAPI*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
    using CreateSwapChainForHwndFn = HRESULT(WINAPI*)(IDXGIFactory2*, IUnknown*, HWND,
        const DXGI_SWAP_CHAIN_DESC1*, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
    
    static PresentFn s_originalPresent;
    static ResizeBuffersFn s_originalResizeBuffers;
    static CreateSwapChainForHwndFn s_originalCreateSwapChainForHwnd;
    
    // 回调
    PresentCallback m_presentCallback;
    ResizeBuffersCallback m_resizeCallback;
    
    // 捕获的 SwapChain
    IDXGISwapChain* m_pCapturedSwapChain = nullptr;
    
    // D3D11 设备
    ID3D11Device* m_pD3D11Device = nullptr;
    ID3D11DeviceContext* m_pD3D11Context = nullptr;
    
    // D3D12 设备和命令队列（从 CreateSwapChainForHwnd 捕获或自己创建）
    bool m_isD3D12 = false;
    ID3D12Device* m_pD3D12Device = nullptr;
    ID3D12CommandQueue* m_pD3D12CommandQueue = nullptr;
    bool m_ownsCommandQueue = false;  // 是否是我们自己创建的命令队列
    
    // 后台缓冲区信息
    static const int MAX_BACK_BUFFERS = 4;
    UINT m_backBufferCount = 0;
    UINT m_currentBackBufferIndex = 0;
    
    bool m_initialized = false;
    bool m_deviceInitialized = false;
    bool m_factoryHooked = false;
    
    std::mutex m_mutex;
};

} // namespace LicHper
