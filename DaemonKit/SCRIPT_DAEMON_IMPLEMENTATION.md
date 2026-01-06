# DaemonKit 脚本守护功能实现总结

## 📋 概述

本文档总结了 DaemonKit 中脚本守护功能的完整实现，包括核心代码改动、构建配置、示例文件和文档。

---

## 🎯 功能特性

### 1. 自动脚本检测
- **支持的扩展名**: `.bat`, `.cmd`, `.ps1`, `.vbs`
- **检测方式**: 
  - 自动：根据文件扩展名识别
  - 手动：通过 `IsScript="true"` 属性标记

### 2. 脚本专用守护模式
与普通程序不同，脚本模式只执行最小化检测：
- ✅ **执行的检测**: `HasExited` (进程是否退出)
- ✗ **跳过的检测**:
  - MainWindowHandle（脚本无图形界面）
  - Responding（进程响应性）
  - WaitForInputIdle（输入空闲/卡死检测）
  - CPU 进度监测

### 3. 使用场景支持
- **短期任务** (`NoDaemon="true"`): 备份、清理、同步等一次性任务
- **长期服务** (`IsScript="true"`): 持续监测、日志监听、实时同步等后台服务

---

## 🔧 代码实现

### 核心文件修改

#### 1. **ProcessItem.cs** - 脚本检测和守护逻辑

**添加脚本标记属性** (Line ~60):
```csharp
[XmlAttribute]
public bool IsScript = false;  // 手动标记脚本类型
```

**自动检测方法** (Line ~500):
```csharp
private bool IsScriptFile(string path) {
    var ext = Path.GetExtension(path).ToLowerInvariant();
    return ext == ".bat" || ext == ".cmd" || ext == ".ps1" || ext == ".vbs";
}
```

**守护逻辑增强** (Line ~520 in `daemonNode()`):
```csharp
bool isScript = metaData.IsScript || IsScriptFile(NodePath);

if (isScript) {
    try {
        if (nodeProcess.HasExited) {
            RestartProcessChain("脚本进程退出");
        }
    } catch {
        RestartProcessChain("脚本进程已不存在");
    }
    return;  // 跳过其他检测
}

// 普通程序继续执行完整的守护检测
```

**日志指示**:
- 脚本模式进程在日志中显示 `[脚本模式]` 标记
- 便于调试和监测

#### 2. **MainWindow.xaml** - UI 菜单调整
菜单结构优化（实现 方案A）：
- **File 菜单**: 扁平化结构（进程目录、资源管理器、截图文件夹）
- **Tools 菜单**: 重新排序（常用功能优先）
- **Development 菜单**: 新增（开发辅助工具）

#### 3. **PickerOverlay.xaml** - ESC 快捷键修复
```xaml
<Window ... Focusable="True" ... />
```
确保截图/取色工具窗口能正确接收 ESC 键事件。

#### 4. **DaemonKit.csproj** - 构建配置
添加脚本文件自动复制配置：
```xml
<None Update="Resources\Scripts\example_short_task.bat">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  <ExcludeFromSingleFile>true</ExcludeFromSingleFile>
</None>
<!-- 类似条目用于其他脚本和说明文件 -->
```

**效果**: 编译时自动复制到输出目录
```
bin/Debug/net9.0-windows7.0/win-x64/Resources/Scripts/
bin/Release/net9.0-windows7.0/win-x64/Resources/Scripts/
```

---

## 📁 示例文件结构

```
DaemonKit/Resources/Scripts/
├── example_short_task.bat        (58 行) - 短期任务示例
├── example_long_service.bat      (85 行) - 长期服务示例
├── example_powershell_task.ps1   (35 行) - PowerShell 脚本示例
├── README_EXAMPLES.xml          (177 行) - 详细配置说明
└── README.md                    (新增) - 快速使用指南
```

### 示例说明

**example_short_task.bat**:
- 演示内容: 文件备份、临时文件清理、日志写入
- 执行时间: 完成后自动退出
- 配置方式: `NoDaemon="true"`
- 适用场景: 定时清理、备份同步

**example_long_service.bat**:
- 演示内容: 心跳循环、定期维护任务
- 执行时间: 持续运行直到被终止
- 配置方式: `IsScript="true"`
- 适用场景: 实时监测、日志监听、持续同步

**example_powershell_task.ps1**:
- 演示内容: 系统信息收集、进程查询
- 调用方式: PowerShell.exe 启动参数中指定脚本路径
- 配置方式: `NoDaemon="true"`
- 适用场景: 系统管理、数据处理

**README_EXAMPLES.xml**:
完整的配置示例和参数说明，包括：
- 4 种配置场景
- 参数详解
- 守护机制对比
- 最佳实践

---

## 📦 构建验证

### 构建结果
```
DaemonKit 成功，出现 85 警告 (0.9 秒)
```

### 文件复制验证
✅ 所有脚本文件已成功复制到输出目录：
- `example_short_task.bat` ✅
- `example_long_service.bat` ✅
- `example_powershell_task.ps1` ✅
- `README_EXAMPLES.xml` ✅
- `README.md` ✅

### 输出路径验证
```
C:\...\DaemonKit\bin\Debug\net9.0-windows7.0\win-x64\Resources\Scripts\
├── example_long_service.bat    ✅
├── example_powershell_task.ps1 ✅
├── example_short_task.bat      ✅
├── README.md                   ✅
└── README_EXAMPLES.xml         ✅
```

---

## 🚀 使用指南

### 配置脚本进程

#### 方式 1: 短期任务（一次性执行）
```xml
<ProcessNode 
  Name="文件备份" 
  Path="Resources\Scripts\example_short_task.bat"
  NoDaemon="true">
</ProcessNode>
```

#### 方式 2: 长期服务（持续运行）
```xml
<ProcessNode 
  Name="监控服务" 
  Path="Resources\Scripts\example_long_service.bat"
  IsScript="true">
</ProcessNode>
```

#### 方式 3: PowerShell 脚本
```xml
<ProcessNode 
  Name="系统信息收集"
  Path="PowerShell.exe"
  Arguments="-NoProfile -ExecutionPolicy Bypass -File &quot;Resources\Scripts\example_powershell_task.ps1&quot;"
  NoDaemon="true">
</ProcessNode>
```

### 常见参数

| 参数 | 说明 | 值 | 备注 |
|------|------|-----|------|
| **Name** | 进程显示名称 | 任意字符串 | 在界面中显示 |
| **Path** | 脚本或程序路径 | 相对/绝对路径 | 相对于 exe 目录 |
| **IsScript** | 标记为脚本类型 | `true`/`false` | 启用脚本模式 |
| **NoDaemon** | 禁用守护重启 | `true`/`false` | 一次性任务用 |
| **Delay** | 启动延迟 | 毫秒数 | 给脚本启动时间 |
| **Arguments** | 命令行参数 | 参数字符串 | 传递给程序 |
| **RunAs** | 管理员权限 | `true`/`false` | 提升权限运行 |

---

## 🔍 日志和调试

### 日志位置
```
%USERPROFILE%\.daemon_kit_logs\
```

### 脚本内日志
示例脚本演示了日志写入：
```batch
echo [%date% %time%] 脚本启动 >> !LOGFILE!
```

### 日志标记
- `[脚本模式]` - 表示进程使用脚本专用守护逻辑
- `脚本进程退出` - 脚本已完成
- `脚本进程已不存在` - 脚本异常终止

---

## ⚠️ 常见问题和解决

### Q: 脚本执行失败？
1. 检查脚本文件路径是否正确
2. 在命令行手动运行脚本测试
3. 查看日志文件了解错误详情
4. 检查脚本是否需要管理员权限

### Q: 脚本被频繁重启？
1. 确保脚本逻辑正确，不会立即异常退出
2. 增加 `Delay` 参数给脚本更多启动时间
3. 查看日志获取异常原因

### Q: PowerShell 脚本无法运行？
1. 检查执行策略：`Set-ExecutionPolicy -ExecutionPolicy RemoteSigned`
2. 使用 `-ExecutionPolicy Bypass` 绕过限制
3. 确保脚本路径中没有特殊字符

### Q: 如何区分短期和长期任务？
- **短期**: 脚本自动完成 → 设置 `NoDaemon="true"`
- **长期**: 脚本持续运行 → 设置 `IsScript="true"` (或只设置脚本扩展名)

---

## 📋 实现检查清单

- [x] ProcessItem.cs: 添加 IsScript 属性
- [x] ProcessItem.cs: 实现 IsScriptFile() 检测方法
- [x] ProcessItem.cs: 修改 daemonNode() 脚本模式逻辑
- [x] ProcessItem.cs: 添加脚本模式日志指示
- [x] MainWindow.xaml: 菜单结构优化 (方案A)
- [x] PickerOverlay.xaml: 添加 Focusable="True"
- [x] DaemonKit.csproj: 添加脚本文件复制配置
- [x] 创建 example_short_task.bat
- [x] 创建 example_long_service.bat
- [x] 创建 example_powershell_task.ps1
- [x] 创建 README_EXAMPLES.xml
- [x] 创建 README.md (快速使用指南)
- [x] 构建验证: 成功，无错误
- [x] 文件复制验证: 所有文件已复制到输出目录

---

## 📝 后续改进建议

1. **GUI 配置工具**: 添加 UI 向导简化脚本配置
2. **脚本模板**: 为不同场景提供更多模板脚本
3. **性能监测**: 添加脚本执行时间统计
4. **错误重试**: 实现智能重试策略（指数退避）
5. **事件通知**: 添加脚本执行通知（邮件、通知栏等）
6. **版本控制**: 为脚本添加版本管理功能

---

## 📞 技术支持

关键文档位置：
- 快速使用指南: `Resources\Scripts\README.md`
- 详细配置示例: `Resources\Scripts\README_EXAMPLES.xml`
- 脚本示例: `Resources\Scripts\example_*.{bat,ps1}`

---

**最后更新**: 2024
**版本**: 1.0
**状态**: ✅ 已完成并验证
