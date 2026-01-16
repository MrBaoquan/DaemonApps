#pragma once

#include "WatermarkConfig.h"
#include "imgui.h"
#include <string>
#include <chrono>
#include <mutex>
#include <functional>
#include <vector>

namespace LicHper {

// ImGui 水印核心渲染逻辑
// 纯 ImGui 绘制代码，与 D3D 版本无关
// 可被 D3D11 WatermarkRenderer 和 D3D12WatermarkRenderer 共用
class ImGuiWatermarkCore {
public:
    ImGuiWatermarkCore() = default;
    ~ImGuiWatermarkCore() = default;
    
    // 禁止拷贝
    ImGuiWatermarkCore(const ImGuiWatermarkCore&) = delete;
    ImGuiWatermarkCore& operator=(const ImGuiWatermarkCore&) = delete;
    
    // ========== 配置管理 ==========
    
    // 更新配置
    void UpdateConfig(const WatermarkConfig& config);
    
    // 设置开始时间（用于倒计时）
    void SetStartTime(std::chrono::high_resolution_clock::time_point startTime) { m_startTime = startTime; }
    
    // ========== 纹理管理（由外部 Renderer 实现） ==========
    
    // 设置水印图片纹理（由具体渲染器加载后传入）
    void SetWatermarkTexture(void* textureId, int width, int height);
    
    // 清除水印图片纹理
    void ClearWatermarkTexture();
    
    // 检查是否需要重新加载图片（路径改变）
    bool NeedsImageReload(const std::string& newPath) const;
    
    // 标记图片已加载
    void MarkImageLoaded(const std::string& path);
    
    // ========== 字体管理（由外部 Renderer 实现） ==========
    
    // 设置水印字体
    void SetWatermarkFont(ImFont* font) { m_titleFont = font; }
    
    // 设置 UI 字体
    void SetUIFont(ImFont* font) { m_font = font; }
    
    // 获取当前配置的字体大小
    int GetConfiguredFontSize() const;
    
    // 检查是否需要重新加载字体（字体大小改变）
    bool NeedsFontReload(int newFontSize) const;
    
    // 标记字体已加载
    void MarkFontLoaded(int fontSize);
    
    // 构建水印精简字符范围
    static std::vector<ImWchar> BuildWatermarkGlyphRanges(const std::string& title, const std::string& appID);
    
    // ========== 核心渲染方法 ==========
    
    // 渲染水印内容（图片 + 文字）
    // 在 ImGui::NewFrame() 和 ImGui::Render() 之间调用
    void RenderWatermarkContent(float windowWidth, float windowHeight);
    
    // 渲染授权窗口
    // @param showLicenseWindow: 引用，控制窗口显示状态
    // @param windowWidth, windowHeight: 窗口尺寸
    // @param onLicenseSuccess: 授权成功时的回调
    // @return: true 表示用户请求退出
    bool RenderLicenseWindow(bool& showLicenseWindow, float windowWidth, float windowHeight,
        std::function<void()> onLicenseSuccess = nullptr);
    
    // 简化版授权窗口（用于 D3D12 早期兼容，无输入框）
    // @return: true 表示用户点击了退出按钮
    bool RenderSimpleLicenseWindow(bool& showLicenseWindow, float windowWidth, float windowHeight);
    
private:
    // ========== 内部渲染方法 ==========
    
    // 渲染水印图片
    void RenderWatermarkImage(float windowWidth, float windowHeight);
    
    // 渲染水印文字
    void RenderWatermarkText(const std::string& text, float windowWidth, float windowHeight);
    
    // 处理水印文字（替换占位符）
    std::string ProcessWatermarkText();
    
    // 格式化倒计时
    std::string FormatCountdown(int seconds);
    
    // ========== 配置 ==========
    WatermarkConfig m_config;
    mutable std::mutex m_configMutex;
    
    // ========== 字体 ==========
    ImFont* m_font = nullptr;       // UI 字体（18px）
    ImFont* m_titleFont = nullptr;  // 水印字体（可变大小）
    int m_loadedFontSize = 0;       // 已加载的字体大小
    
    // ========== 水印图片 ==========
    void* m_pWatermarkTexture = nullptr;  // ImGui 纹理 ID（ImTextureID）
    int m_watermarkWidth = 0;
    int m_watermarkHeight = 0;
    bool m_hasWatermarkImage = false;
    std::string m_currentImagePath;
    
    // ========== 动画状态 ==========
    ImVec2 m_titlePosition = ImVec2(0, 0);
    ImVec2 m_titleVelocity = ImVec2(1, 1);
    ImVec2 m_imagePosition = ImVec2(100, 100);
    ImVec2 m_imageVelocity = ImVec2(1, 1);
    
    // ========== 时间 ==========
    std::chrono::high_resolution_clock::time_point m_startTime;
    
    // ========== 授权窗口状态 ==========
    char m_licenseText[1024 * 16] = "";
    std::string m_licenseError;
};

} // namespace LicHper
