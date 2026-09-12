using System.Net;
using System.Net.Sockets;

namespace ChattingWebsite.Services;

/// <summary>
/// 自定义 WebSocket 服务器（单例）。
/// 
/// 职责：用 TcpListener 托管 WebSocket 连接，不依赖 System.Net.WebSockets。
///   - 监听指定端口，接受客户端连接
///   - 每个连接：握手 → 帧收发（交给 HandlewebSocketsMidWare）
/// 
/// 与 Program.cs 解耦：端口从配置读取，启动错误有日志。
/// </summary>
public class WebSocketServer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebSocketServer> _logger;

    public WebSocketServer(IServiceScopeFactory scopeFactory, ILogger<WebSocketServer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 启动 TCP 监听并持续接受连接。
    /// 这是一个阻塞方法，应放在后台 Task 中运行。
    /// </summary>
    public async Task StartAsync(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);

        // ★ 独占地址：禁止端口复用。
        // Windows 的 socket 默认允许地址复用，两个进程能同时“绑定成功”同一个端口而不报错，
        // 结果 HTTP 请求和 WebSocket 连接会被随机分给不同实例 ——
        // 而 ConnectionManager / FileTransferSessionManager 都是进程内内存，
        // 广播会静默丢失（撤回、新消息都收不到），极难排查。
        // 改成独占后，第二个实例会直接招 SocketException 启动失败，一眼可见。
        if (OperatingSystem.IsWindows())
            listener.ExclusiveAddressUse = true;

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            // 端口被占用：致命错误，绝不能静默继续，
            // 否则会变成“HTTP 正常但 WebSocket 永远连不上”这种最难查的状态
            _logger.LogCritical(ex,
                "[自定义WS] 致命错误：端口 {Port} 无法监听（可能已被另一个后端实例占用）。" +
                "请确认没有第二个后端进程在运行，或修改 appsettings.json 的 WebSocket:Port。", port);
            throw;
        }

        _logger.LogInformation("[自定义WS] TcpListener 已启动，端口 {Port}", port);

        try
        {
            while (true)
            {
                // 等待客户端连接
                TcpClient tcpClient = await listener.AcceptTcpClientAsync();

                // 每个连接独立处理，不阻塞下一个连接
                _ = Task.Run(() => HandleClientSafe(tcpClient));
            }
        }
        catch (Exception ex)
        {
            // 监听器本身崩了（如端口被占用），这里要记日志
            _logger.LogError(ex, "[自定义WS] 监听异常，端口 {Port}", port);
            throw;
        }
    }

    /// <summary>
    /// 单个客户端处理的安全包装：
    /// 捕获所有异常并记日志，避免一个坏连接拖垮整个服务器。
    /// </summary>
    private async Task HandleClientSafe(TcpClient tcpClient)
    {
        try
        {
            await HandleClient(tcpClient);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[自定义WS] 客户端连接处理异常");
        }
        finally
        {
            tcpClient?.Dispose();
        }
    }

    /// <summary>
    /// 处理单个客户端连接：握手 → 帧收发。
    /// </summary>
    private async Task HandleClient(TcpClient tcpClient)
    {
        using (tcpClient)
        {
            // 每次连接创建一个 DI Scope，拿到 Scoped/Singleton 服务
            using (var scope = _scopeFactory.CreateScope())
            {
                var jwtValidator = scope.ServiceProvider.GetRequiredService<JwtTokenValidator>();

                // ── Step 1: 握手（协议细节在 WebSocketHandshake 里） ──
                var identity = await WebSocketHandshake.AcceptAsync(tcpClient, jwtValidator);
                if (identity == null)
                    return; // 握手失败，连接已关闭

                var (userId, nickname) = identity.Value;
                _logger.LogInformation("[自定义WS] 握手成功: {Nickname} ({UserId})", nickname, userId);

                // ── Step 2: 创建帧收发器，进入业务处理 ──
                var wsServer = new MyWebSocketServer(tcpClient.GetStream());
                var wsHandler = scope.ServiceProvider.GetRequiredService<HandlewebSocketsMidWare>();
                await wsHandler.HandleConnection(wsServer, userId, nickname);
            }
        }
    }
}
