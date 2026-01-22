#include "RenderManager.h"
#include "OverlayRenderer.h"
#include "HookRenderer.h"
#include "Logger.h"
#include "../mINI/ini.h"
#include "../stb/stb_image.h"

#include <regex>
#include <algorithm>
#include <filesystem>

// 外部声明（全局命名空间）
extern std::string g_appID;
std::string GetUserFolder();

namespace LicHper {

// 验证水印图片是否有效（至少 1% 像素可见，支持透明背景水印）
static bool IsValidWatermarkImage(const std::string& imagePath) {
    if (!std::filesystem::exists(imagePath)) {
        LOG_WARNING("IsValidWatermarkImage: File not found: {}", imagePath);
        return false;
    }
    
    int width, height;
    unsigned char* data = stbi_load(imagePath.c_str(), &width, &height, NULL, 4);
    if (!data) {
        LOG_WARNING("IsValidWatermarkImage: Failed to load image: {}", imagePath);
        return false;
    }
    
    // 验证图片内容 - 至少 1% 像素可见（降低阈值以支持透明背景水印）
    int totalPixels = width * height;
    int visiblePixels = 0;
    int minRequired = totalPixels / 100;  // 1% 而不是 10%
    
    for (int i = 0; i < totalPixels && visiblePixels < minRequired; i++) {
        unsigned char a = data[i * 4 + 3];  // alpha
        unsigned char r = data[i * 4 + 0];
        unsigned char g = data[i * 4 + 1];
        unsigned char b = data[i * 4 + 2];
        if (a > 30 && (r > 10 || g > 10 || b > 10)) {
            visiblePixels++;
        }
    }
    
    stbi_image_free(data);
    bool isValid = visiblePixels >= minRequired;
    if (!isValid) {
        LOG_WARNING("IsValidWatermarkImage: Image is mostly transparent - {}x{}, visible: {}/{}", 
                    width, height, visiblePixels, minRequired);
    }
    return isValid;
}

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
    // 检查是否是 WPF 应用（WPF 使用 DirectComposition，不走标准 DXGI Present）
    bool isWpfApp = (GetModuleHandleA("PresentationCore.dll") != nullptr) ||
                     (GetModuleHandleA("wpfgfx_v0400.dll") != nullptr) ||
                     (GetModuleHandleA("wpfgfx_cor3.dll") != nullptr);
    
    if (isWpfApp) {
        LOG_INFO("WPF application detected, forcing Overlay mode (WPF doesn't call DXGI Present)");
        return RenderMode::Overlay;
    }
    
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
    
    LOG_INFO("=== LoadConfig START ===");
    LOG_INFO("Config file path: {}", path);
    LOG_INFO("File exists: {}", std::filesystem::exists(path) ? "YES" : "NO");
    
    bool result = ParseIniConfig(path);
    if (result) {
        LOG_INFO("Config loaded successfully");
        LOG_INFO("  - Title: {}", m_config.title);
        LOG_INFO("  - FontSize: {} (RAW VALUE FROM INI)", m_config.fontSize);
        LOG_INFO("  - Animate: {}", m_config.animate ? "true" : "false");
        LOG_INFO("  - ImagePath: {}", m_config.imagePath.empty() ? "(default)" : m_config.imagePath);
        LOG_INFO("  - ImageScale: {:.2f}", m_config.imageScale);
        LOG_INFO("  - ImageAlign: {}", m_config.imageAlign);
        LOG_INFO("  - ImageAnimate: {}", m_config.imageAnimate ? "true" : "false");
        LOG_INFO("  - Timeout: {}s", m_config.timeout);
    } else {
        LOG_WARNING("Failed to load config, using defaults");
        LOG_WARNING("Default fontSize: {}", m_config.fontSize);
    }
    LOG_INFO("=== LoadConfig END ===");
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
    
    // 构建要查找的 section 名称：首先查找特定应用配置，再查找默认配置
    std::string watermarkSection = "watermark:default";
    std::string programSection = "program:default";
    
    // 如果存在特定应用配置，使用特定应用配置
    std::string appWatermarkSection = "watermark:" + g_appID;
    std::string appProgramSection = "program:" + g_appID;
    
    // 检查特定应用的水印配置是否存在
    if (ini.has(appWatermarkSection)) {
        watermarkSection = appWatermarkSection;
    }
    // 向后兼容：检查旧的 "watermark" 节（不带冒号的格式）
    else if (ini.has("watermark")) {
        watermarkSection = "watermark";
    }
    
    // 检查特定应用的程序配置是否存在
    if (ini.has(appProgramSection)) {
        programSection = appProgramSection;
    }
    // 向后兼容：检查旧的 "program" 节
    else if (ini.has("program")) {
        programSection = "program";
    }
    
    LOG_INFO("Loading watermark config from section: [{}]", watermarkSection);
    LOG_INFO("Loading program config from section: [{}]", programSection);
    
    // 解析水印节
    if (ini.has(watermarkSection)) {
        auto& wm = ini[watermarkSection];
        
        // 标题
        if (wm.has("title")) {
            m_config.title = wm["title"];
        }
        
        // 字体大小
        if (wm.has("font_size")) {
            std::string fontSizeStr = wm["font_size"];
            m_config.fontSize = std::stoi(fontSizeStr);
            LOG_INFO("ParseIniConfig: font_size = '{}' -> parsed as {}", fontSizeStr, m_config.fontSize);
        } else {
            LOG_WARNING("ParseIniConfig: font_size NOT found in [{}], using default: {}", watermarkSection, m_config.fontSize);
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
            LOG_INFO("ParseIniConfig: image_path = '{}'", m_config.imagePath);
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
        
        // 图片动画
        if (wm.has("image_animate")) {
            m_config.imageAnimate = (wm["image_animate"] == "true");
        }
    }
    
    // 解析程序节
    if (ini.has(programSection)) {
        auto& prog = ini[programSection];
        
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
    
    ini["help"]["description"] = " {APPID} 为授权软件ID, 在显示时会被替换为真实ID, {COUNTDOWN}为程序退出倒计时 \n timeout_kill_self 超时是否关闭主进程 \n timeout_kill_other 为退出时同时关闭的进程列表, 多个进程用 | 分隔 \n \n 配置说明: \n - [watermark:default] 为所有应用的默认水印配置 \n - [watermark:具体appid] 用来覆盖特定应用的水印配置 \n - [program:default] 为所有应用的默认程序配置 \n - [program:具体appid] 用来覆盖特定应用的程序配置";
    
    // 默认水印配置
    ini["watermark:default"]["title"] = "{APPID} Demo Version";
    ini["watermark:default"]["font_size"] = "80";
    ini["watermark:default"]["color"] = "#dc2626ff";
    ini["watermark:default"]["animate"] = "true";
    ini["watermark:default"]["image_path"] = "";
    ini["watermark:default"]["image_scale"] = "1";
    ini["watermark:default"]["image_alpha"] = "0.8";
    ini["watermark:default"]["image_align"] = "top-center";
    ini["watermark:default"]["image_padding_x"] = "50";
    ini["watermark:default"]["image_padding_y"] = "50";
    ini["watermark:default"]["image_animate"] = "false";
    
    // 默认程序配置
    ini["program:default"]["timeout"] = "60";
    ini["program:default"]["timeout_kill_self"] = "false";
    ini["program:default"]["timeout_kill_other"] = "";
    
    // 示例：应用 app001 的特定配置
    ini["watermark:app001"]["title"] = "App001 - Unlicensed Version";
    ini["watermark:app001"]["font_size"] = "100";
    ini["watermark:app001"]["color"] = "#FF0000";
    ini["watermark:app001"]["animate"] = "true";
    ini["watermark:app001"]["image_path"] = "watermark_app001.png";
    ini["watermark:app001"]["image_align"] = "bottom-right";
    
    ini["program:app001"]["timeout"] = "120";
    ini["program:app001"]["timeout_kill_self"] = "true";
    ini["program:app001"]["timeout_kill_other"] = "notepad.exe|calculator.exe";
    
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
        // 检查是否有有效的水印图片
        std::string imagePath = m_config.imagePath;
        std::string lichperFolder = GetUserFolder() + "\\.lichper";
        
        if (imagePath.empty()) {
            imagePath = lichperFolder + "\\watermark.png";
        } else if (imagePath.find(':') == std::string::npos && 
                   imagePath[0] != '\\' && imagePath[0] != '/') {
            imagePath = lichperFolder + "\\" + imagePath;
        }
        
        // 仅在没有有效水印图片时才使用默认标题
        if (!IsValidWatermarkImage(imagePath)) {
            LOG_INFO("ValidateConfig: No valid watermark image, using default title");
            m_config.title = "{APPID} Demo Version";
        } else {
            LOG_INFO("ValidateConfig: Valid watermark image exists, empty title allowed");
        }
    }
    
    // 验证字体大小
    int originalFontSize = m_config.fontSize;
    // 使用精简字符范围后可支持更大字体（最大 300px）
    m_config.fontSize = std::clamp(m_config.fontSize, 18, 300);
    if (originalFontSize != m_config.fontSize) {
        LOG_WARNING("ValidateConfig: fontSize clamped from {} to {}", originalFontSize, m_config.fontSize);
    } else {
        LOG_INFO("ValidateConfig: fontSize validated: {}", m_config.fontSize);
    }
    
    // 验证颜色透明度（允许更淡的水印，最低 15%）
    if (m_config.color.w < 0.15f) {
        LOG_WARNING("ValidateConfig: color alpha too low, clamping to 0.15");
        m_config.color.w = 0.15f;
    } else if (m_config.color.w > 1.0f) {
        LOG_WARNING("ValidateConfig: color alpha too high, clamping to 1.0");
        m_config.color.w = 1.0f;
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
