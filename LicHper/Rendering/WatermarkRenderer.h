#pragma once

#include "WatermarkConfig.h"
#include "imgui.h"
#include <d3d11.h>
#include <string>
#include <chrono>
#include <mutex>
#include <functional>

namespace LicHper {

// 水印渲染组件
// 负责 ImGui 的初始化和水印的实际绘制
// 可被 OverlayRenderer 和 HookRenderer 共用
class WatermarkRenderer {
public:
    WatermarkRenderer() = default;
    ~WatermarkRenderer();
    
    // 初始化 ImGui（需要 D3D11 设备和窗口句柄）
    bool InitializeImGui(ID3D11Device* pDevice, ID3D11DeviceContext* pContext, HWND hWnd);
    
    // 清理 ImGui
    void CleanupImGui();
    
    // 检查是否已初始化
    bool IsInitialized() const { return m_initialized; }
    
    // 更新配置
    void UpdateConfig(const WatermarkConfig& config);
    
    // 设置开始时间（用于倒计时）
    void SetStartTime(std::chrono::high_resolution_clock::time_point startTime) { m_startTime = startTime; }
    
    // 加载水印纹理
    bool LoadWatermarkTexture(ID3D11Device* pDevice);
    
    // 开始新帧
    void BeginFrame();
    
    // 结束帧并渲染
    void EndFrame();
    
    // 渲染水印内容（在 BeginFrame 和 EndFrame 之间调用）
    void RenderWatermarkContent(float windowWidth, float windowHeight);
    
    // 渲染授权窗口（可选，Overlay 模式使用）
    // @param showLicenseWindow: 引用，控制窗口显示状态
    // @param windowWidth, windowHeight: 窗口尺寸
    // @param onLicenseSuccess: 授权成功时的回调
    // @return: true 表示用户请求退出
    bool RenderLicenseWindow(bool& showLicenseWindow, float windowWidth, float windowHeight,
        std::function<void()> onLicenseSuccess = nullptr);
    
private:
    // 渲染水印图片
    void RenderWatermarkImage(float windowWidth, float windowHeight);
    
    // 渲染水印文字
    void RenderWatermarkText(const std::string& text, float windowWidth, float windowHeight);
    
    // 处理水印文字（替换占位符）
    std::string ProcessWatermarkText();
    
    // 格式化倒计时
    std::string FormatCountdown(int seconds);
    
    // 编码转换
    static std::string GbkToUtf8(const std::string& gbkStr);
    
    // 配置
    WatermarkConfig m_config;
    std::mutex m_configMutex;
    
    // ImGui 状态
    bool m_initialized = false;
    ImFont* m_font = nullptr;
    ImFont* m_titleFont = nullptr;
    
    // 水印图片
    ID3D11ShaderResourceView* m_pWatermarkTexture = nullptr;
    int m_watermarkWidth = 0;
    int m_watermarkHeight = 0;
    bool m_hasWatermarkImage = false;
    
    // 动画状态
    ImVec2 m_titlePosition = ImVec2(0, 0);
    ImVec2 m_titleVelocity = ImVec2(1, 1);
    
    // 时间
    std::chrono::high_resolution_clock::time_point m_startTime;
    
    // 授权窗口状态
    char m_licenseText[1024 * 16] = "";
    std::string m_licenseError;
};

} // namespace LicHper
