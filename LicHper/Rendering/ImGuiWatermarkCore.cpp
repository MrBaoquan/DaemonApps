#pragma execution_character_set("utf-8")

#include "ImGuiWatermarkCore.h"
#include "Logger.h"

#include <regex>
#include <format>
#include <algorithm>
#include <cmath>
#include <set>

// 外部声明
extern std::string g_appID;
int RenewByLicense(const char* key);

namespace LicHper {

// ========== 静态工具函数 ==========

std::vector<ImWchar> ImGuiWatermarkCore::BuildWatermarkGlyphRanges(const std::string& title, const std::string& appID) {
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
    
    // 4. 添加常用替换文本字符
    const char* extras[] = { "Demo", "Version", "未授权", "试用版", "样本", "请输入软件授权码", "APPID", "取消", "确认", "授权码错误" };
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
    
    // 构建 ImGui 字符范围格式
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
    ranges.push_back(0);
    
    LOG_INFO("BuildWatermarkGlyphRanges: {} unique chars, {} ranges", chars.size(), (ranges.size() - 1) / 2);
    return ranges;
}

// ========== 配置管理 ==========

void ImGuiWatermarkCore::UpdateConfig(const WatermarkConfig& config) {
    std::lock_guard<std::mutex> lock(m_configMutex);
    m_config = config;
}

int ImGuiWatermarkCore::GetConfiguredFontSize() const {
    std::lock_guard<std::mutex> lock(m_configMutex);
    return m_config.fontSize;
}

bool ImGuiWatermarkCore::NeedsFontReload(int newFontSize) const {
    return m_loadedFontSize != newFontSize;
}

void ImGuiWatermarkCore::MarkFontLoaded(int fontSize) {
    m_loadedFontSize = fontSize;
}

// ========== 纹理管理 ==========

void ImGuiWatermarkCore::SetWatermarkTexture(void* textureId, int width, int height) {
    m_pWatermarkTexture = textureId;
    m_watermarkWidth = width;
    m_watermarkHeight = height;
    m_hasWatermarkImage = (textureId != nullptr);
}

void ImGuiWatermarkCore::ClearWatermarkTexture() {
    m_pWatermarkTexture = nullptr;
    m_watermarkWidth = 0;
    m_watermarkHeight = 0;
    m_hasWatermarkImage = false;
    m_currentImagePath.clear();
}

bool ImGuiWatermarkCore::NeedsImageReload(const std::string& newPath) const {
    return m_currentImagePath != newPath;
}

void ImGuiWatermarkCore::MarkImageLoaded(const std::string& path) {
    m_currentImagePath = path;
}

// ========== 核心渲染方法 ==========

void ImGuiWatermarkCore::RenderWatermarkContent(float windowWidth, float windowHeight) {
    // 设置全屏透明窗口
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(ImVec2(windowWidth, windowHeight));
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.0f, 0.0f, 0.0f, 0.0f));
    ImGui::Begin("WatermarkOverlay", nullptr, 
        ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | 
        ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoTitleBar |
        ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoBackground |
        ImGuiWindowFlags_NoBringToFrontOnFocus);
    
    // 渲染水印图片
    RenderWatermarkImage(windowWidth, windowHeight);
    
    // 渲染水印文字
    std::string watermarkText = ProcessWatermarkText();
    RenderWatermarkText(watermarkText, windowWidth, windowHeight);
    
    ImGui::End();
    ImGui::PopStyleColor();
}

bool ImGuiWatermarkCore::RenderLicenseWindow(bool& showLicenseWindow, float windowWidth, float windowHeight,
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
    ImGui::SetCursorPosY(60);  // 固定在右上角下方一点的位置
    
    // 入口/关闭按钮
    ImVec2 keyButtonSize(32, 32);
    ImVec2 btnPos = ImGui::GetCursorScreenPos();
    ImVec2 center(btnPos.x + keyButtonSize.x * 0.5f, btnPos.y + keyButtonSize.y * 0.5f);
    bool toggled = false;

    if (ImGui::InvisibleButton("##LicenseToggle", keyButtonSize)) {
        toggled = true;
    }

    bool hovered = ImGui::IsItemHovered();
    bool active = ImGui::IsItemActive();
    ImVec4 bgColor = ImGui::GetStyleColorVec4(active ? ImGuiCol_ButtonActive : (hovered ? ImGuiCol_ButtonHovered : ImGuiCol_Button));
    ImDrawList* draw = ImGui::GetWindowDrawList();
    draw->AddRectFilled(btnPos, ImVec2(btnPos.x + keyButtonSize.x, btnPos.y + keyButtonSize.y), ImColor(bgColor), 4.0f);

    ImU32 iconColor = ImGui::GetColorU32(ImVec4(1.0f, 1.0f, 1.0f, 1.0f));
    float side = keyButtonSize.x * 0.44f;
    float height = side * (std::sqrt(3.0f) * 0.5f);

    if (!showLicenseWindow) {
        ImVec2 p1(center.x + height * (2.0f / 3.0f), center.y);
        ImVec2 p2(center.x - height / 3.0f, center.y - side * 0.5f);
        ImVec2 p3(center.x - height / 3.0f, center.y + side * 0.5f);
        draw->AddTriangleFilled(p1, p2, p3, iconColor);
        if (toggled) showLicenseWindow = true;
    } else {
        ImVec2 p1(center.x, center.y + height * (2.0f / 3.0f));
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
        
        ImGui::InputTextMultiline("##source", m_licenseText, IM_ARRAYSIZE(m_licenseText), inputSize);
        
        ImGui::PopStyleVar();
        
        if (!m_licenseError.empty()) {
            ImGui::SetCursorPosX(20);
            ImGui::SetCursorPosY(310);
            ImGui::TextColored(ImVec4(1.0f, 0.0f, 0.0f, 1.0f), "%s", m_licenseError.c_str());
        }
        
        ImGui::SetCursorPosX((licenseWindowSize.x - 240 - 30) / 2);
        ImGui::SetCursorPosY(340);
        
        ImVec4 btn_color = ImGui::GetStyle().Colors[ImGuiCol_Button];
        ImVec4 btn_hovered_color = ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered];
        ImVec4 btn_active_color = ImGui::GetStyle().Colors[ImGuiCol_ButtonActive];
        
        ImGui::GetStyle().Colors[ImGuiCol_Button] = ImVec4(0.8f, 0.2f, 0.2f, 1.0f);
        ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered] = ImVec4(0.9f, 0.3f, 0.3f, 1.0f);
        ImGui::GetStyle().Colors[ImGuiCol_ButtonActive] = ImVec4(0.7f, 0.1f, 0.1f, 1.0f);
        
        ImVec2 buttonSize = ImVec2(120, 40);
        if (ImGui::Button("取消", buttonSize)) {
            showLicenseWindow = false;
        }
        ImGui::SameLine();
        
        ImGui::SetCursorPosX((licenseWindowSize.x - 240 - 30) / 2 + 150);
        
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
        
        ImGui::GetStyle().Colors[ImGuiCol_Button] = btn_color;
        ImGui::GetStyle().Colors[ImGuiCol_ButtonHovered] = btn_hovered_color;
        ImGui::GetStyle().Colors[ImGuiCol_ButtonActive] = btn_active_color;
        
        ImGui::End();
    }
    
    return requestExit;
}

bool ImGuiWatermarkCore::RenderSimpleLicenseWindow(bool& showLicenseWindow, float windowWidth, float windowHeight) {
    bool shouldExit = false;
    
    if (!showLicenseWindow) return false;
    
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    float winWidth = 350.0f;
    float winHeight = 200.0f;
    float winX = (windowWidth - winWidth) / 2.0f;
    float winY = (windowHeight - winHeight) / 2.0f;
    
    ImGui::SetNextWindowPos(ImVec2(winX, winY), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(ImVec2(winWidth, winHeight), ImGuiCond_FirstUseEver);
    
    ImGuiWindowFlags flags = ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoResize;
    
    if (ImGui::Begin("License", &showLicenseWindow, flags)) {
        ImGui::TextWrapped("This software is running in trial mode.");
        ImGui::Spacing();
        ImGui::Separator();
        ImGui::Spacing();
        
        ImGui::Text("App ID: %s", config.appID.c_str());
        
        ImGui::Spacing();
        ImGui::Separator();
        ImGui::Spacing();
        
        float buttonWidth = 80.0f;
        float spacing = 10.0f;
        float totalWidth = buttonWidth * 2 + spacing;
        float startX = (winWidth - totalWidth) / 2.0f;
        
        ImGui::SetCursorPosX(startX);
        if (ImGui::Button("[=]", ImVec2(buttonWidth, 30))) {
            showLicenseWindow = false;
        }
        
        ImGui::SameLine(0, spacing);
        if (ImGui::Button("[X]", ImVec2(buttonWidth, 30))) {
            shouldExit = true;
        }
    }
    ImGui::End();
    
    return shouldExit;
}

// ========== 内部渲染方法 ==========

void ImGuiWatermarkCore::RenderWatermarkImage(float windowWidth, float windowHeight) {
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
    
    if (config.imageAnimate) {
        // 碰撞边界反弹动画
        if (m_imagePosition.x + imageSize.x >= windowWidth) {
            m_imageVelocity.x = -1;
            m_imagePosition.x = windowWidth - imageSize.x;
        }
        if (m_imagePosition.x <= 0) {
            m_imageVelocity.x = 1;
            m_imagePosition.x = 0;
        }
        if (m_imagePosition.y + imageSize.y >= windowHeight) {
            m_imageVelocity.y = -1;
            m_imagePosition.y = windowHeight - imageSize.y;
        }
        if (m_imagePosition.y <= 0) {
            m_imageVelocity.y = 1;
            m_imagePosition.y = 0;
        }
        
        m_imagePosition.x += m_imageVelocity.x;
        m_imagePosition.y += m_imageVelocity.y;
        
        float maxX = (std::max)(0.0f, windowWidth - imageSize.x);
        float maxY = (std::max)(0.0f, windowHeight - imageSize.y);
        m_imagePosition.x = std::clamp(m_imagePosition.x, 0.0f, maxX);
        m_imagePosition.y = std::clamp(m_imagePosition.y, 0.0f, maxY);
        
        posX = m_imagePosition.x;
        posY = m_imagePosition.y;
    } else {
        // 静态定位
        if (config.imageAlign.find("left") != std::string::npos) {
            posX = (float)config.imagePaddingX;
        } else if (config.imageAlign.find("right") != std::string::npos) {
            posX = windowWidth - imageSize.x - config.imagePaddingX;
        } else {
            posX = (windowWidth - imageSize.x) / 2;
        }
        
        if (config.imageAlign.find("top") != std::string::npos) {
            posY = (float)config.imagePaddingY;
        } else if (config.imageAlign.find("bottom") != std::string::npos) {
            posY = windowHeight - imageSize.y - config.imagePaddingY;
        } else {
            posY = (windowHeight - imageSize.y) / 2;
        }
        
        float maxX = (std::max)(0.0f, windowWidth - imageSize.x);
        float maxY = (std::max)(0.0f, windowHeight - imageSize.y);
        posX = std::clamp(posX, 0.0f, maxX);
        posY = std::clamp(posY, 0.0f, maxY);
    }
    
    float alpha = std::clamp(config.imageAlpha, 0.3f, 1.0f);
    
    ImGui::SetCursorPos(ImVec2(posX, posY));
    ImGui::Image((ImTextureID)m_pWatermarkTexture, imageSize, 
        ImVec2(0, 0), ImVec2(1, 1), ImVec4(1, 1, 1, alpha));
}

void ImGuiWatermarkCore::RenderWatermarkText(const std::string& text, float windowWidth, float windowHeight) {
    if (text.empty()) return;
    
    // 如果有专用水印字体，使用它
    if (m_titleFont) {
        ImGui::PushFont(m_titleFont);
    }
    
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    ImFont* fontToUse = m_titleFont ? m_titleFont : ImGui::GetFont();
    ImVec2 textSize = ImGui::CalcTextSize(text.c_str());
    
    ImVec4 color = config.color;
    float baseAlpha = std::clamp(color.w, 0.15f, 0.6f);
    ImVec4 shadowColor = ImVec4(0.0f, 0.0f, 0.0f, baseAlpha * 0.5f);
    
    ImDrawList* drawList = ImGui::GetWindowDrawList();
    
    if (config.animate) {
        // === 动画模式：单个水印弹跳 ===
        color.w = baseAlpha;
        
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
        
        ImVec2 pos = m_titlePosition;
        float shadowOffset = 2.0f;
        
        drawList->AddText(fontToUse, fontToUse->FontSize,
            ImVec2(pos.x + shadowOffset, pos.y + shadowOffset),
            ImGui::ColorConvertFloat4ToU32(shadowColor), text.c_str());
        
        drawList->AddText(fontToUse, fontToUse->FontSize, pos,
            ImGui::ColorConvertFloat4ToU32(color), text.c_str());
    } else {
        // === 静态模式：专业平铺水印 ===
        float angle = -30.0f * 3.14159f / 180.0f;
        float cosA = cosf(angle);
        float sinA = sinf(angle);
        
        // 水印间距（根据文字大小自适应）
        float spacingX = textSize.x * 1.5f;  // 水平间距稍紧凑
        float spacingY = textSize.y * 2.8f;  // 垂直间距稍紧凑
        
        // 扩展绘制区域（因为倾斜需要更大范围）
        float extendX = windowHeight * fabsf(sinA) + textSize.x;
        float extendY = windowWidth * fabsf(sinA) + textSize.y;
        
        // 计算起始偏移（使水印网格居中）
        // 先计算未旋转时的网格中心，然后调整偏移
        float gridWidth = windowWidth + 2 * extendX;
        float gridHeight = windowHeight + 2 * extendY;
        
        // 计算需要多少个水印来覆盖网格
        int countX = (int)ceilf(gridWidth / spacingX) + 2;
        int countY = (int)ceilf(gridHeight / spacingY) + 2;
        
        // 使网格居中的起始偏移
        float startX = -extendX - spacingX * 0.5f;
        float startY = -extendY - spacingY * 0.5f;
        
        // 旋转后的中心偏移（使旋转后的网格居中于屏幕）
        float centerOffsetX = windowWidth * 0.5f;
        float centerOffsetY = windowHeight * 0.5f;
        
        for (float baseY = startY; baseY < windowHeight + extendY; baseY += spacingY) {
            for (float baseX = startX; baseX < windowWidth + extendX; baseX += spacingX) {
                // 以屏幕中心为原点进行旋转
                float relX = baseX - centerOffsetX;
                float relY = baseY - centerOffsetY;
                float rotatedX = relX * cosA - relY * sinA;
                float rotatedY = relX * sinA + relY * cosA;
                
                // 还原到屏幕坐标
                float finalX = rotatedX + centerOffsetX;
                float finalY = rotatedY + centerOffsetY;
                
                if (finalX > -textSize.x && finalX < windowWidth + textSize.x &&
                    finalY > -textSize.y && finalY < windowHeight + textSize.y) {
                    
                    drawList->AddText(fontToUse, fontToUse->FontSize,
                        ImVec2(finalX + 2.0f, finalY + 2.0f),
                        ImGui::ColorConvertFloat4ToU32(shadowColor), text.c_str());
                    
                    ImVec4 tileColor = color;
                    tileColor.w = baseAlpha;
                    drawList->AddText(fontToUse, fontToUse->FontSize,
                        ImVec2(finalX, finalY),
                        ImGui::ColorConvertFloat4ToU32(tileColor), text.c_str());
                }
            }
        }
    }
    
    if (m_titleFont) {
        ImGui::PopFont();
    }
}

std::string ImGuiWatermarkCore::ProcessWatermarkText() {
    WatermarkConfig config;
    {
        std::lock_guard<std::mutex> lock(m_configMutex);
        config = m_config;
    }
    
    std::string text = config.title;
    
    if (text.empty()) {
        if (!m_hasWatermarkImage) {
            text = "{APPID} Demo Version";
        } else {
            return text;
        }
    }
    
    text = std::regex_replace(text, std::regex("\\{APPID\\}"), config.appID);
    
    auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::high_resolution_clock::now() - m_startTime);
    int remain = config.timeout - (int)elapsed.count();
    remain = (std::max)(remain, 0);
    
    std::string countdown = FormatCountdown(remain);
    text = std::regex_replace(text, std::regex("\\{COUNTDOWN\\}"), countdown);
    
    return text;
}

std::string ImGuiWatermarkCore::FormatCountdown(int seconds) {
    return std::format("{:02d}:{:02d}:{:02d}", 
        seconds / 3600, (seconds % 3600) / 60, seconds % 60);
}

} // namespace LicHper
