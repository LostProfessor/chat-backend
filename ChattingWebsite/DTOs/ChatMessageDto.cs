namespace ChattingWebsite.DTOs
{
    public class ChatMessageDto
    {
        public string Id { get; set; }            // 消息主键（后端 Message.id，供前端撤回使用）
        public string Type { get; set; }          // "chat" 或 "private"
        public string SenderId { get; set; }
        public string SenderNickname { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public string? AvatarUrl { get; set; }       // 发送者头像（相对路径，如 /uploads/avatars/xxx.jpg）
        public string? TargetUserId { get; set; }   // 仅私聊时有
        public string? TargetNickname { get; set; }
    }
}
