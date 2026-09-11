using Microsoft.OpenApi.MicrosoftExtensions;
using System.ComponentModel.DataAnnotations;

namespace ChattingWebsite.DTOs
{
    public class RegisterDto
    {
        [Required(ErrorMessage = "邮箱地址不能为空")]
        [EmailAddress(ErrorMessage = "邮箱地址无效")]
        public string Email { get; set; }
        [Required(ErrorMessage = "昵称不能为空")]
        public string Nickname { get; set; }
        [Required(ErrorMessage = "密码不能为空")]
        [MinLength(8, ErrorMessage = "密码长度不能少于8个字符")]
        public string Password { get; set; }
    }
}
