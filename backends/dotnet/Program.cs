using System;
using System.Text;
using System.Threading.Tasks;
using DuelMasters.Server.Data;
using DuelMasters.Server.Hubs;
using DuelMasters.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddControllers();

builder.Services.AddSignalR();

// PostgreSQL + EF Core. The connection string may come from configuration
// (e.g. ConnectionStrings__Default); otherwise it is composed from DB_* env
// vars. No credential is hardcoded in the repository - "duel" is only the
// local-dev fallback and is overridden via environment outside dev.
var conn = config.GetConnectionString("Default");
if (string.IsNullOrEmpty(conn))
{
    conn = $"Host={config["DB_HOST"] ?? "localhost"};Port={config["DB_PORT"] ?? "5432"};" +
        $"Database={config["DB_NAME"] ?? "duelmasters"};Username={config["DB_USER"] ?? "duel"};" +
        $"Password={config["DB_PASSWORD"] ?? "duel"}";
}
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));

// JWT auth. The signing key is injected via configuration (Jwt__Key) or the
// JWT_KEY environment variable; production must set one of these. The literal
// below is only the local-dev fallback and is never used elsewhere.
var jwtKey = config["Jwt:Key"]
    ?? config["JWT_KEY"]
    ?? "dev-only-change-me-in-production-012345678901234567890123";
var jwtIssuer = config["Jwt:Issuer"] ?? "duel-masters";
var jwtAudience = config["Jwt:Audience"] ?? "duel-masters-client";

builder.Services.AddSingleton<ITokenService>(
    new JwtTokenService(jwtKey, jwtIssuer, jwtAudience));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Ensure schema + seed the card catalog on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup");
    if (db.Database.IsRelational())
    {
        await db.Database.EnsureCreatedAsync();
        CardSeeder.Seed(db, logger);
    }
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<DuelHub>("/duel");

// Liveness probe for container healthchecks and CI readiness polling. Plain
// endpoint (no auth, no side effects) so orchestrators can distinguish "the
// process is up and routing requests" from not-yet-warmed-up.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
