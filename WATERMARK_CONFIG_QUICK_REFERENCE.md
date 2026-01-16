# LicHper 分应用配置 - 快速参考

## 配置文件位置
```
%USERPROFILE%\.authrc.ini
C:\Users\YourUsername\.authrc.ini
```

## 配置结构

```ini
; ===== 所有应用使用的默认配置 =====
[watermark:default]
title = {APPID} Demo Version
font_size = 80
color = #FF6666

[program:default]
timeout = 60

; ===== 特定应用的配置（会覆盖默认值）=====
[watermark:app001]
title = App001 Custom
font_size = 100

[program:app001]
timeout = 120
```

## 最小化配置示例

```ini
[watermark:default]
title = {APPID} Demo
font_size = 80
color = #FF6666
animate = true

[program:default]
timeout = 60
timeout_kill_self = false
```

## 完整配置示例

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

[watermark:PhotoEditor]
title = PhotoEditor - License Required
font_size = 120
color = #FF0000
image_path = watermark_editor.png
image_align = bottom-right

[program:PhotoEditor]
timeout = 180
timeout_kill_self = true
timeout_kill_other = explorer.exe
```

## 颜色代码

| 名称 | 代码 | 示例 |
|------|------|------|
| 红色 | `#FF0000` | 警告/强调 |
| 绿色 | `#00FF00` | 正常/成功 |
| 蓝色 | `#0000FF` | 信息 |
| 黄色 | #FFFF00 | 注意 |
| 橙色 | #FF6600 | 重要 |
| 浅红 | #FF6666 | 默认 |

## 对齐方式

```
top-left       top-center       top-right
     ┌──────────────────────┐
     │    ▲          ▲      │
     │    │          │      │
     │◄── ●                 │
     │    │          │      │
     │    │          │      │
     │    ▼          ▼      │
     └──────────────────────┘
center-left    center         center-right

bottom-left    bottom-center   bottom-right
```

## 占位符

| 占位符 | 说明 | 示例 |
|--------|------|------|
| `{APPID}` | 应用 ID | `MyApp Demo Version` |
| `{COUNTDOWN}` | 倒计时 | `10 秒后关闭` |

## 常用配置

### 配置 1：演示版本（默认）
```ini
[watermark:default]
title = {APPID} Demo Version
font_size = 80
color = #FF6666
animate = true

[program:default]
timeout = 60
timeout_kill_self = false
```

### 配置 2：重要提醒
```ini
[watermark:critical]
title = {APPID} - UNLICENSED
font_size = 120
color = #FF0000
animate = true
image_path = warning.png
image_align = center

[program:critical]
timeout = 30
timeout_kill_self = true
```

### 配置 3：高性能应用
```ini
[watermark:performance]
title = {APPID} Evaluation
font_size = 60
color = #FFAA00
animate = false
image_alpha = 0.5

[program:performance]
timeout = 300
timeout_kill_self = false
```

### 配置 4：隐形水印
```ini
[watermark:stealth]
title = 
image_path = watermark_icon.png
image_alpha = 0.3
image_scale = 0.5
image_align = bottom-right

[program:stealth]
timeout = 600
```

## 配置查找流程

```
DLL 注入到应用 (AppID = "MyApp")
         ↓
    读取 .authrc.ini
         ↓
  查找 [watermark:MyApp]
    ├─ 找到 → ✓ 使用此配置
    └─ 未找到
         ↓
  查找 [watermark:default]
    ├─ 找到 → ✓ 使用此配置
    └─ 未找到
         ↓
  查找 [watermark] (旧格式)
    ├─ 找到 → ✓ 使用此配置
    └─ 未找到
         ↓
    使用硬编码默认值
```

## 参数速查表

### 水印参数

| 参数 | 值 | 范围/示例 |
|------|-----|----------|
| `title` | 字符串 | 任意文本，支持 `{APPID}` 和 `{COUNTDOWN}` |
| `font_size` | 数字 | 20-200 |
| `color` | 十六进制 | `#RRGGBB` 或 `#RRGGBBAA` |
| `animate` | true/false | - |
| `image_path` | 路径 | 相对于 `.lichper` 文件夹 |
| `image_scale` | 小数 | 0.1-5.0 |
| `image_alpha` | 小数 | 0.0-1.0 |
| `image_align` | 字符串 | top-left, top-center, top-right, center-left, center, center-right, bottom-left, bottom-center, bottom-right |
| `image_padding_x` | 数字 | 0-200 |
| `image_padding_y` | 数字 | 0-200 |
| `image_animate` | true/false | - |

### 程序参数

| 参数 | 值 | 范围 |
|------|-----|------|
| `timeout` | 数字 | 10-3600（秒） |
| `timeout_kill_self` | true/false | - |
| `timeout_kill_other` | 进程列表 | 用 `\|` 分隔，如 `notepad.exe\|calc.exe` |

## 故障排除

### 问题：配置没有生效
**解决：**
1. 检查 section 名称：`[watermark:appid]` 中的 appid 必须与应用的 AppID 完全匹配
2. 检查配置文件位置：应该在 `%USERPROFILE%\.authrc.ini`
3. 查看日志：日志会显示加载的 section 名称

### 问题：仍然看到默认配置
**解决：**
1. 检查特定应用的 section 是否存在
2. 如果没有特定应用 section，则使用 `[watermark:default]`
3. 向后兼容：如果有旧格式的 `[watermark]`，会被使用

### 问题：图片水印没有显示
**解决：**
1. 检查 `image_path` 是否正确（相对于 `.lichper` 文件夹）
2. 检查图片文件是否存在且可读
3. 检查 `image_alpha` 是否过低（小于 0.3）

## 更新指南

### 添加新应用配置

1. 编辑 `.authrc.ini`
2. 添加新 section：
   ```ini
   [watermark:newapp]
   title = NewApp Config
   font_size = 100
   
   [program:newapp]
   timeout = 120
   ```
3. 保存文件
4. 重启应用

### 修改默认配置

1. 编辑 `.authrc.ini` 中的 `[watermark:default]` 和 `[program:default]`
2. 保存文件
3. 所有使用默认配置的应用会立即生效

### 删除应用配置

1. 删除相应的 `[watermark:appid]` 和 `[program:appid]` section
2. 该应用会自动使用默认配置

## 性能建议

- 文字水印 + 动画：中等性能开销
- 图片水印：中等性能开销
- 禁用动画：降低 CPU 使用
- 低透明度 (alpha < 0.3)：可能影响可见性

## 相关文件

- 完整指南：`LicHper/PER_APP_CONFIG_GUIDE.md`
- 完整示例：`LicHper/测试配置示例_NEW.ini`
- 实现报告：`WATERMARK_CONFIG_OPTIMIZATION_REPORT.md`
