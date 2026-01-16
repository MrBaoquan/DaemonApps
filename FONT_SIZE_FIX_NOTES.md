# LicHper 字体大小限制修复说明

## 问题描述

设置 `.authrc.ini` 中的 `font_size = 240` 后，水印字体大小没有变化，仍然使用默认大小。

## 根本原因

在 `WatermarkRenderer.cpp` 和 `RenderManager.cpp` 中，字体大小被限制在 36-132 的范围内：

```cpp
// WatermarkRenderer.cpp line 104
int fontSize = std::clamp(config.fontSize, 36, 132);

// RenderManager.cpp line 484  
m_config.fontSize = std::clamp(m_config.fontSize, 36, 132);
```

超过此范围的值会被自动限制，所以 240 会被截断到 132。

## 解决方案

已将字体大小限制范围扩大到 18-500：

### 修改点 1: WatermarkRenderer.cpp (第 104 行)
```cpp
// 之前
int fontSize = std::clamp(config.fontSize, 36, 132);

// 之后
int fontSize = std::clamp(config.fontSize, 18, 500);
```

### 修改点 2: RenderManager.cpp (第 484 行)
```cpp
// 之前
m_config.fontSize = std::clamp(m_config.fontSize, 36, 132);

// 之后
m_config.fontSize = std::clamp(m_config.fontSize, 18, 500);
```

## 测试步骤

1. **重新编译 LicHper.dll**
   ```powershell
   MSBuild LicHper\LicHper.vcxproj /p:Configuration=Release /p:Platform=x64
   ```

2. **更新配置文件** (`~\.authrc.ini`)
   ```ini
   [watermark]
   font_size = 240
   ```

3. **运行 auth_ghost 测试**
   - 应看到水印字体明显变大
   - 字体大小现在支持 18-500 范围

## 新的字体大小范围

| 参数 | 值 | 说明 |
|------|-----|------|
| 最小值 | 18 | 最小可读大小 |
| 最大值 | 500 | 支持超大字体 |
| 推荐值 | 80-240 | 通常效果较好 |

## 相关配置

在 `.authrc.ini` 中完整的水印配置示例：

```ini
[watermark]
title = 样本
font_size = 240          # 现在支持更大的值
color = #FF6666
animate = true
image_path = 
image_scale = 1
image_alpha = 0.8
image_align = top-center
image_padding_x = 50
image_padding_y = 50
```

## 影响范围

- ✅ WPF 应用 (auth_ghost) - 需要重新编译
- ✅ DirectX 应用 - 需要重新编译 LicHper.dll
- ✅ 配置文件兼容性 - 无影响

## 编译后的文件

重新编译后，将生成新的 LicHper.dll：
- 位置: `Binaries\Win64\Release\LicHper.dll`
- 大小: 约 1.6 MB

## 后续使用

编译完成后，复制新的 DLL 到：
1. `auth_ghost\Costura64\LicHper.dll` （用于 WPF 应用）
2. 部署目录的 `System32` 或应用目录

然后使用新的 `font_size` 值测试水印显示。

