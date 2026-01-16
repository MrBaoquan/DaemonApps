# LicHper 水印配置优化说明

## 功能概述

LicHper 现在支持**每个应用有不同的水印和程序配置**，通过继承默认配置并允许特定应用覆盖的方式实现。

## 配置文件格式

### 新格式结构

```ini
; 默认配置（所有应用都使用，除非被覆盖）
[watermark:default]
[program:default]

; 特定应用的配置（会覆盖默认配置）
[watermark:appid1]
[program:appid1]

[watermark:appid2]
[program:appid2]
```

### 配置查找优先级

1. **首先查找**：`[watermark:具体appid]` / `[program:具体appid]`
2. **其次查找**：`[watermark:default]` / `[program:default]`
3. **向后兼容**：`[watermark]` / `[program]`（旧格式）

## 完整配置示例

### 基础默认配置

```ini
[watermark:default]
title = {APPID} Demo Version
font_size = 80
color = #FF6666
animate = true
image_path = 
image_scale = 1
image_alpha = 0.8
image_align = top-center
image_padding_x = 50
image_padding_y = 50
image_animate = false

[program:default]
timeout = 60
timeout_kill_self = false
timeout_kill_other = 
```

### 应用 app001 的特定配置

```ini
; 覆盖某些水印参数
[watermark:app001]
title = App001 - Unlicensed Version
font_size = 100
color = #FF0000
animate = true
image_path = watermark_app001.png
image_scale = 1.5
image_alpha = 0.9
image_align = bottom-right
image_padding_x = 20
image_padding_y = 20

; 覆盖程序配置
[program:app001]
timeout = 120
timeout_kill_self = true
timeout_kill_other = notepad.exe|calculator.exe
```

### 应用 app002 的特定配置

```ini
[watermark:app002]
title = App002 Evaluation
font_size = 60
color = #FFAA00
image_path = watermark_app002.png
image_align = center

[program:app002]
timeout = 180
```

## 配置参数说明

### 水印参数

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `title` | 字符串 | 水印文字，支持 `{APPID}` 和 `{COUNTDOWN}` 占位符 | `{APPID} Demo Version` |
| `font_size` | 整数 | 字体大小（像素） | `80` |
| `color` | 十六进制 | 颜色代码 `#RRGGBB` 或 `#RRGGBBAA` | `#FF6666` |
| `animate` | 布尔 | 是否启用文字动画 | `true` |
| `image_path` | 字符串 | 水印图片路径（相对于 `.lichper` 文件夹） | 空 |
| `image_scale` | 浮点数 | 图片缩放系数 | `1.0` |
| `image_alpha` | 浮点数 | 图片透明度 (0-1) | `0.8` |
| `image_align` | 字符串 | 图片对齐方式 | `top-center` |
| `image_padding_x` | 整数 | 图片水平边距 | `50` |
| `image_padding_y` | 整数 | 图片垂直边距 | `50` |
| `image_animate` | 布尔 | 图片是否启用动画移动 | `false` |

### 程序参数

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `timeout` | 整数 | 程序超时时间（秒） | `60` |
| `timeout_kill_self` | 布尔 | 超时时是否关闭主进程 | `false` |
| `timeout_kill_other` | 字符串 | 超时时要关闭的其他进程列表，用 `\|` 分隔 | 空 |

### 图片对齐选项

- `top-left` - 左上
- `top-center` - 顶部居中
- `top-right` - 右上
- `center-left` - 左边居中
- `center` - 中心
- `center-right` - 右边居中
- `bottom-left` - 左下
- `bottom-center` - 底部居中
- `bottom-right` - 右下

## 使用场景

### 场景 1：所有应用统一配置

只配置 `[watermark:default]` 和 `[program:default]`，所有应用都使用相同配置。

### 场景 2：不同应用不同水印

```ini
[watermark:default]
title = Default Demo
font_size = 80

[watermark:PhotoEditor]
title = PhotoEditor - License Required
font_size = 120
color = #FF0000

[watermark:VideoPlayer]
title = VideoPlayer - Evaluation Copy
font_size = 60
color = #00FF00
```

### 场景 3：特定应用的超时控制

```ini
[program:default]
timeout = 60
timeout_kill_self = false

; 某些应用需要更长的超时时间
[program:HeavyApplication]
timeout = 300

; 某些应用需要在超时后立即关闭
[program:DemoApp]
timeout = 30
timeout_kill_self = true
timeout_kill_other = explorer.exe
```

## 配置文件位置

- **Windows**: `%USERPROFILE%\.authrc.ini`
- **示例**: `C:\Users\Username\.authrc.ini`

## 代码变更

### C++ 实现（RenderManager.cpp）

```cpp
bool RenderManager::ParseIniConfig(const std::string& iniPath) {
    // 构建要查找的 section 名称
    std::string watermarkSection = "watermark:default";
    std::string programSection = "program:default";
    
    // 如果存在特定应用配置，优先使用特定应用配置
    std::string appWatermarkSection = "watermark:" + g_appID;
    std::string appProgramSection = "program:" + g_appID;
    
    if (ini.has(appWatermarkSection)) {
        watermarkSection = appWatermarkSection;
    }
    
    // ... 加载配置
}
```

### 配置查找流程

```
应用启动 (AppID = "MyApp")
    ↓
查找 [watermark:MyApp] ← 如果存在，使用
    ↓ (不存在)
查找 [watermark:default] ← 使用默认配置
    ↓ (都不存在)
查找 [watermark] ← 向后兼容旧格式
    ↓ (都不存在)
使用代码中的硬编码默认值
```

## 向后兼容性

✅ **完全向后兼容**

- 如果使用旧格式 `[watermark]` 和 `[program]`，仍然可以正常工作
- 新格式和旧格式可以混合使用
- 优先级：特定应用 > 默认 > 旧格式 > 代码默认

## 测试建议

1. **测试默认配置**
   ```ini
   [watermark:default]
   title = Default Test
   
   [program:default]
   timeout = 60
   ```
   验证所有应用都使用默认配置。

2. **测试特定应用覆盖**
   ```ini
   [watermark:TestApp]
   title = TestApp Custom
   color = #00FF00
   ```
   验证 AppID 为 "TestApp" 的应用使用自定义配置。

3. **测试混合配置**
   - 只覆盖部分参数（如只改 font_size，其他使用默认）
   - 验证未指定的参数是否使用默认值

4. **测试向后兼容**
   - 使用旧的 `[watermark]` 格式
   - 验证是否仍然可以正常加载

## 日志输出示例

```
Loading watermark config from section: [watermark:MyApp]
Loading program config from section: [program:default]
Config loaded successfully
  - Title: MyApp Custom Version
  - FontSize: 100
  - Timeout: 120s
```

## 常见问题

### Q: 如果只在特定应用配置中指定了某个参数，其他参数会怎样？

A: 其他参数会使用代码中的硬编码默认值。建议在 `[watermark:default]` 中定义所有参数，然后在特定应用配置中仅覆盖需要改变的参数。

### Q: 能否同时加载多个配置文件？

A: 当前实现只支持加载一个 `.authrc.ini` 文件。如需多文件支持，需要修改代码。

### Q: 配置文件不存在时会怎样？

A: 程序会自动生成一个包含默认配置和示例的 `.authrc.ini` 文件。

## 相关文件

- [LicHper/Rendering/RenderManager.cpp](../LicHper/Rendering/RenderManager.cpp) - 配置读取实现
- [LicHper/Rendering/WatermarkConfig.h](../LicHper/Rendering/WatermarkConfig.h) - 配置数据结构
- [LicHper/测试配置示例_NEW.ini](../LicHper/测试配置示例_NEW.ini) - 完整配置示例
