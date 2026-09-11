using ChattingWebsite.DB;
using ChattingWebsite.Model;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace ChattingWebsite.Services;

/// <summary>
/// 文件传输处理器（Scoped）。
///
/// 职责：接收客户端通过 WebSocket Binary 帧发来的文件分块，
/// 完成"开始 → 收块 → 合并 → 落库 → 广播"的完整流程。
///
/// 生命周期说明：由 HandlewebSocketsMidWare 的 OnBinaryMessage 事件
/// 每收到一条 Binary 帧创建独立 DI Scope 调用，因此依赖的 DbContext 安全。
/// </summary>
public class FileTransferHandler
{
    private readonly FileTransferSessionManager _sessionManager;
    private readonly ChattingWebsiteDBContext _db;
    private readonly HandlewebSocketsMidWare _wsHandler;
    private readonly ILogger<FileTransferHandler> _logger;

    private const string MediaRoot = "uploads";

    /// <summary>各媒体类型的规则：允许的扩展名 + 大小上限 + 存储子目录</summary>
    private record MediaRule(string[] Exts, long MaxSize, string SubDir);

    private static readonly Dictionary<string, MediaRule> MediaRules = new()
    {
        ["image"] = new(new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }, 50L * 1024 * 1024, "images"),
        ["video"] = new(new[] { ".mp4", ".webm", ".mov" }, 200L * 1024 * 1024, "videos"),
        ["audio"] = new(new[] { ".mp3", ".wav", ".ogg", ".m4a" }, 50L * 1024 * 1024, "audios"),
        ["file"] = new(
            new[] { ".pdf", ".zip", ".7z", ".rar", ".txt", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" },
            100L * 1024 * 1024, "files")
    };

    /// <summary>各类型处理器（按 MediaType 分派）；无匹配则不做后处理</summary>
    private readonly IEnumerable<IMediaProcessor> _processors;

    public FileTransferHandler(
        FileTransferSessionManager sessionManager,
        ChattingWebsiteDBContext db,
        HandlewebSocketsMidWare wsHandler,
        IEnumerable<IMediaProcessor> processors,
        ILogger<FileTransferHandler> logger)
    {
        _sessionManager = sessionManager;
        _db = db;
        _wsHandler = wsHandler;
        _processors = processors;
        _logger = logger;
    }

    /// <summary>
    /// 处理一条二进制帧。senderSocket 用于给发起者回 ACK / DONE / ERROR。
    /// </summary>
    public async Task HandleAsync(string senderUserId, byte[] data, MyWebSocketServer senderSocket)
    {
        var frame = FileTransferCodec.TryParse(data);
        if (frame == null)
        {
            await SendError(senderSocket, "无法解析文件传输帧");
            return;
        }

        switch (frame.Op)
        {
            case FileTransferOp.Start:
                await HandleStart(senderUserId, frame, senderSocket);
                break;
            case FileTransferOp.Resume:
                await HandleResume(senderUserId, frame, senderSocket);
                break;
            case FileTransferOp.Chunk:
                await HandleChunk(senderUserId, frame, senderSocket);
                break;
            case FileTransferOp.Done:
                await HandleDone(senderUserId, frame, senderSocket);
                break;
            case FileTransferOp.Cancel:
                _sessionManager.RemoveSession(frame.SessionId);
                break;
            default:
                await SendError(senderSocket, "不支持的文件传输帧类型");
                break;
        }
    }

    // ───── 开始传输 ─────

    private async Task HandleStart(string senderUserId, FileTransferFrame frame, MyWebSocketServer socket)
    {
        var meta = FileTransferCodec.ParseMeta(frame.Payload);
        if (meta == null || string.IsNullOrEmpty(meta.FileName) || meta.TotalSize <= 0)
        {
            await SendError(socket, "文件元数据不完整");
            return;
        }

        // 按类型校验：扩展名白名单 + 大小上限
        var ext = Path.GetExtension(meta.FileName).ToLowerInvariant();
        if (!MediaRules.TryGetValue(meta.MediaType, out var rule))
        {
            await SendError(socket, $"不支持的文件类型：{meta.MediaType}");
            return;
        }
        if (!rule.Exts.Contains(ext))
        {
            await SendError(socket, $"该类型不支持的扩展名：{ext}");
            return;
        }
        if (meta.TotalSize > rule.MaxSize)
        {
            await SendError(socket, $"文件不能超过 {rule.MaxSize / 1024 / 1024}MB");
            return;
        }

        var session = _sessionManager.CreateSession(senderUserId, meta);

        // 回 ACK：SessionId 字段携带新会话ID，客户端后续 CHUNK/DONE 使用
        var ack = FileTransferCodec.Encode(FileTransferOp.Ack, session.SessionId, 0, null);
        await socket.SendFrameAsync(ack, Opcode.Binary);
        _logger.LogInformation("[FTS] 会话 {SessionId} 开始：{FileName} ({TotalSize}B)", session.SessionId, meta.FileName, meta.TotalSize);
    }

    // ───── 断点续传 ─────

    private async Task HandleResume(string senderUserId, FileTransferFrame frame, MyWebSocketServer socket)
    {
        var session = _sessionManager.GetSession(frame.SessionId);
        if (session == null || session.OwnerUserId != senderUserId || session.IsComplete)
        {
            await SendError(socket, "会话不存在或已失效，请重新开始");
            return;
        }

        // 回 ACK：Seq 字段携带已收字节数（int32），客户端从断点继续
        var ack = FileTransferCodec.Encode(FileTransferOp.Ack, session.SessionId, (int)Math.Min(session.ReceivedBytes, int.MaxValue), null);
        await socket.SendFrameAsync(ack, Opcode.Binary);
        _logger.LogInformation("[FTS] 会话 {SessionId} 断点续传，已收 {Bytes}B", session.SessionId, session.ReceivedBytes);
    }

    // ───── 数据块 ─────

    private async Task HandleChunk(string senderUserId, FileTransferFrame frame, MyWebSocketServer socket)
    {
        var session = _sessionManager.GetSession(frame.SessionId);
        if (session == null || session.OwnerUserId != senderUserId || session.IsComplete)
        {
            await SendError(socket, "会话不存在，请重新开始");
            return;
        }

        if (session.ReceivedBytes + frame.Payload.Length > session.TotalSize)
        {
            await SendError(socket, "接收数据超过声明大小，传输中止");
            _sessionManager.RemoveSession(frame.SessionId);
            return;
        }

        _sessionManager.AppendChunk(session, frame.Payload);

        // 回 ACK：Seq 携带已收字节数，供前端更新进度
        var ack = FileTransferCodec.Encode(FileTransferOp.Ack, session.SessionId, (int)Math.Min(session.ReceivedBytes, int.MaxValue), null);
        await socket.SendFrameAsync(ack, Opcode.Binary);
    }

    // ───── 完成：合并 + 落库 + 广播 ─────

    private async Task HandleDone(string senderUserId, FileTransferFrame frame, MyWebSocketServer socket)
    {
        var session = _sessionManager.GetSession(frame.SessionId);
        if (session == null || session.OwnerUserId != senderUserId)
        {
            await SendError(socket, "会话不存在");
            return;
        }

        // 完整性校验：收满字节才算成功
        if (session.ReceivedBytes != session.TotalSize)
        {
            await SendError(socket, $"文件不完整（{session.ReceivedBytes}/{session.TotalSize}B），请断点续传");
            return;
        }

        try
        {
            // 1. 把临时文件移动到正式目录（按媒体类型分子目录）
            var mediaId = Guid.NewGuid().ToString("N");
            var ext = Path.GetExtension(session.FileName).ToLowerInvariant();
            var subDir = MediaRules.TryGetValue(session.MediaType, out var rule) ? rule.SubDir : "files";
            var wwwroot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var mediaFolder = Path.Combine(wwwroot, MediaRoot, subDir);
            Directory.CreateDirectory(mediaFolder);

            var fileName = $"{mediaId}{ext}";
            var destPath = Path.Combine(mediaFolder, fileName);

            // 关键：Move 之前必须先关闭会话持有的文件流，否则文件被占用导致 Move 失败
            session.Dispose();
            File.Move(session.TempPath, destPath, overwrite: true);
            var relativeUrl = $"/{MediaRoot}/{subDir}/{fileName}";

            // 1.1 类型相关的后处理（图片 → 缩略图；其他类型暂不处理）
            var processor = _processors.FirstOrDefault(p => p.MediaType == session.MediaType);
            var mediaResult = processor != null
                ? await processor.ProcessAsync(destPath, relativeUrl, mediaFolder, mediaId)
                : new MediaResult(relativeUrl, null);
            var thumbUrl = mediaResult.ThumbUrl;

            // 2. 保存消息记录（图片消息，content 为空）
            var msg = new Message
            {
                id = Guid.NewGuid().ToString(),
                senderId = senderUserId,
                content = string.Empty,
                timestamp = DateTime.UtcNow,
                roomId = session.RoomId,
                MediaType = session.MediaType,
                MediaUrl = relativeUrl,
                MediaThumbUrl = thumbUrl,
                MediaName = session.FileName,
                MediaSize = session.TotalSize
            };
            _db.Messages.Add(msg);
            await _db.SaveChangesAsync();

            // 3. 广播媒体消息给同房间成员（复用现有 WebSocket 分发）
            await BroadcastMediaMessage(session, relativeUrl, thumbUrl, msg.id);

            // 4. 回 DONE（携带媒体元数据 + 消息 Id）给发送者，前端据此渲染/撤回
            var donePayload = JsonSerializer.Serialize(new
            {
                messageId = msg.id,
                mediaUrl = relativeUrl,
                mediaThumbUrl = thumbUrl,
                mediaName = session.FileName,
                mediaSize = session.TotalSize,
                mediaType = session.MediaType
            });
            var done = FileTransferCodec.Encode(FileTransferOp.Done, session.SessionId, 0, Encoding.UTF8.GetBytes(donePayload));
            await socket.SendFrameAsync(done, Opcode.Binary);

            _logger.LogInformation("[FTS] 会话 {SessionId} 完成，媒体已入库并广播", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FTS] 会话 {SessionId} 处理失败", session.SessionId);
            await SendError(socket, "服务器保存文件失败");
        }
        finally
        {
            _sessionManager.RemoveSession(frame.SessionId);
        }
    }

    /// <summary>向群/私聊房间广播媒体消息（与文本消息一致的 JSON 结构）</summary>
    private async Task BroadcastMediaMessage(FileTransferSession session, string mediaUrl, string thumbUrl, string messageId)
    {
        var sender = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == session.OwnerUserId);
        var dto = JsonSerializer.Serialize(new
        {
            Id = messageId,
            Type = session.MediaType,   // image / video / audio / file
            SenderId = session.OwnerUserId,
            SenderNickname = sender?.Nickname ?? "未知",
            Content = string.Empty,
            MediaType = session.MediaType,
            MediaUrl = mediaUrl,
            MediaThumbUrl = thumbUrl,
            MediaName = session.FileName,
            MediaSize = session.TotalSize,
            Timestamp = DateTime.UtcNow,
            TargetUserId = session.RoomId
        });

        // 私聊：发给双方；否则按群成员广播
        if (session.RoomId.StartsWith("private_"))
        {
            var parts = session.RoomId.Replace("private_", "").Split('_');
            await _wsHandler.SendToUser(session.OwnerUserId, dto);
            if (parts.Length > 1) await _wsHandler.SendToUser(parts[1], dto);
        }
        else
        {
            var memberIds = await _db.UserGroups
                .Where(ug => ug.GroupId == session.RoomId)
                .Select(ug => ug.UserId)
                .ToListAsync();
            foreach (var mid in memberIds)
                await _wsHandler.SendToUser(mid, dto);
        }
    }

    private async Task SendError(MyWebSocketServer socket, string message)
    {
        var frame = FileTransferCodec.Encode(FileTransferOp.Error, 0, 0, Encoding.UTF8.GetBytes(message));
        await socket.SendFrameAsync(frame, Opcode.Binary);
    }
}
