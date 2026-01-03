#include "DXGIHook.h"
#include "../Rendering/Logger.h"
#include <MinHook.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi.h>
#include <dxgi1_4.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

namespace LicHper {

// 静态成员初始化
DXGIHook::PresentFn DXGIHook::s_originalPresent = nullptr;
DXGIHook::ResizeBuffersFn DXGIHook::s_originalResizeBuffers = nullptr;
DXGIHook::CreateSwapChainForHwndFn DXGIHook::s_originalCreateSwapChainForHwnd = nullptr;

// 全局变量用于在 Hook 之前保存命令队列
static ID3D12CommandQueue* g_pCapturedCommandQueue = nullptr;
static ID3D12Device* g_pCapturedD3D12Device = nullptr;

DXGIHook& DXGIHook::Instance() {
    static DXGIHook instance;
    return instance;
}

bool DXGIHook::Initialize() {
    if (m_initialized) return true;
    
    LOG_INFO("DXGIHook: Initializing...");
    
    // 初始化 MinHook
    MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
        LOG_ERROR("DXGIHook: MH_Initialize failed with status {}", (int)status);
        return false;
    }
    
    // 首先尝试 Hook DXGI Factory（用于捕获 D3D12 命令队列）
    HookDXGIFactory();
    
    LOG_INFO("DXGIHook: Getting SwapChain VTable...");
    
    // 获取 SwapChain VTable
    void* pVTable[18] = {};
    if (!GetSwapChainVTable(pVTable)) {
        LOG_ERROR("DXGIHook: Failed to get SwapChain VTable");
        MH_Uninitialize();
        return false;
    }
    
    LOG_INFO("DXGIHook: Present address: 0x{:X}", reinterpret_cast<uintptr_t>(pVTable[8]));
    LOG_INFO("DXGIHook: ResizeBuffers address: 0x{:X}", reinterpret_cast<uintptr_t>(pVTable[13]));
    
    // Hook Present (VTable 索引 8)
    status = MH_CreateHook(pVTable[8], &HookedPresent, reinterpret_cast<void**>(&s_originalPresent));
    if (status != MH_OK) {
        LOG_ERROR("DXGIHook: Failed to hook Present, status {}", (int)status);
        MH_Uninitialize();
        return false;
    }
    
    // Hook ResizeBuffers (VTable 索引 13)
    status = MH_CreateHook(pVTable[13], &HookedResizeBuffers, reinterpret_cast<void**>(&s_originalResizeBuffers));
    if (status != MH_OK) {
        LOG_ERROR("DXGIHook: Failed to hook ResizeBuffers, status {}", (int)status);
        MH_RemoveHook(pVTable[8]);
        MH_Uninitialize();
        return false;
    }
    
    // 启用所有 Hook
    status = MH_EnableHook(MH_ALL_HOOKS);
    if (status != MH_OK) {
        LOG_ERROR("DXGIHook: Failed to enable hooks, status {}", (int)status);
        MH_RemoveHook(pVTable[8]);
        MH_RemoveHook(pVTable[13]);
        MH_Uninitialize();
        return false;
    }
    
    LOG_INFO("DXGIHook: Hooks installed successfully");
    
    m_initialized = true;
    return true;
}

bool DXGIHook::HookDXGIFactory() {
    if (m_factoryHooked) return true;
    
    LOG_INFO("DXGIHook: Attempting to hook DXGI Factory...");
    
    // 创建临时 Factory 来获取 VTable
    IDXGIFactory2* pFactory = nullptr;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&pFactory));
    if (FAILED(hr)) {
        LOG_WARNING("DXGIHook: Failed to create DXGI Factory for hooking, hr=0x{:X}", (unsigned int)hr);
        return false;
    }
    
    // 获取 Factory VTable
    void** pVTable = *reinterpret_cast<void***>(pFactory);
    
    // CreateSwapChainForHwnd 是 IDXGIFactory2 的第 15 个方法（从 0 开始）
    // IDXGIFactory: QueryInterface, AddRef, Release, SetPrivateData, SetPrivateDataInterface, 
    //               GetPrivateData, GetParent, EnumAdapters, MakeWindowAssociation, GetWindowAssociation,
    //               CreateSwapChain, CreateSoftwareAdapter
    // IDXGIFactory1: EnumAdapters1, IsCurrent
    // IDXGIFactory2: IsWindowedStereoEnabled, CreateSwapChainForHwnd (index 15)
    void* pCreateSwapChainForHwnd = pVTable[15];
    
    LOG_INFO("DXGIHook: CreateSwapChainForHwnd address: 0x{:X}", reinterpret_cast<uintptr_t>(pCreateSwapChainForHwnd));
    
    MH_STATUS status = MH_CreateHook(pCreateSwapChainForHwnd, &HookedCreateSwapChainForHwnd, 
        reinterpret_cast<void**>(&s_originalCreateSwapChainForHwnd));
    
    pFactory->Release();
    
    if (status != MH_OK) {
        LOG_WARNING("DXGIHook: Failed to hook CreateSwapChainForHwnd, status {}", (int)status);
        return false;
    }
    
    m_factoryHooked = true;
    LOG_INFO("DXGIHook: DXGI Factory hooked successfully");
    return true;
}

void DXGIHook::Shutdown() {
    if (!m_initialized) return;
    
    LOG_INFO("DXGIHook: Shutting down...");
    
    // 禁用并移除所有 Hook
    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();
    
    s_originalPresent = nullptr;
    s_originalResizeBuffers = nullptr;
    s_originalCreateSwapChainForHwnd = nullptr;
    
    // 释放我们创建的命令队列
    if (m_ownsCommandQueue && m_pD3D12CommandQueue) {
        m_pD3D12CommandQueue->Release();
        LOG_INFO("DXGIHook: Released our own CommandQueue");
    }
    
    // 不要 Release 宿主的设备，只清空指针
    m_pD3D11Device = nullptr;
    m_pD3D11Context = nullptr;
    m_pD3D12Device = nullptr;
    m_pD3D12CommandQueue = nullptr;
    m_pCapturedSwapChain = nullptr;
    m_ownsCommandQueue = false;
    
    g_pCapturedCommandQueue = nullptr;
    g_pCapturedD3D12Device = nullptr;
    
    m_isD3D12 = false;
    m_deviceInitialized = false;
    m_factoryHooked = false;
    m_initialized = false;
    
    LOG_INFO("DXGIHook: Shutdown complete");
}

bool DXGIHook::GetSwapChainVTable(void** pVTable) {
    // 创建临时窗口
    WNDCLASSEXA wc = { sizeof(WNDCLASSEXA), CS_CLASSDC, DefWindowProcA, 0L, 0L,
                      GetModuleHandle(nullptr), nullptr, nullptr, nullptr, nullptr,
                      "DXGIHookTemp", nullptr };
    RegisterClassExA(&wc);
    
    HWND hWnd = CreateWindowA(wc.lpszClassName, "", WS_OVERLAPPEDWINDOW,
        100, 100, 300, 300, nullptr, nullptr, wc.hInstance, nullptr);
    
    if (!hWnd) {
        UnregisterClassA(wc.lpszClassName, wc.hInstance);
        return false;
    }
    
    // 创建临时 D3D11 设备和 SwapChain
    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 1;
    sd.BufferDesc.Width = 2;
    sd.BufferDesc.Height = 2;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferDesc.RefreshRate.Numerator = 60;
    sd.BufferDesc.RefreshRate.Denominator = 1;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hWnd;
    sd.SampleDesc.Count = 1;
    sd.SampleDesc.Quality = 0;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    
    D3D_FEATURE_LEVEL featureLevel;
    ID3D11Device* pDevice = nullptr;
    ID3D11DeviceContext* pContext = nullptr;
    IDXGISwapChain* pSwapChain = nullptr;
    
    HRESULT hr = D3D11CreateDeviceAndSwapChain(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION,
        &sd, &pSwapChain, &pDevice, &featureLevel, &pContext);
    
    if (FAILED(hr)) {
        // 尝试 WARP 驱动
        hr = D3D11CreateDeviceAndSwapChain(
            nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0,
            nullptr, 0, D3D11_SDK_VERSION,
            &sd, &pSwapChain, &pDevice, &featureLevel, &pContext);
    }
    
    if (FAILED(hr)) {
        DestroyWindow(hWnd);
        UnregisterClassA(wc.lpszClassName, wc.hInstance);
        return false;
    }
    
    // 获取 VTable
    memcpy(pVTable, *reinterpret_cast<void***>(pSwapChain), sizeof(void*) * 18);
    
    // 清理临时资源
    pSwapChain->Release();
    pContext->Release();
    pDevice->Release();
    DestroyWindow(hWnd);
    UnregisterClassA(wc.lpszClassName, wc.hInstance);
    
    return true;
}

HRESULT WINAPI DXGIHook::HookedCreateSwapChainForHwnd(
    IDXGIFactory2* pFactory,
    IUnknown* pDevice,
    HWND hWnd,
    const DXGI_SWAP_CHAIN_DESC1* pDesc,
    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* pFullscreenDesc,
    IDXGIOutput* pRestrictToOutput,
    IDXGISwapChain1** ppSwapChain)
{
    LOG_INFO("DXGIHook: CreateSwapChainForHwnd intercepted!");
    
    // 检查 pDevice 是否是 D3D12 命令队列
    ID3D12CommandQueue* pCommandQueue = nullptr;
    HRESULT hrQuery = pDevice->QueryInterface(IID_PPV_ARGS(&pCommandQueue));
    
    if (SUCCEEDED(hrQuery) && pCommandQueue) {
        LOG_INFO("DXGIHook: D3D12 CommandQueue captured from CreateSwapChainForHwnd!");
        
        // 获取 D3D12 Device
        ID3D12Device* pD3D12Device = nullptr;
        pCommandQueue->GetDevice(IID_PPV_ARGS(&pD3D12Device));
        
        if (pD3D12Device) {
            // 保存到全局变量（因为 DXGIHook::Instance() 可能还没准备好）
            g_pCapturedCommandQueue = pCommandQueue;
            g_pCapturedD3D12Device = pD3D12Device;
            
            LOG_INFO("DXGIHook: D3D12 Device captured successfully");
        }
        
        // 不要 Release，我们保留引用
    }
    
    // 调用原始函数
    return s_originalCreateSwapChainForHwnd(pFactory, pDevice, hWnd, pDesc, 
        pFullscreenDesc, pRestrictToOutput, ppSwapChain);
}

HRESULT WINAPI DXGIHook::HookedPresent(IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags) {
    auto& hook = Instance();
    
    // 首次调用时初始化设备
    static bool s_initAttempted = false;
    if (!s_initAttempted) {
        s_initAttempted = true;
        hook.m_pCapturedSwapChain = pSwapChain;
        hook.InitializeDevice(pSwapChain);
    }
    
    // 更新当前后台缓冲区索引（D3D12 模式）
    if (hook.m_isD3D12) {
        IDXGISwapChain3* pSwapChain3 = nullptr;
        if (SUCCEEDED(pSwapChain->QueryInterface(IID_PPV_ARGS(&pSwapChain3)))) {
            hook.m_currentBackBufferIndex = pSwapChain3->GetCurrentBackBufferIndex();
            pSwapChain3->Release();
        }
    }
    
    // 调用回调
    if (hook.m_presentCallback) {
        hook.m_presentCallback(pSwapChain);
    }
    
    // 调用原始函数
    return s_originalPresent(pSwapChain, SyncInterval, Flags);
}

HRESULT WINAPI DXGIHook::HookedResizeBuffers(IDXGISwapChain* pSwapChain, UINT BufferCount,
    UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags) {
    
    auto& hook = Instance();
    
    // 调用回调
    if (hook.m_resizeCallback) {
        hook.m_resizeCallback(pSwapChain, BufferCount, Width, Height, NewFormat, SwapChainFlags);
    }
    
    // 调用原始函数
    return s_originalResizeBuffers(pSwapChain, BufferCount, Width, Height, NewFormat, SwapChainFlags);
}

bool DXGIHook::InitializeDevice(IDXGISwapChain* pSwapChain) {
    if (m_deviceInitialized) return true;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_deviceInitialized) return true;  // Double-check
    
    // 检查是否有从 CreateSwapChainForHwnd 捕获的 D3D12 设备
    if (g_pCapturedCommandQueue && g_pCapturedD3D12Device) {
        m_pD3D12CommandQueue = g_pCapturedCommandQueue;
        m_pD3D12Device = g_pCapturedD3D12Device;
        m_isD3D12 = true;
        m_deviceInitialized = true;
        
        // 获取 SwapChain 信息
        DXGI_SWAP_CHAIN_DESC desc;
        pSwapChain->GetDesc(&desc);
        m_backBufferCount = desc.BufferCount;
        
        LOG_INFO("DXGIHook: Using captured D3D12 CommandQueue, buffer count: {}", m_backBufferCount);
        return true;
    }
    
    // 尝试获取 D3D11 设备
    HRESULT hr = pSwapChain->GetDevice(__uuidof(ID3D11Device), 
            reinterpret_cast<void**>(&m_pD3D11Device));
    
    if (SUCCEEDED(hr) && m_pD3D11Device) {
        m_pD3D11Device->GetImmediateContext(&m_pD3D11Context);
        m_isD3D12 = false;
        m_deviceInitialized = true;
        LOG_INFO("DXGIHook: Captured D3D11 device successfully");
        return true;
    }
    
    // 尝试获取 D3D12 设备（可能 CreateSwapChainForHwnd 在我们 Hook 之前被调用）
    hr = pSwapChain->GetDevice(__uuidof(ID3D12Device), 
            reinterpret_cast<void**>(&m_pD3D12Device));
    
    if (SUCCEEDED(hr) && m_pD3D12Device) {
        LOG_INFO("DXGIHook: D3D12 device detected from SwapChain");
        m_isD3D12 = true;
        
        // 没有从 CreateSwapChainForHwnd 捕获到命令队列
        // D3D12 的 SwapChain 绑定到创建时的命令队列，无法使用新创建的队列
        // 必须回退到 Overlay 模式
        LOG_WARNING("DXGIHook: D3D12 device found but CommandQueue not captured");
        LOG_WARNING("DXGIHook: Cannot render on D3D12 SwapChain without original CommandQueue");
        LOG_INFO("DXGIHook: Will fallback to Overlay mode for D3D12 app");
        
        // 标记为已初始化但命令队列不可用，让 HookRenderer 处理回退
        m_deviceInitialized = true;
        return true;
    }
    
    LOG_ERROR("DXGIHook: Failed to get any device from SwapChain");
    return false;
}

} // namespace LicHper
