#include "DXGIHook.h"
#include "../Rendering/Logger.h"
#include <MinHook.h>
#include <d3d11.h>
#include <dxgi.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

namespace LicHper {

// 静态成员初始化
DXGIHook::PresentFn DXGIHook::s_originalPresent = nullptr;
DXGIHook::ResizeBuffersFn DXGIHook::s_originalResizeBuffers = nullptr;

DXGIHook& DXGIHook::Instance() {
    static DXGIHook instance;
    return instance;
}

bool DXGIHook::Initialize() {
    if (m_initialized) return true;
    
    LOG_INFO("DXGIHook: Initializing MinHook...");
    
    // 初始化 MinHook
    MH_STATUS status = MH_Initialize();
    if (status != MH_OK) {
        LOG_ERROR("DXGIHook: MH_Initialize failed with status {}", (int)status);
        return false;
    }
    
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

void DXGIHook::Shutdown() {
    if (!m_initialized) return;
    
    LOG_INFO("DXGIHook: Shutting down...");
    
    // 禁用并移除所有 Hook
    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();
    
    s_originalPresent = nullptr;
    s_originalResizeBuffers = nullptr;
    m_pCapturedSwapChain = nullptr;
    m_pCapturedDevice = nullptr;
    m_pCapturedContext = nullptr;
    
    m_initialized = false;
    LOG_INFO("DXGIHook: Shutdown complete");
}

bool DXGIHook::GetSwapChainVTable(void** pVTable) {
    // 创建临时窗口
    WNDCLASSEX wc = { sizeof(WNDCLASSEX), CS_CLASSDC, DefWindowProc, 0L, 0L,
                      GetModuleHandle(nullptr), nullptr, nullptr, nullptr, nullptr,
                      "DXGIHookTemp", nullptr };
    RegisterClassEx(&wc);
    
    HWND hWnd = CreateWindow(wc.lpszClassName, "", WS_OVERLAPPEDWINDOW,
        100, 100, 300, 300, nullptr, nullptr, wc.hInstance, nullptr);
    
    if (!hWnd) {
        UnregisterClass(wc.lpszClassName, wc.hInstance);
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
        UnregisterClass(wc.lpszClassName, wc.hInstance);
        return false;
    }
    
    // 获取 VTable
    memcpy(pVTable, *reinterpret_cast<void***>(pSwapChain), sizeof(void*) * 18);
    
    // 清理临时资源
    pSwapChain->Release();
    pContext->Release();
    pDevice->Release();
    DestroyWindow(hWnd);
    UnregisterClass(wc.lpszClassName, wc.hInstance);
    
    return true;
}

HRESULT WINAPI DXGIHook::HookedPresent(IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags) {
    auto& hook = Instance();
    
    // 首次调用时捕获设备
    if (!hook.m_pCapturedSwapChain) {
        hook.m_pCapturedSwapChain = pSwapChain;
        
        // 获取 Device 和 Context
        if (SUCCEEDED(pSwapChain->GetDevice(__uuidof(ID3D11Device), 
                reinterpret_cast<void**>(&hook.m_pCapturedDevice)))) {
            hook.m_pCapturedDevice->GetImmediateContext(&hook.m_pCapturedContext);
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

} // namespace LicHper
