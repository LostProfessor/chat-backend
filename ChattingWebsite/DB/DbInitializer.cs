using ChattingWebsite.Model;
using Microsoft.EntityFrameworkCore;

namespace ChattingWebsite.DB
{
    public static class DbInitializer
    {
        // 管理员凭据从配置（AdminSettings:*）注入，不再硬编码在源码里 ——
        // 否则密码会随仓库一起公开。未配置密码时「跳过创建」而不是悄悄用默认密码。
        public static async Task Seed(ChattingWebsiteDBContext db, IConfiguration config)
        {
            await SeedPublicGroup(db);
            await SeedAdminUser(db, config);
        }

        private static async Task SeedPublicGroup(ChattingWebsiteDBContext db)
        {
            if (!await db.Groups.AnyAsync(g => g.Id == "public"))
            {
                db.Groups.Add(new Group
                {
                    Id = "public",
                    Name = "全服大厅",
                    CreatorId = "system",
                    IsDefault = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
        }

        private static async Task SeedAdminUser(ChattingWebsiteDBContext db, IConfiguration config)
        {
            if (await db.Users.AnyAsync(u => u.IsAdmin))
                return;

            var email = config["AdminSettings:AdminEmail"];
            var password = config["AdminSettings:AdminPassword"];
            var nickname = config["AdminSettings:AdminNickname"];

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                Console.WriteLine(
                    "[SEED] 未配置 AdminSettings:AdminEmail / AdminPassword，跳过管理员创建。\n" +
                    "       需要管理员账号时，请先执行：\n" +
                    "         dotnet user-secrets set \"AdminSettings:AdminPassword\" \"<你的密码>\"\n" +
                    "       然后删除数据库中的管理员记录（或整个库）再启动。");
                return;
            }

            db.Users.Add(new User
            {
                PublicId = Guid.NewGuid().ToString(),
                Nickname = string.IsNullOrWhiteSpace(nickname) ? "系统管理员" : nickname,
                Email = email,
                Password = BCrypt.Net.BCrypt.HashPassword(password),
                IsAdmin = true,
                CreateTime = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            // 注意：日志只打印邮箱，不打印密码
            Console.WriteLine($"[SEED] 系统管理员已创建: {email}");
        }
    }
}

