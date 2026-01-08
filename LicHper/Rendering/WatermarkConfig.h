#pragma once

#include <string>
#include <vector>
#include "imgui.h"

namespace LicHper {

// 水印配置结构
struct WatermarkConfig {
    // 文字水印
    std::string title = "{APPID} Demo Version";  // 默认值，仅在有图片水印时允许为空
    int fontSize = 80;
    ImVec4 color = ImVec4(1.0f, 0.4f, 0.4f, 1.0f);
    bool animate = true;
    
    // 图片水印
    std::string imagePath;
    float imageScale = 1.0f;
    float imageAlpha = 0.8f;
    std::string imageAlign = "top-center";
    int imagePaddingX = 50;
    int imagePaddingY = 50;
    bool imageAnimate = false;  // 图片是否启用动画移动
    
    // 程序设置
    int timeout = 60;
    bool timeoutKillSelf = false;
    std::vector<std::string> timeoutKillOther;
    
    // AppID (运行时设置)
    std::string appID;
};

// 颜色转换工具
inline ImVec4 HexToColor(const std::string& hex) {
    ImVec4 color(1.0f, 0.4f, 0.4f, 1.0f);
    if (hex.empty()) return color;
    
    std::string _hex = hex;
    if (_hex[0] == '#') _hex = _hex.substr(1);
    
    if (_hex.size() >= 6) {
        int r = std::stoi(_hex.substr(0, 2), nullptr, 16);
        int g = std::stoi(_hex.substr(2, 2), nullptr, 16);
        int b = std::stoi(_hex.substr(4, 2), nullptr, 16);
        color.x = r / 255.0f;
        color.y = g / 255.0f;
        color.z = b / 255.0f;
    }
    if (_hex.size() >= 8) {
        int a = std::stoi(_hex.substr(6, 2), nullptr, 16);
        color.w = a / 255.0f;
    }
    return color;
}

} // namespace LicHper
