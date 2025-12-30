#include "HookRenderer.h"
#include "Logger.h"
#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"
#include "../stb/stb_image.h"

#include <regex>
#include <format>
#include <filesystem>
#include <tlhelp32.h>

// 外部声明（全局命名空间）
extern std::string g_appID;
void reqQuitAllTargetWindows();
std::string GetUserFolder();

namespace LicHper {

// 编码转换
static std::string GbkToUtf8(const std::string& gbkStr) {
    int len = MultiByteToWideChar(CP_ACP, 0, gbkStr.c_str(), -1, NULL, 0);
    wchar_t* wstr = new wchar_t[len + 1];
    memset(wstr, 0, (len + 1) * sizeof(wchar_t));
    MultiByteToWideChar(CP_ACP, 0, gbkStr.c_str(), -1, wstr, len);

    len = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, NULL, 0, NULL, NULL);
    char* str = new char[len + 1];
    memset(str, 0, len + 1);
    WideCharToMultiByte(CP_UTF8, 0, wstr, -1, str, len, NULL, NULL);

    std::string strTemp = str;
    delete[] wstr;
    delete[] str;
    return strTemp;
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

HookRenderer::HookRenderer() {
}

HookRenderer::~HookRenderer() {
    // 确保回调被清除，防止悬空指针
    DXGIHook::Instance().ClearCallbacks();
    Shutdown();
}

bool HookRenderer::IsHostUsingDirectX() {
    // 检查是否是 WPF 应用程序（WPF 使用 DirectX 但不适合 Hook 模式）
    // WPF 应用程序会加载 wpfgfx_v0400.dll 或 PresentationCore.dll
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
    
    // 检查是否加载了 d3d11.dll 或 dxgi.dll
    HMODULE hD3D11 = GetModuleHandleA("d3d11.dll");
    HMODULE hDXGI = GetModuleHandleA("dxgi.dll");
    
    if (hD3D11 != nullptr || hDXGI != nullptr) {
        LOG_INFO("DirectX application detected, Hook mode suitable");
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
        return false;
    }
    
    m_initialized = true;
    return true;
}

void HookRenderer::UpdateConfig(const WatermarkConfig& config) {
    std::lock_guard<std::mutex> lock(m_configMutex);
    m_config = config;
}

void HookRenderer::RunRenderLoop() {
    if (!m_initialized) return;
    
    // 防止重复运行
    if (m_running) {
        LOG_WARNING("HookRenderer::RunRenderLoop already running, skipping");
        return;
    }
    
    m_running = true;
    
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
}

void HookRenderer::Shutdown() {
    m_running = false;
    
    // 首先清除回调，防止悬空指针
    DXGIHook::Instance().ClearCallbacks();
    
    if (!m_initialized) return;
    
    // 等待超时线程结束
    if (m_timeoutThread.joinable()) {
        m_timeoutThread.join();
    }
    
    // 清理 ImGui
    CleanupImGui();
    
    // 关闭 Hook
    DXGIHook::Instance().Shutdown();
    
    m_initialized = false;
}

void HookRenderer::OnPresent(IDXGISwapChain* pSwapChain) {
    if (!m_running) return;
    
    // 首次调用时初始化 ImGui
    if (!m_imguiInitialized) {
        if (!InitializeImGui(pSwapChain)) {
            return;
        }
    }
    
    // 渲染水印
    RenderWatermark();
}

void HookRenderer::OnResizeBuffers(IDXGISwapChain* pSwapChain, UINT BufferCount,
    UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags) {
    
    // 释放 RenderTargetView
    if (m_pRenderTargetView) {
        m_pRenderTargetView->Release();
        m_pRenderTargetView = nullptr;
    }
}

bool HookRenderer::InitializeImGui(IDXGISwapChain* pSwapChain) {
    auto& hook = DXGIHook::Instance();
    ID3D11Device* pDevice = hook.GetDevice();
    ID3D11DeviceContext* pContext = hook.GetDeviceContext();
    
    if (!pDevice || !pContext) return false;
    
    // 获取后台缓冲区
    ID3D11Texture2D* pBackBuffer = nullptr;
    if (FAILED(pSwapChain->GetBuffer(0, IID_PPV_ARGS(&pBackBuffer)))) {
        return false;
    }
    
    // 创建 RenderTargetView
    if (FAILED(pDevice->CreateRenderTargetView(pBackBuffer, nullptr, &m_pRenderTargetView))) {
        pBackBuffer->Release();
        return false;
    }
    pBackBuffer->Release();
    
    // 获取窗口句柄
    DXGI_SWAP_CHAIN_DESC desc;
    pSwapChain->GetDesc(&desc);
    HWND hWnd = desc.OutputWindow;
    
    // 初始化 ImGui
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    io.Fonts->Flags |= ImFontAtlasFlags_NoPowerOfTwoHeight;
    
    ImGui::StyleColorsDark();
    
    ImGui_ImplWin32_Init(hWnd);
    ImGui_ImplDX11_Init(pDevice, pContext);
    
    // 配置字体
    ImFontConfig fontConfig;
    fontConfig.OversampleH = 3;
    fontConfig.OversampleV = 1;
    fontConfig.PixelSnapH = false;
    fontConfig.RasterizerMultiply = 1.3f;
    
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    int fontSize = std::clamp(config.fontSize, 36, 132);
    
    m_font = io.Fonts->AddFontFromFileTTF(
        "c:\\Windows\\Fonts\\msyh.ttc", 18.0f, &fontConfig,
        io.Fonts->GetGlyphRangesChineseSimplifiedCommon());
    
    ImFontConfig titleFontConfig;
    titleFontConfig.OversampleH = 3;
    titleFontConfig.OversampleV = 1;
    titleFontConfig.PixelSnapH = false;
    titleFontConfig.RasterizerMultiply = 1.3f;
    
    m_titleFont = io.Fonts->AddFontFromFileTTF(
        "c:\\Windows\\Fonts\\msyh.ttc", (float)fontSize, &titleFontConfig,
        io.Fonts->GetGlyphRangesChineseSimplifiedCommon());
    
    // 加载水印纹理
    LoadWatermarkTexture(pDevice);
    
    m_imguiInitialized = true;
    return true;
}

void HookRenderer::CleanupImGui() {
    if (!m_imguiInitialized) return;
    
    // 释放水印纹理
    if (m_pWatermarkTexture) {
        m_pWatermarkTexture->Release();
        m_pWatermarkTexture = nullptr;
    }
    
    // 释放 RenderTargetView
    if (m_pRenderTargetView) {
        m_pRenderTargetView->Release();
        m_pRenderTargetView = nullptr;
    }
    
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    
    m_imguiInitialized = false;
}

bool HookRenderer::LoadWatermarkTexture(ID3D11Device* pDevice) {
    if (!pDevice) return false;
    
    // 释放旧纹理
    if (m_pWatermarkTexture) {
        m_pWatermarkTexture->Release();
        m_pWatermarkTexture = nullptr;
    }
    m_hasWatermarkImage = false;
    
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    // 处理图片路径
    std::string imagePath = config.imagePath;
    std::string lichperFolder = GetUserFolder() + "\\.lichper";
    
    if (imagePath.empty()) {
        imagePath = lichperFolder + "\\watermark.png";
    } else if (imagePath.find(':') == std::string::npos && 
               imagePath[0] != '\\' && imagePath[0] != '/') {
        imagePath = lichperFolder + "\\" + imagePath;
    }
    
    if (!std::filesystem::exists(imagePath)) {
        return false;
    }
    
    // 加载图片
    int width, height;
    unsigned char* data = stbi_load(imagePath.c_str(), &width, &height, NULL, 4);
    if (!data) return false;
    
    // 验证图片内容
    int totalPixels = width * height;
    int visiblePixels = 0;
    int minRequired = totalPixels / 10;
    
    for (int i = 0; i < totalPixels && visiblePixels < minRequired; i++) {
        unsigned char a = data[i * 4 + 3];
        unsigned char r = data[i * 4 + 0];
        unsigned char g = data[i * 4 + 1];
        unsigned char b = data[i * 4 + 2];
        if (a > 30 && (r > 10 || g > 10 || b > 10)) {
            visiblePixels++;
        }
    }
    
    if (visiblePixels < minRequired) {
        stbi_image_free(data);
        return false;
    }
    
    // 创建纹理
    D3D11_TEXTURE2D_DESC desc;
    ZeroMemory(&desc, sizeof(desc));
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    
    ID3D11Texture2D* pTexture = nullptr;
    D3D11_SUBRESOURCE_DATA subResource;
    subResource.pSysMem = data;
    subResource.SysMemPitch = width * 4;
    subResource.SysMemSlicePitch = 0;
    
    HRESULT hr = pDevice->CreateTexture2D(&desc, &subResource, &pTexture);
    if (FAILED(hr) || !pTexture) {
        stbi_image_free(data);
        return false;
    }
    
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc;
    ZeroMemory(&srvDesc, sizeof(srvDesc));
    srvDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MipLevels = 1;
    
    hr = pDevice->CreateShaderResourceView(pTexture, &srvDesc, &m_pWatermarkTexture);
    pTexture->Release();
    stbi_image_free(data);
    
    if (FAILED(hr)) return false;
    
    m_watermarkWidth = width;
    m_watermarkHeight = height;
    m_hasWatermarkImage = true;
    return true;
}

std::string HookRenderer::ProcessWatermarkText() {
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    std::string text = config.title;
    
    // 替换 {APPID}
    text = std::regex_replace(text, std::regex("\\{APPID\\}"), GbkToUtf8(config.appID));
    
    // 替换 {COUNTDOWN}
    auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::high_resolution_clock::now() - m_startTime);
    int remain = config.timeout - (int)elapsed.count();
    remain = (std::max)(remain, 0);
    
    std::string countdown = FormatCountdown(remain);
    text = std::regex_replace(text, std::regex("\\{COUNTDOWN\\}"), countdown);
    
    return text;
}

std::string HookRenderer::FormatCountdown(int seconds) {
    return std::format("{:02d}:{:02d}:{:02d}", 
        seconds / 3600, (seconds % 3600) / 60, seconds % 60);
}

void HookRenderer::RenderWatermark() {
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
    
    // 设置渲染目标
    pContext->OMSetRenderTargets(1, &m_pRenderTargetView, nullptr);
    
    // 开始新帧
    ImGui_ImplDX11_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();
    
    ImGuiIO& io = ImGui::GetIO();
    float windowWidth = io.DisplaySize.x;
    float windowHeight = io.DisplaySize.y;
    
    // 设置全屏透明窗口
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(ImVec2(windowWidth, windowHeight));
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.0f, 0.0f, 0.0f, 0.0f));
    ImGui::Begin("Watermark", nullptr, 
        ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | 
        ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoTitleBar |
        ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoBackground);
    
    // 渲染水印图片
    RenderWatermarkImage(windowWidth, windowHeight);
    
    // 渲染水印文字
    std::string watermarkText = ProcessWatermarkText();
    RenderWatermarkText(watermarkText, windowWidth, windowHeight);
    
    ImGui::End();
    ImGui::PopStyleColor();
    
    // 渲染
    ImGui::Render();
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
}

void HookRenderer::RenderWatermarkImage(float windowWidth, float windowHeight) {
    if (!m_hasWatermarkImage || !m_pWatermarkTexture) return;
    
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    float scale = std::clamp(config.imageScale, 0.1f, 10.0f);
    float displayWidth = m_watermarkWidth * scale;
    float displayHeight = m_watermarkHeight * scale;
    
    ImVec2 imageSize(displayWidth, displayHeight);
    float posX = 0, posY = 0;
    
    // 水平对齐
    if (config.imageAlign.find("left") != std::string::npos) {
        posX = (float)config.imagePaddingX;
    } else if (config.imageAlign.find("right") != std::string::npos) {
        posX = windowWidth - imageSize.x - config.imagePaddingX;
    } else {
        posX = (windowWidth - imageSize.x) / 2;
    }
    
    // 垂直对齐
    if (config.imageAlign.find("top") != std::string::npos) {
        posY = (float)config.imagePaddingY;
    } else if (config.imageAlign.find("bottom") != std::string::npos) {
        posY = windowHeight - imageSize.y - config.imagePaddingY;
    } else {
        posY = (windowHeight - imageSize.y) / 2;
    }
    
    float alpha = std::clamp(config.imageAlpha, 0.3f, 1.0f);
    
    ImGui::SetCursorPos(ImVec2(posX, posY));
    ImGui::Image((void*)m_pWatermarkTexture, imageSize, 
        ImVec2(0, 0), ImVec2(1, 1), ImVec4(1, 1, 1, alpha));
}

void HookRenderer::RenderWatermarkText(const std::string& text, float windowWidth, float windowHeight) {
    ImGui::PushFont(m_titleFont);
    
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    // 确保颜色可见
    ImVec4 color = config.color;
    if (color.w < 0.5f) color.w = 0.5f;
    
    ImGui::PushStyleColor(ImGuiCol_Text, color);
    
    ImVec2 textSize = ImGui::CalcTextSize(text.c_str());
    
    if (config.animate) {
        // 动画：碰撞边界反弹
        if (m_titlePosition.x + textSize.x + 10 >= windowWidth) m_titleVelocity.x = -1;
        if (m_titlePosition.x <= 0) m_titleVelocity.x = 1;
        if (m_titlePosition.y + textSize.y + 10 >= windowHeight) m_titleVelocity.y = -1;
        if (m_titlePosition.y <= 0) m_titleVelocity.y = 1;
        
        m_titlePosition.x += m_titleVelocity.x;
        m_titlePosition.y += m_titleVelocity.y;
    } else {
        m_titlePosition = ImVec2((windowWidth - textSize.x) - 50, 150);
    }
    
    ImGui::SetCursorPos(m_titlePosition);
    ImGui::Text("%s", text.c_str());
    
    ImGui::PopStyleColor();
    ImGui::PopFont();
}

} // namespace LicHper
