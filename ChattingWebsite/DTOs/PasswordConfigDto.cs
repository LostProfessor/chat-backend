using System.ComponentModel.DataAnnotations;
namespace ChattingWebsite.DTOs
{
    /// <summary>
    /// 密码修改DTO，用于封装密码修改请求的数据，在接收到密码后直接进行加密。
    /// </summary>
    public class PasswordConfigDto
    {
        [Required]
        public string CurrentPassword { get; set; } 

        [Required]
        public string NewPassword { get; set; }
    }
}
