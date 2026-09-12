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
            // 媒体后处理器（按 MediaType 分派）：新增文件类型只需加一个实现并注册
            builder.Services.AddSingleton<IMediaProcessor, ImageProcessor>();

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

            // ── 启动自检：HTTP 端口与 WebSocket 端口绝不能相同 ──
            // Windows 的 socket 默认允许地址复用，端口撞车时两个监听器都会“绑定成功”且不报错，
            // 但连接归谁是不确定的 → WebSocket 握手可能被 HTTP 服务器接走 → 前端反复掉线、
            // 收不到任何广播。这个故障现象极像“业务 bug”，所以在这里主动拦下来。
            var httpPorts = app.Urls
                .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Port : 0)
                .Where(p => p > 0)
                .ToList();
            if (httpPorts.Contains(wsPort))
            {
                app.Logger.LogCritical(
                    "[启动自检] HTTP 端口({HttpPorts}) 与 WebSocket 端口({WsPort}) 冲突！" +
                    "请修改 appsettings.json 的 WebSocket:Port 或 launchSettings.json 的 applicationUrl。",
                    string.Join(",", httpPorts), wsPort);
            }

            // 启动 WS 服务。端口被占用时 StartAsync 会抛异常 —— 这里把失败转成“进程退出”，
            // 避免出现“HTTP 正常、WebSocket 永远连不上”这种最难排查的中间状态。
            _ = Task.Run(() => wsServer.StartAsync(wsPort)).ContinueWith(t =>
            {
                app.Logger.LogCritical(t.Exception, "[启动自检] WebSocket 服务启动失败，进程退出");
                Environment.Exit(1);
            }, TaskContinuationOptions.OnlyOnFaulted);

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
