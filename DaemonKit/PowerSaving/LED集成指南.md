# KSV LED 设备集成指南

## 概述
本文档说明如何在 DaemonKit 能源管理系统中集成 KSV LED 控制器。

## 架构设计

### 1. 驱动接口 (`IBrightnessDriver`)
所有亮度控制驱动都实现此接口：
```csharp
public interface IBrightnessDriver
{
    bool CanHandle(DisplayIdentity display);
    Task<BrightnessInfo?> GetBrightnessAsync(DisplayIdentity display, ...);
    Task<bool> SetBrightnessAsync(DisplayIdentity display, byte brightness, ...);
}
```

### 2. 现有驱动
- **DdcCiBrightnessDriver** - 标准显示器（DDC/CI 协议）
- **KsvLedBrightnessDriver** - KSV LED 控制器（串口/网口）

### 3. 驱动注册
在 `PowerSavingViewModel` 构造时自动根据配置注册 LED 驱动：

```csharp
// 在 AppSettings 中配置
var settings = App.Current.Settings;
settings.LedEnabled = true;
settings.LedConnectionType = "Serial"; // 或 "TCP"
settings.LedSerialPort = "COM3";
settings.LedBaudRate = 115200;

// PowerSavingViewModel 会自动注册：
// KsvLedBrightnessDriver.CreateSerial("COM3", 115200)
```

## UI 配置

### 打开节能窗口
在 DaemonKit 主界面，点击"显示器能源管理"按钮打开 PowerSavingWindow。

### LED 设备配置卡片
找到"LED 设备配置"卡片，按以下步骤配置：

1. **启用 LED 控制器**：勾选"启用 KSV LED 控制器"复选框
2. **选择连接类型**：
   - **串口 (RS232)**：适用于 KSV6c, KSV8c, KSV2C, KSV4c, KM2, KM4
   - **网口 (TCP)**：适用于 KSV24c, KSV12c

3. **配置连接参数**：
   - 串口模式：设置 COM 端口（如 COM3）和波特率（默认 115200）
   - 网口模式：设置 IP 地址和端口（默认 18100）

4. **重启窗口生效**：修改配置后，关闭并重新打开节能窗口使 LED 驱动生效

### 亮度控制
启用 LED 后，所有亮度操作（正常模式/省电模式/独立配置）会同时应用到 LED 设备和显示器。

## KSV LED 驱动使用

### 串口模式（工厂方法）
```csharp
// 适用于: KSV6c, KSV8c, KSV2C, KSV4c, KM2, KM4
var driver = KsvLedBrightnessDriver.CreateSerial("COM3", 115200);
```

### 网口模式（工厂方法）
```csharp
// 适用于: KSV24c, KSV12c
var driver = KsvLedBrightnessDriver.CreateTcp("192.168.0.100", 18100);
```

## 亮度读取功能

### 网口模式
使用 0x22 命令读取亮度和对比度：
- **发送指令**：26 字节，命令字为 0x22
- **响应格式**：字节 16 为对比度，字节 17 为亮度
- **实现方法**：`GetBrightnessViaTcpAsync()`

### 串口模式
协议无专门读取命令，采用缓存策略：
- **缓存变量**：`_lastSetBrightness`
- **更新时机**：每次调用 `SetBrightnessAsync()` 后更新缓存
- **读取方法**：返回缓存值

### 亮度值映射
- **UI 范围**：0-100%
- **协议范围**：0x00-0xFF (0-255)
- **设置换算**：`hexValue = uiValue * 255 / 100`
- **读取换算**：`uiValue = hexValue * 100 / 255`

## 设备识别

### 方式1：通过设备名称
LED 设备的 `DeviceName` 包含 "ksv", "led", "km2", "km4" 等关键字时自动识别。

### 方式2：通过设备类型
手动创建 `DisplayIdentity` 时指定：
```csharp
var ledDevice = new DisplayIdentity(
    deviceName: "KSV-LED-001",
    devicePath: "COM3",
    friendlyName: "LED 显示屏",
    displayIndex: 0,
    deviceType: DeviceType.KsvLed
);
```

## 协议细节

### 串口协议帧格式
| 字节 | 说明 | 示例 |
|------|------|------|
| 0 | 帧起始 | 0xE9 |
| 1 | 设备ID | 0x00 |
| 2 | 命令字 | 0x93 (亮度), 0x94 (对比度), 0x95 (固化) |
| 3 | 数据 | 0x64 (100) |
| 4 | 保留 | 0x00 |
| 5 | 校验和 | 0xE0 (前5字节累加) |
| 6-7 | 帧结束 | 0x0D 0x0A |

### 网口协议帧格式（设置亮度）
| 字节范围 | 说明 |
|----------|------|
| 0-19 | 固定包头 (D2 02 96 49 1A ... 21 20 06 00 00 AC 01 3A) |
| 20 | 亮度值 (0x00-0xFF) |
| 21 | 对比度值 (0x00-0xFF) |
| 22-25 | 固定包尾 (2E FD 69 B6) |

### 网口协议帧格式（读取亮度）
**请求**：26 字节，命令字为 0x22 (字节12)
**响应**：26 字节，字节16为对比度，字节17为亮度

### 固化指令
设置亮度后必须发送固化指令（0x95），否则断电后恢复默认值：
```
E9 00 95 00 00 7E 0D 0A
```

## 配置示例

### 场景1：纯显示器环境
```csharp
// AppSettings 中 LedEnabled = false
// 只使用默认 DDC/CI 驱动
```

### 场景2：混合环境（显示器 + 串口 LED）
```csharp
var settings = App.Current.Settings;
settings.LedEnabled = true;
settings.LedConnectionType = "Serial";
settings.LedSerialPort = "COM3";
settings.LedBaudRate = 115200;

// PowerSavingViewModel 会自动注册两个驱动：
// 1. DdcCiBrightnessDriver (默认)
// 2. KsvLedBrightnessDriver.CreateSerial("COM3", 115200)
```

### 场景3：混合环境（显示器 + 网口 LED）
```csharp
var settings = App.Current.Settings;
settings.LedEnabled = true;
settings.LedConnectionType = "TCP";
settings.LedIpAddress = "192.168.1.100";
settings.LedTcpPort = 18100;

// PowerSavingViewModel 会自动注册：
// KsvLedBrightnessDriver.CreateTcp("192.168.1.100", 18100)
```
```csharp
var drivers = new List<IBrightnessDriver>
{
    new DdcCiBrightnessDriver(),                   // 普通显示器
    KsvLedBrightnessDriver.CreateSerial("COM3")    // LED 控制器
};
var coordinator = new BrightnessCoordinator(drivers);
```

### 场景3：多个 LED 设备
```csharp
var drivers = new List<IBrightnessDriver>
{
    new DdcCiBrightnessDriver(),
    KsvLedBrightnessDriver.CreateSerial("COM3"),           // LED 1
    KsvLedBrightnessDriver.CreateTcp("192.168.0.100")      // LED 2
};
```

## 注意事项

1. **网口连接**：首次使用前必须发送"设备连接指令"
2. **串口波特率**：默认 115200，数据位8，停止位1，无校验
3. **固化延迟**：发送固化指令后建议延迟 50-100ms
4. **错误处理**：协议不支持读取当前亮度，返回默认范围 0-100
5. **设备识别**：确保 LED 设备名称包含识别关键字

## 扩展性

### 添加新驱动
1. 实现 `IBrightnessDriver` 接口
2. 在 `BrightnessCoordinator` 构造时注册
3. 实现 `CanHandle()` 方法进行设备识别

### 示例：添加其他品牌 LED
```csharp
public class CustomLedDriver : IBrightnessDriver
{
    public bool CanHandle(DisplayIdentity display)
    {
        return display.DeviceName.Contains("CustomBrand");
    }
    
    // 实现其他方法...
}
```

## 问题排查

| 问题 | 原因 | 解决方案 |
|------|------|----------|
| 亮度不变 | 未发送固化指令 | 检查是否调用 0x95 命令 |
| 串口无响应 | 波特率不匹配 | 确认波特率为 115200 |
| 网口连接失败 | 未发送连接指令 | 先发送设备连接指令 |
| 设备未识别 | 名称不匹配 | 修改 `CanHandle()` 逻辑 |

## 参考文档
- `衢州LED控制协议.md` - 完整协议规范
- `KsvLedBrightnessDriver.cs` - 驱动实现源码

## 故障排查

### 问题1：LED 设备未响应
**症状**：设置亮度后 LED 屏幕无变化
**检查项**：
1. 确认串口/网口连接正常（可用串口助手/网络调试工具测试）
2. 确认 COM 端口号或 IP 地址配置正确
3. 查看日志输出（搜索 `[KSV-LED]` 关键字）
4. 串口模式检查波特率是否为 115200
5. 网口模式检查端口是否为 18100

### 问题2：亮度读取返回默认值
**症状**：GetBrightnessAsync() 始终返回 50%
**原因**：
- 网口模式：TCP 连接失败或响应格式错误
- 串口模式：缓存未初始化（未调用过 SetBrightnessAsync）

### 问题3：修改配置后不生效
**原因**：LED 驱动在 PowerSavingViewModel 构造时注册
**解决**：关闭并重新打开节能窗口

### 问题4：多个 LED 设备冲突
**限制**：当前实现仅支持单个 LED 驱动实例
**方案**：扩展 AppSettings 支持多设备配置（未来版本）

## 技术细节

### 线程安全
- 所有驱动方法都是 `async Task`，避免阻塞 UI 线程
- 串口/TCP 连接在首次使用时懒加载创建
- 资源释放通过 `Dispose()` 方法清理

### 错误处理
- 所有网络/串口异常都被捕获并记录日志
- 失败时返回默认值，不会抛出异常
- 使用 `DNHper.NLogger` 统一日志输出

### 性能优化
- 连接保持：TCP/串口连接建立后复用，不频繁重连
- 节流控制：亮度滑块配置 300ms 节流，减少指令发送频率
- 异步设计：所有 I/O 操作都是异步，不阻塞主线程

## 未来增强

### 计划功能
- [ ] 支持多个 LED 设备同时控制
- [ ] LED 设备自动发现（串口枚举/网络扫描）
- [ ] 对比度控制 UI
- [ ] LED 设备状态监控（在线/离线）
- [ ] 协议扩展（支持更多 KSV 型号）

### 贡献指南
如需添加新 LED 设备支持：
1. 实现 `IBrightnessDriver` 接口
2. 在 `PowerSavingViewModel.RegisterLedDriverIfEnabled()` 中注册驱动
3. 更新 `DeviceType` 枚举（如有必要）
4. 添加 UI 配置选项到 PowerSavingWindow.xaml
5. 更新本文档
