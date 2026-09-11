using System.ComponentModel.DataAnnotations;

namespace ChattingWebsite.Model
{
    /// <summary>
    /// 群组角色枚举
    /// </summary>
    public enum GroupRole
    {
        Owner = 0,   // 群主（有且仅有一个）
        Admin = 1,   // 管理员（可多个）
        Member = 2   // 普通成员
    }

    /// <summary>
    /// 群组实体
    /// 公开频道（全服大厅）也是一个群，Id 固定为 "public"，IsDefault = true
    /// </summary>
    public class Group
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string Name { get; set; }

        [Required]
        public string CreatorId { get; set; }

        /// <summary>是否允许普通成员修改群名</summary>
        public bool AllowMemberEditName { get; set; } = false;

        /// <summary>新成员加入时可查看的历史消息条数（0 = 不可查看，默认 50）</summary>
        public int HistoryMessageCount { get; set; } = 50;

        /// <summary>群公告内容</summary>
        public string? Announcement { get; set; }

        /// <summary>群公告最后更新时间</summary>
        public DateTime? AnnouncementUpdatedAt { get; set; }

        /// <summary>是否为默认群（注册时自动加入）</summary>
        public bool IsDefault { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
