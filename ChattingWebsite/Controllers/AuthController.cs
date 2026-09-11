using ChattingWebsite.DB;
using ChattingWebsite.DTOs;
using ChattingWebsite.Model;
using ChattingWebsite.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace ChattingWebsite.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ChattingWebsiteDBContext _db;
        public AuthController(ChattingWebsiteDBContext db, IConfiguration configuration)
        {
            _db = db;
            _configuration = configuration;
        }
        /// <summary>
        /// 注册方法：验证昵称、邮箱和密码，成功则创建用户并返回注册成功信息
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [HttpPost("register")]
        public async Task<IActionResult> Register(RegisterDto dto)
        {
            //判断格式是否合法
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            //昵称和密码不能为空，且昵称不能重复
            if (string.IsNullOrEmpty(dto.Nickname) || string.IsNullOrEmpty(dto.Password)||string.IsNullOrEmpty(dto.Email))
            {
                return BadRequest("昵称、邮箱或密码不能为空");
            }
            if (await _db.Users.AnyAsync(u => u.Email == dto.Email))
            {
                return BadRequest("邮箱已被注册");
            }
            if (await _db.Users.AnyAsync(u => u.Nickname == dto.Nickname))
            {
                return BadRequest("昵称已存在");
            }
            var user = new Model.User
            {
                PublicId = Guid.NewGuid().ToString(),
                Nickname = dto.Nickname,
                Email = dto.Email,
                Password = PasswordHasher.HashPassword(dto.Password),
                IsAdmin = false,
                CreateTime = DateTime.UtcNow,
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            // 注册成功后，自动加入所有默认群组（IsDefault == true）
            // 目前默认群只有"全服大厅"，以后可扩展更多（如技术频道、灌水区等）
            var defaultGroups = await _db.Groups.Where(g => g.IsDefault).ToListAsync();
            foreach (var group in defaultGroups)
            {
                _db.UserGroups.Add(new UserGroup
                {
                    UserId = user.PublicId,  // 用 PublicId，与 JWT 中的 NameIdentifier 一致
                    GroupId = group.Id,
                    Role = GroupRole.Member,
                    JoinedAt = DateTime.UtcNow
                });
            }
            // 只有当有默认群需要加入时才保存
            if (defaultGroups.Count > 0)
            {
                await _db.SaveChangesAsync();
            }

            return Ok(new { message = "注册成功" });
        }
        /// <summary>
        /// 登陆方法：验证昵称、邮箱和密码，成功则返回access token和refresh token，并将refresh token存入数据库
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginDto dto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Nickname == dto.Nickname && u.Email == dto.Email);
            if (user == null || !PasswordHasher.VerifyPassword(dto.Password, user.Password))
            {
                return Unauthorized("昵称、邮箱或密码错误");
            }
            var accessToken = GenerateJwtToken(user);
            var (refreshToken, refreshExpires) = GenerateRefreshToken();
            user.RefreshToken = refreshToken;
            user.RefreshTokenExpires = refreshExpires;
            await _db.SaveChangesAsync();
            return Ok(new { 
                    message = "登录成功",
                    token = accessToken,
                    refreshToken,
                    publicId = user.PublicId,
                    nickname = user.Nickname,
                    isAdmin = user.IsAdmin 
            });
        }
        /// <summary>
        /// 令牌刷新方法，客户端在access token过期后使用refresh token获取新的access token和refresh token
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh(RefreshDto dto)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.RefreshToken == dto.RefreshToken);
            if (user == null || user.RefreshTokenExpires < DateTime.UtcNow)
            {
                return Unauthorized("刷新令牌无效或已过期");
            }
            if(user.RefreshTokenExpires < DateTime.UtcNow)
            {
                return Unauthorized("刷新令牌已过期，请重新登录");
            }
            //轮换：签发全新的access和refresh，原有作废。
            var accessToken = GenerateJwtToken(user);
            var (newRefreshToken, newRefreshExpires) = GenerateRefreshToken();
            user.RefreshToken = newRefreshToken;
            user.RefreshTokenExpires = newRefreshExpires;
            await _db.SaveChangesAsync();
            //返回新的access和refresh
            return Ok(new
            {
                token = accessToken,
                refreshToken = newRefreshToken,
            });
        }
        /// <summary>
        /// JWT生成方法：根据用户信息生成access token，包含用户的PublicId、Email、Nickname和角色（Admin/User）
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        private string GenerateJwtToken(User user)
        {
            var claims = new[]
            {
            new Claim(ClaimTypes.NameIdentifier, user.PublicId),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Nickname),
            new Claim(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User")
        };
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(Convert.ToDouble(_configuration["Jwt:ExpireMinutes"])),
                signingCredentials: creds
            );
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        /// <summary>
        /// 刷新令牌生成方法：生成一个新的随机refresh token和过期时间（30天后）
        /// </summary>
        /// <returns></returns>
        private (string, DateTime) GenerateRefreshToken()
        {
            return (Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
                    DateTime.UtcNow.AddDays(30));
        }
    }
}