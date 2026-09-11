using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ChattingWebsite.Model
{
    public class User
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        //内部自查ID，按用户数自增，此值不往前端暴露，前端使用PublicId进行用户识别
        public int Id { get; set; }
        [Required]
        //因为有两套ID系统，所以本系统没有也不必使用雪花算法等，Guid作为PublicId足矣
        public string PublicId { get; set; } = Guid.NewGuid().ToString();
        /// <summary>
        /// 用户名，前端判定不可为空
        /// </summary>
        public string Nickname { get; set; }
        /// <summary>
        /// 邮箱，符合对应格式且由前端判定不可为空
        /// </summary>
        [EmailAddress]
        public string Email { get; set; }
        /// <summary>
        /// 密码
        /// </summary>
        public string Password { get; set; }
        /// <summary>
        /// 是否是管理员（超级用户），拥有所有权限，默认 false
        /// </summary>
        public bool IsAdmin { get; set; }
        /// <summary>
        /// 用户创建时间
        /// </summary>
        public DateTime CreateTime { get; set; }

        /// <summary>
        /// 头像路径（null = 默认头像），如 "uploads/avatars/{publicId}.jpg"
        /// </summary>
        public string? AvatarPath { get; set; }
        ///<summary>
        ///刷新令牌
        ///</summary>
        public string? RefreshToken { get; set; }
        ///<summary>
        ///刷新令牌过期时间
        ///</summary>  
        public DateTime? RefreshTokenExpires { get; set; }
    }
}
