#include "HookRenderer.h"
#include "Logger.h"
#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"

#include <windows.h>
#include <tlhelp32.h>

// Forward declare message handler from imgui_impl_win32.cpp
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

// 外部声明（全局命名空间）
extern std::string g_appID;
void reqQuitAllTargetWindows();
std::string GetUserFolder();

namespace LicHper {

// 静态实例指针
HookRenderer* HookRenderer::s_instance = nullptr;

// 终止进程
static void KillProcessByName(const char* processName) {
    HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnapshot) {
        PROCESSENTRY32 pe32;
        pe32.dwSize = sizeof(PROCESSENTRY32);
        if (Process32First(hSnapshot, &pe32)) {
            do {
                if (strcmp(pe32.szExeFile, processName) == 0) {
                    HANDLE hProcess = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pe32.th32ProcessID);
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

HookRenderer::HookRenderer() {
    s_instance = this;
    m_watermarkRenderer = std::make_unique<WatermarkRenderer>();
    m_d3d12Renderer = std::make_unique<D3D12WatermarkRenderer>();
}

HookRenderer::~HookRenderer() {
    // 确保回调被清除，防止悬空指针
    DXGIHook::Instance().ClearCallbacks();
    Shutdown();
    s_instance = nullptr;
}

bool HookRenderer::IsHostUsingDirectX() {
    // 检查是否是 WPF 应用程序（WPF 使用 DirectX 但不适合 Hook 模式）
    HMODULE hWpfGfx = GetModuleHandleA("wpfgfx_v0400.dll");
    HMODULE hPresentationCore = GetModuleHandleA("PresentationCore.dll");
    if (hWpfGfx != nullptr || hPresentationCore != nullptr) {
        LOG_INFO("WPF application detected, Hook mode not suitable");
        return false;
    }
    
    // 检查是否是 .NET Windows Forms（也不适合 Hook）
    HMODULE hWinForms = GetModuleHandleA("System.Windows.Forms.dll");
    if (hWinForms != nullptr) {
        LOG_INFO("Windows Forms application detected, Hook mode not suitable");
        return false;
    }
    
    // 检查是否加载了 D3D12（UE5 等引擎使用 D3D12）
    // 现在支持 D3D12 原生渲染
    HMODULE hD3D12 = GetModuleHandleA("d3d12.dll");
    if (hD3D12 != nullptr) {
        LOG_INFO("D3D12 application detected, Hook mode will use D3D12 native rendering");
        return true;
    }
    
    // 检查是否加载了 d3d11.dll（纯 D3D11 应用）
    HMODULE hD3D11 = GetModuleHandleA("d3d11.dll");
    HMODULE hDXGI = GetModuleHandleA("dxgi.dll");
    
    if (hD3D11 != nullptr && hDXGI != nullptr) {
        LOG_INFO("D3D11 application detected, Hook mode suitable");
        return true;
    }
    
    return false;
}

bool HookRenderer::Initialize(HWND hWndHost) {
    if (m_initialized) return true;
    
    m_hwndHost = hWndHost;
    m_startTime = std::chrono::high_resolution_clock::now();
    
    // 初始化 DXGI Hook
    auto& hook = DXGIHook::Instance();
    
    // 设置回调
    hook.SetPresentCallback([this](IDXGISwapChain* pSwapChain) {
        OnPresent(pSwapChain);
    });
    
    hook.SetResizeBuffersCallback([this](IDXGISwapChain* pSwapChain, UINT BufferCount,
        UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags) {
        OnResizeBuffers(pSwapChain, BufferCount, Width, Height, NewFormat, SwapChainFlags);
    });
    
    if (!hook.Initialize()) {
        LOG_ERROR("HookRenderer: Failed to initialize DXGI Hook");
        return false;
    }
    
    m_initialized = true;
    LOG_INFO("HookRenderer: Initialized successfully");
    return true;
}

void HookRenderer::UpdateConfig(const WatermarkConfig& config) {
    std::lock_guard<std::mutex> lock(m_configMutex);
    m_config = config;
    if (m_watermarkRenderer) {
        m_watermarkRenderer->UpdateConfig(config);
    }
    if (m_d3d12Renderer) {
        m_d3d12Renderer->UpdateConfig(config);
    }
}

void HookRenderer::RunRenderLoop() {
    if (!m_initialized) return;
    
    // 防止重复运行
    if (m_running) {
        LOG_WARNING("HookRenderer::RunRenderLoop already running, skipping");
        return;
    }
    
    m_running = true;
    LOG_INFO("HookRenderer: Starting render loop");
    
    // 确保之前的线程已结束
    if (m_timeoutThread.joinable()) {
        m_timeoutThread.join();
    }
    
    // 启动超时检查线程
    m_timeoutThread = std::thread([this]() {
        while (m_running) {
            std::this_thread::sleep_for(std::chrono::seconds(1));
            
            if (!m_running) break;
            
            // 检查超时
            auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(
                std::chrono::high_resolution_clock::now() - m_startTime);
            
            WatermarkConfig config;
            {
                std::lock_guard<std::mutex> lock(m_configMutex);
                config = m_config;
            }
            
            if (config.timeout > 0 && elapsed.count() > config.timeout) {
                // 终止指定进程
                for (const auto& process : config.timeoutKillOther) {
                    KillProcessByName(process.c_str());
                }
                
                if (config.timeoutKillSelf) {
                    m_running = false;
                    reqQuitAllTargetWindows();
                    if (m_exitCallback) {
                        m_exitCallback(0);
                    }
                }
            }
        }
    });
    
    // Hook 模式下，渲染在 Present 回调中进行
    // 这里只需等待退出信号
    while (m_running) {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    
    if (m_timeoutThread.joinable()) {
        m_timeoutThread.join();
    }
    
    LOG_INFO("HookRenderer: Render loop ended");
}

void HookRenderer::Shutdown() {
    m_running = false;
    
    // 首先清除回调，防止悬空指针
    DXGIHook::Instance().ClearCallbacks();
    
    if (!m_initialized) return;
    
    LOG_INFO("HookRenderer: Shutting down");
    
    // 等待超时线程结束
    if (m_timeoutThread.joinable()) {
        m_timeoutThread.join();
    }
    
    // 清理 D3D12 渲染器
    if (m_d3d12Renderer) {
        m_d3d12Renderer->Shutdown();
    }
    
    // 清理 ImGui (D3D11)
    CleanupImGui();
    
    // 关闭 Hook
    DXGIHook::Instance().Shutdown();
    
    m_initialized = false;
    m_usingD3D12 = false;
}

void HookRenderer::OnPresent(IDXGISwapChain* pSwapChain) {
    if (!m_running) return;
    
    auto& hook = DXGIHook::Instance();
    
    // 检查是否是 D3D12 模式
    if (hook.IsD3D12()) {
        // D3D12 模式：使用原生 D3D12 渲染器
        if (!m_d3d12Renderer->IsInitialized()) {
            if (!InitializeD3D12Renderer(pSwapChain)) {
                // 如果没有命令队列，触发回退
                if (!hook.GetD3D12CommandQueue()) {
                    LOG_INFO("HookRenderer: D3D12 CommandQueue not available, triggering fallback");
                    m_needsFallback = true;
                    m_running = false;
                }
                return;
            }
        }
        
        // D3D12 渲染
        RenderWatermarkD3D12(pSwapChain);
    } else {
        // D3D11 模式：使用原有逻辑
        if (!m_watermarkRenderer || !m_watermarkRenderer->IsInitialized()) {
            if (!InitializeImGui(pSwapChain)) {
                return;
            }
        }
        
        // D3D11 渲染
        RenderWatermark();
    }
}

void HookRenderer::OnResizeBuffers(IDXGISwapChain* pSwapChain, UINT BufferCount,
    UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags) {
    
    // 释放 D3D11 RenderTargetView
    if (m_pRenderTargetView) {
        m_pRenderTargetView->Release();
        m_pRenderTargetView = nullptr;
    }
    
    // D3D12 渲染器需要在下一帧重新初始化
    if (m_d3d12Renderer && m_d3d12Renderer->IsInitialized()) {
        m_d3d12Renderer->OnResize(Width, Height);
    }
}

bool HookRenderer::InitializeImGui(IDXGISwapChain* pSwapChain) {
    auto& hook = DXGIHook::Instance();
    ID3D11Device* pDevice = hook.GetDevice();
    ID3D11DeviceContext* pContext = hook.GetDeviceContext();
    
    if (!pDevice || !pContext) {
        LOG_ERROR("HookRenderer: Cannot initialize ImGui - device or context is null");
        // 检查是否是 D3D12 应用，如果是则标记需要回退
        if (hook.IsD3D12()) {
            LOG_INFO("HookRenderer: D3D12 detected at runtime, marking for fallback to Overlay mode");
            m_needsFallback = true;
            m_running = false;  // 停止渲染循环
        }
        return false;
    }
    
    // 获取后台缓冲区
    ID3D11Texture2D* pBackBuffer = nullptr;
    if (FAILED(pSwapChain->GetBuffer(0, IID_PPV_ARGS(&pBackBuffer)))) {
        LOG_ERROR("HookRenderer: Failed to get back buffer");
        // 也可能是 D3D12 但未检测到，标记回退
        if (hook.IsD3D12()) {
            LOG_INFO("HookRenderer: D3D12 SwapChain detected, marking for fallback to Overlay mode");
            m_needsFallback = true;
            m_running = false;
        }
        return false;
    }
    
    // 创建 RenderTargetView
    if (FAILED(pDevice->CreateRenderTargetView(pBackBuffer, nullptr, &m_pRenderTargetView))) {
        pBackBuffer->Release();
        LOG_ERROR("HookRenderer: Failed to create render target view");
        return false;
    }
    pBackBuffer->Release();
    
    // 获取窗口句柄
    DXGI_SWAP_CHAIN_DESC desc;
    pSwapChain->GetDesc(&desc);
    m_hwndTarget = desc.OutputWindow;
    
    // 初始化共享水印渲染器
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        m_watermarkRenderer->UpdateConfig(m_config);
    }
    m_watermarkRenderer->SetStartTime(m_startTime);
    
    if (!m_watermarkRenderer->InitializeImGui(pDevice, pContext, m_hwndTarget)) {
        LOG_ERROR("HookRenderer: Failed to initialize shared watermark renderer");
        if (m_pRenderTargetView) {
            m_pRenderTargetView->Release();
            m_pRenderTargetView = nullptr;
        }
        return false;
    }
    
    // 安装输入 Hook
    InstallInputHook();
    
    LOG_INFO("HookRenderer: ImGui initialized successfully");
    return true;
}

void HookRenderer::CleanupImGui() {
    // 卸载输入 Hook
    UninstallInputHook();
    
    // 释放 RenderTargetView
    if (m_pRenderTargetView) {
        m_pRenderTargetView->Release();
        m_pRenderTargetView = nullptr;
    }
    
    // 清理共享水印渲染器
    if (m_watermarkRenderer) {
        m_watermarkRenderer->CleanupImGui();
    }
}

void HookRenderer::RenderWatermark() {
    if (!m_watermarkRenderer || !m_watermarkRenderer->IsInitialized()) return;
    
    auto& hook = DXGIHook::Instance();
    ID3D11DeviceContext* pContext = hook.GetDeviceContext();
    
    if (!pContext || !m_pRenderTargetView) {
        // 尝试重新创建 RenderTargetView
        IDXGISwapChain* pSwapChain = hook.GetSwapChain();
        ID3D11Device* pDevice = hook.GetDevice();
        if (pSwapChain && pDevice) {
            ID3D11Texture2D* pBackBuffer = nullptr;
            if (SUCCEEDED(pSwapChain->GetBuffer(0, IID_PPV_ARGS(&pBackBuffer)))) {
                pDevice->CreateRenderTargetView(pBackBuffer, nullptr, &m_pRenderTargetView);
                pBackBuffer->Release();
            }
        }
        if (!m_pRenderTargetView) return;
    }
    
    // 处理输入
    ProcessInput();
    
    // 设置渲染目标
    pContext->OMSetRenderTargets(1, &m_pRenderTargetView, nullptr);
    
    ImGuiIO& io = ImGui::GetIO();
    float windowWidth = io.DisplaySize.x;
    float windowHeight = io.DisplaySize.y;
    
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
}

void HookRenderer::ProcessInput() {
    if (!m_hwndTarget) return;
    
    ImGuiIO& io = ImGui::GetIO();
    
    // 获取鼠标位置
    POINT pt;
    if (GetCursorPos(&pt)) {
        ScreenToClient(m_hwndTarget, &pt);
        io.MousePos = ImVec2((float)pt.x, (float)pt.y);
    }
    
    // 获取鼠标按键状态
    io.MouseDown[0] = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
    io.MouseDown[1] = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
    io.MouseDown[2] = (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;
}

void HookRenderer::InstallInputHook() {
    if (m_inputHookInstalled) return;
    
    // 安装 GetMessage Hook 来拦截窗口消息（包括 WM_CHAR）
    m_hGetMsgHook = SetWindowsHookExA(WH_GETMESSAGE, GetMsgHookProc, nullptr, GetCurrentThreadId());
    if (m_hGetMsgHook) {
        m_inputHookInstalled = true;
        LOG_INFO("HookRenderer: GetMessage hook installed successfully");
    } else {
        LOG_ERROR("HookRenderer: Failed to install GetMessage hook, error: {}", GetLastError());
    }
}

void HookRenderer::UninstallInputHook() {
    if (!m_inputHookInstalled) return;
    
    if (m_hGetMsgHook) {
        UnhookWindowsHookEx(m_hGetMsgHook);
        m_hGetMsgHook = nullptr;
    }
    
    m_inputHookInstalled = false;
    LOG_INFO("HookRenderer: Input hooks uninstalled");
}

LRESULT CALLBACK HookRenderer::KeyboardHookProc(int nCode, WPARAM wParam, LPARAM lParam) {
    // 未使用，保留以防需要
    return CallNextHookEx(nullptr, nCode, wParam, lParam);
}

LRESULT CALLBACK HookRenderer::GetMsgHookProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode >= 0 && s_instance && wParam == PM_REMOVE) {
        MSG* pMsg = reinterpret_cast<MSG*>(lParam);
        
        // 将消息转发给 ImGui
        if (pMsg->hwnd == s_instance->m_hwndTarget || 
            pMsg->hwnd == nullptr ||
            GetParent(pMsg->hwnd) == s_instance->m_hwndTarget) {
            
            ImGui_ImplWin32_WndProcHandler(pMsg->hwnd, pMsg->message, pMsg->wParam, pMsg->lParam);
        }
    }
    
    return CallNextHookEx(s_instance ? s_instance->m_hGetMsgHook : nullptr, nCode, wParam, lParam);
}

bool HookRenderer::InitializeD3D12Renderer(IDXGISwapChain* pSwapChain) {
    auto& hook = DXGIHook::Instance();
    
    ID3D12Device* pDevice = hook.GetD3D12Device();
    ID3D12CommandQueue* pCommandQueue = hook.GetD3D12CommandQueue();
    
    if (!pDevice || !pCommandQueue) {
        LOG_ERROR("HookRenderer: D3D12 device or command queue not available");
        return false;
    }
    
    // 获取窗口句柄
    DXGI_SWAP_CHAIN_DESC desc;
    pSwapChain->GetDesc(&desc);
    m_hwndTarget = desc.OutputWindow;
    
    // 初始化 D3D12 渲染器
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        m_d3d12Renderer->UpdateConfig(m_config);
    }
    m_d3d12Renderer->SetStartTime(m_startTime);
    m_d3d12Renderer->SetExitCallback([this]() { m_running = false; });
    
    if (!m_d3d12Renderer->Initialize(pSwapChain, pDevice, pCommandQueue, m_hwndTarget)) {
        LOG_ERROR("HookRenderer: Failed to initialize D3D12 renderer");
        return false;
    }
    
    // 安装输入 Hook
    InstallInputHook();
    
    LOG_INFO("HookRenderer: D3D12 renderer initialized successfully");
    return true;
}

void HookRenderer::RenderWatermarkD3D12(IDXGISwapChain* pSwapChain) {
    if (!m_d3d12Renderer || !m_d3d12Renderer->IsInitialized()) return;
    
    // 处理输入（更新 ImGui 输入状态）
    ProcessInput();
    
    // 渲染
    m_d3d12Renderer->Render(pSwapChain);
}

} // namespace LicHper
