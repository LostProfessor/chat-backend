using ChattingWebsite.DB;
using ChattingWebsite.Model;
using ChattingWebsite.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ChattingWebsite.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly ChattingWebsiteDBContext _db;
        private readonly HandlewebSocketsMidWare _wsHandler;

        public AdminController(ChattingWebsiteDBContext db, HandlewebSocketsMidWare wsHandler)
        {
            _db = db;
            _wsHandler = wsHandler;
        }

        // ====== 用户管理 ======
        /// <summary>
        /// 用户获取方法：根据可选的搜索参数获取用户列表
        /// </summary>
        /// <param name="search">可选的搜索关键字，用于匹配昵称或邮箱</param>
        /// <returns>返回用户列表</returns>
        [HttpGet("users")]
        public async Task<IActionResult> GetUsers([FromQuery] string? search)
        {
            var query = _db.Users.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(u => u.Nickname.Contains(search) || u.Email.Contains(search));

            var users = await query.Select(u => new { u.PublicId, u.Nickname, u.Email, u.IsAdmin, u.CreateTime })
                .OrderByDescending(u => u.CreateTime).Take(100).ToListAsync();
            return Ok(users);
        }
        /// <summary>
        /// 用户删除方法：根据用户的 PublicId 删除用户及其相关数据（如群组关系、好友关系等）
        /// </summary>
        /// <param name="publicId"></param>
        /// <returns></returns>
        [HttpDelete("users/{publicId}")]
        public async Task<IActionResult> DeleteUser(string publicId)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == publicId);
            if (user == null) return NotFound();
            if (user.IsAdmin) return BadRequest("不能删除管理员");

            _db.UserGroups.RemoveRange(_db.UserGroups.Where(ug => ug.UserId == publicId));
            _db.Friendships.RemoveRange(_db.Friendships.Where(f => f.RequesterId == publicId || f.AddresseeId == publicId));
            _db.Users.Remove(user);
            await _db.SaveChangesAsync();
            return Ok(new { message = "用户已删除" });
        }

        // ====== 群组管理 ======
        /// <summary>
        /// 群搜索方法：获取所有群组及其成员数量，按创建时间降序排列
        /// </summary>
        /// <returns>返回群组列表</returns>
        [HttpGet("groups")]
        public async Task<IActionResult> GetGroups()
        {
            var groups = await _db.Groups.Select(g => new { g.Id, g.Name, g.CreatorId, g.CreatedAt, g.IsDefault })
                .OrderByDescending(g => g.CreatedAt).ToListAsync();

            var result = new List<object>();
            foreach (var g in groups)
            {
                var count = await _db.UserGroups.CountAsync(ug => ug.GroupId == g.Id);
                result.Add(new { g.Id, g.Name, g.CreatorId, g.CreatedAt, g.IsDefault, MemberCount = count });
            }
            return Ok(result);
        }
        /// <summary>
        /// 删除群组方法：根据群组 ID 删除群组及其相关数据（如成员关系、消息记录等），并广播系统消息通知所有用户
        /// </summary>
        /// <param name="groupId"></param>
        /// <returns></returns>
        [HttpDelete("groups/{groupId}")]
        public async Task<IActionResult> DeleteGroup(string groupId)
        {
            var group = await _db.Groups.FindAsync(groupId);
            if (group == null) return NotFound();
            if (group.IsDefault) return BadRequest("不能删除默认群组");

            _db.UserGroups.RemoveRange(_db.UserGroups.Where(ug => ug.GroupId == groupId));
            _db.Messages.RemoveRange(_db.Messages.Where(m => m.roomId == groupId));
            _db.Groups.Remove(group);
            await _db.SaveChangesAsync();

            var sysMsg = JsonSerializer.Serialize(new { Type = "system", Content = "该群已被系统管理员关闭" });
            await _wsHandler.BroadcastToAll(sysMsg);

            return Ok(new { message = "群组已关闭" });
        }

        // ====== 全服公告 ======

        /// <summary>
        /// 获取全服公告方法：获取全服公告内容及更新时间
        /// </summary>
        /// <returns>返回全服公告内容及更新时间</returns>
        [HttpGet("announcement")]
        public async Task<IActionResult> GetGlobalAnnouncement()
        {
            var group = await _db.Groups.FindAsync("public");
            if (group == null) return NotFound();
            return Ok(new[]
            {
                new { content = group.Announcement ?? "", updatedAt = group.AnnouncementUpdatedAt }
            });
        }
        /// <summary>
        /// 发布全服公告方法：设置全服公告内容，并更新公告时间
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [HttpPut("announcement")]
        public async Task<IActionResult> SetGlobalAnnouncement([FromBody] AnnouncementDto dto)
        {
            var group = await _db.Groups.FindAsync("public");
            if (group == null) return NotFound();
            group.Announcement = dto.Content;
            group.AnnouncementUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { message = "全服公告已发布" });
        }

        // ====== 系统统计 ======
        /// <summary>
        /// 数据统计方法：获取用户总数、消息总数、今日新增用户数、今日新增消息数，以及最近 7 天每日新增用户和消息数据
        /// </summary>
        /// <returns>返回系统统计数据</returns>
        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            var now = DateTime.UtcNow;
            var today = now.Date;

            var totalUsers = await _db.Users.CountAsync();
            var totalMessages = await _db.Messages.CountAsync();
            var todayNewUsers = await _db.Users.CountAsync(u => u.CreateTime >= today);
            var todayNewMessages = await _db.Messages.CountAsync(m => m.timestamp >= today);

            // 最近 7 天每日数据（用于图表）
            var dailyData = new List<object>();
            for (int i = 6; i >= 0; i--)
            {
                var day = today.AddDays(-i);
                var next = day.AddDays(1);
                var newUsers = await _db.Users.CountAsync(u => u.CreateTime >= day && u.CreateTime < next);
                var newMessages = await _db.Messages.CountAsync(m => m.timestamp >= day && m.timestamp < next);
                dailyData.Add(new { Date = day.ToString("MM-dd"), NewUsers = newUsers, NewMessages = newMessages });
            }

            return Ok(new
            {
                TotalUsers = totalUsers,
                TotalMessages = totalMessages,
                TodayNewUsers = todayNewUsers,
                TodayNewMessages = todayNewMessages,
                DailyData = dailyData
            });
        }

        // ====== 重置用户密码 ======
        /// <summary>
        /// 重置用户密码方法：管理员根据用户的 PublicId 重置密码为默认临时密码，并返回新密码给管理员
        /// </summary>
        /// <param name="publicId"></param>
        /// <returns></returns>
        [HttpPost("users/{publicId}/reset-password")]
        public async Task<IActionResult> ResetPassword(string publicId)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == publicId);
            if (user == null) return NotFound("用户不存在");
            if (user.IsAdmin) return BadRequest("不能重置管理员密码");

            var newPwd = "chat123456"; // 默认临时密码，用户登录后自行修改
            user.Password = BCrypt.Net.BCrypt.HashPassword(newPwd);
            await _db.SaveChangesAsync();

            return Ok(new { message = "密码已重置", newPassword = newPwd, nickname = user.Nickname });
        }
    }
}
