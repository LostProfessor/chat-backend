using System.ComponentModel.DataAnnotations;

namespace ChattingWebsite.DTOs
{
    public class RefreshDto
    {
        [Required(ErrorMessage = "刷新令牌不能为空")]
        public string RefreshToken { get; set; }
    }
}
