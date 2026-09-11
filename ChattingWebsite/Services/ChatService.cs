
using ChattingWebsite.DB;
using ChattingWebsite.DTOs;
using ChattingWebsite.Model;
using Microsoft.EntityFrameworkCore;
using System;

namespace ChattingWebsite.Services;

/// <summary>
/// 聊天业务服务：负责消息存储、准备广播数据（不直接发送）
/// </summary>
public class ChatService
{
    private readonly ChattingWebsiteDBContext _dbContext;
    private readonly ILogger<ChatService> _logger;

    public ChatService(ChattingWebsiteDBContext dbContext, ILogger<ChatService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// 保存公共聊天消息，返回广播用的 DTO
    /// </summary>
    public async Task<ChatMessageDto> SavePublicMessage(string senderUserId, string content)
    {
        var message = new Message
        {
            id = Guid.NewGuid().ToString(),
            senderId = senderUserId,
            content = content,
            timestamp = DateTime.UtcNow,
            roomId = "public"
        };
        _dbContext.Messages.Add(message);
        await _dbContext.SaveChangesAsync();

        // 获取发送者昵称和头像
        var (senderNickname, senderAvatar) = await GetUserInfo(senderUserId);

        return new ChatMessageDto
        {
            Id = message.id,
            Type = "chat",
            SenderId = senderUserId,
            SenderNickname = senderNickname,
            Content = content,
            Timestamp = message.timestamp,
            AvatarUrl = string.IsNullOrEmpty(senderAvatar) ? null : $"/{senderAvatar}"
        };
    }

    /// <summary>
    /// 保存私聊消息，返回两个 DTO（分别发给发送者和接收者）
    /// </summary>
    public async Task<(ChatMessageDto ToSender, ChatMessageDto ToTarget)> SavePrivateMessage(
        string senderUserId, string targetUserId, string content)
    {
        // 私聊 roomId 用两个用户ID排序后拼接，保证 A↔B 的对话存在同一个房间
        var ids = new[] { senderUserId, targetUserId }.OrderBy(x => x).ToArray();
        var message = new Message
        {
            id = Guid.NewGuid().ToString(),
            senderId = senderUserId,
            content = content,
            timestamp = DateTime.UtcNow,
            roomId = $"private_{ids[0]}_{ids[1]}"
        };
        _dbContext.Messages.Add(message);
        await _dbContext.SaveChangesAsync();

        var (senderNickname, senderAvatar) = await GetUserInfo(senderUserId);
        var (targetNickname, targetAvatar) = await GetUserInfo(targetUserId);

        var dtoForSender = new ChatMessageDto
        {
            Id = message.id,
            Type = "private",
            SenderId = senderUserId,
            SenderNickname = senderNickname,
            Content = content,
            Timestamp = message.timestamp,
            AvatarUrl = string.IsNullOrEmpty(senderAvatar) ? null : $"/{senderAvatar}",
            TargetUserId = targetUserId,
            TargetNickname = targetNickname
        };

        var dtoForTarget = new ChatMessageDto
        {
            Id = message.id,
            Type = "private",
            SenderId = senderUserId,
            SenderNickname = senderNickname,
            Content = content,
            Timestamp = message.timestamp,
            AvatarUrl = string.IsNullOrEmpty(senderAvatar) ? null : $"/{senderAvatar}",
            TargetUserId = senderUserId,
            TargetNickname = senderNickname
        };

        return (dtoForSender, dtoForTarget);
    }

    private async Task<(string Nickname, string? AvatarPath)> GetUserInfo(string publicId)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.PublicId == publicId);
        return (user?.Nickname ?? "未知用户", user?.AvatarPath);
    }

    /// <summary>
    /// 保存群组消息（通用，支持任意群组，包括公开频道）
    /// </summary>
    /// <param name="senderUserId">发送者 ID</param>
    /// <param name="groupId">群组 ID（公开频道为 "public"）</param>
    /// <param name="content">消息内容</param>
    /// <returns>带有 groupId 的 ChatMessageDto，前端据此筛选当前群的消息</returns>
    public async Task<ChatMessageDto> SaveGroupMessage(string senderUserId, string groupId, string content)
    {
        var message = new Message
        {
            id = Guid.NewGuid().ToString(),
            senderId = senderUserId,
            content = content,
            timestamp = DateTime.UtcNow,
            roomId = groupId       // 直接用群 ID 作为 roomId，不再硬编码 "public"
        };
        _dbContext.Messages.Add(message);
        await _dbContext.SaveChangesAsync();

        var (senderNickname, senderAvatar) = await GetUserInfo(senderUserId);

        return new ChatMessageDto
        {
            Id = message.id,
            Type = "group",
            SenderId = senderUserId,
            SenderNickname = senderNickname,
            Content = content,
            Timestamp = message.timestamp,
            AvatarUrl = string.IsNullOrEmpty(senderAvatar) ? null : $"/{senderAvatar}",
            TargetUserId = groupId   // 复用 TargetUserId 字段携带 groupId 给前端
        };
    }
}