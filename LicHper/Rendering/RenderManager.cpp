#include "RenderManager.h"
#include "OverlayRenderer.h"
#include "HookRenderer.h"
#include "Logger.h"
#include "../mINI/ini.h"

#include <regex>
#include <algorithm>
#include <filesystem>

// 外部声明（全局命名空间）
extern std::string g_appID;
std::string GetUserFolder();

namespace LicHper {

// 渲染模式名称
static const char* RenderModeToString(RenderMode mode) {
    switch (mode) {
        case RenderMode::Overlay: return "Overlay";
        case RenderMode::Hook:    return "Hook";
        default:                  return "Unknown";
    }
}

RenderManager& RenderManager::Instance() {
    static RenderManager instance;
    return instance;
}

RenderMode RenderManager::DetectBestMode() {
    // 检查宿主是否已加载 DirectX
    bool hasDirectX = HookRenderer::IsHostUsingDirectX();
    
    LOG_INFO("DirectX detection: d3d11.dll/dxgi.dll loaded = {}", hasDirectX ? "true" : "false");
    
    if (hasDirectX) {
        // 宿主使用 DirectX，尝试 Hook 模式
        LOG_INFO("Recommended mode: Hook (host uses DirectX)");
        return RenderMode::Hook;
    }
    
    // 默认使用透明窗口模式
    LOG_INFO("Recommended mode: Overlay (default)");
    return RenderMode::Overlay;
}

bool RenderManager::Initialize(HWND hWndHost, RenderMode forceMode) {
    // 加锁防止并发初始化
    std::lock_guard<std::mutex> lock(m_initMutex);
    
    // 如果已经初始化或正在运行，直接返回
    if (m_initialized || m_running.load()) {
        LOG_INFO("RenderManager already initialized or running, skipping");
        return true;
    }
    
    // 初始化日志系统
    std::string lichperFolder = GetUserFolder() + "\\.lichper";
    std::filesystem::create_directories(lichperFolder);
    Logger::Instance().Initialize(lichperFolder);
    
    LOG_INFO("=== RenderManager Initialization ===");
    LOG_INFO("AppID: {}", g_appID);
    LOG_INFO("Host HWND: 0x{:X}", reinterpret_cast<uintptr_t>(hWndHost));
    
    m_hwndHost = hWndHost;
    
    // 确定渲染模式
    RenderMode mode = (forceMode != RenderMode::Unknown) ? forceMode : DetectBestMode();
    LOG_INFO("Selected render mode: {} (forced={})", 
        RenderModeToString(mode), forceMode != RenderMode::Unknown ? "true" : "false");
    
    // 创建渲染器
    m_renderer = CreateRenderer(mode);
    if (!m_renderer) {
        LOG_ERROR("Failed to create {} renderer", RenderModeToString(mode));
        // 如果 Hook 模式失败，回退到 Overlay 模式
        if (mode == RenderMode::Hook) {
            LOG_WARNING("Falling back to Overlay mode");
            m_renderer = CreateRenderer(RenderMode::Overlay);
        }
    }
    
    if (!m_renderer) {
        LOG_ERROR("Failed to create any renderer");
        return false;
    }
    
    LOG_INFO("Renderer created: {}", RenderModeToString(m_renderer->GetMode()));
    
    // 初始化渲染器
    if (!m_renderer->Initialize(hWndHost)) {
        LOG_ERROR("{} renderer initialization failed", RenderModeToString(mode));
        // 如果是 Hook 模式失败，尝试回退
        if (mode == RenderMode::Hook) {
            LOG_WARNING("Hook initialization failed, falling back to Overlay mode");
            m_renderer = CreateRenderer(RenderMode::Overlay);
            if (m_renderer && !m_renderer->Initialize(hWndHost)) {
                LOG_ERROR("Overlay renderer initialization also failed");
                m_renderer.reset();
                return false;
            }
        } else {
            m_renderer.reset();
            return false;
        }
    }
    
    LOG_INFO("Renderer initialized successfully: {}", RenderModeToString(m_renderer->GetMode()));
    
    // 应用配置
    m_renderer->UpdateConfig(m_config);
    
    m_initialized = true;
    return true;
}

bool RenderManager::LoadConfig(const std::string& iniPath) {
    std::string path = iniPath;
    if (path.empty()) {
        path = GetUserFolder() + "\\.authrc.ini";
    }
    
    LOG_INFO("Loading config from: {}", path);
    bool result = ParseIniConfig(path);
    if (result) {
        LOG_INFO("Config loaded successfully");
        LOG_INFO("  - Title: {}", m_config.title);
        LOG_INFO("  - FontSize: {}", m_config.fontSize);
        LOG_INFO("  - Animate: {}", m_config.animate ? "true" : "false");
        LOG_INFO("  - ImagePath: {}", m_config.imagePath.empty() ? "(default)" : m_config.imagePath);
        LOG_INFO("  - ImageScale: {:.2f}", m_config.imageScale);
        LOG_INFO("  - ImageAlign: {}", m_config.imageAlign);
        LOG_INFO("  - Timeout: {}s", m_config.timeout);
    } else {
        LOG_WARNING("Failed to load config, using defaults");
    }
    return result;
}

void RenderManager::UpdateConfig(const WatermarkConfig& config) {
    m_config = config;
    ValidateConfig();
    
    if (m_renderer) {
        m_renderer->UpdateConfig(m_config);
    }
}

void RenderManager::Run() {
    if (!m_initialized || !m_renderer) {
        LOG_ERROR("Cannot run: not initialized or no renderer");
        return;
    }
    
    m_running.store(true);
    LOG_INFO("Starting render loop with {} mode", RenderModeToString(m_renderer->GetMode()));
    m_renderer->RunRenderLoop();
    
    // 检查是否需要回退到 Overlay 模式
    if (m_renderer->NeedsFallback() && m_renderer->GetMode() == RenderMode::Hook) {
        LOG_INFO("Hook mode failed, falling back to Overlay mode...");
        
        // 关闭当前渲染器
        m_renderer->Shutdown();
        m_renderer.reset();
        
        // 创建 Overlay 渲染器
        m_renderer = CreateRenderer(RenderMode::Overlay);
        if (m_renderer && m_renderer->Initialize(m_hwndHost)) {
            LOG_INFO("Fallback to Overlay mode successful");
            m_renderer->UpdateConfig(m_config);
            m_renderer->RunRenderLoop();
        } else {
            LOG_ERROR("Fallback to Overlay mode failed");
        }
    }
    
    LOG_INFO("Render loop ended");
    m_running.store(false);
}

void RenderManager::Stop() {
    LOG_INFO("Stop requested");
    if (m_renderer) {
        // 触发停止（通过设置运行状态）
        // 具体实现依赖于渲染器
    }
}

void RenderManager::Shutdown() {
    std::lock_guard<std::mutex> lock(m_initMutex);
    
    LOG_INFO("Shutting down RenderManager");
    m_running.store(false);
    
    if (m_renderer) {
        m_renderer->Shutdown();
        m_renderer.reset();
    }
    m_initialized = false;
    LOG_INFO("RenderManager shutdown complete");
}

RenderMode RenderManager::GetCurrentMode() const {
    if (m_renderer) {
        return m_renderer->GetMode();
    }
    return RenderMode::Unknown;
}

std::unique_ptr<IWatermarkRenderer> RenderManager::CreateRenderer(RenderMode mode) {
    switch (mode) {
    case RenderMode::Hook:
        return std::make_unique<HookRenderer>();
    case RenderMode::Overlay:
    default:
        return std::make_unique<OverlayRenderer>();
    }
}

bool RenderManager::ParseIniConfig(const std::string& iniPath) {
    mINI::INIFile file(iniPath);
    mINI::INIStructure ini;
    
    if (!file.read(ini)) {
        // 配置文件不存在，生成默认配置
        GenerateDefaultConfig(iniPath);
        file.read(ini);
    }
    
    // 解析 watermark 节
    if (ini.has("watermark")) {
        auto& wm = ini["watermark"];
        
        // 标题
        if (wm.has("title")) {
            m_config.title = wm["title"];
        }
        
        // 字体大小
        if (wm.has("font_size")) {
            m_config.fontSize = std::stoi(wm["font_size"]);
        }
        
        // 颜色
        if (wm.has("color")) {
            m_config.color = HexToColor(wm["color"]);
        }
        
        // 动画
        if (wm.has("animate")) {
            m_config.animate = (wm["animate"] == "true");
        }
        
        // 图片路径
        if (wm.has("image_path")) {
            m_config.imagePath = wm["image_path"];
        }
        
        // 图片缩放
        if (wm.has("image_scale")) {
            m_config.imageScale = std::stof(wm["image_scale"]);
        }
        
        // 图片透明度
        if (wm.has("image_alpha")) {
            m_config.imageAlpha = std::stof(wm["image_alpha"]);
        }
        
        // 图片对齐
        if (wm.has("image_align")) {
            m_config.imageAlign = wm["image_align"];
        }
        
        // 图片边距
        if (wm.has("image_padding_x")) {
            m_config.imagePaddingX = std::stoi(wm["image_padding_x"]);
        }
        if (wm.has("image_padding_y")) {
            m_config.imagePaddingY = std::stoi(wm["image_padding_y"]);
        }
    }
    
    // 解析 program 节
    if (ini.has("program")) {
        auto& prog = ini["program"];
        
        // 超时时间
        if (prog.has("timeout")) {
            m_config.timeout = std::stoi(prog["timeout"]);
        }
        
        // 超时关闭自身
        if (prog.has("timeout_kill_self")) {
            m_config.timeoutKillSelf = (prog["timeout_kill_self"] == "true");
        }
        
        // 超时关闭其他进程
        if (prog.has("timeout_kill_other")) {
            std::string killList = prog["timeout_kill_other"];
            if (!killList.empty()) {
                std::regex re(R"(\|)");
                std::sregex_token_iterator first{killList.begin(), killList.end(), re, -1}, last;
                m_config.timeoutKillOther = {first, last};
                
                // 移除非 .exe 进程
                m_config.timeoutKillOther.erase(
                    std::remove_if(m_config.timeoutKillOther.begin(), 
                                   m_config.timeoutKillOther.end(),
                                   [](const std::string& s) { 
                                       return s.find(".exe") == std::string::npos; 
                                   }),
                    m_config.timeoutKillOther.end());
            }
        }
    }
    
    // 设置 AppID
    m_config.appID = g_appID;
    
    // 验证配置
    ValidateConfig();
    
    return true;
}

void RenderManager::GenerateDefaultConfig(const std::string& iniPath) {
    mINI::INIFile file(iniPath);
    mINI::INIStructure ini;
    
    ini["help"]["description"] = "\r\n {APPID} 为授权软件ID, 在显示时会被替换为真实ID, {COUNTDOWN}为程序退出倒计时 \n timeout_kill_self 超时是否关闭主进程 \n timeout_kill_other 为退出时同时关闭的进程列表, 多个进程用 | 分隔";
    
    ini["watermark"]["title"] = "{APPID} Demo Version";
    ini["watermark"]["font_size"] = "80";
    ini["watermark"]["color"] = "#FF6666";
    ini["watermark"]["animate"] = "true";
    ini["watermark"]["image_path"] = "";
    ini["watermark"]["image_scale"] = "1";
    ini["watermark"]["image_alpha"] = "0.8";
    ini["watermark"]["image_align"] = "top-center";
    ini["watermark"]["image_padding_x"] = "50";
    ini["watermark"]["image_padding_y"] = "50";
    
    ini["program"]["timeout"] = "60";
    ini["program"]["timeout_kill_self"] = "false";
    ini["program"]["timeout_kill_other"] = "";
    
    file.generate(ini, true);
}

void RenderManager::ValidateConfig() {
    // 验证水印文字
    std::string textWithoutPlaceholders = std::regex_replace(
        m_config.title, std::regex("\\{APPID\\}|\\{COUNTDOWN\\}"), "");
    textWithoutPlaceholders.erase(
        std::remove_if(textWithoutPlaceholders.begin(), textWithoutPlaceholders.end(),
            [](unsigned char c) { return std::isspace(c); }),
        textWithoutPlaceholders.end());
    
    if (textWithoutPlaceholders.size() < 2) {
        m_config.title = "{APPID} Demo Version";
    }
    
    // 验证字体大小
    m_config.fontSize = std::clamp(m_config.fontSize, 36, 132);
    
    // 验证颜色透明度
    if (m_config.color.w < 0.5f) {
        m_config.color.w = 0.5f;
    }
    
    // 验证图片缩放
    m_config.imageScale = std::clamp(m_config.imageScale, 0.1f, 10.0f);
    
    // 验证图片透明度
    m_config.imageAlpha = std::clamp(m_config.imageAlpha, 0.3f, 1.0f);
    
    // 验证边距
    m_config.imagePaddingX = (std::max)(0, m_config.imagePaddingX);
    m_config.imagePaddingY = (std::max)(0, m_config.imagePaddingY);
}

} // namespace LicHper
