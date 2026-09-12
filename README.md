# 聊天室 · 后端

**.NET 8 + EF Core** 实现的实时聊天后端。HTTP 接口走 ASP.NET Core，
实时通信用一个**手写的 WebSocket 服务器**（基于 `TcpListener` 自行实现握手与帧层，
未使用 `System.Net.WebSockets`），以理解协议细节。

配套前端：[chat-frontend](https://github.com/LostProfessor/chat-frontend)。

## 功能

- 用户注册 / 登录，JWT（access + refresh token）认证与自动续期
- 群聊、私聊、好友申请与好友列表
- 群组管理：创建、加入申请、成员与角色、禁言、群公告、改名权限
- 消息撤回（同时删除已落盘的媒体文件）
- **通用文件传输**：图片 / 视频 / 音频 / 任意文档，统一走自定义二进制分块协议，
  支持进度上报、取消、断点续传
- 图片自动生成 400px 缩略图
- 管理后台接口（用户 / 群组 / 全服公告 / 重置密码）

## 技术栈

| | |
|---|---|
| 运行时 | .NET 8（ASP.NET Core） |
| ORM | EF Core 8 + SQL Server（开发用 LocalDB） |
| 认证 | JWT Bearer（`Microsoft.AspNetCore.Authentication.JwtBearer`） |
| 实时通信 | 自定义 WebSocket 服务器（`System.Net.Sockets`） |
| 密码哈希 | BCrypt.Net-Next |
| 图片处理 | SixLabors.ImageSharp |
| 接口文档 | Swagger / Swashbuckle |

## ⚠️ 首次运行前必须配置密钥

**本仓库不包含任何密钥。** `appsettings.json` 里 `Jwt:Key` 与
`AdminSettings:AdminPassword` 都是空值，需要通过
[user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
注入到本机（密钥库位于 `%APPDATA%\Microsoft\UserSecrets\` 或
`~/.microsoft/usersecrets/`，在仓库之外，不会被提交）。

```bash
cd ChattingWebsite

dotnet user-secrets init          # 已在 csproj 里初始化过，重复执行无害

# JWT 签名密钥，至少 32 字符（HS256 要求）
# PowerShell 生成随机串：
#   -join ((1..64) | % { [char](Get-Random -Minimum 48 -Maximum 122) })
dotnet user-secrets set "Jwt:Key" "<64 位随机字符串>"

# 首次启动用于创建管理员账号。不配置则跳过创建（不会回退到默认密码）
dotnet user-secrets set "AdminSettings:AdminPassword" "<你的密码>"
```

没有配置 `Jwt:Key` 时程序会**直接启动失败**并打印上述步骤，而不是等到
签发令牌时才抛出难以理解的异常。

> 注意：管理员种子用的是「库里已存在 `IsAdmin` 用户就跳过」的策略，
> 所以修改 `AdminSettings:AdminPassword` **只对全新数据库生效**；
> 已有数据库需要自行更新密码或删掉管理员记录。

## 运行

```bash
dotnet restore
dotnet run --project ChattingWebsite
```

首次启动会自动执行 EF Core 迁移并写入种子数据（默认群组「全服大厅」+ 管理员账号）。

- HTTP API：<http://localhost:5258>
- Swagger：<http://localhost:5258/swagger>（仅 Development 环境）
- WebSocket：`ws://localhost:5259/ws?token=<JWT>`

## 端口（重要）

| 用途 | 默认端口 | 配置位置 |
|---|---|---|
| HTTP API / Kestrel | **5258** | `ChattingWebsite/Properties/launchSettings.json` |
| 自定义 WebSocket | **5259** | `ChattingWebsite/appsettings.json` → `WebSocket:Port` |

### 这两个端口绝不能相同

Windows 的 socket 默认允许地址复用，端口撞车时**两个监听器都会"绑定成功"且不报错**，
但连接归谁是不确定的 —— WebSocket 握手可能被 HTTP 服务器接走，
表现为前端反复掉线、收不到任何广播，而日志里没有任何错误。
程序启动时会做自检并打印 `LogCritical`。

### 只能单实例运行

`ConnectionManager`（WebSocket 连接表）和 `FileTransferSessionManager`
（分块上传会话）都是**进程内内存**。两个实例同时跑时，HTTP 请求可能落在实例 A
而 WebSocket 连接挂在实例 B，A 广播时查不到连接 → **广播静默丢失**
（新消息、撤回通知、上传完成通知全部收不到，且不报错）。

因此 `WebSocketServer` 在 Windows 上设置了 `ExclusiveAddressUse = true`，
第二个实例会**在启动时直接失败退出（退出码 1）**，而不是静默共存。

如需水平扩展，必须把连接表与会话迁移到 Redis 之类的共享存储，并引入发布订阅机制。

## 在 Linux / macOS 上运行

代码本身是跨平台的，唯一的阻塞项是数据库：

1. 默认连接串用的是 SQL Server **LocalDB**：
   `Data Source=(localdb)\ProjectModels;...;Integrated Security=True`
   —— `LocalDB` 和 Windows 集成认证都只存在于 Windows。

2. 换成任意可用的 SQL Server 实例即可，例如用 Docker：

   ```bash
   docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<强密码>" \
     -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest
   ```

   然后把连接串通过 user-secrets 注入（因为它带密码，不应写进仓库）：

   ```bash
   dotnet user-secrets set "ConnectionStrings:chatconn" \
     "Server=localhost,1433;Database=ChattingWebsiteDB;User Id=sa;Password=<强密码>;Encrypt=False;TrustServerCertificate=True"
   ```

3. 也可以改用 SQLite / PostgreSQL，但需要修改 `ChattingWebsite/Program.cs`
   中的 `UseSqlServer(...)` 并重新生成迁移。

其余部分（ASP.NET Core、EF Core、手写 WebSocket 帧层、ImageSharp）都是跨平台的。
`WebSocketServer` 里 `listener.ExclusiveAddressUse = true` 包在
`if (OperatingSystem.IsWindows())` 判断中 —— 该选项只在 Windows 上有意义。

## 文件传输协议

二进制帧格式：

```
Magic(0xAB, 1B) | Op(1B) | SessionId(int32 BE) | Seq(int32 BE) | Payload(...)
```

| Op | 含义 |
|---|---|
| 1 `Start` | 请求开始，服务端分配 SessionId 并回 `Ack` |
| 2 `Chunk` | 数据分块（256 KB / 块） |
| 3 `Ack` | 累积确认，驱动发送窗口滑动 |
| 4 `Done` | 传输结束，触发落盘、入库、广播 |
| 5 `Cancel` | 主动取消 |
| 6 `Error` | 错误上报 |
| 7 `Resume` | 断线重连后从已确认位置续传 |

媒体类型规则集中在 `FileTransferHandler.MediaRules`（扩展名白名单 + 体积上限 +
存储子目录），后处理通过 `IMediaProcessor` 抽象按 `MediaType` 分派，
**新增一种文件类型只需加一条规则 + 实现一个处理器 + 在 DI 里注册**。

## 目录结构

```
ChattingWebsite/
├── Program.cs              # 启动、DI 注册、端口自检、JWT 密钥校验
├── Controllers/            # HTTP 接口
├── Model/  DTOs/           # 实体与数据传输对象
├── DB/                     # DbContext + 种子数据
├── Migrations/             # EF Core 迁移
├── Services/
│   ├── WebSocketServer.cs      # TCP 监听
│   ├── WebSocketHandshake.cs   # HTTP 升级握手 + JWT 校验
│   ├── MyWebSocketServer.cs    # 帧编解码（Text / Binary / Ping-Pong / Close）
│   ├── FileTransferCodec.cs    # 文件传输帧编解码
│   ├── FileTransferHandler.cs  # 分块组装、落盘、入库、广播
│   └── ImageProcessor.cs       # 缩略图生成
├── MiddleWare/             # WebSocket 生命周期中间件、JWT 中间件
└── Manager/                # 连接管理、消息分发
```

## 第三方许可证

`SixLabors.ImageSharp` 采用 **Six Labors Split License**：开源与个人用途免费，
**商业用途需购买授权**。本项目为学习用途，符合免费条件。

编译时会看到一条 `warning : No Six Labors license found` —— 这是正常的，
它只是提示当前没有商业授权文件。如需完全避开该许可证，可替换为
`SkiaSharp` 或 `Magick.NET`。

其余依赖：`BCrypt.Net-Next`（MIT）、`Swashbuckle.AspNetCore`（MIT）、
`Portable.BouncyCastle`（MIT）。

## 许可

学习项目。
