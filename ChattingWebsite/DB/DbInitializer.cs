using ChattingWebsite.Model;
using Microsoft.EntityFrameworkCore;

namespace ChattingWebsite.DB
{
    public static class DbInitializer
    {
        public static async Task Seed(ChattingWebsiteDBContext db)
        {
            await SeedPublicGroup(db);
            await SeedAdminUser(db);
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

        private static async Task SeedAdminUser(ChattingWebsiteDBContext db)
        {
            if (await db.Users.AnyAsync(u => u.IsAdmin))
                return;

            db.Users.Add(new User
            {
                PublicId = Guid.NewGuid().ToString(),
                Nickname = "系统管理员",
                Email = "admin@chat.com",
                Password = BCrypt.Net.BCrypt.HashPassword("REDACTED"),
                IsAdmin = true,
                CreateTime = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            Console.WriteLine("[SEED] 系统管理员已创建: admin@chat.com / REDACTED");
        }
    }
}

