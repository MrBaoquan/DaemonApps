# 路径转换问题修复说明

## 问题描述

**用户反馈**:
- 原始路径: `F:\Project Released Files\A-安徽名人馆\UNIPlayer@名人馆六尺巷\UNIPlayer.exe`
- 导出后导入，右键定位进程所在目录还是 `F:\...` 这个目录
- 预期: 应该指向 `Applications\UNIPlayer@名人馆六尺巷\...`

## 根本原因

### 1. 导出时路径未转换

**问题代码** (Line 509-519):
```csharp
// 只有当路径在Applications目录下时才转换
if (!string.IsNullOrEmpty(originalPath) && 
    Path.IsPathRooted(originalPath) && 
    originalPath.StartsWith(applicationsDir, StringComparison.OrdinalIgnoreCase))
{
    convertedPath = ...
}
```

**问题**: 
- 用户的原始路径 `F:\Project Released Files\...` **不在** `Applications` 目录
- 导出时条件不满足，路径保持绝对路径
- 日志: `Converting export path for 'UNIPlayer': F:\...` (无 "Converted to relative" 日志)

### 2. 导入时未处理相对路径

**问题代码** (Line 1283-1291):
```csharp
// 只处理"绝对路径转相对路径"，没有处理"相对路径转绝对路径"
if (!isPathChanged && Path.IsPathRooted(convertedPath))
{
    // 转换为相对路径
}
```

**问题**:
- 如果导出时成功转换为相对路径（如 `UNIPlayer@名人馆六尺巷\UNIPlayer.exe`）
- 导入时没有转换回 `Applications\UNIPlayer@名人馆六尺巷\UNIPlayer.exe`
- 导致程序无法找到文件

## 修复方案

### 修复1: 导出逻辑 (Line 500-530)

**新逻辑**:
```csharp
// 转换为相对路径：无论原始路径在哪，都提取为相对于Applications的相对路径
if (!string.IsNullOrEmpty(originalPath))
{
    if (Path.IsPathRooted(originalPath))
    {
        // 绝对路径：检查是否在Applications目录下
        if (originalPath.StartsWith(applicationsDir, StringComparison.OrdinalIgnoreCase))
        {
            // 在Applications下：直接提取相对路径
            convertedPath = originalPath
                .Substring(applicationsDir.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        else
        {
            // 不在Applications下：使用程序名作为基准
            // 例: F:\xxx\UNIPlayer@名人馆六尺巷\UNIPlayer.exe 
            //  -> UNIPlayer@名人馆六尺巷\UNIPlayer.exe
            var programDir = Path.GetDirectoryName(originalPath);
            var programName = Path.GetFileName(programDir); // UNIPlayer@名人馆六尺巷
            var fileName = Path.GetFileName(originalPath);  // UNIPlayer.exe
            convertedPath = Path.Combine(programName, fileName);
        }
    }
    // 如果已经是相对路径，保持不变
}
```

**改进点**:
- ✅ 支持任意位置的绝对路径导出
- ✅ 使用程序目录名作为相对路径基准
- ✅ 保持原有相对路径不变

### 修复2: 导入逻辑 (Line 1283-1306)

**新逻辑**:
```csharp
// 第二步：如果还是绝对路径，尝试转换为相对路径；如果是相对路径，转换为绝对路径
if (!isPathChanged)
{
    if (Path.IsPathRooted(convertedPath))
    {
        // 绝对路径：检查是否在Applications目录下，转换为相对路径
        if (convertedPath.StartsWith(applicationsDir, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = convertedPath.Substring(applicationsDir.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            convertedPath = relativePath;
            isPathChanged = true;
        }
    }
    else
    {
        // 相对路径：转换为Applications目录下的绝对路径
        var absolutePath = Path.Combine(applicationsDir, convertedPath);
        Logger.Debug($"  Converting relative to absolute: {convertedPath} -> {absolutePath}");
        convertedPath = absolutePath;
        isPathChanged = true;
    }
}
```

**改进点**:
- ✅ 新增相对路径转绝对路径逻辑
- ✅ 导入后路径指向 `Applications\程序名\...`
- ✅ 完整日志记录转换过程

## 测试场景

### 场景1: 外部路径导出导入

**原始路径**: `F:\Project Released Files\A-安徽名人馆\UNIPlayer@名人馆六尺巷\UNIPlayer.exe`

**导出过程**:
1. 检测到绝对路径，但不在Applications下
2. 提取程序目录名: `UNIPlayer@名人馆六尺巷`
3. 导出路径: `UNIPlayer@名人馆六尺巷\UNIPlayer.exe`
4. 日志: `Converted to relative (from program name): F:\... -> UNIPlayer@名人馆六尺巷\UNIPlayer.exe`

**导入过程**:
1. 程序文件复制到: `Applications\UNIPlayer@名人馆六尺巷\`
2. 读取相对路径: `UNIPlayer@名人馆六尺巷\UNIPlayer.exe`
3. 转换为绝对路径: `<AppDir>\Applications\UNIPlayer@名人馆六尺巷\UNIPlayer.exe`
4. 日志: `Converting relative to absolute: UNIPlayer@... -> C:\...\Applications\UNIPlayer@...\UNIPlayer.exe`

**验证点**:
- ✅ 导出包中路径为相对路径
- ✅ 导入后路径指向Applications目录
- ✅ 右键定位到正确目录

### 场景2: Applications目录内导出导入

**原始路径**: `C:\DaemonKit\Applications\TestApp\TestApp.exe`

**导出过程**:
1. 检测到在Applications目录下
2. 提取相对路径: `TestApp\TestApp.exe`
3. 日志: `Converted to relative (from AppDir): C:\... -> TestApp\TestApp.exe`

**导入过程**:
1. 检测到相对路径
2. 转换为绝对路径: `C:\DaemonKit\Applications\TestApp\TestApp.exe`
3. 日志: `Converting relative to absolute: TestApp\... -> C:\...\Applications\TestApp\...`

### 场景3: 已是相对路径

**原始路径**: `MyApp\MyApp.exe`

**导出过程**:
1. 检测到相对路径
2. 保持不变: `MyApp\MyApp.exe`
3. 无转换日志（已是目标格式）

**导入过程**:
1. 检测到相对路径
2. 转换为绝对路径: `<AppDir>\Applications\MyApp\MyApp.exe`

## 日志关键字

**导出日志**:
```
[Debug] - Converting export path for '...': <原始路径>
[Debug] -   Converted to relative (from AppDir): ... -> ...
[Debug] -   Converted to relative (from program name): ... -> ...
[Info] - Process tree exported with relative paths: X root nodes
```

**导入日志**:
```
[Debug] - Converting path for node '...': <路径>
[Debug] -   Converting relative to absolute: ... -> ...
[Debug] -   Converting absolute to relative: ... -> ...
[Info] - Path converted for node '...': ... -> ...
```

**错误日志**:
```
[Warn] - Could not convert path for node '...': ... (not in mapping and not in Applications dir)
```

## 验证步骤

1. **清理环境**:
   ```powershell
   # 删除旧的导出包
   Remove-Item "C:\Users\Administrator\Desktop\DaemonKit_Export_*.dkit" -Force
   ```

2. **测试导出**:
   - 选择外部路径的进程节点
   - 导出配置包
   - 检查日志: 应有 "Converted to relative (from program name)" 日志

3. **测试导入**:
   - 导入配置包
   - 检查日志: 应有 "Converting relative to absolute" 日志
   - 右键定位: 应打开 `Applications\程序名\` 目录

4. **验证运行**:
   - 启动导入的进程
   - 确认程序正常运行
   - 路径应指向Applications目录

## 影响范围

**修改文件**:
- `DaemonKit/Services/ExportImportService.cs`

**影响功能**:
- ✅ 导出配置包 (路径转换)
- ✅ 导入配置包 (路径还原)
- ✅ 进程树右键定位

**兼容性**:
- ✅ 向后兼容：已在Applications下的路径继续工作
- ✅ 新功能：外部路径导出后也能正确导入
- ✅ 相对路径：保持不变，导入时转换

## 编译状态

✅ **编译成功** (0 errors, 4 warnings - 均为包兼容性警告)

---

**修复日期**: 2026-01-10  
**版本**: 方案B - 2.1  
**状态**: ✅ 已修复，待测试
