#pragma once

#include <d3d11.h>
#include <functional>
#include "imgui.h"
#include "WatermarkConfig.h"

namespace LicHper {

// 渲染模式枚举
enum class RenderMode {
    Unknown,
    Overlay,    // 透明窗口覆盖模式（默认）
    Hook        // DirectX Hook 模式
};

// 水印渲染器接口
class IWatermarkRenderer {
public:
    virtual ~IWatermarkRenderer() = default;
    
    // 初始化渲染器
    // @param hWndHost: 宿主窗口句柄
    // @return: 初始化是否成功
    virtual bool Initialize(HWND hWndHost) = 0;
    
    // 更新配置
    virtual void UpdateConfig(const WatermarkConfig& config) = 0;
    
    // 主渲染循环（阻塞调用，直到程序退出）
    virtual void RunRenderLoop() = 0;
    
    // 关闭渲染器
    virtual void Shutdown() = 0;
    
    // 获取渲染模式
    virtual RenderMode GetMode() const = 0;
    
    // 检查是否正在运行
    virtual bool IsRunning() const = 0;
    
    // 设置退出回调
    using ExitCallback = void(*)(int exitCode);
    virtual void SetExitCallback(ExitCallback callback) = 0;
    
    // 设置回退回调（当 Hook 模式失败时调用）
    using FallbackCallback = std::function<void()>;
    virtual void SetFallbackCallback(FallbackCallback callback) {}
    
    // 检查是否需要回退到其他模式
    virtual bool NeedsFallback() const { return false; }
};

} // namespace LicHper
