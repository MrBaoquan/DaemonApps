#include "OverlayRenderer.h"
#include "Logger.h"
#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"

#include <thread>
#include <tlhelp32.h>

// 外部声明（全局命名空间）
extern std::string g_appID;
void reqQuitAllTargetWindows();
std::string GetUserFolder();

// Forward declare message handler from imgui_impl_win32.cpp
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

namespace LicHper {

// 静态实例指针（用于窗口过程回调）
OverlayRenderer* OverlayRenderer::s_instance = nullptr;

static std::wstring ToWString(const std::string& str) {
    int strLength = (int)str.length() + 1;
    int len = MultiByteToWideChar(CP_ACP, 0, str.c_str(), strLength, 0, 0);
    std::wstring wstr(len, L'\0');
    MultiByteToWideChar(CP_ACP, 0, str.c_str(), strLength, &wstr[0], len);
    return wstr;
}

// 终止进程
static void KillProcessByName(const char* processName) {
    HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnapshot) {
        PROCESSENTRY32 pe32;
        pe32.dwSize = sizeof(PROCESSENTRY32);
        if (Process32First(hSnapshot, &pe32)) {
            do {
                if (strcmp(pe32.szExeFile, processName) == 0) {
                    HANDLE hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pe32.th32ProcessID);
                    if (hProcess) {
                        TerminateProcess(hProcess, 0);
                        CloseHandle(hProcess);
                    }
                }
            } while (Process32Next(hSnapshot, &pe32));
        }
        CloseHandle(hSnapshot);
    }
}

OverlayRenderer::OverlayRenderer() {
    s_instance = this;
    m_watermarkRenderer = std::make_unique<WatermarkRenderer>();
}

OverlayRenderer::~OverlayRenderer() {
    Shutdown();
    s_instance = nullptr;
}

bool OverlayRenderer::Initialize(HWND hWndHost) {
    if (m_initialized) return true;
    
    m_hwndHost = hWndHost;
    m_startTime = std::chrono::high_resolution_clock::now();
    
    // 创建覆盖窗口
    if (!CreateOverlayWindow()) {
        LOG_ERROR("OverlayRenderer: Failed to create overlay window");
        return false;
    }
    
    // 初始化 DirectX
    if (!CreateDeviceD3D()) {
        LOG_ERROR("OverlayRenderer: Failed to create D3D device");
        ::DestroyWindow(m_hwnd);
        ::UnregisterClassW(m_wc.lpszClassName, m_wc.hInstance);
        return false;
    }
    
    // 显示窗口
    ::ShowWindow(m_hwnd, SW_SHOWDEFAULT);
    ::UpdateWindow(m_hwnd);
    
    // 初始化共享水印渲染器
    m_watermarkRenderer->UpdateConfig(m_config);
    m_watermarkRenderer->SetStartTime(m_startTime);
    if (!m_watermarkRenderer->InitializeImGui(m_pd3dDevice, m_pd3dDeviceContext, m_hwnd)) {
        LOG_ERROR("OverlayRenderer: Failed to initialize ImGui");
        CleanupDeviceD3D();
        ::DestroyWindow(m_hwnd);
        ::UnregisterClassW(m_wc.lpszClassName, m_wc.hInstance);
        return false;
    }
    
    m_initialized = true;
    LOG_INFO("OverlayRenderer: Initialized successfully");
    return true;
}

void OverlayRenderer::UpdateConfig(const WatermarkConfig& config) {
    m_config = config;
    if (m_watermarkRenderer) {
        m_watermarkRenderer->UpdateConfig(config);
    }
}

void OverlayRenderer::RunRenderLoop() {
    if (!m_initialized) return;
    
    m_running = true;
    LOG_INFO("OverlayRenderer: Starting render loop");
    
    const float frameTime = 1.0f / 60.0f;
    const auto frameDuration = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::duration<float>(frameTime));
    
    while (m_running) {
        auto frameStart = std::chrono::high_resolution_clock::now();
        
        // 处理消息
        MSG msg;
        while (::PeekMessage(&msg, nullptr, 0U, 0U, PM_REMOVE)) {
            ::TranslateMessage(&msg);
            ::DispatchMessage(&msg);
            if (msg.message == WM_QUIT) {
                m_running = false;
            }
        }
        
        if (!m_running) break;
        
        // 检查超时
        auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::high_resolution_clock::now() - m_startTime);
        
        if (m_config.timeout > 0 && elapsed.count() > m_config.timeout) {
            // 终止指定进程
            for (const auto& process : m_config.timeoutKillOther) {
                KillProcessByName(process.c_str());
            }
            
            if (m_config.timeoutKillSelf) {
                m_running = false;
                break;
            }
        }
        
        // 保持窗口置顶
        SetWindowPos(m_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        
        // 渲染帧
        RenderFrame();
        
        // 帧率限制
        auto frameEnd = std::chrono::high_resolution_clock::now();
        auto frameElapsed = std::chrono::duration_cast<std::chrono::milliseconds>(frameEnd - frameStart);
        if (frameElapsed < frameDuration) {
            std::this_thread::sleep_for(frameDuration - frameElapsed);
        }
    }
    
    LOG_INFO("OverlayRenderer: Render loop ended");
    
    // 通知退出
    reqQuitAllTargetWindows();
    
    if (m_exitCallback) {
        m_exitCallback(0);
    }
}

void OverlayRenderer::Shutdown() {
    m_running = false;
    
    if (!m_initialized) return;
    
    LOG_INFO("OverlayRenderer: Shutting down");
    
    // 清理共享水印渲染器
    if (m_watermarkRenderer) {
        m_watermarkRenderer->CleanupImGui();
    }
    
    // 清理 DirectX
    CleanupDeviceD3D();
    
    // 清理窗口
    if (m_hwnd) {
        ::DestroyWindow(m_hwnd);
        m_hwnd = nullptr;
    }
    
    if (m_wc.lpszClassName) {
        ::UnregisterClassW(m_wc.lpszClassName, m_wc.hInstance);
    }
    
    m_initialized = false;
}

bool OverlayRenderer::CreateOverlayWindow() {
    m_wc = { sizeof(m_wc), CS_CLASSDC, WndProc, 0L, 0L, 
             GetModuleHandle(nullptr), nullptr, nullptr, nullptr, nullptr, 
             L"LicHper Overlay", nullptr };
    ::RegisterClassExW(&m_wc);
    
    std::wstring windowTitle = ToWString(m_config.appID + " Auth Required");
    m_hwnd = ::CreateWindowW(
        m_wc.lpszClassName, 
        windowTitle.c_str(), 
        WS_POPUP,
        0, 0, 
        GetSystemMetrics(SM_CXSCREEN), 
        GetSystemMetrics(SM_CYSCREEN),
        nullptr, nullptr, m_wc.hInstance, nullptr);
    
    if (!m_hwnd) return false;
    
    // 设置透明窗口并置顶
    LONG style = GetWindowLong(m_hwnd, GWL_EXSTYLE);
    SetWindowLong(m_hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED | WS_EX_TOPMOST);
    
    // 设置黑色为透明
    SetLayeredWindowAttributes(m_hwnd, RGB(0, 0, 0), 0, LWA_COLORKEY);
    
    return true;
}

bool OverlayRenderer::CreateDeviceD3D() {
    LOG_INFO("Creating D3D11 device for overlay window");
    
    DXGI_SWAP_CHAIN_DESC sd;
    ZeroMemory(&sd, sizeof(sd));
    sd.BufferCount = 2;
    sd.BufferDesc.Width = 0;
    sd.BufferDesc.Height = 0;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferDesc.RefreshRate.Numerator = 60;
    sd.BufferDesc.RefreshRate.Denominator = 1;
    sd.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = m_hwnd;
    sd.SampleDesc.Count = 1;
    sd.SampleDesc.Quality = 0;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    
    UINT createDeviceFlags = 0;
    D3D_FEATURE_LEVEL featureLevel;
    const D3D_FEATURE_LEVEL featureLevelArray[2] = {
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_0,
    };
    
    HRESULT res = D3D11CreateDeviceAndSwapChain(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, createDeviceFlags,
        featureLevelArray, 2, D3D11_SDK_VERSION, &sd,
        &m_pSwapChain, &m_pd3dDevice, &featureLevel, &m_pd3dDeviceContext);
    
    if (res == DXGI_ERROR_UNSUPPORTED) {
        LOG_WARNING("Hardware D3D11 unsupported, trying WARP");
        res = D3D11CreateDeviceAndSwapChain(
            nullptr, D3D_DRIVER_TYPE_WARP, nullptr, createDeviceFlags,
            featureLevelArray, 2, D3D11_SDK_VERSION, &sd,
            &m_pSwapChain, &m_pd3dDevice, &featureLevel, &m_pd3dDeviceContext);
    }
    
    if (res != S_OK) {
        LOG_ERROR("Failed to create D3D11 device, HRESULT: 0x{:X}", static_cast<unsigned int>(res));
        return false;
    }
    
    if (!m_pd3dDevice || !m_pd3dDeviceContext) {
        LOG_ERROR("D3D11 device or context is null after creation");
        return false;
    }
    
    LOG_INFO("D3D11 device created successfully, device=0x{:X}, context=0x{:X}", 
        reinterpret_cast<uintptr_t>(m_pd3dDevice),
        reinterpret_cast<uintptr_t>(m_pd3dDeviceContext));
    
    CreateRenderTarget();
    return true;
}

void OverlayRenderer::CleanupDeviceD3D() {
    CleanupRenderTarget();
    if (m_pSwapChain) { m_pSwapChain->Release(); m_pSwapChain = nullptr; }
    if (m_pd3dDeviceContext) { m_pd3dDeviceContext->Release(); m_pd3dDeviceContext = nullptr; }
    if (m_pd3dDevice) { m_pd3dDevice->Release(); m_pd3dDevice = nullptr; }
}

void OverlayRenderer::CreateRenderTarget() {
    ID3D11Texture2D* pBackBuffer;
    m_pSwapChain->GetBuffer(0, IID_PPV_ARGS(&pBackBuffer));
    m_pd3dDevice->CreateRenderTargetView(pBackBuffer, nullptr, &m_mainRenderTargetView);
    pBackBuffer->Release();
}

void OverlayRenderer::CleanupRenderTarget() {
    if (m_mainRenderTargetView) {
        m_mainRenderTargetView->Release();
        m_mainRenderTargetView = nullptr;
    }
}

void OverlayRenderer::RenderFrame() {
    if (!m_watermarkRenderer || !m_watermarkRenderer->IsInitialized()) return;
    
    ImGuiIO& io = ImGui::GetIO();
    float windowWidth = io.DisplaySize.x;
    float windowHeight = io.DisplaySize.y;
    
    // 设置渲染目标
    const float clear_color[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
    m_pd3dDeviceContext->OMSetRenderTargets(1, &m_mainRenderTargetView, nullptr);
    m_pd3dDeviceContext->ClearRenderTargetView(m_mainRenderTargetView, clear_color);
    
    // 开始新帧
    m_watermarkRenderer->BeginFrame();
    
    // 渲染水印内容
    m_watermarkRenderer->RenderWatermarkContent(windowWidth, windowHeight);
    
    // 渲染授权窗口
    if (m_watermarkRenderer->RenderLicenseWindow(m_showLicenseWindow, windowWidth, windowHeight, 
        [this]() { m_running = false; })) {
        m_running = false;
    }
    
    // 结束帧并渲染
    m_watermarkRenderer->EndFrame();
    
    // Present
    m_pSwapChain->Present(1, 0);
}

LRESULT WINAPI OverlayRenderer::WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam))
        return true;
    
    switch (msg) {
    case WM_SIZE:
        if (s_instance && s_instance->m_pd3dDevice && wParam != SIZE_MINIMIZED) {
            s_instance->CleanupRenderTarget();
            s_instance->m_pSwapChain->ResizeBuffers(0, (UINT)LOWORD(lParam), (UINT)HIWORD(lParam), 
                DXGI_FORMAT_UNKNOWN, 0);
            s_instance->CreateRenderTarget();
        }
        return 0;
    case WM_SYSCOMMAND:
        if ((wParam & 0xfff0) == SC_KEYMENU)
            return 0;
        break;
    case WM_DESTROY:
        ::PostQuitMessage(0);
        return 0;
    }
    return ::DefWindowProcW(hWnd, msg, wParam, lParam);
}

} // namespace LicHper
