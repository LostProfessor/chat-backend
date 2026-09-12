using System.Net.Sockets;
using System.Text;

namespace ChattingWebsite.Services;

/// <summary>
/// 服务端 WebSocket 帧收发器
/// 
/// 一个实例对应一个客户端连接。
/// 
/// 和客户端的区别（对称镜像）：
///   客户端发帧 mask:true  →  服务端收帧 masked:true（解掩码）
///   客户端收帧 masked:false → 服务端发帧 mask:false（不掩码）
/// </summary>
public class MyWebSocketServer
{
    /// <summary>
    /// 单帧 payload 上限。
    /// 本项目应用层最大块是 256 KB（FileTransferCodec），加 9 字节帧头约 262 KB，
    /// 取 1 MB = 约 4 倍余量，正常流量不会触顶。
    /// ★ 没有这个上限时：客户端只要发一个 10 字节的帧头声明 payload 为 1 GB，
    ///   服务器就会立刻分配 1 GB（见 ReadExactAsync 是「先分配后读取」），
    ///   一个字节都还没收到 —— 10 个连接即可 OOM。
    /// </summary>
    private const ulong MaxFramePayload = 1 * 1024 * 1024;

    private readonly NetworkStream _stream;
    private volatile bool _isOpen = true;
    private int _closeSent;   // 关闭帧只允许发一次（Interlocked：0=未发 1=已发）

    /// <summary>
    /// 写入串行化门。
    /// BroadcastToAll 可能被多个并发处理的消息同时触发，而同一个 NetworkStream
    /// 上的并发 WriteAsync 不保证原子性 —— 两个帧的字节会交错写进 TCP 流：
    ///   [帧A头][帧B头][帧A数据][帧B数据]  → 客户端解析直接失败
    /// 所有写入必须过这把锁。
    /// </summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // ────────── 事件 ──────────
    /// <summary>收到一条完整的文本消息</summary>
    public event Action<MyWebSocketServer, string> OnTextMessage;

    /// <summary>收到一条完整的二进制消息（用于文件传输等）</summary>
    public event Action<MyWebSocketServer, byte[]> OnBinaryMessage;

    /// <summary>连接关闭</summary>
    public event Action<MyWebSocketServer> OnClose;

    /// <summary>发生错误</summary>
    public event Action<MyWebSocketServer, string> OnError;

    public MyWebSocketServer(NetworkStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// 从 TCP 流中读取若干字节。
    /// </summary>
    private async Task<byte[]> ReadExactAsync(int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await _stream.ReadAsync(buffer, offset, count - offset);
            if (read == 0) throw new Exception("客户端断开连接");
            offset += read;
        }
        return buffer;
    }

    /// <summary>
    /// 接收一个 WebSocket 帧。
    /// 任何协议违规一律抛 WebSocketProtocolException（带应回给对端的关闭码），
    /// 由 ReceiveLoopAsync 统一回 Close 并终止连接。
    /// </summary>
    private async Task<WebSocketFrame> ReceiveFrameAsync()
    {
        // ── 帧头前 2 字节 ──
        byte[] header = await ReadExactAsync(2);

        bool fin = (header[0] & 0x80) != 0;
        int rsv = header[0] & 0x70;
        var opcode = (Opcode)(header[0] & 0x0F);

        bool masked = (header[1] & 0x80) != 0;
        ulong payloadLen = (ulong)(header[1] & 0x7F);

        // ══════ 全部校验都放在「分配内存之前」（RFC 6455） ══════

        // RSV1/2/3：本项目没协商任何扩展，必须全为 0
        if (rsv != 0)
            throw new WebSocketProtocolException(1002, $"RSV 位非 0（0x{rsv:X2}），但未协商任何扩展");

        // ⚠ 未知 opcode 必须中断连接（§5.2）。旧版只是记条日志然后 continue ——
        //   一旦 TCP 流错位（比如残留的 HTTP 头被当成帧解析），服务器就会陷入
        //   「无限消费垃圾字节且永不报错」的僵尸状态，把致命故障伪装成静默异常。
        //   当初 WebSocketHandshake 丢头的 bug 之所以那么难查，根因就在这里。
        if (!IsKnownOpcode(opcode))
            throw new WebSocketProtocolException(1002, $"未知或不允许的 opcode 0x{(byte)opcode:X}");

        // 客户端发来的帧必须带掩码（§5.1）
        if (!masked)
            throw new WebSocketProtocolException(1002, "客户端帧未使用掩码");

        bool isControl = ((byte)opcode & 0x08) != 0;

        // 控制帧：不可分片、payload ≤ 125 字节（§5.5）
        if (isControl && !fin)
            throw new WebSocketProtocolException(1002, $"控制帧 {opcode} 不可分片");
        if (isControl && payloadLen > 125)
            throw new WebSocketProtocolException(1002, $"控制帧 {opcode} 的 payload 超过 125 字节");

        // 本项目协议不使用分片：前端走浏览器原生 WebSocket（永远 FIN=1），
        // 后端 FrameEncoder 也总是以 fin:true 发送。
        // 因此直接拒绝分片，而不是维护一个永不可达、却又没有长度上限的分片缓冲区。
        if (!isControl && !fin)
            throw new WebSocketProtocolException(1002, $"{opcode} 帧使用了分片（FIN=0），本项目协议不支持分片");

        // ── 扩展长度 ──
        if (payloadLen == 126)
        {
            byte[] ext = await ReadExactAsync(2);
            if (BitConverter.IsLittleEndian) Array.Reverse(ext);
            payloadLen = BitConverter.ToUInt16(ext, 0);
        }
        else if (payloadLen == 127)
        {
            byte[] ext = await ReadExactAsync(8);
            // 网络字节序（大端）：ext[0] 是最高字节，RFC 要求最高位必须为 0
            if ((ext[0] & 0x80) != 0)
                throw new WebSocketProtocolException(1002, "64 位长度字段的最高位必须为 0");
            if (BitConverter.IsLittleEndian) Array.Reverse(ext);
            payloadLen = BitConverter.ToUInt64(ext, 0);
        }

        // ★★ 上限校验必须放在 ReadExactAsync 之前。
        //    ReadExactAsync 是「先 new byte[count] 再读」，只要把超长长度传进去，
        //    内存就已经被占掉了。这一句同时顺带消灭了 (int) 强转的截断问题：
        //    ulong 超过 int.MaxValue 时强转会环绕（4 GB → 0），原本会「静默成功」
        //    并让整个 TCP 流永久错位。
        if (payloadLen > MaxFramePayload)
            throw new WebSocketProtocolException(1009,
                $"帧 payload {payloadLen} 字节，超过上限 {MaxFramePayload} 字节");

        // ── 掩码密钥（上面已强制要求 masked，故此处无条件读 4 字节） ──
        byte[] maskKey = await ReadExactAsync(4);

        // ── Payload（长度已校验，此处强转 int 安全） ──
        byte[] payload = await ReadExactAsync((int)payloadLen);

        // ── 解掩码（客户端发来的帧一定被掩码了） ──
        for (int i = 0; i < payload.Length; i++)
            payload[i] ^= maskKey[i % 4];

        return new WebSocketFrame
        {
            FIN = fin,
            Opcode = opcode,
            Masked = masked,
            PayloadLength = payloadLen,
            MaskKey = maskKey,
            Payload = payload
        };
    }

    /// <summary>允许的 opcode。故意不含 Continuation(0x0) —— 本项目不支持分片。</summary>
    private static bool IsKnownOpcode(Opcode opcode) => opcode is
        Opcode.Text or Opcode.Binary or Opcode.Close or Opcode.Ping or Opcode.Pong;

    // ═══════════════ 帧发送 ═══════════════

    /// <summary>
    /// 裸写入一帧，不检查 _isOpen。
    /// Close 帧必须能在 _isOpen 翻成 false 之后仍写出去，所以单独留这个内部方法。
    /// </summary>
    private async Task WriteFrameAsync(byte[] payload, Opcode opcode)
    {
        // ★ 关键：mask: false，服务端不允许掩码
        byte[] frame = FrameEncoder.EncodeFrame(payload, opcode, mask: false);

        await _sendLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(frame, 0, frame.Length);
            await _stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>发送一帧（服务端不掩码）。连接已关闭则静默丢弃。</summary>
    public async Task SendFrameAsync(byte[] payload, Opcode opcode)
    {
        if (!_isOpen) return;
        await WriteFrameAsync(payload, opcode);
    }

    /// <summary>发送文本消息的快捷方法</summary>
    public async Task SendTextAsync(string text)
        => await SendFrameAsync(Encoding.UTF8.GetBytes(text), Opcode.Text);

    /// <summary>
    /// 发送 Close 帧（幂等：整个连接生命周期只发一次）。
    /// ★ 用 WriteFrameAsync 而非 SendFrameAsync —— 后者会被 _isOpen 拦掉，
    ///   而这正是旧版 CloseAsync 发不出关闭帧的原因（先置 _isOpen=false 再发）。
    /// </summary>
    private async Task SendCloseAsync(ushort code, string reason)
    {
        if (Interlocked.Exchange(ref _closeSent, 1) != 0) return;

        byte[] reasonBytes = string.IsNullOrEmpty(reason)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(reason);
        // 控制帧 payload 上限 125 字节，其中 2 字节留给状态码
        if (reasonBytes.Length > 123) reasonBytes = reasonBytes[..123];

        byte[] closePayload = new byte[2 + reasonBytes.Length];
        closePayload[0] = (byte)(code >> 8);      // 网络字节序（大端）
        closePayload[1] = (byte)(code & 0xFF);
        reasonBytes.CopyTo(closePayload, 2);

        try
        {
            await WriteFrameAsync(closePayload, Opcode.Close);
        }
        catch
        {
            // 对端可能已经没有读端了，关闭帧发不出去属正常，不应影响后续清理
        }
    }

    // ═══════════════ 持续接收循环 ═══════════════

    /// <summary>
    /// 持续接收帧，直到连接关闭。
    /// 自动处理：Ping→Pong、Close 握手、协议校验。
    /// 退出时保证 OnClose 恰好触发一次（无论正常关闭、协议违规还是对端掉线）。
    /// </summary>
    public async Task ReceiveLoopAsync()
    {
        try
        {
            while (_isOpen)
            {
                WebSocketFrame frame = await ReceiveFrameAsync();

                switch (frame.Opcode)
                {
                    case Opcode.Text:
                        OnTextMessage?.Invoke(this, Encoding.UTF8.GetString(frame.Payload));
                        break;

                    case Opcode.Binary:
                        OnBinaryMessage?.Invoke(this, frame.Payload);
                        break;

                    case Opcode.Ping:
                        // RFC 6455：收到 Ping 必须回 Pong，payload 原样返回
                        await SendFrameAsync(frame.Payload, Opcode.Pong);
                        break;

                    case Opcode.Pong:
                        break; // 通常不处理

                    case Opcode.Close:
                        // 对端发起关闭：回一个 Close 完成握手，然后退出循环。
                        // 沿用对端的状态码；1005/1006/1015 是「不可放在线上」的保留值，换成 1000。
                        ushort peerCode = 1000;
                        if (frame.Payload.Length >= 2)
                            peerCode = (ushort)((frame.Payload[0] << 8) | frame.Payload[1]);
                        if (peerCode is 1005 or 1006 or 1015)
                            peerCode = 1000;
                        await SendCloseAsync(peerCode, string.Empty);
                        return;
                }
            }
        }
        catch (WebSocketProtocolException ex)
        {
            // 协议违规：回 Close 并终止连接，绝不继续解析已经不可信的字节流
            OnError?.Invoke(this, $"协议错误({ex.CloseCode}) {ex.Message}");
            await SendCloseAsync(ex.CloseCode, ex.Message);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, ex.Message);
        }
        finally
        {
            _isOpen = false;
            // 保证 OnClose 恰好触发一次：以往它只在「对端主动发 Close」时触发，
            // 掉线/协议违规都不会触发，导致订阅者只能再靠自己兜底，
            // 两边各写一份清理逻辑（这就是重复「已断开」日志的来源）。
            OnClose?.Invoke(this);
        }
    }

    // ═══════════════ 生命周期 ═══════════════

    public bool IsOpen => _isOpen;

    /// <summary>
    /// 主动关闭连接（服务端发起）。
    /// ⚠ 顺序很关键：必须先发 Close 帧，再翻 _isOpen —— 旧版写反了，
    ///   置 false 之后 SendFrameAsync 第一行就 return，关闭帧一个字节都发不出去，
    ///   导致客户端永远只能拿到 onclose(1006)，无法分辨「服务器主动关」和「掉线」。
    /// 幂等，可安全重复调用。
    /// </summary>
    public async Task CloseAsync(ushort code = 1000, string reason = "")
    {
        if (!_isOpen) return;
        await SendCloseAsync(code, reason);
        _isOpen = false;
    }

    public void Dispose()
    {
        _isOpen = false;
        try { _stream?.Close(); } catch { /* 已关闭则忽略 */ }
    }
}


public enum Opcode : byte
{
    Continuation = 0x0,
    Text = 0x1,
    Binary = 0x2,
    Close = 0x8,
    Ping = 0x9,
    Pong = 0xA
}

/// <summary>
/// 帧层协议违规。CloseCode 是要回给对端的 WebSocket 关闭码：
///   1002 = 协议错误，1008 = 策略违规，1009 = 消息过大。
/// </summary>
public class WebSocketProtocolException : Exception
{
    public ushort CloseCode { get; }

    public WebSocketProtocolException(ushort closeCode, string message) : base(message)
    {
        CloseCode = closeCode;
    }
}

public class WebSocketFrame
{
    public bool FIN { get; set; }
    public Opcode Opcode { get; set; }
    public bool Masked { get; set; }
    public ulong PayloadLength { get; set; }
    public byte[] MaskKey { get; set; }
    public byte[] Payload { get; set; }
}

public static class FrameEncoder
{
    /// <summary>将 payload 编码为一个 WebSocket 帧</summary>
    /// <param name="payload">负载数据</param>
    /// <param name="opcode">帧类型</param>
    /// <param name="mask">客户端必须 true，服务端必须 false</param>
    /// <param name="fin">是否最后一帧（单帧消息为 true）</param>
    public static byte[] EncodeFrame(byte[] payload, Opcode opcode, bool mask, bool fin = true)
    {
        using var ms = new MemoryStream();

        // ── 字节 0：FIN(1) + RSV(000) + Opcode(4 bits) ──
        byte byte0 = (byte)((fin ? 0x80 : 0x00) | (byte)opcode);
        ms.WriteByte(byte0);

        // ── 字节 1：MASK(1) + PayloadLength(7) ──
        byte byte1 = mask ? (byte)0x80 : (byte)0x00;

        if (payload.Length < 126)
        {
            byte1 |= (byte)payload.Length;
            ms.WriteByte(byte1);
        }
        else if (payload.Length <= 0xFFFF)
        {
            byte1 |= 126;
            ms.WriteByte(byte1);
            byte[] len2 = BitConverter.GetBytes((ushort)payload.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(len2);
            ms.Write(len2, 0, 2);
        }
        else
        {
            byte1 |= 127;
            ms.WriteByte(byte1);
            byte[] len8 = BitConverter.GetBytes((ulong)payload.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(len8);
            ms.Write(len8, 0, 8);
        }

        // ── 掩码密钥 + Payload ──
        if (mask)
        {
            byte[] maskKey = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(maskKey);
            ms.Write(maskKey, 0, 4);

            for (int i = 0; i < payload.Length; i++)
                ms.WriteByte((byte)(payload[i] ^ maskKey[i % 4]));
        }
        else
        {
            ms.Write(payload, 0, payload.Length);
        }

        return ms.ToArray();
    }
}
