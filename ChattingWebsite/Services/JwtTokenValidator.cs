using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace ChattingWebsite.Services
{
    public class JwtTokenValidator
    {
        private readonly IConfiguration _configuration;
        private readonly TokenValidationParameters _validationParameters;
        public JwtTokenValidator(IConfiguration configuration)
        {
            _configuration = configuration;
            var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]);
            _validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _configuration["Jwt:Audience"],
                ValidateLifetime = true,
                IssuerSigningKey = new SymmetricSecurityKey(key)
            };
        }

        //公共方法
        public ClaimsPrincipal Validate(string token)
        {
            var handler  = new JwtSecurityTokenHandler();
            try
            {
                var principal = handler.ValidateToken(token, _validationParameters, out _);
                return principal;
            }
            catch
            {
                return null;
            }
        }
    }
}
