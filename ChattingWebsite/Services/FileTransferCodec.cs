using System.Text;

namespace ChattingWebsite.Services;

/// <summary>
/// 文件传输帧类型（协议 Op 字段）
/// </summary>
public enum FileTransferOp : byte
{
    /// <summary>开始传输：声明文件名、类型、大小（payload = JSON 元数据）</summary>
    Start = 0x01,
    /// <summary>数据块：Seq = 块序号，payload = 该块原始字节</summary>
    Chunk = 0x02,
    /// <summary>确认（服务端→客户端）：Seq = 已收块序号，用于进度/断点</summary>
    Ack = 0x03,
    /// <summary>传输完成：payload = 图片元数据 JSON（url/thumbUrl 等）</summary>
    Done = 0x04,
    /// <summary>取消传输</summary>
    Cancel = 0x05,
    /// <summary>错误（服务端→客户端）：payload = UTF8 错误文本</summary>
    Error = 0x06,
    /// <summary>断点续传：客户端请求从断点继续（payload = JSON 元数据）</summary>
    Resume = 0x07
}

/// <summary>
/// 解析后的文件传输帧（协议头 + payload）
/// </summary>
public class FileTransferFrame
{
    public FileTransferOp Op { get; set; }
    public int SessionId { get; set; }
    public int Seq { get; set; }
    public byte[] Payload { get; set; }
}

/// <summary>
/// 文件传输二进制协议编解码器。
///
/// 帧结构（定长 10 字节头 + 可变 payload）：
///   [0]      Magic = 0xAB（校验协议是否匹配）
///   [1]      Op（FileTransferOp）
///   [2..5]   SessionId（int32 大端）
///   [6..9]   Seq（int32 大端，块序号）
///   [10..]   Payload（可变长）
///
/// 说明：这一层是"帧之上"的应用协议，负责把大文件拆成多块
/// 通过 WebSocket Binary 帧传输，而不是直接塞进 JSON 文本消息。
/// </summary>
public static class FileTransferCodec
{
    public const byte Magic = 0xAB;
    public const int HeaderSize = 10;

    /// <summary>
    /// 把一帧编码为字节数组（作为 WebSocket Binary 帧的 payload 发送）
    /// </summary>
    public static byte[] Encode(FileTransferOp op, int sessionId, int seq, byte[] payload = null)
    {
        payload ??= Array.Empty<byte>();
        var data = new byte[HeaderSize + payload.Length];

        data[0] = Magic;
        data[1] = (byte)op;
        WriteInt32BE(data, 2, sessionId);
        WriteInt32BE(data, 6, seq);
        Buffer.BlockCopy(payload, 0, data, HeaderSize, payload.Length);

        return data;
    }

    /// <summary>
    /// 解析二进制 payload。Magic 不匹配或长度不足返回 null。
    /// </summary>
    public static FileTransferFrame TryParse(byte[] data)
    {
        if (data == null || data.Length < HeaderSize || data[0] != Magic)
            return null;

        return new FileTransferFrame
        {
            Op = (FileTransferOp)data[1],
            SessionId = ReadInt32BE(data, 2),
            Seq = ReadInt32BE(data, 6),
            Payload = data.Length > HeaderSize
                ? data[HeaderSize..]
                : Array.Empty<byte>()
        };
    }

    /// <summary>从 payload 解析 JSON 元数据（Start/Resume 用）</summary>
    public static FileTransferMeta ParseMeta(byte[] payload)
    {
        var json = Encoding.UTF8.GetString(payload);
        // 前端发送的是 camelCase（fileName/totalSize），后端属性是 PascalCase，
        // 必须开启大小写不敏感，否则反序列化后全部为 null 导致校验失败。
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        return System.Text.Json.JsonSerializer.Deserialize<FileTransferMeta>(json, options);
    }

    /// <summary>把元数据编码为 payload</summary>
    public static byte[] EncodeMeta(FileTransferMeta meta)
        => Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(meta));

    private static void WriteInt32BE(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static int ReadInt32BE(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
}

/// <summary>
/// 文件传输元数据（Start/Resume 帧的 JSON payload）
/// </summary>
public class FileTransferMeta
{
    /// <summary>原始文件名</summary>
    public string FileName { get; set; }
    /// <summary>媒体类型：image/video/audio/doc（P0 仅支持 image）</summary>
    public string MediaType { get; set; }
    /// <summary>文件总字节数</summary>
    public long TotalSize { get; set; }
    /// <summary>目标群/私聊房间（复用现有 roomId 体系）</summary>
    public string RoomId { get; set; }
}
