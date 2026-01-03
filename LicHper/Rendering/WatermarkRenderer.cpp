#include "WatermarkRenderer.h"
#include "Logger.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_dx11.h"
#include "../stb/stb_image.h"

#include <regex>
#include <format>
#include <filesystem>
#include <algorithm>

// 外部声明
extern std::string g_appID;
std::string GetUserFolder();
int RenewByLicense(const char* key);

namespace LicHper {

WatermarkRenderer::~WatermarkRenderer() {
    CleanupImGui();
}

std::string WatermarkRenderer::GbkToUtf8(const std::string& gbkStr) {
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
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad;
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
    
    m_initialized = true;
    LOG_INFO("WatermarkRenderer: ImGui initialized successfully");
    return true;
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
    
    m_initialized = false;
}

void WatermarkRenderer::UpdateConfig(const WatermarkConfig& config) {
    std::lock_guard<std::mutex> lock(m_configMutex);
    m_config = config;
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
    LOG_INFO("WatermarkRenderer: Watermark texture loaded, {}x{}", width, height);
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
    
    ImGui::PushStyleColor(ImGuiCol_Button, (ImVec4)ImColor::HSV(0.12f, 0.6f, 0.6f));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, (ImVec4)ImColor::HSV(0.12f, 0.7f, 0.7f));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, (ImVec4)ImColor::HSV(0.12f, 0.8f, 0.8f));
    
    // 使用文字按钮（ASCII兼容）
    ImVec2 keyButtonSize(50, 36);
    if (!showLicenseWindow) {
        if (ImGui::Button("[=]", keyButtonSize)) {  // 密钥样式图标
            showLicenseWindow = true;
        }
    } else {
        if (ImGui::Button("[X]", keyButtonSize)) {  // 关闭图标
            showLicenseWindow = false;
        }
    }
    
    ImGui::PopStyleColor(3);
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
        std::string tipText = std::format("请输入软件授权码:    APPID - [{}]", GbkToUtf8(config.appID));
        ImGui::Text("%s", tipText.c_str());
        
        ImVec2 inputSize = ImVec2(600, 250);
        ImGui::SetCursorPosX((licenseWindowSize.x - inputSize.x) / 2);
        ImGui::SetCursorPosY(50);
        ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(16.0f, 16.0f));
        ImGui::InputTextMultiline("##source", m_licenseText, IM_ARRAYSIZE(m_licenseText), inputSize);
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

void WatermarkRenderer::RenderWatermarkText(const std::string& text, float windowWidth, float windowHeight) {
    if (!m_titleFont) return;
    
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

std::string WatermarkRenderer::ProcessWatermarkText() {
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

std::string WatermarkRenderer::FormatCountdown(int seconds) {
    return std::format("{:02d}:{:02d}:{:02d}", 
        seconds / 3600, (seconds % 3600) / 60, seconds % 60);
}

} // namespace LicHper
