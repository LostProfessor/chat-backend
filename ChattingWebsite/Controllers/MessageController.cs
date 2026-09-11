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
    /// <summary>
    /// 消息控制器，提供消息相关的 API，包括撤回消息等
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MessageController : ControllerBase
    {
        private readonly ChattingWebsiteDBContext _db;
        private readonly HandlewebSocketsMidWare _wsHandler;

        public MessageController(ChattingWebsiteDBContext db, HandlewebSocketsMidWare wsHandler)
        {
            _db = db;
            _wsHandler = wsHandler;
        }

        private string? CurrentUserId =>
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        /// <summary>
        /// 撤回消息
        /// </summary>
        [HttpPut("{messageId}/recall")]
        public async Task<IActionResult> Recall(string messageId)
        {
            var userId = CurrentUserId;
            if (userId == null) return Unauthorized();

            var message = await _db.Messages.FindAsync(messageId);
            if (message == null) return NotFound("消息不存在");
            if (message.IsRecalled) return BadRequest("消息已撤回");

            bool isOwnMessage = message.senderId == userId;
            bool isSystemAdmin = User.IsInRole("Admin");
            if (!isOwnMessage && !isSystemAdmin)
            {
                // 检查是否是群主/管理
                var myRole = await _db.UserGroups
                    .Where(ug => ug.UserId == userId && ug.GroupId == message.roomId)
                    .Select(ug => ug.Role)
                    .FirstOrDefaultAsync();

                // 如果没有找到 UserGroup 记录，尝试私聊场景
                if (myRole == default && !isOwnMessage)
                {
                    // 私聊消息：只有发送者能撤回
                    return BadRequest("只能撤回自己的消息");
                }

                if (myRole != GroupRole.Owner && myRole != GroupRole.Admin)
                    return BadRequest("仅群主/管理员可撤回他人消息");
            }

            message.IsRecalled = true;
            await _db.SaveChangesAsync();

            // 撤回图片/媒体消息时，顺带清理磁盘上的媒体文件（原图 + 缩略图）
            DeleteMediaFiles(message);

            // 通知相关用户
            var recallMsg = JsonSerializer.Serialize(new { Type = "recall", MessageId = message.id, RoomId = message.roomId });
            if (message.roomId?.StartsWith("private_") == true)
            {
                // 私聊：通知双方
                var parts = message.roomId.Replace("private_", "").Split('_');
                await _wsHandler.SendToUser(parts[0], recallMsg);
                if (parts.Length > 1) await _wsHandler.SendToUser(parts[1], recallMsg);
            }
            else
            {
                // 群聊：通知群内所有成员
                var memberIds = await _db.UserGroups.Where(ug => ug.GroupId == message.roomId).Select(ug => ug.UserId).ToListAsync();
                foreach (var mid in memberIds)
                    await _wsHandler.SendToUser(mid, recallMsg);
            }

            return Ok(new { message = "消息已撤回" });
        }

        /// <summary>
        /// 删除消息关联的媒体文件（原图 + 缩略图）。
        /// 仅删除磁盘文件，不修改数据库记录（撤回后前端按 IsRecalled 显示"已撤回"，不再加载图片）。
        /// </summary>
        private static void DeleteMediaFiles(Message message)
        {
            if (string.IsNullOrEmpty(message.MediaUrl)) return;

            var wwwroot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            TryDelete(Path.Combine(wwwroot, message.MediaUrl.TrimStart('/')));

            // 缩略图与原图不同才删（P0 早期缩略图可能指向原图）
            if (!string.IsNullOrEmpty(message.MediaThumbUrl) && message.MediaThumbUrl != message.MediaUrl)
                TryDelete(Path.Combine(wwwroot, message.MediaThumbUrl.TrimStart('/')));
        }

        private static void TryDelete(string path)
        {
            // 注意：ControllerBase.File() 会遮蔽 System.IO.File，必须全限定
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch { /* 忽略删除失败（文件可能已被删或占用） */ }
        }
    }
}
