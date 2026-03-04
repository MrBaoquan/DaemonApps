# DaemonKit 协议规范文档

> 版本: 1.1  
> 更新日期: 2026-02-27  
> 适用组件: DaemonKit（运维管家）

---

## 目录

- [第一部分：软件包文件结构](#第一部分软件包文件结构)
  - [1. 包类型概览](#1-包类型概览)
  - [2. 清单文件 manifest.json](#2-清单文件-manifestjson)
  - [3. TreeBundle 进程树包](#3-treebundle-进程树包)
  - [4. NodeFull 单节点全量包](#4-nodefull-单节点全量包)
  - [5. NodePatch 单节点补丁包](#5-nodepatch-单节点补丁包)
  - [6. 补丁应用模式](#6-补丁应用模式)
  - [7. 压缩格式](#7-压缩格式)
- [第二部分：网络通讯协议](#第二部分网络通讯协议)
  - [1. 端口总览](#1-端口总览)
  - [2. 设备发现广播（UDP 7007）](#2-设备发现广播udp-7007)
  - [3. 控制指令（UDP 7008）](#3-控制指令udp-7008)
  - [4. 进程心跳（UDP 7777）](#4-进程心跳udp-7777)
  - [5. P2P 文件传输（TCP 7009）](#5-p2p-文件传输tcp-7009)
  - [6. TCP 设备探测（TCP 7009）](#6-tcp-设备探测tcp-7009)
  - [7. 通讯架构总览](#7-通讯架构总览)

---

## 第一部分：软件包文件结构

### 1. 包类型概览

DaemonKit 支持三种软件包类型，均使用 ZIP 压缩格式：

| 包类型 | 枚举值 | 文件扩展名 | 用途 |
|--------|--------|-----------|------|
| **进程树包** | `TreeBundle` | `.dkp.zip` | 导出多节点进程树 + 配置文件 + 所有关联程序目录 |
| **单节点全量包** | `NodeFull` | `.dkp.zip` | 单个节点的完整程序目录快照 |
| **单节点补丁包** | `NodePatch` | `.dkp-patch.zip` | 单个节点的增量/选择性文件更新 |

### 2. 清单文件 manifest.json

所有包的根目录下均包含 `manifest.json`，为统一清单格式（Schema Version 1.0）。

#### 2.1 完整 JSON Schema

```jsonc
{
  // ─── 基础信息（所有包类型通用）───
  "schemaVersion": "1.0",               // 清单格式版本，固定 "1.0"
  "packageType": "TreeBundle",           // 枚举: "TreeBundle" | "NodeFull" | "NodePatch"
  "createdAt": "2026-02-24T15:30:00",    // 创建时间 (ISO 8601)
  "description": "用户填写的描述",        // 可选，用户自定义描述

  // ─── 制作来源信息（所有包类型通用）───
  "source": {
    "machineName": "DESKTOP-ABC123",     // 制作机器名
    "userName": "Administrator",          // 制作用户名
    "builder": "DaemonKit",              // 制作工具: "DaemonKit" | "UnityCI" | "Manual"
    "builderVersion": "1.0.0"            // 制作工具版本（可选）
  },

  // ─── 进程树信息（仅 TreeBundle）───
  "tree": {
    "projectName": "Demo",               // 项目名称（根节点名）
    "includedConfigs": [                  // 包含的配置文件列表
      "treeViewData.xml",
      "schedule_config.json",
      "hotkey_config.json",
      "app_settings.json",
      "global_schedule.json"
    ],
    "programs": [                         // 包含的程序概要列表
      {
        "name": "MyApp",                  // 程序目录名
        "exePath": "MyApp/MyApp.exe",     // 可执行文件相对路径
        "sizeBytes": 104857600,           // 目录总大小（字节）
        "programType": "Unity"            // 程序类型: "Unity" | "UnrealEngine" | "Other"
      }
    ]
  },

  // ─── 目标程序信息（仅 NodeFull / NodePatch）───
  "target": {
    "exeName": "MyApp.exe",              // 主可执行文件名（主匹配键）
    "nodeName": "我的应用",               // 节点显示名（回退匹配键，可选）
    "programType": "Unity",              // 程序类型（可选）
    "version": "1.2.3"                   // 程序版本号（可选）
  },

  // ─── 补丁信息（仅 NodePatch）───
  "patch": {
    "patchMode": "Overlay",              // 补丁模式: "Overlay" | "Replace"
    "baseVersion": "1.0.0"               // 基于的程序版本（可选，校验用）
  }
}
```

#### 2.2 字段出现规则

| 字段 | TreeBundle | NodeFull | NodePatch |
|------|:----------:|:--------:|:---------:|
| `schemaVersion` | ✅ | ✅ | ✅ |
| `packageType` | ✅ | ✅ | ✅ |
| `createdAt` | ✅ | ✅ | ✅ |
| `description` | 可选 | 可选 | 可选 |
| `source` | ✅ | ✅ | ✅ |
| `tree` | ✅ | ✗ | ✗ |
| `target` | ✗ | ✅ | ✅ |
| `patch` | ✗ | ✗ | ✅ |

---

### 3. TreeBundle 进程树包

#### 3.1 目录结构

```
<archive>.dkp.zip
├── manifest.json              ← 统一清单 (PackageType = "TreeBundle")
├── Configs/                   ← 配置文件目录
│   ├── treeViewData.xml       ← 进程树序列化 (XML, List<ProcessItem>)
│   ├── schedule_config.json   ← 计划任务配置
│   ├── hotkey_config.json     ← 快捷键配置
│   ├── app_settings.json      ← 应用设置
│   └── global_schedule.json   ← 全局计划
└── Programs/                  ← 程序文件目录
    ├── MyApp/                 ← 程序目录（完整拷贝）
    │   ├── MyApp.exe
    │   ├── MyApp_Data/
    │   └── ...
    └── AnotherApp/
        ├── AnotherApp.exe
        └── ...
```

#### 3.2 导出规则

- **节点选择**：仅第二级节点（SuperRoot 直接子节点）可由用户勾选，第三级及更深的子节点随父节点自动包含
- **程序根目录检测**：
  - Unity 程序 → 含 `*_Data` 文件夹的 exe 所在目录
  - UnrealEngine 程序 → `Binaries/Win64` 上两级目录
  - 其他程序 → exe 所在目录
- **路径转换**：导出时将绝对路径转为相对路径，存入 `Configs/treeViewData.xml`

---

### 4. NodeFull 单节点全量包

#### 4.1 目录结构

```
<NodeName>_v<Version>.dkp.zip
├── manifest.json              ← 统一清单 (PackageType = "NodeFull")
└── files/                     ← 完整程序目录内容
    ├── MyApp.exe
    ├── MyApp_Data/
    │   └── ...
    ├── config.ini
    └── ...
```

#### 4.2 文件命名

```
{节点名}_{版本号}.dkp.zip
```
示例：`MyApp_v1.2.3.dkp.zip`

---

### 5. NodePatch 单节点补丁包

#### 5.1 目录结构

```
<NodeName>_v<Version>_patch.dkp-patch.zip
├── manifest.json              ← 统一清单 (PackageType = "NodePatch")
└── files/                     ← 仅用户选择的文件（保留相对路径结构）
    ├── MyApp.exe              ← 已更新的可执行文件
    └── Plugins/
        └── updated.dll        ← 已更新的特定文件
```

#### 5.2 文件命名

```
{节点名}_{版本号}_patch.dkp-patch.zip
```
示例：`MyApp_v1.2.3_patch.dkp-patch.zip`

#### 5.3 文件选择

补丁包通过 UI 的树形文件浏览器让用户勾选需要包含的文件，仅选中的文件（保留相对目录结构）被打入 `files/` 目录。

---

### 6. 补丁应用模式

导入节点包时，用户可选择应用模式：

| 模式 | 枚举值 | 行为 | 默认适用 |
|------|--------|------|---------|
| **覆盖模式** | `Overlay` | 仅覆盖 `files/` 中存在的文件，保留目标目录的其他文件 | NodePatch |
| **替换模式** | `Replace` | 先清空目标目录（自动备份），再拷贝 `files/` 全部内容 | NodeFull |

**自动备份**：替换模式执行前会自动将目标目录备份。

**导入节点匹配逻辑**（按优先级）：
1. 按 `target.exeName` 匹配进程树中的可执行文件名（不区分大小写）
2. 按 `target.nodeName` 匹配节点显示名
3. 零匹配：NodeFull 可创建新节点并添加到进程树；NodePatch 显示完整列表供手动选择
4. 多匹配：显示候选列表供用户选择

---

### 7. 压缩格式

| 属性 | 值 |
|------|-----|
| 格式 | ZIP (Deflate) |
| 实现库 | SharpCompress |
| 文件名编码 | UTF-8 |
| 读写缓冲区 | 8 MB |
| ZIP 签名校验 | `PK\x03\x04` 魔数 |

---

## 第二部分：网络通讯协议

### 1. 端口总览

| 端口 | 默认值 | 传输层 | 用途 | 可配置 |
|------|--------|--------|------|:------:|
| MetaPort | **7007** | UDP | 设备发现广播 | ✅ |
| ControlPort | **7008** | UDP | 控制指令 + 进程心跳（统一端口） | ✅ |
| FileTransferPort | **7009** | TCP | P2P 文件传输 + 设备探测 + 文件列表查询 | ✅ |

**端口配置**：MetaPort / ControlPort / FileTransferPort 可通过 `AppSettings` 的 `CustomMetaPort` / `CustomControlPort` / `CustomFileTransferPort` 覆盖（值 > 0 时生效）。

> **v2 变更**：HeartbeatPort（原 7777）已合并至 ControlPort，减少端口占用；FileTransferPort 新增可配置支持。

---

### 2. 设备发现广播（UDP 7007）

用于局域网内自动发现其他 DaemonKit 实例。

| 属性 | 值 |
|------|-----|
| 端口 | `MetaPort`（默认 7007） |
| 传输 | UDP 广播 |
| 方向 | 一对多（子网广播） |
| 间隔 | 每 **3 秒** |
| 超时判定 | **15 秒** 无广播 → 设备离线（约错过 5 次广播） |

#### 广播消息格式

UTF-8 编码的 JSON，序列化自 `MachineInfo` 对象：

```json
{
  "ID": "",
  "Name": "设备名称",
  "IPs": ["192.168.1.100", "10.0.0.5"],
  "CPUs": ["Intel Core i9-13900K"],
  "GPUs": ["NVIDIA GeForce RTX 4090"],
  "Memories": ["Samsung 32GB DDR5"]
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `ID` | string | 设备唯一标识（空字符串或 GUID） |
| `Name` | string | 设备名称（取进程树根节点名，默认回退到首个子节点名） |
| `IPs` | string[] | 所有网络接口 IP 地址 |
| `CPUs` | string[] | CPU 型号列表 |
| `GPUs` | string[] | GPU 型号列表 |
| `Memories` | string[] | 内存条信息列表 |

---

### 3. 控制指令（UDP 7008）

用于向指定设备发送远程控制命令。

| 属性 | 值 |
|------|-----|
| 端口 | `ControlPort`（默认 7008） |
| 传输 | UDP 单播（点对点） |
| 方向 | 双向（发送端 ↔ 接收端） |
| 重试 | 最多 **3 次**，指数退避（300ms × 次数） |

#### 消息格式

UTF-8 编码的 JSON，序列化自 `Command` 对象：

```json
{
  "evt": 1002,
  "data": { },
  "token": ""
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `evt` | int | 事件 ID（见下表） |
| `data` | object | 可选的附加数据（JSON 对象） |
| `token` | string | 认证令牌（仅启用认证时携带，见下方"认证机制"） |

#### 认证机制

可选的共享令牌认证，**默认不启用**。

| 属性 | 值 |
|------|-----|
| 配置项 | `AppSettings.AuthToken` |
| 默认值 | 空字符串（空 = 禁用认证） |
| 作用范围 | ControlPort 上的非心跳指令 |
| 豁免指令 | `HEARTBEAT`（evt 1221）— 心跳是高频状态信号，跳过认证 |

**验证逻辑**：
1. 发送端：若 `CommonVars.IsAuthEnabled`，自动将 `AuthToken` 附加到 `Command.token` 字段
2. 接收端：若 `CommonVars.IsAuthEnabled` 且指令非 HEARTBEAT，校验 `cmd.Token == CommonVars.AuthToken`
3. 令牌不匹配 → 丢弃指令 + 记录警告日志（含来源 IP）
4. 双方必须配置相同的 `AuthToken` 才能互相控制

#### ACK 确认机制

控制指令执行后，接收端向发送方回送 ACK 确认消息（best-effort，不影响主流程）。

```json
{
  "evt": 1300,
  "data": {
    "ackEvt": 1002,
    "machineName": "DESKTOP-TARGET",
    "timestamp": "2026-02-27T10:30:00.000+08:00"
  }
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `evt` | int | 固定为 `1300` (ACK) |
| `data.ackEvt` | int | 被确认的原始指令 evt |
| `data.machineName` | string | 响应方的设备名称 |
| `data.timestamp` | string | 执行时间戳（ISO 8601，含时区偏移） |

> **ACK 路由**：接收端通过 UDP 报文的来源地址（`RemoteEndPoint`）自动确定回包目标，无需在 `data` 中显式携带 `requesterIP`。

**支持 ACK 的指令**：RESTART (1002)、SHUTDOWN (1001)、RESTART_NODE_TREE (1004)、STOP (1005)、BOOT (1003)、SET_VOLUME (1013)、MUTE (1014)、UNMUTE (1015)、TOGGLE_MUTE (1016)、VOLUME_UP (1017)、VOLUME_DOWN (1018)、ENTER_POWER_SAVING (1020)、EXIT_POWER_SAVING (1021)、MONITOR_OFF (1025)、MONITOR_ON (1026)、TAKE_SCREENSHOT (1030)、DISABLE_DESKTOP (1031)、ENABLE_DESKTOP (1032)、TOGGLE_TOUCH (1033)

#### 指令类型一览

| evt | 常量名 | 方向 | data 载荷 | 说明 |
|-----|--------|------|-----------|------|
| **1001** | `SHUTDOWN` | 发送方 → 目标 | *无* | 关闭目标系统 |
| **1002** | `RESTART` | 发送方 → 目标 | *无* | 重启目标系统 |
| **1003** | `BOOT` | 发送方 → 目标 | *无* | 远程开机（WoL） |
| **1004** | `RESTART_NODE_TREE` | 发送方 → 目标 | *无* | 重启目标的进程树 |
| **1005** | `STOP` | 发送方 → 目标 | *无* | 停止/终止目标的进程树 |
| **1006** | `EXPORT_PACKAGE` | 请求方 → 远端 | `{"taskId":"c3a1b2d4-5e6f-...","requesterIP":"192.168.1.50"}` | 请求远端导出进程包到共享目录（`requesterIP` 用于远端异步回调） |
| **1007** | `EXPORT_PACKAGE_COMPLETED` | 远端 → 请求方 | `{"remoteIP":"192.168.1.100","success":true,"error":"","machineName":"DESKTOP-TARGET","packageFileName":"Demo_20260227_143022.dkp.zip","taskId":"c3a1b2d4-5e6f-..."}` | 导出完成通知 |
| **1008** | `EXPORT_PACKAGE_PROGRESS` | 远端 → 请求方 | `{"remoteIP":"192.168.1.100","message":"正在打包程序文件..."}` | 导出进度通知 |
| **1009** | `PUSH_PACKAGE_TO_REQUESTER` | 请求方 → 远端 | `{"fileName":"Demo_20260227_143022.dkp.zip","requesterIP":"192.168.1.50","requesterPort":7009}` | 请求远端通过 P2P 推送指定文件（`requesterIP`/`requesterPort` 用于远端主动建立 TCP 连接） |
| ~~1010~~ | ~~`LIST_SHARED_FILES`~~ | — | — | **已废弃**：迁移至 TCP 通道（见 5.4 节） |
| ~~1011~~ | ~~`LIST_SHARED_FILES_RESPONSE`~~ | — | — | **已废弃**：迁移至 TCP 通道（见 5.4 节） |
| **1012** | `PUSH_DOWNLOAD_FILES` | 请求方 → 远端 | `{"fileNames":["App_v1.2.dkp.zip","Patch_v1.3.dkp-patch.zip"],"requesterIP":"192.168.1.50"}` | 请求远端推送指定文件供下载（`requesterIP` 用于远端主动建立 TCP 连接） |
| **1013** | `SET_VOLUME` | 发送方 → 目标 | `{"volume":75}` | 设置系统音量（0–100） |
| **1014** | `MUTE` | 发送方 → 目标 | *无* | 静音 |
| **1015** | `UNMUTE` | 发送方 → 目标 | *无* | 取消静音 |
| **1016** | `TOGGLE_MUTE` | 发送方 → 目标 | *无* | 切换静音状态 |
| **1017** | `VOLUME_UP` | 发送方 → 目标 | *无* | 系统步进增量（OS 定义步长） |
| **1018** | `VOLUME_DOWN` | 发送方 → 目标 | *无* | 系统步进减量（OS 定义步长） |
| **1020** | `ENTER_POWER_SAVING` | 发送方 → 目标 | *无* | 开启节能模式 |
| **1021** | `EXIT_POWER_SAVING` | 发送方 → 目标 | *无* | 退出节能模式 |
| **1025** | `MONITOR_OFF` | 发送方 → 目标 | *无* | 关闭显示器背光 |
| **1026** | `MONITOR_ON` | 发送方 → 目标 | *无* | 唤醒显示器 |
| **1030** | `TAKE_SCREENSHOT` | 发送方 → 目标 | *无* | 远程触发截图 |
| **1031** | `DISABLE_DESKTOP` | 发送方 → 目标 | *无* | 关闭桌面进程（explorer.exe） |
| **1032** | `ENABLE_DESKTOP` | 发送方 → 目标 | *无* | 启用桌面进程（explorer.exe） |
| **1033** | `TOGGLE_TOUCH` | 发送方 → 目标 | *无* | 切换触摸屏启用/禁用 |
| **1221** | `HEARTBEAT` | 子进程 → DaemonKit | `{"process":"C:\\\\Apps\\\\MyApp\\\\MyApp.exe"}` | 进程心跳信号（统一于 ControlPort） |
| **1300** | `ACK` | 接收端 → 发送方 | `{"ackEvt":1002,"machineName":"DESKTOP-TARGET","timestamp":"2026-02-27T10:30:00.000+08:00"}` | 指令执行确认 |

#### 完整数据包示例

以下展示各类典型指令的完整 UDP 数据包（`token` 字段仅在启用认证时携带）。

##### 1. 系统控制（以重启为例）

```json
// 发送方 → 目标（192.168.1.100:7008）
{
  "evt": 1002
}

// 目标 → 发送方（ACK 回包，192.168.1.50:7008）
{
  "evt": 1300,
  "data": {
    "ackEvt": 1002,
    "machineName": "DESKTOP-TARGET",
    "timestamp": "2026-02-27T10:30:00.000+08:00"
  }
}
```

##### 2. 音量控制

```json
// 设置音量至 60%
{ "evt": 1013, "data": { "volume": 60 } }

// 静音
{ "evt": 1014 }

// 取消静音
{ "evt": 1015 }

// 切换静音
{ "evt": 1016 }

// 步进增量
{ "evt": 1017 }
```

##### 3. 远程导出包（三步流程）

```json
// ① 请求方 → 远端：发起导出
{
  "evt": 1006,
  "data": {
    "taskId": "c3a1b2d4-5e6f-7890-abcd-ef1234567890",
    "requesterIP": "192.168.1.50"
  }
}

// ② 远端 → 请求方：进度推送（多次）
{
  "evt": 1008,
  "data": {
    "remoteIP": "192.168.1.100",
    "message": "正在打包程序文件 MyApp/..."
  }
}

// ③ 远端 → 请求方：导出完成
{
  "evt": 1007,
  "data": {
    "remoteIP": "192.168.1.100",
    "success": true,
    "error": "",
    "machineName": "DESKTOP-TARGET",
    "packageFileName": "Demo_20260227_143022.dkp.zip",
    "taskId": "c3a1b2d4-5e6f-7890-abcd-ef1234567890"
  }
}
```

##### 4. P2P 文件推送请求

```json
// 请求远端推送单个包文件（PUSH_PACKAGE_TO_REQUESTER）
{
  "evt": 1009,
  "data": {
    "fileName": "Demo_20260227_143022.dkp.zip",
    "requesterIP": "192.168.1.50",
    "requesterPort": 7009
  }
}

// 请求远端推送多个文件（PUSH_DOWNLOAD_FILES，文件浏览下载）
{
  "evt": 1012,
  "data": {
    "fileNames": ["App_v1.2.dkp.zip", "Patch_v1.3.dkp-patch.zip"],
    "requesterIP": "192.168.1.50"
  }
}
```

##### 5. 进程心跳

```json
// 受管进程 → DaemonKit（每秒发送，不含 token）
{
  "evt": 1221,
  "data": {
    "process": "C:\\Apps\\MyApp\\MyApp.exe"
  }
}
```

##### 6. 带认证令牌的示例

```json
{
  "evt": 1001,
  "token": "my-shared-secret-2026"
}
```

---

### 4. 进程心跳（统一于 ControlPort）

受管进程向 DaemonKit 报告存活状态。

| 属性 | 值 |
|------|-----|
| 端口 | `ControlPort`（默认 7008，与控制指令共用） |
| 传输 | UDP 单播 |
| 方向 | 子进程 → DaemonKit |
| 认证 | **豁免**（心跳不校验 token） |

> **v2 变更**：心跳端口从独立的 7777 合并至 ControlPort 7008，减少端口占用。`NetworkBroadcastService` 内部通过 `evt == HEARTBEAT` 区分心跳与控制指令。

#### 心跳消息格式

复用 `Command` 结构，事件 ID 固定为 `1221`：

```json
{
  "evt": 1221,
  "data": {
    "process": "C:\\path\\to\\monitored\\process.exe"
  }
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `evt` | int | 固定为 `1221` (HEARTBEAT) |
| `data.process` | string | 受管进程的可执行文件路径 |

**处理逻辑**：DaemonKit 接收到心跳后，按 `process` 路径匹配 `ProcessItem` 节点，更新其存活状态。

---

### 5. P2P 文件传输（TCP 7009）

基于 NetMQ (ZeroMQ) 的可靠文件传输协议，支持断点续传。

| 属性 | 值 |
|------|-----|
| 端口 | `FileTransferPort`（默认 7009，可配置） |
| 传输 | TCP |
| ZMQ 模式 | ROUTER（服务端）/ DEALER（客户端） |
| 块大小 | **256 KB** |
| 并发控制 | 可配置信号量（1–16，默认 4） |

#### 5.1 消息帧格式

NetMQ 多帧消息，基本结构：

```
帧 0: [Identity]        ← ROUTER 自动附加的发送方标识
帧 1: [MessageType]     ← UTF-8 字符串，消息类型标识
帧 2: [Payload]         ← JSON 或 二进制数据
```

#### 5.2 传输流程

```
发送方 (DEALER)                         接收方 (ROUTER)
     │                                      │
     │──── METADATA ────────────────────────→│  1. 发送文件元数据
     │                                      │
     │←─── RESUME_RESPONSE ─────────────────│  2. 确认接受/续传偏移
     │                                      │
     │──── DATA_CHUNK [0] ──────────────────→│  3. 分块传输文件数据
     │──── DATA_CHUNK [1] ──────────────────→│     ...
     │──── DATA_CHUNK [N] (IsLastChunk) ────→│     最后一块携带 FileHash
     │                                      │
     │←─── TRANSFER_COMPLETE ───────────────│  4. 传输完成 + MD5 校验结果
     │                                      │
```

#### 5.3 消息类型详解

##### METADATA — 传输发起（发送方 → 接收方）

```json
{
  "TaskId": "guid",
  "FileName": "package.dkp.zip",
  "TotalBytes": 104857600,
  "ResumeOffset": 0,
  "FileHash": "",
  "SenderName": "MACHINE-01",
  "SenderIP": "192.168.1.100",
  "SourceHint": "ManualSend",
  "MessageType": "METADATA"
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `TaskId` | string | 传输任务唯一 ID (GUID) |
| `FileName` | string | 文件名 |
| `TotalBytes` | long | 文件总大小（字节） |
| `ResumeOffset` | long | 请求从此偏移量续传 |
| `FileHash` | string | 文件 MD5 哈希（可为空） |
| `SenderName` | string | 发送方设备名称 |
| `SenderIP` | string | 发送方 IP 地址 |
| `SourceHint` | string | 任务来源类型（`ManualSend` / `PackageDownload` 等） |

##### RESUME_RESPONSE — 续传响应（接收方 → 发送方）

```json
{
  "TaskId": "guid",
  "ActualOffset": 0,
  "Accepted": true,
  "Error": "",
  "MessageType": "RESUME_RESPONSE"
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `TaskId` | string | 任务 ID |
| `ActualOffset` | long | 接收端已有字节数（实际续传起点） |
| `Accepted` | bool | 是否接受此传输 |
| `Error` | string | 拒绝原因（Accepted=false 时） |

##### DATA_CHUNK — 数据块（发送方 → 接收方）

帧 2 的载荷为 **JSON 头 + 换行符 + 二进制数据** 的混合格式：

```
{"TaskId":"guid","ChunkIndex":0,"IsLastChunk":false,"MessageType":"DATA_CHUNK"}\n<binary data>
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `TaskId` | string | 任务 ID |
| `ChunkIndex` | int | 块序号（从 0 开始） |
| `IsLastChunk` | bool | 是否为最后一块 |
| `FileHash` | string | 文件 MD5（仅最后一块携带，用于接收端校验） |

> **设计说明**：JSON 头与二进制数据合并在同一帧中，以 `\n` 分隔，接收端使用零分配解析器避免每块的对象分配开销。

##### TRANSFER_COMPLETE — 传输完成（接收方 → 发送方）

```json
{
  "TaskId": "guid",
  "ReceivedHash": "d41d8cd98f00b204e9800998ecf8427e",
  "HashMatch": true,
  "MessageType": "TRANSFER_COMPLETE"
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `TaskId` | string | 任务 ID |
| `ReceivedHash` | string | 接收端计算的 MD5 |
| `HashMatch` | bool | MD5 是否与发送端一致 |

##### TRANSFER_CANCEL — 取消传输（发送方 → 接收方）

```json
{
  "TaskId": "guid"
}
```

##### TRANSFER_CANCELLED — 取消通知（接收方 → 发送方）

帧 2 载荷为 TaskId 字符串。

#### 5.4 远程文件列表查询

##### LIST_FILES_REQUEST（客户端 → 服务端）

```json
{
  "MessageType": "LIST_FILES_REQUEST",
  "RequestId": "guid"
}
```

##### LIST_FILES_RESPONSE（服务端 → 客户端）

```json
{
  "MessageType": "LIST_FILES_RESPONSE",
  "RequestId": "guid",
  "Files": [
    {
      "FileName": "package.dkp.zip",
      "RelativePath": "package.dkp.zip",
      "FullPath": "",
      "FileSize": 104857600,
      "LastModified": "2026-02-24T10:00:00",
      "FileMD5": ""
    }
  ]
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `FileName` | string | 文件名 |
| `RelativePath` | string | 相对于共享目录的路径 |
| `FullPath` | string | 完整路径（响应中通常为空，仅服务端内部使用） |
| `FileSize` | long | 文件大小（字节） |
| `LastModified` | DateTime | 最后修改时间 |
| `FileMD5` | string | MD5 哈希（可为空） |

#### 5.5 远程文件下载

##### DOWNLOAD_FILE_REQUEST（客户端 → 服务端）

```json
{
  "MessageType": "DOWNLOAD_FILE_REQUEST",
  "RequestId": "guid",
  "FileNames": ["file1.dkp.zip", "file2.dkp.zip"],
  "RequesterIP": "192.168.1.50",
  "RequesterPort": 7009
}
```

**处理逻辑**：服务端收到后，反向建立 DEALER 连接到请求方的 ROUTER（`tcp://{RequesterIP}:{RequesterPort}`），主动推送文件——即**推送模式**，避免请求方的 TCP 入站防火墙问题。

#### 5.6 设备信息查询

用于跨网段设备探测时获取完整的 `MachineInfo`（包含设备名称、硬件信息等）。

##### MACHINE_INFO_REQUEST（客户端 → 服务端）

无载荷（空帧），仅消息类型标识即可。

##### MACHINE_INFO_RESPONSE（服务端 → 客户端）

```json
{
  "ID": "",
  "Name": "设备名称",
  "IPs": ["192.168.1.100"],
  "CPUs": ["Intel Core i9-13900K"],
  "GPUs": ["NVIDIA GeForce RTX 4090"],
  "Memories": ["Samsung 32GB DDR5"]
}
```

**使用场景**：`DeviceDiscoveryService` 对手动配置的跨网段 IP 进行探测时，优先通过 P2P TCP 通道发送此请求，获取完整设备信息（而非仅显示“Device-x.x.x.x”）。若 P2P 通道不可用，回退到纯 TCP 握手探测。

---

### 6. TCP 设备探测（复用 FileTransferPort）

跨路由器设备发现（手动配置 IP 场景），复用 P2P 文件传输端口。

| 属性 | 值 |
|------|-----|
| 端口 | `FileTransferPort`（默认 7009，可配置） |
| 超时 | **3 秒** |
| 方式 | 优先 P2P `MACHINE_INFO_REQUEST` 交换完整设备信息，回退到纯 TCP 握手探测 |
| 周期 | 每 **15 秒** 自动重探测所有手动设备 |

> **v2 变更**：探测时通过 P2P TCP 通道请求远端 `MachineInfo`，解决跨网段设备显示“未知设备”的问题；增加周期性重探测，检测手动设备的上/下线变化。

**探测流程**：
1. 尝试发送 `MACHINE_INFO_REQUEST` → 收到 `MACHINE_INFO_RESPONSE` → 获取完整设备名称、硬件信息
2. 若步骤 1 失败，回退到 TCP `connect()` 握手 → 连接成功则设备在线（但仅显示 IP）
3. 超时/拒绝 → 设备离线

---

### 7. 通讯架构总览

```
DaemonKit 实例 A                                    DaemonKit 实例 B
─────────────────                                    ─────────────────

 ┌─────────────┐    MachineInfo JSON (每3秒)     ┌─────────────┐
 │ UDP 广播发送  │ ────────── 子网广播 ──────────→ │ UDP 7007 监听 │
 │              │                                │              │
 └─────────────┘                                 └─────────────┘

 ┌─────────────┐    Command JSON (单播)          ┌─────────────┐
 │ UDP 指令发送  │ ←──────── 点对点 ────────────→ │ UDP 7008 监听 │
 │  + 心跳接收   │    evt: 1001-1012, 1221, 1300  │  + 心跳接收   │
 │  + ACK 收发   │    token 认证 (可选)            │  + ACK 收发   │
 └─────────────┘                                 └─────────────┘

 ┌─────────────┐    NetMQ ROUTER/DEALER          ┌─────────────┐
 │ TCP 7009     │ ←──────── 文件传输 ────────────→│ TCP 7009     │
 │ ROUTER 服务端 │    METADATA → DATA_CHUNK(×N)   │ DEALER 客户端 │
 │ + 文件列表查询 │    → TRANSFER_COMPLETE          │              │
 │ + 设备探测    │    LIST_FILES_REQUEST/RESPONSE  │              │
 └─────────────┘                                 └─────────────┘
```

**关键设计决策**：

1. **推送模式**：文件下载采用"请求方发 UDP 指令让远端主动推送"模式，而非请求方直接拉取，规避请求方 TCP 入站防火墙限制
2. **断点续传**：METADATA → RESUME_RESPONSE 握手支持中断恢复，服务端按已有文件大小报告实际偏移
3. **TCP 唯一文件列表**：文件列表查询统一走 TCP 通道（NetMQ `LIST_FILES_REQUEST/RESPONSE`），原 UDP 通道（evt 1010/1011）已废弃，避免 UDP 大报文截断风险
4. **零拷贝优化**：DATA_CHUNK 将 JSON 头与二进制数据合并为单帧、以 `\n` 分隔，避免逐块对象分配开销
5. **统一端口**：心跳合并至 ControlPort（原 7777 → 7008），减少端口占用至 3 个
6. **可选认证**：共享令牌认证默认关闭，启用后自动附加 token 到所有非心跳指令
7. **ACK 确认**：关键控制指令（开关机、进程树操作）执行后回送 ACK，发送方可确认指令已被执行
