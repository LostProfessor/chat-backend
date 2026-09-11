using System.ComponentModel.DataAnnotations;

namespace ChattingWebsite.Model
{
    /// <summary>
    /// 用户-群组关联表
    /// 记录用户加入了哪些群组及其角色
    /// </summary>
    public class UserGroup
    {
        /// <summary>关联ID（自增主键）</summary>
        [Key]
        public int Id { get; set; }

        /// <summary>用户ID（对应 User.Id）</summary>
        [Required]
        public string UserId { get; set; }

        /// <summary>群组ID（对应 Group.Id）</summary>
        [Required]
        public string GroupId { get; set; }

        /// <summary>用户在此群中的角色：群主 / 管理员 / 普通成员</summary>
        [Required]
        public GroupRole Role { get; set; } = GroupRole.Member;

        /// <summary>加入时间</summary>
        public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

        /// <summary>是否被禁言</summary>
        public bool IsMuted { get; set; } = false;
    }
}
