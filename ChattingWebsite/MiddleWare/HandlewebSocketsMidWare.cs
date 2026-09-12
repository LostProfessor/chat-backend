using System.Text;

namespace ChattingWebsite.Services;

/// <summary>
/// WebSocket 连接处理器（单例），管理连接生命周期和消息收发。
/// 不再依赖 System.Net.WebSockets，全部用自己的 MyWebSocketServer。
/// </summary>
public class HandlewebSocketsMidWare
{
    private readonly ConnectionManager _connectionManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HandlewebSocketsMidWare> _logger;

    public HandlewebSocketsMidWare(
        ConnectionManager connectionManager,
        IServiceScopeFactory scopeFactory,
        ILogger<HandlewebSocketsMidWare> logger)
    {
        _connectionManager = connectionManager;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 处理一个已完成握手的连接。
    /// 注册到 ConnectionManager，绑定消息事件，启动接收循环。
    /// </summary>
    public async Task HandleConnection(MyWebSocketServer socket, string userId, string nickname)
    {
        _connectionManager.AddConnection(userId, nickname, socket);
        _logger.LogInformation("用户 {UserId}({Nickname}) 已连接", userId, nickname);

        // ── 断开清理：必须「恰好一次」 ──
        // OnClose 事件（正常关闭 / 协议违规 / 对端掉线）与下面的 finally 都会走到这里。
        // 旧版两边各写一份移除+日志，于是客户端发一个正常的 Close 帧就会刷出两行
        // 「已断开」（而暴力掉线只有一行）—— 日志行数随断开方式变化，排查时极易误判
        // 成「同时关掉了两个连接」。用 Interlocked 保证只放行第一个。
        int cleanedUp = 0;
        void CleanupOnce()
        {
            if (Interlocked.Exchange(ref cleanedUp, 1) != 0) return;
            _connectionManager.RemoveConnection(userId, socket);
            _logger.LogInformation("用户 {UserId}({Nickname}) 已断开", userId, nickname);
        }

        // ── 绑定事件 ──
        socket.OnTextMessage += async (ws, json) =>
        {
            // 每收到一条完整文本消息，创建独立 DI Scope 处理。
            // 注意：必须在 await Dispatch 完成后再释放 scope，
            // 否则 DbContext 会被提前销毁导致 "disposed context" 异常。
            using (var scope = _scopeFactory.CreateScope())
            {
                var dispatcher = scope.ServiceProvider
                    .GetRequiredService<MessageDispatcher>();
                await dispatcher.Dispatch(userId, json, ws);
            }
        };

        // ── 二进制帧：文件传输（P0：图片分块上传） ──
        socket.OnBinaryMessage += async (ws, data) =>
        {
            // 每收到一条二进制帧（一个文件块/控制帧）创建独立 Scope 处理，
            // 与文本消息同理：await 完成后再释放 scope，保证 DbContext 安全。
            using (var scope = _scopeFactory.CreateScope())
            {
                var handler = scope.ServiceProvider
                    .GetRequiredService<FileTransferHandler>();
                await handler.HandleAsync(userId, data, ws);
            }
        };

        socket.OnClose += (ws) => CleanupOnce();

        socket.OnError += (ws, err) =>
        {
            _logger.LogWarning("用户 {UserId} WebSocket 错误: {Error}", userId, err);
        };

        try
        {
            // ── 启动接收循环（会阻塞直到客户端断开） ──
            await socket.ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket 接收循环异常，用户 {UserId}", userId);
        }
        finally
        {
            // ReceiveLoopAsync 已保证 OnClose 恰好触发一次，这里是兜底
            // （例如连接还没进入接收循环就抛异常的情况）
            CleanupOnce();
        }
    }

    /// <summary>
    /// 向指定用户的所有连接发送消息
    /// </summary>
    public async Task SendToUser(string targetUserId, string messageJson)
    {
        var sockets = _connectionManager.GetConnections(targetUserId);
        foreach (var socket in sockets)
        {
            if (socket.IsOpen)
            {
                try
                {
                    await socket.SendTextAsync(messageJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "向用户 {TargetUserId} 发送消息失败", targetUserId);
                }
            }
        }
    }

    /// <summary>
    /// 广播消息给所有在线用户
    /// </summary>
    public async Task BroadcastToAll(string messageJson)
    {
        var allSockets = _connectionManager.GetAllConnections();
        var tasks = allSockets
            .Where(s => s.IsOpen)
            .Select(s => s.SendTextAsync(messageJson));
        await Task.WhenAll(tasks);
    }
}