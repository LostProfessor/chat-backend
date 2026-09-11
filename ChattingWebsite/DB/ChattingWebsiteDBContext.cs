using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;

namespace ChattingWebsite.DB
{
    public class ChattingWebsiteDBContext:DbContext
    {
        public ChattingWebsiteDBContext(DbContextOptions<ChattingWebsiteDBContext>options):base(options)
        {
            
        }
        public DbSet<Model.User> Users { get; set; }
        public DbSet<Model.Message> Messages { get; set; }
        public DbSet<Model.Group> Groups { get; set; }
        public DbSet<Model.UserGroup> UserGroups { get; set; }
        public DbSet<Model.Friendship> Friendships { get; set; }
        public DbSet<Model.GroupJoinRequest> GroupJoinRequests { get; set; }
        public DbSet<Model.Announcement> Announcements { get; set; }
    }
}
