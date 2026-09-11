using System.ComponentModel.DataAnnotations;

namespace ChattingWebsite.DTOs
{
    public class LoginDto
    {
        [Required(ErrorMessage ="昵称不能为空")]
        public string Nickname { get; set; }
        [EmailAddress(ErrorMessage = "邮箱地址无效")]
        [Required(ErrorMessage = "邮箱地址不能为空")]
        public string Email { get; set; }
        [Required(ErrorMessage = "密码不能为空")]
        public string Password { get; set; }
    }
}
