using ChattingWebsite.Services;
using Microsoft.IdentityModel.Tokens;
using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace ChattingWebsite.MiddleWare
{
    public class JwtTokenMidWare
    {
        private readonly AuthSettings _authSettings;
        private readonly RequestDelegate _next;
        private readonly JwtTokenValidator _validator;

        public JwtTokenMidWare(RequestDelegate next, JwtTokenValidator validator)
        {
            _next = next;
            _validator = validator;
        }
        public async Task InvokeAsync(HttpContext context)
        {
            //从请求头中获取Authorization字段
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (authHeader != null && authHeader.StartsWith("Bearer "))
            {
                var token = authHeader.Substring("Bearer ".Length).Trim();
                ////将token存储在HttpContext.Items中，供后续中间件和控制器使用
                //context.Items["JwtToken"] = token;
                //var token = ExtractToken(context);
                if (token != null)
                {
                    var principal = _validator.Validate(token);
                    if (principal != null)
                        context.User = principal;
                }
                await _next(context);
            }
            await _next(context);
        }
       
    }
}
