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
    private readonly NetworkStream _stream;
    private bool _isOpen = true;

    // ────────── 分片缓冲区（和客户端完全一样） ──────────
    private readonly List<byte> _fragmentBuffer = new();
    private Opcode _fragmentOpcode;

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

    /// <summary>接收一个 WebSocket 帧</summary>
    private async Task<WebSocketFrame> ReceiveFrameAsync()
    {
        // ── 帧头前 2 字节 ──
        byte[] header = await ReadExactAsync(2);
        int pos = 0;

        bool fin = (header[pos] & 0x80) != 0;
        var opcode = (Opcode)(header[pos] & 0x0F);
        pos++;

        bool masked = (header[pos] & 0x80) != 0;
        ulong payloadLen = (ulong)(header[pos] & 0x7F);

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
            if (BitConverter.IsLittleEndian) Array.Reverse(ext);
            payloadLen = BitConverter.ToUInt64(ext, 0);
        }

        // ── 掩码密钥 ──
        byte[] maskKey = null;
        if (masked)
            maskKey = await ReadExactAsync(4);

        // ── Payload ──
        byte[] payload = await ReadExactAsync((int)payloadLen);

        // ── 解掩码（客户端发来的帧一定被掩码了） ──
        if (masked && maskKey != null)
        {
            for (int i = 0; i < payload.Length; i++)
                payload[i] ^= maskKey[i % 4];
        }

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

    // ═══════════════ 帧发送 ═══════════════

    /// <summary>发送一帧（服务端不掩码）</summary>
    public async Task SendFrameAsync(byte[] payload, Opcode opcode)
    {
        if (!_isOpen) return;
        // ★ 关键：mask: false，服务端不允许掩码
        byte[] frame = FrameEncoder.EncodeFrame(payload, opcode, mask: false);
        await _stream.WriteAsync(frame, 0, frame.Length);
        await _stream.FlushAsync();
    }

    /// <summary>发送文本消息的快捷方法</summary>
    public async Task SendTextAsync(string text)
        => await SendFrameAsync(Encoding.UTF8.GetBytes(text), Opcode.Text);

    /// <summary>发送 Close 帧</summary>
    private async Task SendCloseFrameAsync()
    {
        byte[] closePayload = { 0x03, 0xE8 };  // 状态码 1000 = 正常关闭，大端序
        await SendFrameAsync(closePayload, Opcode.Close);
    }

    // ═══════════════ 持续接收循环 ═══════════════

    /// <summary>
    /// 持续接收帧，直到连接关闭。
    /// 自动处理：文本消息组装、分片拼接、Ping→Pong、Close 握手。
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
                        HandleTextFrame(frame);
                        break;

                    case Opcode.Binary:
                        HandleBinaryFrame(frame);
                        break;

                    case Opcode.Continuation:
                        HandleContinuationFrame(frame);
                        break;

                    case Opcode.Ping:
                        // RFC 6455：收到 Ping 必须回 Pong，payload 原样返回
                        await SendFrameAsync(frame.Payload, Opcode.Pong);
                        break;

                    case Opcode.Pong:
                        break; // 通常不处理

                    case Opcode.Close:
                        await SendFrameAsync(frame.Payload, Opcode.Close);
                        _isOpen = false;
                        OnClose?.Invoke(this);
                        return;

                    default:
                        OnError?.Invoke(this, $"未知帧类型: {frame.Opcode}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, ex.Message);
        }
        finally
        {
            _isOpen = false;
        }
    }

    // ───── 分片消息拼接 ─────

    private void HandleTextFrame(WebSocketFrame frame)
    {
        if (frame.FIN)
        {
            string text = Encoding.UTF8.GetString(frame.Payload);
            OnTextMessage?.Invoke(this, text);
        }
        else
        {
            _fragmentBuffer.Clear();
            _fragmentBuffer.AddRange(frame.Payload);
            _fragmentOpcode = Opcode.Text;
        }
    }

    private void HandleBinaryFrame(WebSocketFrame frame)
    {
        if (frame.FIN)
        {
            OnBinaryMessage?.Invoke(this, frame.Payload);
        }
        else
        {
            // 分片消息起点：清空缓冲，记录是 Binary 类型
            _fragmentBuffer.Clear();
            _fragmentBuffer.AddRange(frame.Payload);
            _fragmentOpcode = Opcode.Binary;
        }
    }

    private void HandleContinuationFrame(WebSocketFrame frame)
    {
        _fragmentBuffer.AddRange(frame.Payload);
        if (frame.FIN)
        {
            byte[] full = _fragmentBuffer.ToArray();
            _fragmentBuffer.Clear();
            if (_fragmentOpcode == Opcode.Text)
                OnTextMessage?.Invoke(this, Encoding.UTF8.GetString(full));
            else if (_fragmentOpcode == Opcode.Binary)
                OnBinaryMessage?.Invoke(this, full);
        }
    }

    // ═══════════════ 生命周期 ═══════════════

    public bool IsOpen => _isOpen;

    /// <summary>关闭连接</summary>
    public async Task CloseAsync()
    {
        if (!_isOpen) return;
        _isOpen = false;
        await SendCloseFrameAsync();
    }

    public void Dispose()
    {
        _isOpen = false;
        _stream?.Close();
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
