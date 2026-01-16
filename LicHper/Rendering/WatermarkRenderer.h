#pragma once

#include "WatermarkConfig.h"
#include "ImGuiWatermarkCore.h"
#include "imgui.h"
#include <d3d11.h>
#include <string>
#include <chrono>
#include <mutex>
#include <functional>
#include <vector>
#include <memory>

namespace LicHper {

// 水印渲染组件
// 负责 ImGui 的初始化和水印的实际绘制
// 可被 OverlayRenderer 和 HookRenderer 共用
// 现在内部使用 ImGuiWatermarkCore 实现统一的渲染逻辑
class WatermarkRenderer {
public:
    WatermarkRenderer();
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
    void SetStartTime(std::chrono::high_resolution_clock::time_point startTime);
    
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
    // 重新加载字体
    void ReloadFonts();
    
    // 配置
    WatermarkConfig m_config;
    std::mutex m_configMutex;
    
    // 共享水印渲染核心
    std::unique_ptr<ImGuiWatermarkCore> m_watermarkCore;
    
    // ImGui 状态
    bool m_initialized = false;
    ImFont* m_font = nullptr;
    ImFont* m_titleFont = nullptr;
    std::vector<ImWchar> m_watermarkGlyphRanges;  // 水印字体精简字符范围
    
    // D3D11 设备（用于重新加载纹理）
    ID3D11Device* m_pDevice = nullptr;
    
    // 水印图片
    ID3D11ShaderResourceView* m_pWatermarkTexture = nullptr;
    int m_watermarkWidth = 0;
    int m_watermarkHeight = 0;
    bool m_hasWatermarkImage = false;
    std::string m_currentImagePath;  // 当前加载的图片路径
    
    // 时间
    std::chrono::high_resolution_clock::time_point m_startTime;
};

} // namespace LicHper
