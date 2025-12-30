#pragma once

#define SPDLOG_ACTIVE_LEVEL SPDLOG_LEVEL_DEBUG
#include <spdlog/spdlog.h>
#include <spdlog/sinks/basic_file_sink.h>
#include <spdlog/sinks/rotating_file_sink.h>
#include <memory>
#include <string>
#include <filesystem>

namespace LicHper {

// 日志管理器
// 使用 spdlog 库，将日志写入 .lichper 目录下的 lichper.log 文件
class Logger {
public:
    static Logger& Instance() {
        static Logger instance;
        return instance;
    }
    
    // 初始化日志（指定日志目录）
    void Initialize(const std::string& lichperFolder) {
        if (m_initialized) return;
        
        try {
            // 确保目录存在
            std::filesystem::create_directories(lichperFolder);
            
            std::string logPath = lichperFolder + "\\lichper.log";
            
            // 创建滚动日志文件（最大 5MB，保留 3 个备份）
            auto file_sink = std::make_shared<spdlog::sinks::rotating_file_sink_mt>(
                logPath, 5 * 1024 * 1024, 3);
            
            // 创建 logger
            m_logger = std::make_shared<spdlog::logger>("lichper", file_sink);
            
            // 设置日志格式：[时间] [级别] [来源] 消息
            m_logger->set_pattern("[%Y-%m-%d %H:%M:%S.%e] [%^%l%$] %v");
            
            // 设置日志级别
            m_logger->set_level(spdlog::level::debug);
            
            // 立即刷新
            m_logger->flush_on(spdlog::level::debug);
            
            // 设置为默认 logger
            spdlog::set_default_logger(m_logger);
            
            m_initialized = true;
            
            // 记录启动信息
            m_logger->info("================================================================================");
            m_logger->info("  LicHper Session Started");
            m_logger->info("================================================================================");
            
        } catch (const spdlog::spdlog_ex& ex) {
            // 日志初始化失败，静默处理
            (void)ex;
        }
    }
    
    // 获取 logger 实例
    std::shared_ptr<spdlog::logger> GetLogger() {
        return m_logger;
    }
    
    // 检查是否已初始化
    bool IsInitialized() const { return m_initialized; }
    
    // 关闭日志
    void Shutdown() {
        if (m_logger) {
            m_logger->info("LicHper Session Ended");
            m_logger->flush();
        }
        spdlog::shutdown();
        m_initialized = false;
    }
    
private:
    Logger() = default;
    ~Logger() { Shutdown(); }
    
    std::shared_ptr<spdlog::logger> m_logger;
    bool m_initialized = false;
};

// 便捷宏 - 使用 spdlog 的格式化功能
#define LOG_DEBUG(...)   do { if (LicHper::Logger::Instance().IsInitialized()) SPDLOG_DEBUG(__VA_ARGS__); } while(0)
#define LOG_INFO(...)    do { if (LicHper::Logger::Instance().IsInitialized()) SPDLOG_INFO(__VA_ARGS__); } while(0)
#define LOG_WARNING(...) do { if (LicHper::Logger::Instance().IsInitialized()) SPDLOG_WARN(__VA_ARGS__); } while(0)
#define LOG_ERROR(...)   do { if (LicHper::Logger::Instance().IsInitialized()) SPDLOG_ERROR(__VA_ARGS__); } while(0)

} // namespace LicHper
