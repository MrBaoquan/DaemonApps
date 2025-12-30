#pragma once

#include "IWatermarkRenderer.h"
#include "WatermarkConfig.h"
#include <memory>
#include <string>
#include <mutex>
#include <atomic>

namespace LicHper {

// 渲染管理器
// 负责检测环境、选择合适的渲染模式、管理渲染器生命周期
class RenderManager {
public:
    static RenderManager& Instance();
    
    // 初始化渲染管理器
    // @param hWndHost: 宿主窗口句柄（可为空）
    // @param forceMode: 强制使用的渲染模式（Unknown=自动检测）
    // @return: 初始化是否成功
    bool Initialize(HWND hWndHost = nullptr, RenderMode forceMode = RenderMode::Unknown);
    
    // 加载配置（从 INI 文件）
    bool LoadConfig(const std::string& iniPath = "");
    
    // 更新配置
    void UpdateConfig(const WatermarkConfig& config);
    
    // 获取当前配置
    const WatermarkConfig& GetConfig() const { return m_config; }
    
    // 运行渲染循环（阻塞）
    void Run();
    
    // 停止渲染
    void Stop();
    
    // 关闭并清理
    void Shutdown();
    
    // 获取当前渲染模式
    RenderMode GetCurrentMode() const;
    
    // 获取渲染器实例
    IWatermarkRenderer* GetRenderer() const { return m_renderer.get(); }
    
    // 检测最佳渲染模式
    static RenderMode DetectBestMode();
    
private:
    RenderManager() = default;
    ~RenderManager() = default;
    RenderManager(const RenderManager&) = delete;
    RenderManager& operator=(const RenderManager&) = delete;
    
    // 创建渲染器
    std::unique_ptr<IWatermarkRenderer> CreateRenderer(RenderMode mode);
    
    // 解析 INI 配置
    bool ParseIniConfig(const std::string& iniPath);
    
    // 生成默认配置文件
    void GenerateDefaultConfig(const std::string& iniPath);
    
    // 验证水印配置
    void ValidateConfig();
    
    // 成员变量
    std::unique_ptr<IWatermarkRenderer> m_renderer;
    WatermarkConfig m_config;
    HWND m_hwndHost = nullptr;
    bool m_initialized = false;
    std::atomic<bool> m_running{false};  // 是否正在运行
    std::mutex m_initMutex;  // 初始化锁
};

} // namespace LicHper
