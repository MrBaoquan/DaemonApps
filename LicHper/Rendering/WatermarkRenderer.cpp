#pragma execution_character_set("utf-8")

#include "WatermarkRenderer.h"
#include "ImGuiWatermarkCore.h"
#include "Logger.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"
#include "../stb/stb_image.h"

#include <regex>
#include <format>
#include <filesystem>
#include <algorithm>
#include <cmath>
#include <set>

// 外部声明
extern std::string g_appID;
std::string GetUserFolder();
int RenewByLicense(const char* key);

namespace LicHper {

WatermarkRenderer::WatermarkRenderer() {
    m_watermarkCore = std::make_unique<ImGuiWatermarkCore>();
}

WatermarkRenderer::~WatermarkRenderer() {
    CleanupImGui();
}

bool WatermarkRenderer::InitializeImGui(ID3D11Device* pDevice, ID3D11DeviceContext* pContext, HWND hWnd) {
    if (m_initialized) return true;
    
    if (!pDevice || !pContext || !hWnd) {
        LOG_ERROR("WatermarkRenderer::InitializeImGui - Invalid parameters");
        return false;
    }
    
    LOG_INFO("WatermarkRenderer: Initializing ImGui, device=0x{:X}", reinterpret_cast<uintptr_t>(pDevice));
    
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    
    // 启用完整的输入支持
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad;
    
    // 启用剪贴板支持
    io.SetClipboardTextFn = [](void*, const char* text) {
        int len = MultiByteToWideChar(CP_UTF8, 0, text, -1, NULL, 0);
        if (len > 0) {
            HGLOBAL hMem = GlobalAlloc(GMEM_MOVEABLE, len * sizeof(wchar_t));
            if (hMem) {
                wchar_t* w_text = (wchar_t*)GlobalLock(hMem);
                MultiByteToWideChar(CP_UTF8, 0, text, -1, w_text, len);
                GlobalUnlock(hMem);
                if (OpenClipboard(NULL)) {
                    EmptyClipboard();
                    SetClipboardData(CF_UNICODETEXT, hMem);
                    CloseClipboard();
                }
            }
        }
    };
    
    io.GetClipboardTextFn = [](void*) -> const char* {
        static std::string clipboard_text;
        clipboard_text.clear();
        if (OpenClipboard(NULL)) {
            HANDLE hMem = GetClipboardData(CF_UNICODETEXT);
            if (hMem) {
                const wchar_t* w_text = (const wchar_t*)GlobalLock(hMem);
                if (w_text) {
                    int len = WideCharToMultiByte(CP_UTF8, 0, w_text, -1, NULL, 0, NULL, NULL);
                    if (len > 0) {
                        clipboard_text.resize(len - 1);
                        WideCharToMultiByte(CP_UTF8, 0, w_text, -1, (char*)clipboard_text.data(), len, NULL, NULL);
                    }
                }
                GlobalUnlock(hMem);
            }
            CloseClipboard();
        }
        return clipboard_text.c_str();
    };
    
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
    
    // 水印文字字体大小（从配置读取）
    // 使用精简字符范围后可支持更大字体（最大 300px）
    int watermarkFontSize = std::clamp(config.fontSize, 18, 300);
    LOG_INFO("InitializeImGui: Watermark font size from config = {} (clamped to {})", config.fontSize, watermarkFontSize);
    
    // UI 控件字体（固定 18px，用于授权输入框、按钮等）
    m_font = io.Fonts->AddFontFromFileTTF(
        "c:\\Windows\\Fonts\\msyh.ttc", 18.0f, &fontConfig,
        io.Fonts->GetGlyphRangesChineseSimplifiedCommon());
    LOG_INFO("InitializeImGui: UI font loaded (fixed 18px)");
    
    // 水印文字字体（可变大小，使用精简字符范围）
    ImFontConfig titleFontConfig;
    titleFontConfig.OversampleH = 3;
    titleFontConfig.OversampleV = 1;
    titleFontConfig.PixelSnapH = false;
    titleFontConfig.RasterizerMultiply = 1.3f;
    
    // 构建精简字符范围（根据字体大小动态调整）- 使用共享核心的静态方法
    m_watermarkGlyphRanges = ImGuiWatermarkCore::BuildWatermarkGlyphRanges(config.title, g_appID, watermarkFontSize);
    
    m_titleFont = io.Fonts->AddFontFromFileTTF(
        "c:\\Windows\\Fonts\\msyh.ttc", (float)watermarkFontSize, &titleFontConfig,
        m_watermarkGlyphRanges.data());
    LOG_INFO("InitializeImGui: Watermark font loaded ({}px)", watermarkFontSize);
    
    // 保存设备指针
    m_pDevice = pDevice;
    
    // 设置共享核心的字体
    if (m_watermarkCore) {
        m_watermarkCore->SetUIFont(m_font);
        m_watermarkCore->SetWatermarkFont(m_titleFont);
        m_watermarkCore->MarkFontLoaded(watermarkFontSize);
    }
    
    m_initialized = true;
    
    // 尝试加载水印图片（如果配置已设置）
    if (!m_hasWatermarkImage && !m_config.imagePath.empty()) {
        LOG_INFO("InitializeImGui: Loading watermark texture after init");
        LoadWatermarkTexture(pDevice);
    }
    
    LOG_INFO("WatermarkRenderer: ImGui initialized - UI font: 18px, Watermark font: {}px", watermarkFontSize);
    return true;
}

void WatermarkRenderer::ReloadFonts() {
    LOG_INFO("=== ReloadFonts START ===");
    
    if (!m_initialized) {
        LOG_WARNING("WatermarkRenderer: Cannot reload fonts - not initialized");
        return;
    }
    
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    LOG_INFO("ReloadFonts: config.fontSize = {}", config.fontSize);
    // 使用精简字符范围后可支持更大字体（最大 300px）
    int watermarkFontSize = std::clamp(config.fontSize, 18, 300);
    LOG_INFO("ReloadFonts: watermark fontSize = {} (clamped to {})", config.fontSize, watermarkFontSize);
    
    // 获取 ImGui IO
    ImGuiIO& io = ImGui::GetIO();
    
    // 清除现有字体
    io.Fonts->Clear();
    m_font = nullptr;
    m_titleFont = nullptr;
    LOG_INFO("ReloadFonts: Cleared old fonts");
    
    // 重新加载字体
    // UI 控件字体（固定 18px）
    ImFontConfig fontConfig;
    fontConfig.OversampleH = 3;
    fontConfig.OversampleV = 1;
    fontConfig.PixelSnapH = false;
    fontConfig.RasterizerMultiply = 1.3f;
    
    m_font = io.Fonts->AddFontFromFileTTF(
        "c:\\Windows\\Fonts\\msyh.ttc", 18.0f, &fontConfig,
        io.Fonts->GetGlyphRangesChineseSimplifiedCommon());
    LOG_INFO("ReloadFonts: UI font reloaded (fixed 18px)");
    
    // 水印文字字体（可变大小，使用精简字符范围）
    ImFontConfig titleFontConfig;
    titleFontConfig.OversampleH = 3;
    titleFontConfig.OversampleV = 1;
    titleFontConfig.PixelSnapH = false;
    titleFontConfig.RasterizerMultiply = 1.3f;
    
    // 重新构建精简字符范围（根据字体大小动态调整）
    m_watermarkGlyphRanges = ImGuiWatermarkCore::BuildWatermarkGlyphRanges(config.title, g_appID, watermarkFontSize);
    
    m_titleFont = io.Fonts->AddFontFromFileTTF(
        "c:\\Windows\\Fonts\\msyh.ttc", (float)watermarkFontSize, &titleFontConfig,
        m_watermarkGlyphRanges.data());
    LOG_INFO("ReloadFonts: Watermark font reloaded ({}px)", watermarkFontSize);
    
    // 重新构建字体纹理
    ImGui_ImplDX11_InvalidateDeviceObjects();
    ImGui_ImplDX11_CreateDeviceObjects();
    
    // 更新共享核心的字体
    if (m_watermarkCore) {
        m_watermarkCore->SetUIFont(m_font);
        m_watermarkCore->SetWatermarkFont(m_titleFont);
        m_watermarkCore->MarkFontLoaded(watermarkFontSize);
    }
    
    LOG_INFO("WatermarkRenderer: Fonts reloaded successfully");
}

void WatermarkRenderer::CleanupImGui() {
    if (!m_initialized) return;
    
    LOG_INFO("WatermarkRenderer: Cleaning up ImGui");
    
    // 释放水印纹理
    if (m_pWatermarkTexture) {
        m_pWatermarkTexture->Release();
        m_pWatermarkTexture = nullptr;
    }
    
    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    
    m_pDevice = nullptr;
    m_initialized = false;
}

void WatermarkRenderer::UpdateConfig(const WatermarkConfig& config) {
    LOG_INFO("=== UpdateConfig START ===");
    LOG_INFO("New config.fontSize = {}, title = '{}'", config.fontSize, config.title);
    
    std::string oldImagePath;
    std::string oldTitle;
    int oldFontSize;
    bool firstLoad = false;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        oldImagePath = m_config.imagePath;
        oldTitle = m_config.title;
        oldFontSize = m_config.fontSize;
        // 如果是首次设置配置（m_config.title 为空或为默认值）
        firstLoad = m_config.title.empty() || m_config.title.find("Demo Version") != std::string::npos;
        m_config = config;
    }
    
    // 同步到共享核心
    if (m_watermarkCore) {
        m_watermarkCore->UpdateConfig(config);
    }
    
    LOG_INFO("UpdateConfig: oldFontSize={}, newFontSize={}, firstLoad={}, initialized={}",
             oldFontSize, config.fontSize, firstLoad, m_initialized);
    
    // 检查字体大小或 title 是否改变（title 改变需要重新构建字形范围）
    bool fontSizeChanged = (oldFontSize != config.fontSize);
    bool titleChanged = (oldTitle != config.title);
    
    // 如果已初始化且字体大小或 title 改变，重新加载字体
    if (m_initialized && (fontSizeChanged || (firstLoad && titleChanged))) {
        LOG_INFO("WatermarkRenderer: Font config changed (fontSize: {}->{}, title changed: {}), reloading fonts", 
                 oldFontSize, config.fontSize, titleChanged);
        ReloadFonts();
    } else {
        LOG_INFO("UpdateConfig: No font reload needed (initialized={}, fontSizeChanged={}, titleChanged={})",
                 m_initialized, fontSizeChanged, titleChanged);
        if (!m_initialized && fontSizeChanged) {
            LOG_INFO("UpdateConfig: Config saved, will use new fontSize on next ImGui init");
        }
    }
    
    // 检查图片路径是否改变或首次加载图片
    bool imageFirstLoad = oldImagePath.empty() || oldImagePath == "placeholder.png";
    if (imageFirstLoad || oldImagePath != config.imagePath) {
        if (m_pDevice) {
            if (imageFirstLoad) {
                LOG_INFO("WatermarkRenderer: First config update, loading watermark texture");
            } else {
                LOG_INFO("WatermarkRenderer: Image path changed from '{}' to '{}', reloading texture", 
                         oldImagePath, config.imagePath);
            }
            LoadWatermarkTexture(m_pDevice);
        }
    }
}

void WatermarkRenderer::SetStartTime(std::chrono::high_resolution_clock::time_point startTime) {
    m_startTime = startTime;
    
    // 同步到共享核心
    if (m_watermarkCore) {
        m_watermarkCore->SetStartTime(startTime);
    }
}

bool WatermarkRenderer::LoadWatermarkTexture(ID3D11Device* pDevice) {
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
        LOG_INFO("WatermarkRenderer: Using default watermark image path: {}", imagePath);
    } else if (imagePath.find(':') == std::string::npos && 
               imagePath[0] != '\\' && imagePath[0] != '/') {
        imagePath = lichperFolder + "\\" + imagePath;
        LOG_INFO("WatermarkRenderer: Using relative watermark image path: {}", imagePath);
    } else {
        LOG_INFO("WatermarkRenderer: Using absolute watermark image path: {}", imagePath);
    }
    
    if (!std::filesystem::exists(imagePath)) {
        LOG_WARNING("WatermarkRenderer: Watermark image file not found: {}", imagePath);
        return false;
    }
    
    // 加载图片
    int width, height;
    unsigned char* data = stbi_load(imagePath.c_str(), &width, &height, NULL, 4);
    if (!data) return false;
    
    // 验证图片内容 - 至少 1% 像素可见（支持透明背景水印）
    int totalPixels = width * height;
    int visiblePixels = 0;
    int minRequired = totalPixels / 100;  // 1% 而不是 10%
    
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
    m_currentImagePath = imagePath;  // 保存当前加载的图片路径
    
    // 同步纹理到共享核心
    if (m_watermarkCore) {
        m_watermarkCore->SetWatermarkTexture((void*)m_pWatermarkTexture, width, height);
        m_watermarkCore->MarkImageLoaded(imagePath);
    }
    
    LOG_INFO("WatermarkRenderer: Watermark texture loaded, {}x{} from {}", width, height, imagePath);
    return true;
}

void WatermarkRenderer::BeginFrame() {
    ImGui_ImplDX11_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();
}

void WatermarkRenderer::EndFrame() {
    ImGui::Render();
    ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
}

void WatermarkRenderer::RenderWatermarkContent(float windowWidth, float windowHeight) {
    // 使用共享核心渲染
    if (m_watermarkCore) {
        m_watermarkCore->RenderWatermarkContent(windowWidth, windowHeight);
    }
}

bool WatermarkRenderer::RenderLicenseWindow(bool& showLicenseWindow, float windowWidth, float windowHeight,
    std::function<void()> onLicenseSuccess) {
    
    // 使用共享核心渲染
    if (m_watermarkCore) {
        return m_watermarkCore->RenderLicenseWindow(showLicenseWindow, windowWidth, windowHeight, onLicenseSuccess);
    }
    return false;
}

} // namespace LicHper
