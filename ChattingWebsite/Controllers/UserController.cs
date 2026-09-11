using ChattingWebsite.DB;
using ChattingWebsite.DTOs;
using ChattingWebsite.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ChattingWebsite.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly ChattingWebsiteDBContext _db;
        private readonly ConnectionManager _connectionManager;

        // 头像保存目录（相对于 wwwroot）
        private const string AvatarDir = "uploads/avatars";
        // 允许的图片类型
        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

        public UserController(ChattingWebsiteDBContext db, ConnectionManager connectionManager)
        {
            _db = db;
            _connectionManager = connectionManager;
        }

        private string? GetCurrentUserId()
        {
            return User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }

        /// <summary>
        /// 上传头像（覆盖旧文件，路径按 PublicId 命名）
        /// </summary>
        [HttpPost("avatar")]
        [RequestSizeLimit(3 * 1024 * 1024)]  // 最大 3MB
        public async Task<IActionResult> UploadAvatar(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("请选择图片");

            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            // 校验格式
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return BadRequest($"仅支持 {string.Join(", ", AllowedExtensions)} 格式");

            if (file.Length > 3 * 1024 * 1024)
                return BadRequest("文件不能超过 3MB");

            // 确保目录存在
            var wwwroot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var avatarFolder = Path.Combine(wwwroot, AvatarDir);
            Directory.CreateDirectory(avatarFolder);

            // 存为 {publicId}{ext}
            var fileName = $"{userId}{ext}";
            var filePath = Path.Combine(avatarFolder, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
                await file.CopyToAsync(stream);

            // 更新数据库
            var relativePath = $"{AvatarDir}/{fileName}";
            var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId);
            if (user != null)
            {
                user.AvatarPath = relativePath;
                await _db.SaveChangesAsync();
            }

            return Ok(new { avatarUrl = $"/{relativePath}", message = "头像上传成功" });
        }

        /// <summary>
        /// 删除头像，恢复默认
        /// </summary>
        [HttpDelete("avatar")]
        public async Task<IActionResult> DeleteAvatar()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId);
            if (user == null) return NotFound();

            // 删除磁盘文件
            if (!string.IsNullOrEmpty(user.AvatarPath))
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", user.AvatarPath);
                if (System.IO.File.Exists(filePath))
                    System.IO.File.Delete(filePath);
            }

            // 清空数据库
            user.AvatarPath = null;
            await _db.SaveChangesAsync();

            return Ok(new { message = "已恢复默认头像" });
        }

        /// <summary>
        /// 获取当前用户个人信息
        /// </summary>
        [HttpGet("profile")]
        public async Task<IActionResult> GetProfile()
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId);
            if (user == null) return NotFound();

            return Ok(new
            {
                user.PublicId,
                user.Nickname,
                user.Email,
                user.IsAdmin,
                user.CreateTime,
                AvatarUrl = string.IsNullOrEmpty(user.AvatarPath) ? null : $"/{user.AvatarPath}"
            });
        }

        /// <summary>
        /// 修改昵称
        /// </summary>
        [HttpPost("nickname")]
        public async Task<IActionResult> ChangeNickname([FromBody] NicknameConfigDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            // 查重：排除自己（用 PublicId）
            bool duplicate = await _db.Users.AnyAsync(u =>
                u.Nickname == dto.NewNickname && u.PublicId != userId);
            if (duplicate)
                return BadRequest("昵称已被占用");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId);
            if (user == null) return NotFound("用户不存在");

            user.Nickname = dto.NewNickname;
            await _db.SaveChangesAsync();

            return Ok(new { message = "昵称修改成功", nickname = dto.NewNickname });
        }

        /// <summary>
        /// 修改邮箱
        /// </summary>
        [HttpPost("email")]
        public async Task<IActionResult> ChangeEmail([FromBody] EmailConfigDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            bool duplicate = await _db.Users.AnyAsync(u =>
                u.Email == dto.NewEmail && u.PublicId != userId);
            if (duplicate)
                return BadRequest("邮箱已被占用");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId);
            if (user == null) return NotFound("用户不存在");

            user.Email = dto.NewEmail;
            await _db.SaveChangesAsync();

            return Ok(new { message = "邮箱修改成功", email = dto.NewEmail });
        }

        /// <summary>
        /// 修改密码
        /// </summary>
        [HttpPost("password")]
        public async Task<IActionResult> ChangePassword([FromBody] PasswordConfigDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (string.IsNullOrEmpty(dto.NewPassword) || dto.NewPassword.Length < 8)
                return BadRequest("新密码长度不能少于8个字符");

            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId);
            if (user == null) return NotFound("用户不存在");

            if (!PasswordHasher.VerifyPassword(dto.CurrentPassword, user.Password))
                return BadRequest("当前密码错误");

            user.Password = PasswordHasher.HashPassword(dto.NewPassword);
            await _db.SaveChangesAsync();

            return Ok(new { message = "密码修改成功" });
        }
    }
}
