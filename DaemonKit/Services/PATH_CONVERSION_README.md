# 路径转换功能文档

## 概述

为了解决导出的配置包无法在不同目录结构中正确导入的问题，我们实现了三层次的路径转换机制：

1. **导出时路径转换** - 导出配置包时自动转换为相对路径
2. **导入时路径转换** - 导入配置包时自动转换为绝对路径（基于Applications目录）
3. **事后迁移功能** - 为现有的进程树检测并转换绝对路径

## 实现细节

### 核心组件

#### 1. 导出时路径转换 (`CloneProcessItemWithRelativePaths`)

在 `ExportImportService.ExportPackageAsync()` 中调用：

```csharp
// 创建树的深拷贝，进行路径转换
var treeForExport = processTreeList
    .Select(CloneProcessItemWithRelativePaths)
    .ToList();
```

**功能：**
- 深拷贝ProcessItem树
- 检测每个节点的路径
- 如果是绝对路径且在Applications目录下，转换为相对路径
- 保留所有其他元数据（名称、参数、触发器等）

**示例：**
```
原始路径：C:\Users\Admin\Applications\MyApp\app.exe
导出路径：MyApp\app.exe
```

#### 2. 导入时路径转换 (`ConvertToRelativePaths`)

在 `ImportPackageAsync()` 中调用，用于处理从导出包恢复的树：

```csharp
ConvertToRelativePaths(importedTree, pathMappings);
```

**功能：**
- 检查路径映射（如果程序被重定位）
- 如果仍是绝对路径且在Applications目录下，转换为相对
- 支持多种匹配策略（直接匹配、规范化、前缀匹配、子路径匹配）

**处理流程：**
1. 尝试直接映射匹配
2. 尝试规范化路径后的映射匹配
3. 尝试目录级别的前缀匹配
4. 最后检查是否在Applications目录下

#### 3. 事后迁移功能 (Public APIs)

为现有的进程树添加检测和迁移功能：

```csharp
// 检测所有绝对路径节点
public static List<ProcessItem> DetectAbsolutePathNodes(
    IEnumerable<ProcessItem> processTree)

// 转换指定节点为相对路径
public static int MigrateNodesToRelativePaths(
    IEnumerable<ProcessItem> nodesToMigrate)

// 完整的迁移API（检测+转换）
public static (List<ProcessItem> AbsolutePathNodes, int MigratedCount) MigrateProcessTreeToRelativePaths(
    IEnumerable<ProcessItem> processTree,
    bool migrateAll = false,
    IEnumerable<ProcessItem> selectedNodesToMigrate = null)
```

## 使用场景

### 场景1：导出配置包（自动处理）

```csharp
var exported = await ExportImportService.ExportPackageAsync(
    packagePath: "config.zip",
    processTree: myProcessTree,
    includeAllPrograms: true
);
// 导出时自动转换所有绝对路径为相对路径
```

### 场景2：导入配置包（自动处理）

```csharp
var imported = await ExportImportService.ImportPackageAsync(
    packagePath: "config.zip",
    overwriteConfigs: true
    // 导入时自动转换相对路径为绝对路径
);
```

### 场景3：检测现有树中的绝对路径

```csharp
var absolutePathNodes = ExportImportService.DetectAbsolutePathNodes(currentProcessTree);
Console.WriteLine($"检测到 {absolutePathNodes.Count} 个使用绝对路径的节点");

foreach (var node in absolutePathNodes)
{
    Console.WriteLine($"  {node.MetaData.Name}: {node.MetaData.Path}");
}
```

### 场景4：迁移所有绝对路径节点

```csharp
var (absoluteNodes, migratedCount) = ExportImportService.MigrateProcessTreeToRelativePaths(
    currentProcessTree,
    migrateAll: true  // 迁移所有检测到的绝对路径节点
);
Console.WriteLine($"迁移了 {migratedCount} 个节点为相对路径");
```

### 场景5：迁移选定的节点

```csharp
var nodesToMigrate = userSelectedNodes; // 用户选择的节点

var (absoluteNodes, migratedCount) = ExportImportService.MigrateProcessTreeToRelativePaths(
    currentProcessTree,
    migrateAll: false,
    selectedNodesToMigrate: nodesToMigrate
);
Console.WriteLine($"迁移了 {migratedCount} 个选定节点为相对路径");
```

## 路径转换规则

### 绝对路径转相对路径

条件：
- 路径是绝对路径（`Path.IsPathRooted()` 返回true）
- 路径在Applications目录下（通常是 `%USERPROFILE%\Applications`）

转换：
```
Remove Applications dir prefix and trim separators
C:\Users\Admin\Applications\MyApp\app.exe
→ MyApp\app.exe
```

### 相对路径转绝对路径

条件：
- 路径不是绝对路径

转换：
```
Combine with Applications dir
MyApp\app.exe
→ C:\Users\Admin\Applications\MyApp\app.exe (via NodePath property)
```

## 防御性检查（导入时）

在 `ExtractAndMovePrograms()` 中添加了对畸形ZIP结构的检查：

```csharp
// 防止 Applications/Applications 的重复嵌套
if (folderName == "Applications" && parent == programsDir)
{
    // 直接使用其内容
    moveSource = Path.Combine(moveSource, "Applications");
}
```

这处理了ZIP中包含嵌套Applications文件夹的旧版本包。

## 日志记录

所有路径转换操作都有详细的日志记录：

```
DEBUG: Converting export path for 'MyApp': C:\Users\Admin\Applications\MyApp\app.exe
DEBUG:   Converted to relative: ... -> MyApp\app.exe
INFO: Process tree exported with relative paths: ...

DEBUG: Converting path for node 'MyApp': MyApp\app.exe
INFO: Path converted for node 'MyApp': ... -> ...
```

## 测试建议

1. **导出-导入循环测试**
   - 创建包含绝对路径的进程树
   - 导出为配置包
   - 验证ZIP中的XML包含相对路径
   - 导入配置包
   - 验证导入后的进程树正确使用Applications目录

2. **跨用户迁移测试**
   - 用户A导出配置
   - 用户B导入配置
   - 验证程序路径正确指向用户B的Applications目录

3. **事后迁移测试**
   - 检测现有树中的绝对路径
   - 选择性或全部迁移
   - 验证路径已转换

## 兼容性

- **向后兼容**：导入包含绝对路径的旧版本包时自动转换
- **向前兼容**：导出时总是相对路径，确保新包可在任何环境导入
- **防御性**：处理畸形包结构（嵌套Applications目录）

## 性能考虑

- 导出时的深拷贝: O(n) 其中n是树中的节点数
- 导入时的路径转换: O(n) + O(m) 其中m是映射条目数
- 绝对路径检测: O(n) 遍历整个树一次

对于典型的进程树（< 1000节点），性能影响可以忽略不计。
