using ChattingWebsite.DB;
using ChattingWebsite.Model;
using ChattingWebsite.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChattingWebsite.Controllers
{
    /// <summary>
    /// 存放好友相关的 API，包括搜索用户、发送好友申请、获取好友列表、处理好友申请等
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class FriendController : ControllerBase
    {
        private readonly ChattingWebsiteDBContext _db;
        private readonly ConnectionManager _connectionManager;

        public FriendController(ChattingWebsiteDBContext db, ConnectionManager connectionManager)
        {
            _db = db;
            _connectionManager = connectionManager;
        }

        /// <summary>
        /// 用户搜索方法：根据昵称模糊搜索用户，并返回前 20 个结果，同时标记在线状态
        /// </summary>
        [HttpGet("search")]
        public async Task<IActionResult> SearchUsers([FromQuery] string nickname)
        {
            if (string.IsNullOrWhiteSpace(nickname))
                return BadRequest("请输入搜索关键词");

            // 先查数据库拿到用户列表，再在内存中判断在线状态（EF Core 无法翻译 IsOnline）
            var users = await _db.Users
                .Where(u => u.Nickname.Contains(nickname))
                .Take(20)
                .ToListAsync();

            var result = users.Select(u => new
            {
                PublicId = u.PublicId,
                u.Nickname,
                Online = _connectionManager.IsOnline(u.PublicId)
            });

            return Ok(result);
        }

        /// <summary>
        /// 发送好友申请：当前用户向指定用户发送好友申请，若已存在关系或申请，则返回错误
        /// </summary>
        [HttpPost("request")]
        public async Task<IActionResult> SendRequest([FromBody] FriendRequestDto dto)
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Unauthorized();

            if (userId == dto.AddresseeId)
                return BadRequest("不能添加自己为好友");

            // 检查是否已存在关系（不管什么状态）
            var exists = await _db.Friendships.AnyAsync(f =>
                (f.RequesterId == userId && f.AddresseeId == dto.AddresseeId) ||
                (f.RequesterId == dto.AddresseeId && f.AddresseeId == userId));

            if (exists)
                return BadRequest("你们已经是好友或已存在待处理的申请");

            var friendship = new Friendship
            {
                RequesterId = userId,
                AddresseeId = dto.AddresseeId,
                Status = FriendshipStatus.Pending,
                RequestedAt = DateTime.UtcNow
            };

            _db.Friendships.Add(friendship);
            await _db.SaveChangesAsync();

            return Ok(new { message = "好友申请已发送", id = friendship.Id });
        }

        /// <summary>
        /// 获取我的好友列表（已接受的）
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetFriends()
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Unauthorized();

            var friends = await _db.Friendships
                .Where(f => f.Status == FriendshipStatus.Accepted &&
                    (f.RequesterId == userId || f.AddresseeId == userId))
                .ToListAsync();

            var result = friends.Select(f =>
            {
                var friendId = f.RequesterId == userId ? f.AddresseeId : f.RequesterId;
                return new
                {
                    f.Id,
                    FriendId = friendId,
                    Nickname = GetCachedNickname(friendId),
                    Online = _connectionManager.IsOnline(friendId)
                };
            });

            return Ok(result);
        }

        /// <summary>
        /// 获取待处理的好友申请（别人发给我的
        /// ）</summary>
        [HttpGet("pending")]
        public async Task<IActionResult> GetPending()
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Unauthorized();

            // 先查数据库，再在内存中映射昵称（EF Core 无法翻译 GetCachedNickname）
            var friendships = await _db.Friendships
                .Where(f => f.AddresseeId == userId && f.Status == FriendshipStatus.Pending)
                .ToListAsync();

            var pending = friendships.Select(f => new
            {
                f.Id,
                f.RequesterId,
                RequesterNickname = GetCachedNickname(f.RequesterId),
                f.RequestedAt
            });

            return Ok(pending);
        }

        /// <summary>接受好友申请</summary>
        [HttpPut("{id}/accept")]
        public async Task<IActionResult> Accept(int id)
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Unauthorized();

            var friendship = await _db.Friendships.FindAsync(id);
            if (friendship == null) return NotFound();
            if (friendship.AddresseeId != userId) return Forbid();
            if (friendship.Status != FriendshipStatus.Pending)
                return BadRequest("该申请已处理");

            friendship.Status = FriendshipStatus.Accepted;
            friendship.RespondedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Ok(new { message = "已添加好友" });
        }

        /// <summary>
        /// 拒绝好友申请
        /// </summary>
        [HttpPut("{id}/reject")]
        public async Task<IActionResult> Reject(int id)
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (userId == null) return Unauthorized();

            var friendship = await _db.Friendships.FindAsync(id);
            if (friendship == null) return NotFound();
            if (friendship.AddresseeId != userId) return Forbid();
            if (friendship.Status != FriendshipStatus.Pending)
                return BadRequest("该申请已处理");

            friendship.Status = FriendshipStatus.Rejected;
            friendship.RespondedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Ok(new { message = "已拒绝" });
        }

        /// <summary>
        /// 从 ConnectionManager 缓存中取昵称（避免反复查数据库）
        /// </summary>
        private string GetCachedNickname(string userId)
        {
            return _connectionManager.GetNickname(userId) ?? "未知用户";
        }
    }

    public class FriendRequestDto
    {
        public string AddresseeId { get; set; }
    }
}
