# DaemonKit 脚本文件选择功能优化说明

## 功能改进

### 问题描述
之前在进程结点配置中选择路径时，文件选择对话框只能选择 `.exe` 可执行文件，无法选择脚本文件（`.bat`、`.cmd`、`.ps1`、`.vbs`）。

### 解决方案
优化了 `OpenFileDialog` 配置和 `PNFViewModel`，现在支持以下功能：

1. **扩展文件选择范围**
   - ✅ 可执行文件 (`.exe`)
   - ✅ 批处理脚本 (`.bat`, `.cmd`)
   - ✅ PowerShell 脚本 (`.ps1`)
   - ✅ VBScript (`.vbs`)
   - ✅ 所有文件 (`*.*`)

2. **自动脚本检测**
   - 选择脚本文件时，自动检测并标记 `IsScript` 属性
   - 用户无需手动配置脚本模式

3. **界面改进**
   - 在进程结点编辑表单中新增"脚本模式"选项卡
   - 提供工具提示说明脚本模式的作用

---

## 技术实现

### 1. PNFViewModel.cs 修改

#### 添加脚本检测方法
```csharp
// 检测文件是否为脚本
private bool IsScriptFile(string path)
{
    var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
    return ext == ".bat" || ext == ".cmd" || ext == ".ps1" || ext == ".vbs";
}
```

#### 更新文件选择过滤器
```csharp
openFileDialog.Filter = "所有支持的文件(*.exe;*.bat;*.cmd;*.ps1;*.vbs)|*.exe;*.bat;*.cmd;*.ps1;*.vbs|" +
                        "可执行文件(*.exe)|*.exe|" +
                        "批处理脚本(*.bat;*.cmd)|*.bat;*.cmd|" +
                        "PowerShell脚本(*.ps1)|*.ps1|" +
                        "VBScript脚本(*.vbs)|*.vbs|" +
                        "所有文件(*.*)|*.*";
```

#### 自动标记脚本文件
```csharp
openFileDialog.FileOk += (o, args) =>
{
    var _path = openFileDialog.FileName;
    // ... 其他验证 ...
    
    // 自动检测并标记脚本文件
    IsScript = IsScriptFile(_path);
    
    // ... 其他处理 ...
};
```

#### 添加 IsScript 属性
```csharp
private bool isScript = false;
public bool IsScript
{
    get { return isScript; }
    set { this.RaiseAndSetIfChanged(ref isScript, value); }
}
```

#### 更新 Confirm 命令
```csharp
this.Confirm = ReactiveCommand.Create<ProcessMetaData>(
    () =>
    {
        return new ProcessMetaData
        {
            // ... 其他属性 ...
            IsScript = this.IsScript,  // 包含脚本标记
            // ...
        };
    }
);
```

#### 更新编辑表单加载
```csharp
public void SyncEditFormProperties(ProcessMetaData InMeta)
{
    // ...
    this.IsScript = InMeta.IsScript;  // 加载脚本标记
    // ...
}
```

### 2. ProcessNodeForm.xaml 修改

在运行选项后添加脚本选项卡：

```xaml
<!-- 脚本选项分组 -->
<materialDesign:Card Margin="0,0,0,30"
                     Padding="20,16,20,16"
                     materialDesign:ElevationAssist.Elevation="Dp2">
    <CheckBox IsChecked="{Binding Path=IsScript, Mode=TwoWay}"
              Content="脚本模式（支持 .bat/.cmd/.ps1/.vbs）"
              VerticalAlignment="Center"
              FontSize="14"
              ToolTip="启用脚本模式后，只监测进程是否存在，跳过窗口句柄和响应性检测"/>
</materialDesign:Card>
```

---

## 使用流程

### 方法 1：自动识别（推荐）

1. 点击进程路径文本框或旁边的浏览按钮
2. 文件选择对话框打开
3. 在下拉列表中选择 "批处理脚本" 或 "PowerShell脚本"
4. 选择脚本文件，点击"确定"
5. **脚本模式会自动启用** ✅

### 方法 2：手动配置

1. 选择任何类型的脚本文件
2. 在表单中找到"脚本模式"复选框
3. 手动勾选或取消勾选

---

## 配置示例

### 配置短期脚本任务
```xml
<ProcessNode 
  Name="文件备份" 
  Path="Resources\Scripts\backup.bat"
  IsScript="false"
  NoDaemon="true">
</ProcessNode>
```

### 配置长期脚本服务
```xml
<ProcessNode 
  Name="监控服务" 
  Path="Resources\Scripts\monitor.bat"
  IsScript="true">
</ProcessNode>
```

### 配置 PowerShell 脚本
```xml
<ProcessNode 
  Name="系统检查" 
  Path="PowerShell.exe"
  Arguments="-ExecutionPolicy Bypass -File &quot;Scripts\check.ps1&quot;"
  IsScript="false"
  NoDaemon="true">
</ProcessNode>
```

---

## 文件选择对话框说明

### 过滤器选项

| 选项 | 支持的文件 | 说明 |
|------|----------|------|
| 所有支持的文件 | `.exe;.bat;.cmd;.ps1;.vbs` | 默认选项，可看到所有程序和脚本 |
| 可执行文件 | `.exe` | 只显示 Windows 可执行程序 |
| 批处理脚本 | `.bat;.cmd` | 只显示批处理脚本 |
| PowerShell脚本 | `.ps1` | 只显示 PowerShell 脚本 |
| VBScript脚本 | `.vbs` | 只显示 VBScript 脚本 |
| 所有文件 | `*.*` | 显示所有文件类型 |

---

## 脚本模式说明

### 什么是脚本模式？

脚本模式是为 Windows 脚本（`.bat`、`.cmd`、`.ps1`、`.vbs`）优化的守护模式。

### 脚本模式 vs 普通模式

| 检测项 | 脚本模式 | 普通模式 |
|--------|---------|---------|
| 进程存在 | ✅ | ✅ |
| 主窗口句柄 | ❌ | ✅ |
| 进程响应性 | ❌ | ✅ |
| 输入空闲检测 | ❌ | ✅ |
| CPU 进度检测 | ❌ | ✅ |

### 为什么脚本需要特殊模式？

- **没有主窗口**: 脚本进程没有 `MainWindowHandle`，普通模式会抛异常
- **无响应性检测**: 脚本运行时无法有效检测响应性
- **简化守护逻辑**: 脚本只需关心是否还在运行，不需要复杂的卡死检测

---

## 兼容性说明

- ✅ 向后兼容：现有的 `.exe` 配置不受影响
- ✅ 自动升级：旧的 XML 配置可以正常加载
- ✅ 智能识别：自动检测文件类型并配置脚本标记

---

## 故障排查

### Q: 为什么脚本文件选不了？
**A**: 检查以下几点：
1. 确保使用最新编译的 DaemonKit
2. 在文件选择对话框中验证过滤器设置为"所有支持的文件"或特定脚本类型
3. 确保文件扩展名正确

### Q: 脚本被自动标记为脚本模式，如何取消？
**A**: 
1. 在表单中找到"脚本模式"复选框
2. 取消勾选，保存配置
3. 脚本将使用普通模式运行（不推荐）

### Q: 选择 .exe 为什么没有自动标记为脚本模式？
**A**: 这是正确的行为。只有 `.bat/.cmd/.ps1/.vbs` 会自动标记为脚本模式。

---

## 相关文档

- 脚本守护详细说明: `SCRIPT_DAEMON_IMPLEMENTATION.md`
- 脚本使用快速参考: `Resources\Scripts\QUICKREF.md`
- 脚本配置示例: `Resources\Scripts\README_EXAMPLES.xml`

---

**更新日期**: 2024  
**版本**: 1.1  
**状态**: ✅ 已实现并验证
