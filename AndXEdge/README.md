# AndXEdge 边缘网关

> 展项扫码平台（AndX）的本机信任边界组件。把「鉴权、后端地址、网络复杂性」收进本机守护进程，
> 让 Unity/UE 插件侧参数保持最小：插件只访问 `http://127.0.0.1:6699`，其余交给 AndXEdge。

## 是什么

AndXEdge 是一个带诊断界面的 Windows 托盘/窗口程序，进程内托管 **Kestrel + YARP**：

- **仅监听本机环回**（默认 `127.0.0.1:6699`），不接受公网访问；
- 把 `/api/edge/*` 与 `/api/resources/*` **流式转发**到上游后端（不缓冲请求/响应体，支撑大文件分片透传）；
- 转发前**注入 / 覆盖 `x-edge-key`**（边缘共享密钥），插件无需持有密钥、无需处理鉴权；
- 可选补充 `exhibitId` 查询参数（服务端 `ANDX_EDGE_EXHIBIT_ID` 亦可作为单展项默认）；
- 提供 `/healthz` 健康检查、带请求 ID 的请求日志、脱敏配置查看与一键链路自检。

## 工程结构

| 项目 | 目标框架 | 说明 |
| --- | --- | --- |
| `AndXEdge.Core` | `net8.0` | 纯逻辑：配置、DPAPI 密钥保护、YARP 反代宿主、请求日志、自检。可 `dotnet test`。 |
| `AndXEdge` | `net8.0-windows` | WPF 诊断界面（原生 MVVM，无第三方 MVVM 框架），托管 Core 生命周期。 |
| `AndXEdge.Tests` | `net8.0` | Core 单测 + 真实 Kestrel 假上游的代理集成测试。 |

## 快速开始

```powershell
# 1) 配置上游后端地址（本机需可达该地址）
#    与边缘密钥（需与后端 ANDX_EDGE_KEY 一致）
#    方式一：界面「设置」页填写后保存
#    方式二：环境变量（优先级最高，适合无人值守部署）
$env:ANDX_EDGE_UPSTREAM = 'http://10.0.0.8:3000'
$env:ANDX_EDGE_KEY      = '<edge-key>'

# 2) 运行
.\AndXEdge.exe
```

启动后插件即可把 API 基址指向 `http://127.0.0.1:6699`。

## 配置

持久化于 `~/.andx-edge.json`（可用环境变量 `ANDX_EDGE_CONFIG` 改路径），密钥以 **Windows DPAPI**
（CurrentUser）加密，格式 `enc:v1:<base64>`，**绝不落明文**。

| 字段 | 环境变量 | 默认 | 说明 |
| --- | --- | --- | --- |
| `upstreamBaseUrl` | `ANDX_EDGE_UPSTREAM` | 空 | 上游后端 API 基址，如 `http://10.0.0.8:3000` |
| `listenAddress` | `ANDX_EDGE_LISTEN_ADDRESS` | `127.0.0.1` | 监听地址（**勿改为 0.0.0.0**，见安全注意） |
| `listenPort` | `ANDX_EDGE_LISTEN_PORT` | `6699` | 监听端口 |
| `exhibitId` | `ANDX_EDGE_EXHIBIT_ID` | 空 | 可选展项 ID，转发 `/api/edge/*` 时补充 |
| `edgeKeyEncrypted` | `ANDX_EDGE_KEY` | 空 | 边缘密钥（`x-edge-key`），环境变量优先 |

环境变量优先级高于配置文件。

## 部署

编译产物为自包含单文件 `AndXEdge.exe`（含 .NET + ASP.NET Core 运行时，无需目标机预装）：

```powershell
dotnet publish AndXEdge/AndXEdge.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true
```

无人值守安装密钥（密钥只经 stdin，不落命令行）：

```powershell
echo "<edge-key>" | .\AndXEdge.exe --install-edge-key
```

排障：

```powershell
.\AndXEdge.exe --print-config   # 打印脱敏配置与就绪状态
```

## 诊断

- **概览**：监听地址、上游、密钥状态、展项。
- **请求日志**：时间 / 方法 / 路径 / 状态 / 耗时 / 请求 ID（最近 500 条，同时写入程序目录 `Logs/`）。
- **链路自检**：经本机 Edge 调用受保护的真实路由，区分「Edge 未运行 / 上游不可达 / 密钥无效 / 链路正常」。
- **`GET /healthz`**：返回 `{ status, service, version, upstream, exhibitId }`。

## 代理行为

| 客户端请求 | Edge 转发 | 说明 |
| --- | --- | --- |
| `GET /healthz` | 本地响应 | 不转发 |
| `/api/edge/*` | 上游同路径 | 注入 `x-edge-key`，可选补 `exhibitId`，流式透传 |
| `/api/resources/*` | 上游同路径 | 公开二维码出图，供无外网场景经 Edge 访问 |

- 客户端携带的 `x-edge-key` 会被**移除并覆盖**；
- 请求缺失 `x-request-id` 时由 Edge 生成并转发，用于日志串联；
- Kestrel 不限制请求体大小（本机信任边界），请求体不缓冲。

## 开发

```powershell
dotnet build AndXEdge/AndXEdge.csproj -c Debug
dotnet test  AndXEdge.Tests/AndXEdge.Tests.csproj -c Debug
```

## 安全注意

- 默认仅环回监听，信任边界为**本机**：本机任意进程可经 Edge 使用边缘密钥上传资源（这是产品取向）。
- **不要**把 `listenAddress` 改为 `0.0.0.0`，除非明确需要局域网共享边缘网关且信任该网段。
- 密钥仅以 DPAPI 密文落盘、不入日志；请求日志只记录方法/路径/状态/耗时/请求 ID。
