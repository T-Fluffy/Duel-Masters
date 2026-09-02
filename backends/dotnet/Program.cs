using System;
using System.Text;
using System.Threading.Tasks;
using DuelMasters.Server.Data;
using DuelMasters.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddControllers();

// PostgreSQL + EF Core.
var conn = config.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=duelmasters;Username=duel;Password=duel";
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));

// JWT auth.
var jwtKey = config["Jwt:Key"]
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

app.Run();
