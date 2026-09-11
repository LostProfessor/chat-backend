
using System.ComponentModel.DataAnnotations;

namespace ChattingWebsite.DTOs
{
    public class NicknameConfigDto
    {
        [Required]   
        public string NewNickname { get; set; }
    }
}
