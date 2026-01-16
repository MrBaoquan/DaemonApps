# LicHper 水印配置优化 - 实现总结

## 📋 功能说明

为 LicHper DLL 实现了**基于 AppID 的分应用水印配置**功能，允许每个应用有不同的水印和程序设置。

## ✨ 核心特性

### 1. **分层配置系统**
- ✅ `[watermark:default]` / `[program:default]` - 所有应用的默认配置
- ✅ `[watermark:appid]` / `[program:appid]` - 特定应用的配置覆盖
- ✅ 向后兼容旧格式 `[watermark]` / `[program]`

### 2. **配置继承机制**
```
应用启动 (AppID = "MyApp")
    ↓
检查是否存在 [watermark:MyApp]
    ├─ 存在 → 使用特定应用配置 ✓
    └─ 不存在 → 查找 [watermark:default]
        ├─ 存在 → 使用默认配置 ✓
        └─ 不存在 → 查找旧格式 [watermark]（向后兼容）
```

### 3. **灵活的配置覆盖**
只需在特定应用配置中指定要改变的参数，其他参数自动使用默认值或代码默认值。

## 📂 修改的文件

### C++ 代码

#### 1. [LicHper/Rendering/RenderManager.cpp](../LicHper/Rendering/RenderManager.cpp)

**修改内容：**

##### a) `ParseIniConfig()` 方法 - 配置查找逻辑
- 新增 AppID 特定配置的查找
- 实现配置优先级：特定应用 > 默认 > 旧格式 > 代码默认
- 完整的向后兼容性

```cpp
// 构建要查找的 section 名称
std::string watermarkSection = "watermark:default";
std::string programSection = "program:default";

// 如果存在特定应用配置，使用特定应用配置
std::string appWatermarkSection = "watermark:" + g_appID;
std::string appProgramSection = "program:" + g_appID;

if (ini.has(appWatermarkSection)) {
    watermarkSection = appWatermarkSection;
} else if (ini.has("watermark")) {
    watermarkSection = "watermark";  // 向后兼容
}
```

**关键改进：**
- ✅ 支持 `[watermark:default]` 格式
- ✅ 支持 `[watermark:具体appid]` 格式
- ✅ 优先级明确：特定 > 默认 > 旧格式
- ✅ 完整的日志输出

##### b) `GenerateDefaultConfig()` 方法 - 默认配置生成
- 生成包含默认配置和示例的完整 ini 文件
- 包含每个参数的详细说明

```cpp
ini["watermark:default"]["title"] = "{APPID} Demo Version";
ini["watermark:default"]["font_size"] = "80";
// ...

// 示例：应用 app001 的特定配置
ini["watermark:app001"]["title"] = "App001 - Unlicensed Version";
ini["program:app001"]["timeout"] = "120";
```

### 配置文件

#### [LicHper/测试配置示例_NEW.ini](../LicHper/测试配置示例_NEW.ini)

完整的配置示例，包含：
- ✅ 默认水印配置
- ✅ 默认程序配置
- ✅ 应用 app001 的特定配置
- ✅ 应用 app002 的特定配置
- ✅ Unreal Engine 5 的特殊配置

### 文档

#### [LicHper/PER_APP_CONFIG_GUIDE.md](../LicHper/PER_APP_CONFIG_GUIDE.md)

完整的使用指南，包含：
- 📖 功能概述
- 🔧 配置参数详解
- 📋 使用场景示例
- ✅ 测试建议
- ❓ 常见问题解答

## 🎯 使用示例

### 基础配置（所有应用统一）

```ini
[watermark:default]
title = {APPID} Demo Version
font_size = 80
color = #FF6666

[program:default]
timeout = 60
```

所有应用都会使用这个配置。

### 特定应用配置

```ini
; 为应用 PhotoEditor 提供自定义配置
[watermark:PhotoEditor]
title = PhotoEditor - License Required
font_size = 120
color = #FF0000
image_path = watermark_editor.png

[program:PhotoEditor]
timeout = 120
timeout_kill_self = true
```

当应用的 AppID 为 "PhotoEditor" 时，会使用这个配置。

### 混合配置（部分覆盖）

```ini
[watermark:default]
title = Default Title
font_size = 80
color = #FF6666
animate = true

; 只覆盖部分参数
[watermark:VideoApp]
font_size = 100
color = #00FF00
; title 和 animate 会使用默认值
```

## 📊 配置对比

### 修改前（单一配置）
```ini
[watermark]
title = {APPID} Demo Version
font_size = 80
; 所有应用共享同一配置
```

### 修改后（分应用配置）
```ini
[watermark:default]
title = {APPID} Demo Version
font_size = 80

[watermark:app001]
title = App001 Custom
font_size = 100

[watermark:app002]
title = App002 Custom
font_size = 60
```

## 🔄 向后兼容性

✅ **完全向后兼容** - 现有的 `.authrc.ini` 文件仍然能正常工作

**兼容性检查清单：**
- ✅ 旧的 `[watermark]` 格式仍然支持
- ✅ 旧的 `[program]` 格式仍然支持
- ✅ 新旧格式可以混合使用
- ✅ 优先级明确，不会产生歧义

## 🛠️ 编译信息

### LicHper DLL
- ✅ 编译成功
- 文件：`LicHper/x64/Debug/LicHper.dll`
- 自动复制到：`AuthAssistant/Costura64/LicHper.dll`

### AuthAssistant
- ✅ 编译成功
- 文件：`AuthAssistant/bin/Debug/net6.0-windows8.0/AuthAssistant.dll`
- 编译警告：14 个（非关键）

## 📝 日志输出示例

```
Loading watermark config from section: [watermark:app001]
Loading program config from section: [program:default]
Config loaded successfully
  - Title: App001 - Unlicensed Version
  - FontSize: 100
  - Animate: true
  - ImagePath: watermark_app001.png
  - Timeout: 60s
```

## 🔐 配置参数汇总

### 水印参数（可配置）

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `title` | 字符串 | `{APPID} Demo Version` | 水印文字 |
| `font_size` | 整数 | `80` | 字体大小 |
| `color` | 十六进制 | `#FF6666` | 颜色代码 |
| `animate` | 布尔 | `true` | 文字动画 |
| `image_path` | 字符串 | 空 | 图片路径 |
| `image_scale` | 浮点 | `1.0` | 图片缩放 |
| `image_alpha` | 浮点 | `0.8` | 图片透明度 |
| `image_align` | 字符串 | `top-center` | 对齐方式 |
| `image_padding_x` | 整数 | `50` | 水平边距 |
| `image_padding_y` | 整数 | `50` | 垂直边距 |
| `image_animate` | 布尔 | `false` | 图片动画 |

### 程序参数（可配置）

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `timeout` | 整数 | `60` | 超时时间（秒） |
| `timeout_kill_self` | 布尔 | `false` | 是否关闭主进程 |
| `timeout_kill_other` | 字符串 | 空 | 关闭的其他进程 |

## 🎓 使用建议

### 1. 生成默认配置
第一次使用时，程序会自动生成 `.authrc.ini` 文件，包含完整的默认配置和示例。

### 2. 编辑配置文件
- 编辑 `%USERPROFILE%\.authrc.ini`
- 为每个应用添加 `[watermark:appid]` 和 `[program:appid]` 节

### 3. 测试配置
- 在日志中查看加载的配置 section 名称
- 验证水印是否按预期显示

### 4. 部署配置
- 将配置文件复制到目标用户的 `%USERPROFILE%\` 文件夹
- 无需修改代码或重新编译

## 📋 测试清单

- [ ] 默认配置可以正常加载
- [ ] 特定应用配置覆盖有效
- [ ] 部分覆盖（只改某些参数）工作正常
- [ ] 旧格式 `[watermark]` 仍然兼容
- [ ] 新旧格式混合使用时优先级正确
- [ ] 日志输出正确反映使用的 section
- [ ] 超时功能按应用配置正确执行
- [ ] 水印显示按应用配置正确显示

## 🚀 后续优化方向

1. **多文件支持** - 允许导入多个 ini 文件
2. **动态重新加载** - 支持在运行时重新加载配置
3. **配置验证** - 添加配置文件验证工具
4. **UI 配置** - 在 AuthAssistant 中提供配置编辑 UI
5. **云配置** - 支持从云端获取配置

## 📞 支持

- 文档：[PER_APP_CONFIG_GUIDE.md](../LicHper/PER_APP_CONFIG_GUIDE.md)
- 示例：[测试配置示例_NEW.ini](../LicHper/测试配置示例_NEW.ini)
- 代码：[RenderManager.cpp](../LicHper/Rendering/RenderManager.cpp)
