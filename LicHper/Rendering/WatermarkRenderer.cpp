#pragma execution_character_set("utf-8")

#include "WatermarkRenderer.h"
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

// 构建水印专用的精简字符范围（只包含水印文本需要的字符）
// 这样可以支持更大的字体而不超出 GPU 纹理限制
static std::vector<ImWchar> BuildWatermarkGlyphRanges(const std::string& title, const std::string& appID) {
    std::set<ImWchar> chars;
    
    // 1. 基础 ASCII（数字、字母、常用符号，用于倒计时和 AppID）
    for (ImWchar c = 0x0020; c <= 0x007E; ++c) {
        chars.insert(c);
    }
    
    // 2. 从 title 提取所有字符（支持 UTF-8 中文）
    const char* p = title.c_str();
    const char* end = p + title.size();
    while (p < end) {
        unsigned int c;
        // 简单 UTF-8 解码
        unsigned char byte = *p;
        if ((byte & 0x80) == 0) {
            c = byte;
            p += 1;
        } else if ((byte & 0xE0) == 0xC0) {
            c = (byte & 0x1F) << 6;
            if (p + 1 < end) c |= (p[1] & 0x3F);
            p += 2;
        } else if ((byte & 0xF0) == 0xE0) {
            c = (byte & 0x0F) << 12;
            if (p + 1 < end) c |= (p[1] & 0x3F) << 6;
            if (p + 2 < end) c |= (p[2] & 0x3F);
            p += 3;
        } else if ((byte & 0xF8) == 0xF0) {
            c = (byte & 0x07) << 18;
            if (p + 1 < end) c |= (p[1] & 0x3F) << 12;
            if (p + 2 < end) c |= (p[2] & 0x3F) << 6;
            if (p + 3 < end) c |= (p[3] & 0x3F);
            p += 4;
        } else {
            p += 1;
            continue;
        }
        if (c > 0 && c <= 0xFFFF) {
            chars.insert((ImWchar)c);
        }
    }
    
    // 3. 从 appID 提取字符
    for (char c : appID) {
        if (c > 0) chars.insert((ImWchar)(unsigned char)c);
    }
    
    // 4. 添加常用替换文本字符（如 "Demo Version", "未授权" 等）
    const char* extras[] = { "Demo", "Version", "未授权", "试用版", "样本" };
    for (const char* extra : extras) {
        const char* ep = extra;
        const char* eend = ep + strlen(ep);
        while (ep < eend) {
            unsigned char byte = *ep;
            unsigned int c;
            if ((byte & 0x80) == 0) {
                c = byte;
                ep += 1;
            } else if ((byte & 0xE0) == 0xC0) {
                c = (byte & 0x1F) << 6;
                if (ep + 1 < eend) c |= (ep[1] & 0x3F);
                ep += 2;
            } else if ((byte & 0xF0) == 0xE0) {
                c = (byte & 0x0F) << 12;
                if (ep + 1 < eend) c |= (ep[1] & 0x3F) << 6;
                if (ep + 2 < eend) c |= (ep[2] & 0x3F);
                ep += 3;
            } else {
                ep += 1;
                continue;
            }
            if (c > 0 && c <= 0xFFFF) {
                chars.insert((ImWchar)c);
            }
        }
    }
    
    // 构建 ImGui 字符范围格式：[start, end, start, end, ..., 0]
    std::vector<ImWchar> ranges;
    ImWchar rangeStart = 0;
    ImWchar rangeEnd = 0;
    
    for (ImWchar c : chars) {
        if (rangeStart == 0) {
            rangeStart = rangeEnd = c;
        } else if (c == rangeEnd + 1) {
            rangeEnd = c;
        } else {
            ranges.push_back(rangeStart);
            ranges.push_back(rangeEnd);
            rangeStart = rangeEnd = c;
        }
    }
    if (rangeStart != 0) {
        ranges.push_back(rangeStart);
        ranges.push_back(rangeEnd);
    }
    ranges.push_back(0); // 终止符
    
    LOG_INFO("BuildWatermarkGlyphRanges: {} unique chars, {} ranges", chars.size(), (ranges.size() - 1) / 2);
    return ranges;
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
    
    // 构建精简字符范围（只包含水印需要的字符）
    m_watermarkGlyphRanges = BuildWatermarkGlyphRanges(config.title, g_appID);
    
    m_titleFont = io.Fonts->AddFontFromFileTTF(
        "c:\\Windows\\Fonts\\msyh.ttc", (float)watermarkFontSize, &titleFontConfig,
        m_watermarkGlyphRanges.data());
    LOG_INFO("InitializeImGui: Watermark font loaded ({}px)", watermarkFontSize);
    
    // 保存设备指针，等待配置更新后再加载图片
    m_pDevice = pDevice;
    
    m_initialized = true;
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
    
    // 重新构建精简字符范围
    m_watermarkGlyphRanges = BuildWatermarkGlyphRanges(config.title, g_appID);
    
    m_titleFont = io.Fonts->AddFontFromFileTTF(
        "c:\\Windows\\Fonts\\msyh.ttc", (float)watermarkFontSize, &titleFontConfig,
        m_watermarkGlyphRanges.data());
    LOG_INFO("ReloadFonts: Watermark font reloaded ({}px)", watermarkFontSize);
    
    // 重新构建字体纹理
    ImGui_ImplDX11_InvalidateDeviceObjects();
    ImGui_ImplDX11_CreateDeviceObjects();
    
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
    LOG_INFO("New config.fontSize = {}", config.fontSize);
    
    std::string oldImagePath;
    int oldFontSize;
    bool firstLoad = false;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        oldImagePath = m_config.imagePath;
        oldFontSize = m_config.fontSize;
        // 如果是首次设置配置（m_config.imagePath 为空且 m_currentImagePath 为空）
        firstLoad = (m_config.imagePath.empty() && m_currentImagePath.empty());
        m_config = config;
    }
    
    LOG_INFO("UpdateConfig: oldFontSize={}, newFontSize={}, firstLoad={}, initialized={}",
             oldFontSize, config.fontSize, firstLoad, m_initialized);
    
    // 检查字体大小是否改变
    bool fontSizeChanged = (oldFontSize != config.fontSize);
    
    // 如果已初始化且字体大小改变，重新加载字体
    if (m_initialized && fontSizeChanged) {
        LOG_INFO("WatermarkRenderer: Font size changed from {} to {}, reloading fonts", 
                 oldFontSize, config.fontSize);
        ReloadFonts();
    } else {
        LOG_INFO("UpdateConfig: No font reload needed (initialized={}, fontSizeChanged={})",
                 m_initialized, fontSizeChanged);
        if (!m_initialized && fontSizeChanged) {
            LOG_INFO("UpdateConfig: Config saved, will use new fontSize on next ImGui init");
        }
    }
    
    // 检查图片路径是否改变或首次加载
    if (firstLoad || oldImagePath != config.imagePath) {
        if (m_pDevice) {
            if (firstLoad) {
                LOG_INFO("WatermarkRenderer: First config update, loading watermark texture");
            } else {
                LOG_INFO("WatermarkRenderer: Image path changed from '{}' to '{}', reloading texture", 
                         oldImagePath, config.imagePath);
            }
            LoadWatermarkTexture(m_pDevice);
        }
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
    m_currentImagePath = imagePath;  // 保存当前加载的图片路径
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
    // 设置全屏透明窗口
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(ImVec2(windowWidth, windowHeight));
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.0f, 0.0f, 0.0f, 0.0f));
    ImGui::Begin("WatermarkOverlay", nullptr, 
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
}

bool WatermarkRenderer::RenderLicenseWindow(bool& showLicenseWindow, float windowWidth, float windowHeight,
    std::function<void()> onLicenseSuccess) {
    
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    // 授权按钮
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(ImVec2(windowWidth, windowHeight));
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.0f, 0.0f, 0.0f, 0.0f));
    ImGui::Begin("LicenseButton", nullptr, 
        ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | 
        ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoBackground);
    
    ImGui::SetCursorPosX(windowWidth - 90);
    if (config.animate) ImGui::SetCursorPosY(100);
    
    // 入口/关闭按钮：使用绘制的图形图标（无字体依赖）
    // 方形按钮尺寸
    ImVec2 keyButtonSize(32, 32);
    ImVec2 btnPos = ImGui::GetCursorScreenPos();
    ImVec2 center(btnPos.x + keyButtonSize.x * 0.5f, btnPos.y + keyButtonSize.y * 0.5f);
    bool toggled = false;

    // 隐藏按钮用于捕获点击
    if (ImGui::InvisibleButton("##LicenseToggle", keyButtonSize)) {
        toggled = true;
    }

    // 计算背景颜色
    bool hovered = ImGui::IsItemHovered();
    bool active = ImGui::IsItemActive();
    ImVec4 bgColor = ImGui::GetStyleColorVec4(active ? ImGuiCol_ButtonActive : (hovered ? ImGuiCol_ButtonHovered : ImGuiCol_Button));
    ImDrawList* draw = ImGui::GetWindowDrawList();
    draw->AddRectFilled(btnPos, ImVec2(btnPos.x + keyButtonSize.x, btnPos.y + keyButtonSize.y), ImColor(bgColor), 4.0f);

    // 图标颜色
    ImU32 iconColor = ImGui::GetColorU32(ImVec4(1.0f, 1.0f, 1.0f, 1.0f));

    // 计算等边三角形尺寸（随按钮缩放）
    float side = keyButtonSize.x * 0.44f;                // 边长占按钮宽度约 44%
    float height = side * (std::sqrt(3.0f) * 0.5f);      // 等边三角形高

    if (!showLicenseWindow) {
        // 折叠状态：向右的等边三角形（居中）
        ImVec2 p1(center.x + height * (2.0f / 3.0f), center.y);
        ImVec2 p2(center.x - height / 3.0f,             center.y - side * 0.5f);
        ImVec2 p3(center.x - height / 3.0f,             center.y + side * 0.5f);
        draw->AddTriangleFilled(p1, p2, p3, iconColor);
        if (toggled) showLicenseWindow = true;
    } else {
        // 展开状态：向下的等边三角形（居中）
        ImVec2 p1(center.x,              center.y + height * (2.0f / 3.0f));
        ImVec2 p2(center.x - side * 0.5f, center.y - height / 3.0f);
        ImVec2 p3(center.x + side * 0.5f, center.y - height / 3.0f);
        draw->AddTriangleFilled(p1, p2, p3, iconColor);
        if (toggled) showLicenseWindow = false;
    }
    ImGui::End();
    ImGui::PopStyleColor();
    
    bool requestExit = false;
    
    if (showLicenseWindow) {
        ImVec2 licenseWindowSize = ImVec2(640, 420);
        ImGui::SetNextWindowPos(ImVec2((windowWidth - licenseWindowSize.x) / 2, 
            (windowHeight - licenseWindowSize.y) / 2));
        ImGui::SetNextWindowSize(licenseWindowSize);
        ImGui::Begin("License", nullptr, 
            ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | 
            ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoTitleBar);
        
        ImGui::SetCursorPosX(20);
        ImGui::SetCursorPosY(20);
        std::string tipText = std::format("请输入软件授权码:    APPID - [{}]", config.appID);
        ImGui::Text("%s", tipText.c_str());
        
        ImVec2 inputSize = ImVec2(600, 250);
        ImGui::SetCursorPosX((licenseWindowSize.x - inputSize.x) / 2);
        ImGui::SetCursorPosY(50);
        ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(16.0f, 16.0f));
        
        // 诊断：记录输入框状态
        static bool logged = false;
        if (!logged) {
            ImGuiIO& io = ImGui::GetIO();
            LOG_INFO("InputText state: WantCaptureKeyboard={}, WantTextInput={}", 
                     io.WantCaptureKeyboard, io.WantTextInput);
            logged = true;
        }
        
        bool inputChanged = ImGui::InputTextMultiline("##source", m_licenseText, IM_ARRAYSIZE(m_licenseText), inputSize);
        
        // 诊断：记录输入框焦点状态
        if (!logged) {
            bool isFocused = ImGui::IsItemFocused();
            bool isActive = ImGui::IsItemActive();
            LOG_INFO("InputText after render: focused={}, active={}, changed={}, text=\"{}\"", 
                     isFocused, isActive, inputChanged, m_licenseText);
        }
        
        ImGui::PopStyleVar();
        
        if (!m_licenseError.empty()) {
            ImGui::SetCursorPosX(20);
            ImGui::SetCursorPosY(310);
            ImGui::TextColored(ImVec4(1.0f, 0.0f, 0.0f, 1.0f), "%s", m_licenseError.c_str());
        }
        
        ImGui::SetCursorPosX((licenseWindowSize.x - 240 - 30) / 2);
        ImGui::SetCursorPosY(340);
        
        // 保存原始按钮颜色
        ImVec4 btn_color = ImGui::GetStyle().Colors[ImGuiCol_Button];
        ImVec4 btn_hovered_color = ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered];
        ImVec4 btn_active_color = ImGui::GetStyle().Colors[ImGuiCol_ButtonActive];
        
        // 取消按钮
        ImGui::GetStyle().Colors[ImGuiCol_Button] = ImVec4(0.8f, 0.2f, 0.2f, 1.0f);
        ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered] = ImVec4(0.9f, 0.3f, 0.3f, 1.0f);
        ImGui::GetStyle().Colors[ImGuiCol_ButtonActive] = ImVec4(0.7f, 0.1f, 0.1f, 1.0f);
        
        ImVec2 buttonSize = ImVec2(120, 40);
        if (ImGui::Button("取消", buttonSize)) {
            showLicenseWindow = false;
        }
        ImGui::SameLine();
        
        ImGui::SetCursorPosX((licenseWindowSize.x - 240 - 30) / 2 + 150);
        
        // 确认按钮
        ImGui::GetStyle().Colors[ImGuiCol_Button] = ImVec4(0.2f, 0.8f, 0.2f, 1.0f);
        ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered] = ImVec4(0.3f, 0.9f, 0.3f, 1.0f);
        ImGui::GetStyle().Colors[ImGuiCol_ButtonActive] = ImVec4(0.1f, 0.7f, 0.1f, 1.0f);
        
        if (ImGui::Button("确认", buttonSize)) {
            if (RenewByLicense(m_licenseText) != 0) {
                m_licenseError = "授权码错误，请检查...";
            } else {
                requestExit = true;
                if (onLicenseSuccess) {
                    onLicenseSuccess();
                }
            }
        }
        
        // 恢复按钮颜色
        ImGui::GetStyle().Colors[ImGuiCol_Button] = btn_color;
        ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered] = btn_hovered_color;
        ImGui::GetStyle().Colors[ImGuiCol_ButtonActive] = btn_active_color;
        
        ImGui::End();
    }
    
    return requestExit;
}

void WatermarkRenderer::RenderWatermarkImage(float windowWidth, float windowHeight) {
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
    
    // 如果启用了图片动画
    if (config.imageAnimate) {
        // 碰撞边界反弹动画 - 确保图片完全在屏幕内
        if (m_imagePosition.x + imageSize.x >= windowWidth) {
            m_imageVelocity.x = -1;
            m_imagePosition.x = windowWidth - imageSize.x;  // 立即修正位置
        }
        if (m_imagePosition.x <= 0) {
            m_imageVelocity.x = 1;
            m_imagePosition.x = 0;  // 立即修正位置
        }
        if (m_imagePosition.y + imageSize.y >= windowHeight) {
            m_imageVelocity.y = -1;
            m_imagePosition.y = windowHeight - imageSize.y;  // 立即修正位置
        }
        if (m_imagePosition.y <= 0) {
            m_imageVelocity.y = 1;
            m_imagePosition.y = 0;  // 立即修正位置
        }
        
        m_imagePosition.x += m_imageVelocity.x;
        m_imagePosition.y += m_imageVelocity.y;
        
        // 确保移动后仍在边界内（防止图片超过窗口尺寸）
        float maxX = (std::max)(0.0f, windowWidth - imageSize.x);
        float maxY = (std::max)(0.0f, windowHeight - imageSize.y);
        m_imagePosition.x = std::clamp(m_imagePosition.x, 0.0f, maxX);
        m_imagePosition.y = std::clamp(m_imagePosition.y, 0.0f, maxY);
        
        posX = m_imagePosition.x;
        posY = m_imagePosition.y;
    } else {
        // 静态定位 - 确保完全在屏幕内
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
        
        // 确保位置在屏幕边界内（即使padding设置不合理或图片超过窗口）
        float maxX = (std::max)(0.0f, windowWidth - imageSize.x);
        float maxY = (std::max)(0.0f, windowHeight - imageSize.y);
        posX = std::clamp(posX, 0.0f, maxX);
        posY = std::clamp(posY, 0.0f, maxY);
    }
    
    float alpha = std::clamp(config.imageAlpha, 0.3f, 1.0f);
    
    ImGui::SetCursorPos(ImVec2(posX, posY));
    ImGui::Image((void*)m_pWatermarkTexture, imageSize, 
        ImVec2(0, 0), ImVec2(1, 1), ImVec4(1, 1, 1, alpha));
}

void WatermarkRenderer::RenderWatermarkText(const std::string& text, float windowWidth, float windowHeight) {
    // 如果文本为空，不渲染
    if (text.empty() || !m_titleFont) return;
    
    ImGui::PushFont(m_titleFont);
    
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    ImVec2 textSize = ImGui::CalcTextSize(text.c_str());
    
    // 水印颜色（降低透明度，更专业）
    ImVec4 color = config.color;
    float baseAlpha = std::clamp(color.w, 0.15f, 0.6f);
    
    // 阴影颜色（黑色半透明）
    ImVec4 shadowColor = ImVec4(0.0f, 0.0f, 0.0f, baseAlpha * 0.5f);
    
    ImDrawList* drawList = ImGui::GetWindowDrawList();
    
    if (config.animate) {
        // === 动画模式：单个水印弹跳 ===
        color.w = baseAlpha;
        
        // 碰撞边界反弹
        if (m_titlePosition.x + textSize.x >= windowWidth) {
            m_titleVelocity.x = -1;
            m_titlePosition.x = windowWidth - textSize.x;
        }
        if (m_titlePosition.x <= 0) {
            m_titleVelocity.x = 1;
            m_titlePosition.x = 0;
        }
        if (m_titlePosition.y + textSize.y >= windowHeight) {
            m_titleVelocity.y = -1;
            m_titlePosition.y = windowHeight - textSize.y;
        }
        if (m_titlePosition.y <= 0) {
            m_titleVelocity.y = 1;
            m_titlePosition.y = 0;
        }
        
        m_titlePosition.x += m_titleVelocity.x;
        m_titlePosition.y += m_titleVelocity.y;
        
        float maxX = (std::max)(0.0f, windowWidth - textSize.x);
        float maxY = (std::max)(0.0f, windowHeight - textSize.y);
        m_titlePosition.x = std::clamp(m_titlePosition.x, 0.0f, maxX);
        m_titlePosition.y = std::clamp(m_titlePosition.y, 0.0f, maxY);
        
        // 绘制带阴影的文字
        ImVec2 pos = m_titlePosition;
        float shadowOffset = 2.0f;
        
        // 阴影（右下偏移）
        drawList->AddText(m_titleFont, m_titleFont->FontSize,
            ImVec2(pos.x + shadowOffset, pos.y + shadowOffset),
            ImGui::ColorConvertFloat4ToU32(shadowColor), text.c_str());
        
        // 主文字
        drawList->AddText(m_titleFont, m_titleFont->FontSize, pos,
            ImGui::ColorConvertFloat4ToU32(color), text.c_str());
    } else {
        // === 静态模式：专业平铺水印 ===
        // 斜向 -30 度倾斜排列
        float angle = -30.0f * 3.14159f / 180.0f;
        float cosA = cosf(angle);
        float sinA = sinf(angle);
        
        // 水印间距（根据文字大小自适应）
        float spacingX = textSize.x * 1.8f;
        float spacingY = textSize.y * 3.5f;
        
        // 扩展绘制区域（因为倾斜需要更大范围）
        float extendX = windowHeight * fabsf(sinA);
        float extendY = windowWidth * fabsf(sinA);
        
        // 计算起始偏移（使水印网格居中）
        float startX = -extendX;
        float startY = -extendY;
        
        // 遍历平铺位置
        for (float baseY = startY; baseY < windowHeight + extendY; baseY += spacingY) {
            for (float baseX = startX; baseX < windowWidth + extendX; baseX += spacingX) {
                // 旋转变换
                float rotatedX = baseX * cosA - baseY * sinA;
                float rotatedY = baseX * sinA + baseY * cosA;
                
                // 偏移到屏幕中心区域
                float finalX = rotatedX + windowWidth * 0.3f;
                float finalY = rotatedY + windowHeight * 0.3f;
                
                // 只绘制可见区域内的水印
                if (finalX > -textSize.x && finalX < windowWidth + textSize.x &&
                    finalY > -textSize.y && finalY < windowHeight + textSize.y) {
                    
                    // 阴影
                    drawList->AddText(m_titleFont, m_titleFont->FontSize,
                        ImVec2(finalX + 2.0f, finalY + 2.0f),
                        ImGui::ColorConvertFloat4ToU32(shadowColor), text.c_str());
                    
                    // 主文字
                    ImVec4 tileColor = color;
                    tileColor.w = baseAlpha;
                    drawList->AddText(m_titleFont, m_titleFont->FontSize,
                        ImVec2(finalX, finalY),
                        ImGui::ColorConvertFloat4ToU32(tileColor), text.c_str());
                }
            }
        }
    }
    
    ImGui::PopFont();
}

std::string WatermarkRenderer::ProcessWatermarkText() {
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    std::string text = config.title;
    
    // 如果title为空，仅在有图片水印时允许
    if (text.empty()) {
        // 如果没有图片水印，使用默认标题
        if (!m_hasWatermarkImage) {
            text = "{APPID} Demo Version";
        } else {
            return text;  // 有图片时允许空标题
        }
    }
    
    // 替换 {APPID}
    text = std::regex_replace(text, std::regex("\\{APPID\\}"), config.appID);
    
    // 替换 {COUNTDOWN}
    auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::high_resolution_clock::now() - m_startTime);
    int remain = config.timeout - (int)elapsed.count();
    remain = (std::max)(remain, 0);
    
    std::string countdown = FormatCountdown(remain);
    text = std::regex_replace(text, std::regex("\\{COUNTDOWN\\}"), countdown);
    
    return text;
}

std::string WatermarkRenderer::FormatCountdown(int seconds) {
    return std::format("{:02d}:{:02d}:{:02d}", 
        seconds / 3600, (seconds % 3600) / 60, seconds % 60);
}

} // namespace LicHper
