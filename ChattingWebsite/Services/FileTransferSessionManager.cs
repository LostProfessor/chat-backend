namespace ChattingWebsite.Services;

/// <summary>
/// 一次文件传输会话（一个文件对应一个会话）
/// </summary>
public class FileTransferSession : IDisposable
{
    public int SessionId { get; set; }
    /// <summary>发起传输的用户 PublicId</summary>
    public string OwnerUserId { get; set; }
    public string FileName { get; set; }
    public string MediaType { get; set; }
    public long TotalSize { get; set; }
    /// <summary>目标房间（群 ID 或私聊 roomId）</summary>
    public string RoomId { get; set; }
    /// <summary>临时文件路径（未完成前）</summary>
    public string TempPath { get; set; }
    /// <summary>已接收字节数（用于进度和断点续传）</summary>
    public long ReceivedBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>最后活动时间（用于超时清理，断线后保留一段时间供续传）</summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    /// <summary>是否已完成接收（等待落库）</summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// 会话期间的写文件流（首次写入时创建，会话存活期间保持打开，顺序写）。
    /// 避免每收一块都 open/close 文件句柄造成的 I/O 放大。
    /// </summary>
    internal FileStream? WriteStream { get; set; }

    public void Dispose()
    {
        WriteStream?.Dispose();
        WriteStream = null;
    }
}

/// <summary>
/// 文件传输会话管理器（单例）。
///
/// 职责：为每个进行中的文件传输分配/管理会话，记录进度，
/// 把到达的二进制块流式写入临时文件，供断点续传与合并使用。
///
/// 线程安全：使用锁保护字典（多连接并发访问）。
/// </summary>
public class FileTransferSessionManager
{
    private readonly object _lock = new();
    private readonly Dictionary<int, FileTransferSession> _sessions = new();
    private int _nextId = 1;

    // 会话超时：超过该时长无活动则视为废弃（防止断线后残留的内存/临时文件）
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 创建新会话（客户端 START 时调用），返回分配好 SessionId 的会话。
    /// 临时文件路径按 SessionId 命名，放在系统临时目录。
    /// 创建前顺带清理一次过期会话。
    /// </summary>
    public FileTransferSession CreateSession(string ownerUserId, FileTransferMeta meta)
    {
        lock (_lock)
        {
            CleanupExpiredLocked();
            var session = new FileTransferSession
            {
                SessionId = _nextId++,
                OwnerUserId = ownerUserId,
                FileName = meta.FileName,
                MediaType = meta.MediaType,
                TotalSize = meta.TotalSize,
                RoomId = meta.RoomId,
                TempPath = Path.Combine(Path.GetTempPath(), $"fts_{ownerUserId}_{_nextId - 1}.tmp")
            };
            _sessions[session.SessionId] = session;
            return session;
        }
    }

    /// <summary>根据 SessionId 获取会话，并刷新最后活动时间（供续传判定）</summary>
    public FileTransferSession GetSession(int sessionId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
                session.LastActivity = DateTime.UtcNow;
            return session;
        }
    }

    /// <summary>移除会话（完成或取消时清理）：先关流释放文件句柄，再删临时文件</summary>
    public void RemoveSession(int sessionId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                _sessions.Remove(sessionId);
                // 必须先 Dispose 关闭文件流，否则后续 File.Move / File.Delete 会因文件被占用而失败
                session.Dispose();
                try { if (File.Exists(session.TempPath)) File.Delete(session.TempPath); }
                catch { /* 忽略清理失败 */ }
            }
        }
    }

    /// <summary>
    /// 把一块数据写入临时文件（追加到已接收字节之后）。
    /// 首次写入时创建文件流，之后复用（会话期间保持打开，避免每块 open/close）。
    /// 返回写入后的累计字节数。
    /// </summary>
    public long AppendChunk(FileTransferSession session, byte[] chunk)
    {
        lock (_lock)
        {
            session.LastActivity = DateTime.UtcNow;

            // 首次写入才创建流（大缓冲 + 顺序访问提示）
            session.WriteStream ??= new FileStream(
                session.TempPath, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.None, bufferSize: 64 * 1024, FileOptions.SequentialScan);

            // 对齐写入位置：正常顺序写时 Position 天然等于 ReceivedBytes；
            // 断点续传/流重建时强制对齐到已收字节，保证不错位。
            if (session.WriteStream.Position != session.ReceivedBytes)
                session.WriteStream.Position = session.ReceivedBytes;

            session.WriteStream.Write(chunk, 0, chunk.Length);
            // 刷到 OS 缓冲（非刷盘），保证断点续传时内存进度与文件内容一致
            session.WriteStream.Flush();
            session.ReceivedBytes += chunk.Length;
            return session.ReceivedBytes;
        }
    }

    /// <summary>
    /// 清理超过超时时间无活动的会话（断线后用户未续传的残留）。
    /// 供外部定时调用或内部惰性调用。
    /// </summary>
    public void CleanupExpired()
    {
        lock (_lock) { CleanupExpiredLocked(); }
    }

    private void CleanupExpiredLocked()
    {
        var now = DateTime.UtcNow;
        var expired = _sessions.Values
            .Where(s => !s.IsComplete && now - s.LastActivity > SessionTimeout)
            .ToList();
        foreach (var s in expired)
            RemoveSession(s.SessionId);
    }
}
