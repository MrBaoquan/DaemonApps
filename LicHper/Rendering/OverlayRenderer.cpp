#include "OverlayRenderer.h"
#include "Logger.h"
#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"
#include "../stb/stb_image.h"
#include "../mINI/ini.h"

#include <regex>
#include <format>
#include <thread>
#include <filesystem>
#include <tlhelp32.h>

// 外部声明（全局命名空间）
extern std::string g_appID;
void reqQuitAllTargetWindows();
int RenewByLicense(const char* key);
std::string GetUserFolder();

namespace LicHper {

// 静态实例指针（用于窗口过程回调）
OverlayRenderer* OverlayRenderer::s_instance = nullptr;

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
        return false;
    }
    
    // 初始化 DirectX
    if (!CreateDeviceD3D()) {
        ::DestroyWindow(m_hwnd);
        ::UnregisterClassW(m_wc.lpszClassName, m_wc.hInstance);
        return false;
    }
    
    // 加载水印图片
    LoadWatermarkTexture();
    
    // 显示窗口
    ::ShowWindow(m_hwnd, SW_SHOWDEFAULT);
    ::UpdateWindow(m_hwnd);
    
    // 设置 ImGui
    SetupImGui();
    
    m_initialized = true;
    return true;
}

void OverlayRenderer::UpdateConfig(const WatermarkConfig& config) {
    m_config = config;
    
    // 如果已初始化，重新加载水印图片
    if (m_initialized) {
        LoadWatermarkTexture();
    }
}

void OverlayRenderer::RunRenderLoop() {
    if (!m_initialized) return;
    
    m_running = true;
    
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
    
    // 通知退出
    reqQuitAllTargetWindows();
    
    if (m_exitCallback) {
        m_exitCallback(0);
    }
}

void OverlayRenderer::Shutdown() {
    m_running = false;
    
    if (!m_initialized) return;
    
    // 清理水印纹理
    if (m_pWatermarkTexture) {
        m_pWatermarkTexture->Release();
        m_pWatermarkTexture = nullptr;
    }
    
    // 清理 ImGui
    CleanupImGui();
    
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

bool OverlayRenderer::LoadWatermarkTexture() {
    // 释放旧纹理
    if (m_pWatermarkTexture) {
        m_pWatermarkTexture->Release();
        m_pWatermarkTexture = nullptr;
    }
    m_hasWatermarkImage = false;
    
    // 处理图片路径
    std::string imagePath = m_config.imagePath;
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
    
    HRESULT hr = m_pd3dDevice->CreateTexture2D(&desc, &subResource, &pTexture);
    if (FAILED(hr) || !pTexture) {
        stbi_image_free(data);
        return false;
    }
    
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc;
    ZeroMemory(&srvDesc, sizeof(srvDesc));
    srvDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MipLevels = 1;
    
    hr = m_pd3dDevice->CreateShaderResourceView(pTexture, &srvDesc, &m_pWatermarkTexture);
    pTexture->Release();
    stbi_image_free(data);
    
    if (FAILED(hr)) return false;
    
    m_watermarkWidth = width;
    m_watermarkHeight = height;
    m_hasWatermarkImage = true;
    return true;
}

void OverlayRenderer::SetupImGui() {
    LOG_INFO("Setting up ImGui, device=0x{:X}, context=0x{:X}", 
        reinterpret_cast<uintptr_t>(m_pd3dDevice),
        reinterpret_cast<uintptr_t>(m_pd3dDeviceContext));
    
    if (!m_pd3dDevice || !m_pd3dDeviceContext) {
        LOG_ERROR("Cannot setup ImGui: D3D device or context is null!");
        return;
    }
    
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad;
    io.Fonts->Flags |= ImFontAtlasFlags_NoPowerOfTwoHeight;
    
    ImGui::StyleColorsDark();
    
    ImGui_ImplWin32_Init(m_hwnd);
    ImGui_ImplDX11_Init(m_pd3dDevice, m_pd3dDeviceContext);
    
    // 配置字体
    ImFontConfig fontConfig;
    fontConfig.OversampleH = 3;
    fontConfig.OversampleV = 1;
    fontConfig.PixelSnapH = false;
    fontConfig.RasterizerMultiply = 1.3f;
    
    int fontSize = std::clamp(m_config.fontSize, 36, 132);
    
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
}

void OverlayRenderer::CleanupImGui() {
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
}

std::string OverlayRenderer::ProcessWatermarkText() {
    std::string text = m_config.title;
    
    // 替换 {APPID}
    text = std::regex_replace(text, std::regex("\\{APPID\\}"), GbkToUtf8(m_config.appID));
    
    // 替换 {COUNTDOWN}
    auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::high_resolution_clock::now() - m_startTime);
    int remain = m_config.timeout - (int)elapsed.count();
    remain = (std::max)(remain, 0);
    
    std::string countdown = FormatCountdown(remain);
    text = std::regex_replace(text, std::regex("\\{COUNTDOWN\\}"), countdown);
    
    return text;
}

std::string OverlayRenderer::FormatCountdown(int seconds) {
    return std::format("{:02d}:{:02d}:{:02d}", 
        seconds / 3600, (seconds % 3600) / 60, seconds % 60);
}

void OverlayRenderer::RenderFrame() {
    ImGuiIO& io = ImGui::GetIO();
    
    // 开始新帧
    ImGui_ImplDX11_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();
    
    float windowWidth = io.DisplaySize.x;
    float windowHeight = io.DisplaySize.y;
    
    // 设置全屏透明窗口
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(ImVec2(windowWidth, windowHeight));
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(1.0f, 1.0f, 1.0f, 0.0f));
    ImGui::Begin("Transparent", nullptr, 
        ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | 
        ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoTitleBar);
    
    // 渲染水印图片
    RenderWatermarkImage(windowWidth, windowHeight);
    
    // 渲染水印文字
    std::string watermarkText = ProcessWatermarkText();
    RenderWatermarkText(watermarkText, windowWidth, windowHeight);
    
    // 授权按钮
    static bool showLicenseWindow = false;
    ImGui::SetCursorPosX(windowWidth - 90);
    if (m_config.animate) ImGui::SetCursorPosY(100);
    
    ImGui::PushStyleColor(ImGuiCol_Button, (ImVec4)ImColor::HSV(0.0f, 0.6f, 0.6f));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, (ImVec4)ImColor::HSV(0.0f, 0.7f, 0.7f));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, (ImVec4)ImColor::HSV(0.0f, 0.8f, 0.8f));
    
    if (!showLicenseWindow) {
        if (ImGui::ArrowButton("##right", ImGuiDir_Right)) {
            showLicenseWindow = true;
        }
    } else {
        if (ImGui::ArrowButton("##down", ImGuiDir_Down)) {
            showLicenseWindow = false;
        }
    }
    ImGui::PopStyleColor(3);
    
    ImGui::End();
    ImGui::PopStyleColor();
    
    // 授权窗口
    if (showLicenseWindow) {
        RenderLicenseWindow();
    }
    
    // 渲染
    ImGui::Render();
    
    float clearColor[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
    m_pd3dDeviceContext->OMSetRenderTargets(1, &m_mainRenderTargetView, nullptr);
    m_pd3dDeviceContext->ClearRenderTargetView(m_mainRenderTargetView, clearColor);
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
    
    m_pSwapChain->Present(1, 0);
}

void OverlayRenderer::RenderWatermarkImage(float windowWidth, float windowHeight) {
    if (!m_hasWatermarkImage || !m_pWatermarkTexture) return;
    
    float scale = std::clamp(m_config.imageScale, 0.1f, 10.0f);
    float displayWidth = m_watermarkWidth * scale;
    float displayHeight = m_watermarkHeight * scale;
    
    ImVec2 imageSize(displayWidth, displayHeight);
    float posX = 0, posY = 0;
    
    // 水平对齐
    if (m_config.imageAlign.find("left") != std::string::npos) {
        posX = (float)m_config.imagePaddingX;
    } else if (m_config.imageAlign.find("right") != std::string::npos) {
        posX = windowWidth - imageSize.x - m_config.imagePaddingX;
    } else {
        posX = (windowWidth - imageSize.x) / 2;
    }
    
    // 垂直对齐
    if (m_config.imageAlign.find("top") != std::string::npos) {
        posY = (float)m_config.imagePaddingY;
    } else if (m_config.imageAlign.find("bottom") != std::string::npos) {
        posY = windowHeight - imageSize.y - m_config.imagePaddingY;
    } else {
        posY = (windowHeight - imageSize.y) / 2;
    }
    
    float alpha = std::clamp(m_config.imageAlpha, 0.3f, 1.0f);
    
    ImGui::SetCursorPos(ImVec2(posX, posY));
    ImGui::Image((void*)m_pWatermarkTexture, imageSize, 
        ImVec2(0, 0), ImVec2(1, 1), ImVec4(1, 1, 1, alpha));
}

void OverlayRenderer::RenderWatermarkText(const std::string& text, float windowWidth, float windowHeight) {
    ImGui::PushFont(m_titleFont);
    
    // 确保颜色可见
    ImVec4 color = m_config.color;
    if (color.w < 0.5f) color.w = 0.5f;
    
    ImGui::PushStyleColor(ImGuiCol_Text, color);
    
    ImVec2 textSize = ImGui::CalcTextSize(text.c_str());
    
    if (m_config.animate) {
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

void OverlayRenderer::RenderLicenseWindow() {
    ImGuiIO& io = ImGui::GetIO();
    static std::string licenseError;
    static char inputText[1024 * 16] = "";
    
    ImVec2 windowSize(640, 420);
    ImGui::SetNextWindowPos(ImVec2(
        (io.DisplaySize.x - windowSize.x) / 2,
        (io.DisplaySize.y - windowSize.y) / 2));
    ImGui::SetNextWindowSize(windowSize);
    
    ImGui::Begin("License", nullptr, 
        ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | 
        ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoTitleBar);
    
    ImGui::SetCursorPos(ImVec2(20, 20));
    std::string tipText = std::format("请输入软件授权码:    APPID - [{}]", GbkToUtf8(m_config.appID));
    ImGui::Text("%s", tipText.c_str());
    
    ImVec2 inputSize(600, 250);
    ImGui::SetCursorPosX((windowSize.x - inputSize.x) / 2);
    ImGui::SetCursorPosY(50);
    ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(16.0f, 16.0f));
    ImGui::InputTextMultiline("##source", inputText, IM_ARRAYSIZE(inputText), inputSize);
    ImGui::PopStyleVar();
    
    if (!licenseError.empty()) {
        ImGui::SetCursorPos(ImVec2(20, 310));
        ImGui::TextColored(ImVec4(1.0f, 0.0f, 0.0f, 1.0f), "%s", licenseError.c_str());
    }
    
    // 按钮
    ImGui::SetCursorPos(ImVec2((windowSize.x - 240 - 30) / 2, 340));
    
    ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.8f, 0.2f, 0.2f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, ImVec4(0.9f, 0.3f, 0.3f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, ImVec4(0.7f, 0.1f, 0.1f, 1.0f));
    
    if (ImGui::Button("取消", ImVec2(120, 40))) {
        // 关闭窗口由外部处理
    }
    ImGui::PopStyleColor(3);
    
    ImGui::SameLine();
    ImGui::SetCursorPosX((windowSize.x - 240 - 30) / 2 + 150);
    
    ImGui::PushStyleColor(ImGuiCol_Button, ImVec4(0.2f, 0.8f, 0.2f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, ImVec4(0.3f, 0.9f, 0.3f, 1.0f));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, ImVec4(0.1f, 0.7f, 0.1f, 1.0f));
    
    if (ImGui::Button("确认", ImVec2(120, 40))) {
        if (RenewByLicense(inputText) != 0) {
            licenseError = "授权码错误，请检查...";
        } else {
            m_running = false;
        }
    }
    ImGui::PopStyleColor(3);
    
    ImGui::End();
}

} // namespace LicHper

// 窗口过程 - ImGui 处理函数声明在 namespace 外
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

LRESULT WINAPI LicHper::OverlayRenderer::WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (::ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam))
        return true;
    
    switch (msg) {
    case WM_SIZE:
        if (wParam == SIZE_MINIMIZED) return 0;
        if (s_instance && s_instance->m_pSwapChain) {
            s_instance->CleanupRenderTarget();
            s_instance->m_pSwapChain->ResizeBuffers(0, (UINT)LOWORD(lParam), (UINT)HIWORD(lParam), 
                DXGI_FORMAT_UNKNOWN, 0);
            s_instance->CreateRenderTarget();
        }
        return 0;
    case WM_SYSCOMMAND:
        if ((wParam & 0xfff0) == SC_KEYMENU) return 0;
        break;
    case WM_DESTROY:
        ::PostQuitMessage(0);
        return 0;
    }
    return ::DefWindowProcW(hWnd, msg, wParam, lParam);
}
