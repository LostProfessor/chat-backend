namespace ChattingWebsite.Model
{
    public class Message
    {
        public string id { get; set; }
        public string senderId { get; set; }
        /// <summary>
        /// 消息接收者ID
        /// 群组消息时为 NULL（接收者是整个群）
        /// 私聊消息时为目标用户ID
        /// </summary>
        public string? receiverId { get; set; }
        public string content { get; set; }
        public DateTime timestamp { get; set; } = DateTime.UtcNow;
        public string roomId { get; set; }
        /// <summary>是否已被撤回</summary>
        public bool IsRecalled { get; set; } = false;

        /// <summary>媒体类型（null = 纯文本消息）：image / video / audio / doc</summary>
        public string? MediaType { get; set; }

        /// <summary>媒体文件相对路径（如 /uploads/images/xxx.jpg）</summary>
        public string? MediaUrl { get; set; }

        /// <summary>缩略图相对路径（P0 暂不生成，预留）</summary>
        public string? MediaThumbUrl { get; set; }

        /// <summary>原始文件名</summary>
        public string? MediaName { get; set; }

        /// <summary>媒体文件字节大小</summary>
        public long? MediaSize { get; set; }
    }
}
