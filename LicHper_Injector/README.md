# LicHper_Injector 项目

DLL 注入工具，用于自动向目标应用程序注入 LicHper.dll，并同步配置文件和水印资源。

## 功能

- 拖拽 EXE 文件进行 DLL 注入
- 自动复制 .authrc.ini 配置文件到用户主目录
- 自动复制水印图片资源到 ~/.lichper 目录
- 支持命令行调用

## 使用方法

### 方法 1: 拖拽注入（推荐）

直接将要注入的 EXE 文件拖拽到 LicHper_Injector.exe 上

### 方法 2: 命令行

```bash
LicHper_Injector.exe "C:\path\to\your_app.exe"
```

## 编译

使用 Visual Studio 2022 或更高版本打开 LicHper_Injector.vcxproj 并编译

```bash
msbuild LicHper_Injector.vcxproj /p:Configuration=Release /p:Platform=x64
```

## 文件结构要求

```
LicHper_Injector.exe 所在目录/
├── LicHper_Injector.exe      ← 注入工具
├── LicHper.dll               ← 水印 DLL（必需）
├── .authrc.ini               ← 配置文件（可选）
└── watermark*.png            ← 水印图片（可选）
```

## 工作原理

1. 检验目标 EXE 文件和 LicHper.dll 是否存在
2. 以被挂起状态创建目标进程
3. 在目标进程内存中分配空间并写入 DLL 路径
4. 在目标进程中创建远程线程执行 LoadLibraryA
5. 复制配置文件和资源文件到用户主目录
6. 恢复目标进程执行

## 返回值

- 0: 成功
- 1: 失败
