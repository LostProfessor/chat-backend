using ChattingWebsite.DB;
using ChattingWebsite.Model;
using ChattingWebsite.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace ChattingWebsite.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class GroupController : ControllerBase
    {
        private readonly ChattingWebsiteDBContext _db;
        private readonly ConnectionManager _connectionManager;
        private readonly HandlewebSocketsMidWare _wsHandler;

        public GroupController(ChattingWebsiteDBContext db, ConnectionManager connectionManager, HandlewebSocketsMidWare wsHandler)
        {
            _db = db;
            _connectionManager = connectionManager;
            _wsHandler = wsHandler;
        }

        private string? CurrentUserId =>
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // ====== 获取我的群列表 ======
        /// <summary>
        /// 获取群列表方法：查询当前用户所在的所有群组，并返回群组信息和用户在群中的角色
        /// </summary>
        /// <returns>返回群组列表</returns>
        [HttpGet]
        public async Task<IActionResult> GetMyGroups()
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            var groups = await _db.UserGroups
                .Where(ug => ug.UserId == userId)
                .Join(_db.Groups, ug => ug.GroupId, g => g.Id, (ug, g) => new
                {
                    g.Id, g.Name, g.CreatorId, g.IsDefault,
                    MyRole = ug.Role,
                    g.Announcement
                })
                .ToListAsync();

            return Ok(groups);
        }

        // ====== 创建群组 ======
        /// <summary>
        /// 创建群组方法：当前用户创建一个新的群组，并将自己加入为群主
        /// </summary>
        /// <param name="dto">群组创建信息</param>
        /// <returns>返回创建的群组信息</returns>
        [HttpPost("create")]
        public async Task<IActionResult> Create([FromBody] GroupCreateDto dto)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(dto.Name))
                return BadRequest("群名不能为空");

            var group = new Group
            {
                Id = Guid.NewGuid().ToString(),
                Name = dto.Name.Trim(),
                CreatorId = userId,
                CreatedAt = DateTime.UtcNow
            };
            _db.Groups.Add(group);

            _db.UserGroups.Add(new UserGroup
            {
                UserId = userId,
                GroupId = group.Id,
                Role = GroupRole.Owner,
                JoinedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
            return Ok(new { group.Id, group.Name, group.CreatorId, group.CreatedAt });
        }

        // ====== 搜索群组 ======
        /// <summary>
        /// 搜索群组方法：根据群名关键字搜索群组，返回匹配的群组列表（最多 20 条）
        /// </summary>
        /// <param name="name">群名关键字</param>
        /// <returns>返回匹配的群组列表（最多 20 条）</returns>
        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest("请输入搜索关键词");

            var groups = await _db.Groups
                .Where(g => g.Name.Contains(name))
                .Take(20)
                .Select(g => new { g.Id, g.Name, g.CreatorId, MemberCount = _db.UserGroups.Count(ug => ug.GroupId == g.Id) })
                .ToListAsync();

            return Ok(groups);
        }

        // ====== 加入群组 ======

        [HttpPost("{groupId}/join")]
        public async Task<IActionResult> Join(string groupId)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            var group = await _db.Groups.FindAsync(groupId);
            if (group == null) return NotFound("群组不存在");

            var already = await _db.UserGroups.AnyAsync(ug => ug.UserId == userId && ug.GroupId == groupId);
            if (already) return BadRequest("你已在该群中");

            _db.UserGroups.Add(new UserGroup { UserId = userId, GroupId = groupId, Role = GroupRole.Member });
            await _db.SaveChangesAsync();

            // 广播加入群聊的系统消息给群内所有成员
            var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId);
            var joinMsg = new { Type = "system", Content = $"{user?.Nickname ?? "新成员"} 加入了群聊", GroupId = groupId };
            var msgJson = JsonSerializer.Serialize(joinMsg);

            var memberIds = await _db.UserGroups.Where(ug => ug.GroupId == groupId).Select(ug => ug.UserId).ToListAsync();
            foreach (var mid in memberIds)
                await _wsHandler.SendToUser(mid, msgJson);

            return Ok(new { message = "加入成功", group.Name });
        }

        // ====== 群成员列表 ======
        /// <summary>
        /// 查找群成员方法：根据群组 ID 查询该群的所有成员，并返回成员信息（昵称、角色、是否禁言、在线状态）  
        /// </summary>
        /// <param name="groupId">群组 ID</param>
        /// <returns>返回群成员列表</returns>
        [HttpGet("{groupId}/members")]
        public async Task<IActionResult> Members(string groupId)
        {
            var members = await _db.UserGroups
                .Where(ug => ug.GroupId == groupId)
                .ToListAsync();

            var result = new List<object>();
            foreach (var m in members)
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == m.UserId);
                result.Add(new
                {
                    m.UserId,
                    Nickname = user?.Nickname ?? "未知",
                    m.Role,
                    m.IsMuted,
                    Online = _connectionManager.IsOnline(m.UserId)
                });
            }

            return Ok(result);
        }

        // ====== 修改群名 ======
        /// <summary>
        /// 修改群名方法：根据群组 ID 修改群名，只有群主或允许成员修改群名的情况下，成员才能修改群名
        /// </summary>
        /// <param name="groupId"></param>
        /// <param name="dto"></param>
        /// <returns></returns>
        [HttpPut("{groupId}/name")]
        public async Task<IActionResult> ChangeName(string groupId, [FromBody] GroupNameDto dto)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) return NotFound("群组不存在");

            var member = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == userId && ug.GroupId == groupId);
            if (member == null) return BadRequest("你不在该群中");

            if (!group.AllowMemberEditName && member.Role != GroupRole.Owner)
                return BadRequest("仅群主可修改群名");

            group.Name = dto.Name.Trim();
            await _db.SaveChangesAsync();
            return Ok(new { message = "群名已修改", group.Name });
        }

        // ====== 群公告 ======
        /// <summary>
        /// 群公告方法：获取群公告内容和更新时间，只有群主或管理员才能修改公告
        /// </summary>
        /// <param name="groupId"></param>
        /// <returns></returns>
        [HttpGet("{groupId}/announcement")]
        public async Task<IActionResult> GetAnnouncement(string groupId)
        {
            var group = await _db.Groups.FindAsync(groupId);
            if (group == null) return NotFound();
            return Ok(new { group.Announcement, group.AnnouncementUpdatedAt });
        }
        /// <summary>
        /// 创建群公告方法：根据群组 ID 创建或修改群公告，只有群主或管理员才能修改公告
        /// </summary>
        /// <param name="groupId"></param>
        /// <param name="dto"></param>
        /// <returns></returns>
        [HttpPut("{groupId}/announcement")]
        public async Task<IActionResult> SetAnnouncement(string groupId, [FromBody] AnnouncementDto dto)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null) return NotFound();

            var member = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == userId && ug.GroupId == groupId);
            if (member == null || (member.Role != GroupRole.Owner && member.Role != GroupRole.Admin))
                return BadRequest("仅群主/管理员可发布公告");

            group.Announcement = dto.Content;
            group.AnnouncementUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { message = "公告已更新", group.Announcement });
        }

        // ====== 群设置 ======
        /// <summary>
        /// 群设置：获取群设置内容，只有群主才能修改设置
        /// </summary>
        /// <param name="groupId">群组 ID</param>
        /// <param name="dto">群设置 DTO</param>
        /// <returns>返回操作结果</returns>
        [HttpPut("{groupId}/settings")]
        public async Task<IActionResult> Settings(string groupId, [FromBody] GroupSettingsDto dto)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            var member = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == userId && ug.GroupId == groupId);
            if (member == null || member.Role != GroupRole.Owner)
                return BadRequest("仅群主可修改设置");

            var group = await _db.Groups.FindAsync(groupId);
            if (group == null) return NotFound();
            group.AllowMemberEditName = dto.AllowMemberEditName;
            group.HistoryMessageCount = dto.HistoryMessageCount;
            await _db.SaveChangesAsync();
            return Ok(new { message = "设置已更新" });
        }

        // ====== 修改成员角色 ======
        /// <summary>
        /// 修改成员权限方法：根据群组 ID 和目标用户 ID 修改成员角色，只有群主才能修改成员角色
        /// </summary>
        /// <param name="groupId"></param>
        /// <param name="targetUserId">目标用户 ID</param>
        /// <param name="dto">角色 DTO</param>
        /// <returns>返回操作结果</returns>
        [HttpPut("{groupId}/member/{targetUserId}/role")]
        public async Task<IActionResult> ChangeRole(string groupId, string targetUserId, [FromBody] RoleDto dto)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            var myMembership = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == userId && ug.GroupId == groupId);
            if (myMembership == null || myMembership.Role != GroupRole.Owner)
                return BadRequest("仅群主可修改成员角色");

            var target = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == targetUserId && ug.GroupId == groupId);
            if (target == null) return NotFound("该用户不在群中");
            if (targetUserId == userId) return BadRequest("不能修改自己的角色");

            target.Role = dto.Role;
            await _db.SaveChangesAsync();
            return Ok(new { message = "角色已修改" });
        }

        // ====== 禁言/解除 ======
        /// <summary>
        /// 禁言方法：根据群组 ID 和目标用户 ID 禁言成员，只有群主或管理员才能禁言成员，系统管理员可在全服大厅中禁言任何人
        /// </summary>
        /// <param name="groupId">群组 ID</param>
        /// <param name="targetUserId">目标用户 ID</param>
        /// <returns>返回操作结果</returns>
        [HttpPut("{groupId}/member/{targetUserId}/mute")]
        public async Task<IActionResult> MuteMember(string groupId, string targetUserId)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            // 系统管理员可在全服大厅中禁言任何人
            bool isSystemAdmin = User.IsInRole("Admin");

            var myMembership = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == userId && ug.GroupId == groupId);
            if (!isSystemAdmin && (myMembership == null || (myMembership.Role != GroupRole.Owner && myMembership.Role != GroupRole.Admin)))
                return BadRequest("仅群主/管理员可禁言");

            var target = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == targetUserId && ug.GroupId == groupId);
            if (target == null) return NotFound();
            if (!isSystemAdmin && (target.Role == GroupRole.Owner || target.Role == GroupRole.Admin)) return BadRequest("不能禁言群主或管理员");

            target.IsMuted = true;
            await _db.SaveChangesAsync();
            return Ok(new { message = "已禁言" });
        }
        /// <summary>
        /// 解除禁言方法：根据群组 ID 和目标用户 ID 解除禁言成员，只有群主或管理员才能解除禁言成员，系统管理员可在全服大厅中解除禁言任何人
        /// </summary>
        /// <param name="groupId"></param>
        /// <param name="targetUserId"></param>
        /// <returns></returns>
        [HttpPut("{groupId}/member/{targetUserId}/unmute")]
        public async Task<IActionResult> UnmuteMember(string groupId, string targetUserId)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            // 系统管理员可在全服大厅中解除禁言
            bool isSystemAdmin = User.IsInRole("Admin");

            var myMembership = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == userId && ug.GroupId == groupId);
            if (!isSystemAdmin && (myMembership == null || (myMembership.Role != GroupRole.Owner && myMembership.Role != GroupRole.Admin)))
                return BadRequest("仅群主/管理员可解除禁言");

            var target = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == targetUserId && ug.GroupId == groupId);
            if (target == null) return NotFound();
            target.IsMuted = false;
            await _db.SaveChangesAsync();
            return Ok(new { message = "已解除禁言" });
        }

        // ====== 移除成员 ======

        [HttpDelete("{groupId}/member/{targetUserId}")]
        public async Task<IActionResult> RemoveMember(string groupId, string targetUserId)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            var myMembership = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == userId && ug.GroupId == groupId);
            if (myMembership == null) return BadRequest("你不在该群中");

            var target = await _db.UserGroups.FirstOrDefaultAsync(ug => ug.UserId == targetUserId && ug.GroupId == groupId);
            if (target == null) return NotFound("该用户不在群中");

            // Admin 不能踢 Owner，Admin 可以踢 Admin 和 Member
            if (target.Role == GroupRole.Owner) return BadRequest("不能移除群主");
            if (myMembership.Role == GroupRole.Member) return BadRequest("无权限");

            _db.UserGroups.Remove(target);
            await _db.SaveChangesAsync();
            return Ok(new { message = "已移除" });
        }

        // ====== 历史消息 ======

        /// <summary>
        /// 获取群历史消息（分页），count 默认 100，before 为 ISO 时间戳字符串
        /// </summary>
        [HttpGet("{groupId}/history")]
        public async Task<IActionResult> GetHistory(string groupId, [FromQuery] int count = 100, [FromQuery] string? before = null)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            Console.WriteLine($"[HISTORY] groupId={groupId} userId={userId} count={count} before={before}");

            var isMember = await _db.UserGroups.AnyAsync(ug => ug.UserId == userId && ug.GroupId == groupId);
            if (!isMember)
            {
                Console.WriteLine($"[HISTORY] 用户不在群中");
                return BadRequest("你不在该群中");
            }

            var query = _db.Messages.Where(m => m.roomId == groupId);

            if (!string.IsNullOrEmpty(before) && DateTime.TryParse(before, null, System.Globalization.DateTimeStyles.RoundtripKind, out var beforeTime))
                query = query.Where(m => m.timestamp < beforeTime);

            var messages = await query
                .OrderByDescending(m => m.timestamp)
                .Take(Math.Min(count, 200))
                .OrderBy(m => m.timestamp)
                .ToListAsync();

            Console.WriteLine($"[HISTORY] 返回 {messages.Count} 条消息");

            var result = new List<object>();
            foreach (var m in messages)
            {
                var sender = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == m.senderId);
                result.Add(new
                {
                    Id = m.id,
                    Type = "group",
                    SenderId = m.senderId,
                    SenderNickname = sender?.Nickname ?? "未知",
                    Content = m.content,
                    Timestamp = m.timestamp,
                    AvatarUrl = string.IsNullOrEmpty(sender?.AvatarPath) ? null : $"/{sender.AvatarPath}",
                    TargetUserId = groupId,
                    MediaType = m.MediaType,
                    MediaUrl = m.MediaUrl,
                    MediaThumbUrl = m.MediaThumbUrl,
                    MediaName = m.MediaName,
                    MediaSize = m.MediaSize,
                    // ★ 撤回标记：不返回的话前端刷新后就不知道这条已被撤回，
                    //   文本会原样复现，媒体消息还会因为磁盘文件已删而变成破图。
                    IsRecalled = m.IsRecalled
                });
            }

            return Ok(result);
        }
    }

    public class GroupCreateDto { public string Name { get; set; } }
    public class GroupNameDto { public string Name { get; set; } }
    public class AnnouncementDto { public string Content { get; set; } }
    public class GroupSettingsDto { public bool AllowMemberEditName { get; set; } public int HistoryMessageCount { get; set; } }
    public class RoleDto { public GroupRole Role { get; set; } }
}
