using ChattingWebsite.DB;
using ChattingWebsite.DTOs;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace ChattingWebsite.Services;

/// <summary>
/// 消息分发器：解析 JSON，根据 type 调用对应业务服务
/// </summary>
public class MessageDispatcher
{
    private readonly ChattingWebsiteDBContext _dbContext;
    private readonly ChatService _chatService;
    private readonly HandlewebSocketsMidWare _webSocketHandler;
    private readonly ILogger<MessageDispatcher> _logger;

    public MessageDispatcher(
        ChattingWebsiteDBContext dbContext,
        ChatService chatService,
        HandlewebSocketsMidWare webSocketHandler,
        ILogger<MessageDispatcher> logger)
    {
        _dbContext = dbContext;
        _chatService = chatService;
        _webSocketHandler = webSocketHandler;
        _logger = logger;
    }

    public async Task Dispatch(string senderUserId, string messageJson, MyWebSocketServer senderSocket)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
            {
                await SendError(senderSocket, "缺少 type 字段");
                return;
            }

            var type = typeProp.GetString();
            switch (type)
            {
                case "chat":
                    // 向后兼容：旧版 type="chat" 自动映射为公开频道群组消息
                    {
                        var content = root.GetProperty("content").GetString();
                        if (string.IsNullOrEmpty(content))
                        {
                            await SendError(senderSocket, "消息内容不能为空");
                            return;
                        }
                        await HandleGroupMessage(senderUserId, "public", content);
                    }
                    break;

                case "group":
                    // 新版群组消息：需附带 groupId
                    {
                        var groupId = root.GetProperty("groupId").GetString();
                        var content = root.GetProperty("content").GetString();
                        if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(content))
                        {
                            await SendError(senderSocket, "群组消息需要 groupId 和 content");
                            return;
                        }
                        await HandleGroupMessage(senderUserId, groupId, content);
                    }
                    break;

                case "private":
                    var targetUserId = root.GetProperty("to").GetString();
                    var privateContent = root.GetProperty("content").GetString();
                    if (string.IsNullOrEmpty(targetUserId) || string.IsNullOrEmpty(privateContent))
                    {
                        await SendError(senderSocket, "私聊参数缺失");
                        return;
                    }
                    await HandlePrivateMessage(senderUserId, targetUserId, privateContent);
                    break;

                case "ping":
                    await SendPong(senderSocket);
                    break;

                default:
                    await SendError(senderSocket, $"未知消息类型: {type}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "无效 JSON，用户 {UserId}", senderUserId);
            await SendError(senderSocket, "无效的 JSON 格式");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理消息异常，用户 {UserId}", senderUserId);
            await SendError(senderSocket, "服务器内部错误");
        }
    }

    private async Task HandleChatMessage(string senderUserId, string content)
    {
        // 调用 ChatService 保存消息并获取广播数据
        var broadcastDto = await _chatService.SavePublicMessage(senderUserId, content);
        var json = JsonSerializer.Serialize(broadcastDto);
        await _webSocketHandler.BroadcastToAll(json);
    }

    /// <summary>
    /// 处理群组消息
    /// 
    /// 流程：
    /// 1. 校验发送者是否在该群中（查 UserGroups 表）
    /// 2. 调用 ChatService.SaveGroupMessage 存库
    /// 3. 查 UserGroups 表获取该群所有成员 userId
    /// 4. 遍历发送给每个在线成员（不在线的消息已存库，后续可拉历史）
    /// </summary>
    private async Task HandleGroupMessage(string senderUserId, string groupId, string content)
    {
        // --- 校验：发送者必须在该群中 ---
        var isMember = await _dbContext.UserGroups
            .AnyAsync(ug => ug.UserId == senderUserId && ug.GroupId == groupId);
        if (!isMember)
        {
            await SendErrorByUserId(senderUserId, "你不在该群中，无法发送消息");
            return;
        }

        // --- 校验：是否被禁言 ---
        var membership = await _dbContext.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == senderUserId && ug.GroupId == groupId);
        if (membership != null && membership.IsMuted)
        {
            await SendErrorByUserId(senderUserId, "你已被禁言");
            return;
        }

        // --- 保存消息 ---
        var broadcastDto = await _chatService.SaveGroupMessage(senderUserId, groupId, content);
        var json = JsonSerializer.Serialize(broadcastDto);

        // --- 只发给该群成员 ---
        var memberUserIds = await _dbContext.UserGroups
            .Where(ug => ug.GroupId == groupId)
            .Select(ug => ug.UserId)
            .ToListAsync();

        foreach (var memberId in memberUserIds)
        {
            await _webSocketHandler.SendToUser(memberId, json);
        }
    }

    private async Task HandlePrivateMessage(string senderUserId, string targetUserId, string content)
    {
        // 调用 ChatService 保存私聊消息，并分别发给双方
        var (toSenderDto, toTargetDto) = await _chatService.SavePrivateMessage(senderUserId, targetUserId, content);
        var toSenderJson = JsonSerializer.Serialize(toSenderDto);
        var toTargetJson = JsonSerializer.Serialize(toTargetDto);
        await _webSocketHandler.SendToUser(senderUserId, toSenderJson);
        await _webSocketHandler.SendToUser(targetUserId, toTargetJson);
    }

    /// <summary>
    /// 通过 userId 发送错误消息（用于群组消息等非当前 socket 的上下文中）
    /// </summary>
    private async Task SendErrorByUserId(string userId, string errorMessage)
    {
        var errorJson = JsonSerializer.Serialize(new { type = "error", message = errorMessage });
        await _webSocketHandler.SendToUser(userId, errorJson);
    }

    private async Task SendError(MyWebSocketServer socket, string errorMessage)
    {
        if (socket.IsOpen)
        {
            var errorJson = JsonSerializer.Serialize(new { type = "error", message = errorMessage });
            await socket.SendTextAsync(errorJson);
        }
    }

    private async Task SendPong(MyWebSocketServer socket)
    {
        if (socket.IsOpen)
        {
            var pongJson = "{\"type\":\"pong\"}";
            await socket.SendTextAsync(pongJson);
        }
    }
}