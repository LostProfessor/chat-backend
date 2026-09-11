using ChattingWebsite.DB;
using ChattingWebsite.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;

namespace ChattingWebsite
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // ═══════════════ 1. 配置服务（DI 注册） ═══════════════

            // 控制器 + JSON 强制 PascalCase，避免前后端大小写混乱
            builder.Services.AddControllers()
                .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null);

            builder.Services.AddEndpointsApiExplorer();

            // ── CORS：允许前端跨域访问 ──
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.WithOrigins("http://localhost:5173", "http://localhost:5174")
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                });
            });

            // ── 业务服务注册 ──
            // Scoped：每次请求/消息处理新建（内部依赖 DbContext，必须是 Scoped）
            builder.Services.AddScoped<ChatService>();
            builder.Services.AddScoped<MessageDispatcher>();
            builder.Services.AddScoped<FileTransferHandler>();
            // Singleton：全局唯一，多线程共享
            builder.Services.AddSingleton<JwtTokenValidator>();
            builder.Services.AddSingleton<ConnectionManager>();
            builder.Services.AddSingleton<HandlewebSocketsMidWare>();
            builder.Services.AddSingleton<WebSocketServer>();
            builder.Services.AddSingleton<FileTransferSessionManager>();

            // ── 数据库（SQL Server） ──
            builder.Services.AddDbContext<ChattingWebsiteDBContext>(option =>
            {
                option.UseSqlServer(builder.Configuration.GetConnectionString("chatconn"));
            });

            // ── Swagger + JWT 认证说明 ──
            builder.Services.AddSwaggerGen(c =>
            {
                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Description = "JWTBearer {token}\"",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.Http,
                    Scheme = "Bearer"
                });
                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                 Type = ReferenceType.SecurityScheme,
                                 Id = "Bearer"
                            }
                        },
                        new List<string>()
                    }
                });
            });

            // ── JWT 认证 ──
            var keyBytes = Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]);
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = builder.Configuration["Jwt:Issuer"],
                        ValidateAudience = true,
                        ValidAudience = builder.Configuration["Jwt:Audience"],
                        ValidateLifetime = true,
                        IssuerSigningKey = new SymmetricSecurityKey(keyBytes)
                    };
                });

            // ═══════════════ 2. 构建应用 + 配置中间件管线 ═══════════════

            var app = builder.Build();

            app.UseRouting();
            app.UseCors();
            app.UseStaticFiles();          // 提供头像等静态文件
            app.UseAuthentication();       // JWT 认证中间件
            app.UseAuthorization();        // 授权中间件
            app.MapControllers();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            // ═══════════════ 3. 启动自定义 WebSocket 服务器 ═══════════════

            // 端口从配置读取（appsettings.json → WebSocket:Port）
            int wsPort = builder.Configuration.GetValue<int>("WebSocket:Port", 5259);
            var wsServer = app.Services.GetRequiredService<WebSocketServer>();
            _ = Task.Run(() => wsServer.StartAsync(wsPort));

            // ═══════════════ 4. 数据库迁移 + 种子数据 ═══════════════

            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ChattingWebsiteDBContext>();
                await dbContext.Database.MigrateAsync();   // 确保表结构最新
                await DbInitializer.Seed(dbContext);       // 确保必要数据存在
                Console.WriteLine(dbContext.Database.CanConnect()
                    ? "数据库连接成功！"
                    : "数据库连接异常！请检查数据库连接字符串。");
            }

            // ═══════════════ 5. 运行 ═══════════════

            await app.RunAsync();
        }
    }
}
