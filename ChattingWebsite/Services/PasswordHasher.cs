namespace ChattingWebsite.Services
{
    public class PasswordHasher
    {
            public static string HashPassword(string password)
        {
            // 使用 BCrypt.Net-Next 库进行密码哈希
            return BCrypt.Net.BCrypt.HashPassword(password);
        }
        public static bool VerifyPassword(string password, string hashedPassword)
        {
            // 验证密码是否匹配
            return BCrypt.Net.BCrypt.Verify(password, hashedPassword);
        }
    }
}
