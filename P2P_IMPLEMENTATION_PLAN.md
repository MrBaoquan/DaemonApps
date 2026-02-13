# P2P 设备联调与文件传输功能实现计划

## 项目概述

为DaemonKit添加P2P文件传输功能，重构联调面板支持分页、搜索、多文件并发传输及断点续传。

---

## 技术栈

| 组件 | 库 | 版本 | 用途 |
|------|-----|------|------|
| 设备发现 | Zeroconf | 3.6.11 | mDNS跨子网发现 |
| P2P通信 | NetMQ | 4.0.1.13 | 高性能消息传输 |
| 响应式集合 | DynamicData | 8.4.1 | 分页、过滤、排序 |

---

## 实施阶段

### Phase 1: 基础设施 ✅ 完成
- [x] 创建实现计划文档
- [x] 添加NuGet依赖包 (NetMQ, Zeroconf, DynamicData)
- [x] 创建文件传输模型 (`FileTransferModels.cs`)

### Phase 2: 文件传输服务 ✅ 完成
- [x] 实现 `P2PFileTransferService`
  - [x] NetMQ Router/Dealer 模式通信
  - [x] 多文件并发传输 (最多4个)
  - [x] 断点续传协议
  - [x] 传输进度Observable流
  - [x] 暂停/恢复/取消功能

### Phase 3: ViewModel重构 ✅ 完成
- [x] 实现 `DaemonPanelViewModel`
  - [x] DynamicData SourceCache 响应式缓存
  - [x] 搜索过滤 (设备名、IP、CPU)
  - [x] 状态过滤 (在线/离线/忙碌)
  - [x] 分页控制 (10/20/50条)
  - [x] 文件传输队列管理

### Phase 4: UI重构 ✅ 完成
- [x] 重构 `DaemonTable.xaml`
  - [x] 工具栏 (搜索框、状态筛选、刷新)
  - [x] DataGrid 设备列表 (状态指示灯、操作按钮)
  - [x] 分页控件 (上一页/下一页/页码)
  - [x] 文件传输队列面板 (进度条、速度、操作)
- [x] 更新 `DaemonTable.xaml.cs` 代码后置
- [x] 创建 `TransferConverters.cs` 转换器

### Phase 5: 集成与测试 ✅ 完成
- [x] MainWindow集成 (DaemonTable已在构造函数中初始化)
- [x] 设备离线检测定时器 (5秒间隔自动更新状态)
- [x] 编译验证通过
- [ ] 可选: Zeroconf跨子网发现 (当前使用UDP广播)

---

## 文件清单

### 新建文件
| 路径 | 描述 |
|------|------|
| `DaemonKit/Models/FileTransferModels.cs` | 传输任务模型 |
| `DaemonKit/Services/P2PFileTransferService.cs` | P2P传输服务 |
| `DaemonKit/ViewModels/DaemonPanelViewModel.cs` | 联调面板ViewModel |
| `DaemonKit/Converters/TransferConverters.cs` | 传输相关转换器 |

### 修改文件
| 路径 | 修改内容 |
|------|----------|
| `DaemonKit/DaemonKit.csproj` | 添加NuGet包引用 |
| `DaemonKit/ViewModels/DaemonTableViewModel.cs` | MachineInfo扩展 |
| `DaemonKit/Views/DaemonTable.xaml` | UI重构 |
| `DaemonKit/Views/DaemonTable.xaml.cs` | 绑定ViewModel |
| `DaemonKit/Services/NetworkBroadcastService.cs` | 集成Zeroconf |

---

## 断点续传协议

### 传输流程
```
Client                              Server
  |                                    |
  |-- 1. TransferMetadata ------------>|  (文件名、大小、MD5、续传偏移)
  |                                    |
  |<-- 2. ResumeResponse --------------|  (实际续传位置)
  |                                    |
  |-- 3. Data Chunk [256KB] ---------->|  (循环发送)
  |-- 3. Data Chunk [256KB] ---------->|
  |       ...                          |
  |                                    |
  |<-- 4. TransferComplete ------------|  (接收端MD5验证结果)
  |                                    |
```

### 消息格式
```json
// 1. TransferMetadata
{
  "taskId": "guid",
  "fileName": "data.zip",
  "totalBytes": 1048576,
  "resumeOffset": 524288,
  "fileHash": "md5..."
}

// 2. ResumeResponse  
{
  "taskId": "guid",
  "actualOffset": 524288,
  "accepted": true
}

// 4. TransferComplete
{
  "taskId": "guid",
  "receivedHash": "md5...",
  "hashMatch": true
}
```

---

## 端口分配

| 端口 | 用途 | 协议 |
|------|------|------|
| 7007 | 设备广播 (现有) | UDP |
| 7008 | 命令控制 (现有) | UDP |
| 7009 | P2P文件传输 | TCP (NetMQ) |
| 5353 | mDNS发现 (Zeroconf) | UDP Multicast |

---

## 注意事项

1. **Windows防火墙**: 需添加7009端口入站规则
2. **并发限制**: 最多4个同时传输任务
3. **分块大小**: 256KB (与现有AsyncFileCopy一致)
4. **超时检测**: 设备15秒无心跳标记离线
5. **MD5验证**: 传输完成后校验文件完整性

---

## 依赖关系

```
DaemonPanelViewModel
    ├── NetworkBroadcastService (设备发现)
    ├── P2PFileTransferService (文件传输)
    └── DynamicData.SourceCache (响应式集合)
           ├── Filter (搜索/状态过滤)
           ├── Sort (排序)
           └── Page (分页)
```
