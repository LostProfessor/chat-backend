using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace ChattingWebsite.Services;

/// <summary>
/// WebSocket 握手协议处理（纯函数，不依赖 DI）。
/// 
/// 职责：把一条 TCP 连接从 HTTP 升级为 WebSocket。
///   - 解析客户端发来的 HTTP Upgrade 请求
///   - 验证 JWT token
///   - 计算并返回 Sec-WebSocket-Accept
/// 
/// 只负责握手，不负责握手之后的帧收发（那是 MyWebSocketServer 的事）。
/// 拆出来是为了：纯逻辑、可单元测试、Program.cs 不再臃肿。
/// </summary>
public static class WebSocketHandshake
{
    /// <summary>
    /// 从客户端读取 HTTP 请求并完成 WebSocket 握手。
    /// 成功时返回用户身份；失败时返回 null（连接已关闭或已写入错误响应）。
    /// </summary>
    /// <param name="tcpClient">已建立 TCP 连接的客户端</param>
    /// <param name="jwtValidator">JWT 校验器</param>
    /// <returns>成功返回 (userId, nickname)，失败返回 null</returns>
    public static async Task<(string userId, string nickname)?> AcceptAsync(
        TcpClient tcpClient, JwtTokenValidator jwtValidator)
    {
        NetworkStream stream = tcpClient.GetStream();

        // ── Step 1: 读 HTTP 请求行 ──
        // StreamReader 只用来读"文本形式的 HTTP 头部"，
        // leaveOpen:true 保证读完不关闭底层 TCP 连接。
        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1, true))
        {
            // 请求行示例：GET /ws?token=eyJ... HTTP/1.1
            string requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine))
                return null;

            string[] parts = requestLine.Split(' ');
            if (parts.Length < 3)
                return null;

            // ── Step 2: 从 URL 提取 token ──
            // parts[1] = "/ws?token=xxx"，切出 query string 解析
            string token = ParseTokenFromPath(parts[1]);

            // ── Step 3: 读取 Sec-WebSocket-Key 头部 ──
            // ★ 必须一直读到"空行"为止，绝不能找到 Key 就 break！
            //   否则 Key 之后的其它头部（如 Sec-WebSocket-Extensions）会残留在 TCP 流里，
            //   接着被 ReceiveFrameAsync 当成 WebSocket 帧去解析 → 帧类型乱码、整条连接报废。
            //   能不能正常握手完全取决于客户端把 Key 放在第几行 —— 这是典型的"挑客户端"bug。
            string secWebSocketKey = null;
            string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;                       // 忽略非法头行
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                    secWebSocketKey = value;
            }

            // ── Step 4: 验证 token，提取用户身份 ──
            var principal = jwtValidator.Validate(token);
            if (principal == null || string.IsNullOrEmpty(secWebSocketKey))
            {
                await WriteHttpResponse(stream, "401 Unauthorized");
                return null;
            }

            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var nickname = principal.FindFirst(ClaimTypes.Name)?.Value;
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(nickname))
            {
                await WriteHttpResponse(stream, "400 Bad Request");
                return null;
            }

            // ── Step 5: 计算 Sec-WebSocket-Accept 并返回 101 ──
            string acceptKey = ComputeAcceptKey(secWebSocketKey);
            await WriteSwitchingProtocols(stream, acceptKey);

            return (userId, nickname);
        }
    }

    /// <summary>从请求路径中解析 token 查询参数</summary>
    private static string ParseTokenFromPath(string pathAndQuery)
    {
        int qIndex = pathAndQuery.IndexOf('?');
        if (qIndex < 0) return "";

        string query = pathAndQuery.Substring(qIndex + 1);
        foreach (string pair in query.Split('&'))
        {
            string[] kv = pair.Split(new[] { '=' }, 2);
            if (kv.Length == 2 && kv[0] == "token")
                return Uri.UnescapeDataString(kv[1]);
        }
        return "";
    }

    /// <summary>
    /// 计算 Sec-WebSocket-Accept。
    /// 公式：Base64(SHA1(Sec-WebSocket-Key + 魔数GUID))
    /// 魔数 258EAFA5-... 是 RFC 6455 固定值，用于防止中间人攻击。
    /// </summary>
    private static string ComputeAcceptKey(string secWebSocketKey)
    {
        const string magicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        byte[] hashInput = Encoding.ASCII.GetBytes(secWebSocketKey + magicGuid);
        byte[] hash = SHA1.HashData(hashInput);
        return Convert.ToBase64String(hash);
    }

    /// <summary>写一个简单的 HTTP 错误响应并关闭连接</summary>
    private static async Task WriteHttpResponse(NetworkStream stream, string statusLine)
    {
        string response = $"HTTP/1.1 {statusLine}\r\nContent-Length: 0\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, 0, bytes.Length);
    }

    /// <summary>写 101 Switching Protocols 响应，完成升级</summary>
    private static async Task WriteSwitchingProtocols(NetworkStream stream, string acceptKey)
    {
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
            "\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, 0, bytes.Length);
        await stream.FlushAsync();
    }
}
