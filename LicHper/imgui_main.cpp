// LicHper 水印渲染入口
// 使用模块化渲染架构，支持透明窗口和 DirectX Hook 两种模式

#pragma execution_character_set("utf-8")

#include "Rendering/RenderManager.h"
#include "Rendering/WatermarkConfig.h"

#include <string>
#include <Windows.h>
#include <ShlObj.h>

// stb_image 实现
#define STB_IMAGE_IMPLEMENTATION
#include "stb/stb_image.h"

#include <mutex>
#include <atomic>

// 全局状态：防止多次调用
static std::mutex g_initMutex;
static std::atomic<bool> g_isRunning{false};

// 外部声明
extern std::string g_appID;
std::string GetUserFolder();

// UTF-8 转 GBK
std::string Utf8ToGbk(const std::string& utf8Str) {
    int len = MultiByteToWideChar(CP_UTF8, 0, utf8Str.c_str(), -1, NULL, 0);
    wchar_t* wstr = new wchar_t[len + 1];
    memset(wstr, 0, len + 1);
    MultiByteToWideChar(CP_UTF8, 0, utf8Str.c_str(), -1, wstr, len);

    len = WideCharToMultiByte(CP_ACP, 0, wstr, -1, NULL, 0, NULL, NULL);
    char* str = new char[len + 1];
    memset(str, 0, len + 1);
    WideCharToMultiByte(CP_ACP, 0, wstr, -1, str, len, NULL, NULL);

    std::string strTemp = str;
    if (wstr) delete[] wstr;
    if (str) delete[] str;

    return strTemp;
}

// 请求退出所有目标窗口（外部声明）
void reqQuitAllTargetWindows();

// 通过授权码续期（外部声明）
int RenewByLicense(const char* key);

// 初始化并运行水印渲染
// @param appID: 应用程序 ID
// @param forceOverlay: 是否强制使用透明窗口模式
// @return: 0=成功，非0=失败
int initImgui() {
    // 防止多次调用
    {
        std::lock_guard<std::mutex> lock(g_initMutex);
        if (g_isRunning.load()) {
            // 已经在运行，直接返回成功
            return 0;
        }
        g_isRunning.store(true);
    }
    
    auto& manager = LicHper::RenderManager::Instance();
    
    // 加载配置
    manager.LoadConfig();
    
    // 更新 AppID
    auto config = manager.GetConfig();
    config.appID = g_appID;
    manager.UpdateConfig(config);
    
    // 初始化渲染器（自动选择模式）
    // 如果宿主使用 DirectX (Unity/UE)，尝试 Hook 模式
    // 否则使用透明窗口模式
    if (!manager.Initialize(nullptr)) {
        g_isRunning.store(false);
        return 1;
    }
    
    // 运行渲染循环
    manager.Run();
    
    // 清理
    manager.Shutdown();
    
    g_isRunning.store(false);
    return 0;
}

// 初始化水印渲染（指定模式）
// @param mode: 渲染模式 (0=自动, 1=透明窗口, 2=Hook)
// @return: 0=成功，非0=失败
int initImguiWithMode(int mode) {
    // 防止多次调用
    {
        std::lock_guard<std::mutex> lock(g_initMutex);
        if (g_isRunning.load()) {
            // 已经在运行，直接返回成功
            return 0;
        }
        g_isRunning.store(true);
    }
    
    auto& manager = LicHper::RenderManager::Instance();
    
    // 加载配置
    manager.LoadConfig();
    
    // 更新 AppID
    auto config = manager.GetConfig();
    config.appID = g_appID;
    manager.UpdateConfig(config);
    
    // 确定渲染模式
    LicHper::RenderMode renderMode = LicHper::RenderMode::Unknown;
    switch (mode) {
    case 1:
        renderMode = LicHper::RenderMode::Overlay;
        break;
    case 2:
        renderMode = LicHper::RenderMode::Hook;
        break;
    default:
        renderMode = LicHper::RenderMode::Unknown; // 自动检测
        break;
    }
    
    // 初始化渲染器
    if (!manager.Initialize(nullptr, renderMode)) {
        g_isRunning.store(false);
        return 1;
    }
    
    // 运行渲染循环
    manager.Run();
    
    // 清理
    manager.Shutdown();
    
    g_isRunning.store(false);
    return 0;
}

// 停止水印渲染
void stopImgui() {
    auto& manager = LicHper::RenderManager::Instance();
    manager.Shutdown();
    g_isRunning.store(false);
}
