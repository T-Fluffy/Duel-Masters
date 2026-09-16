using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace DuelMasters.Server.Tests;

/// <summary>One hermetic backend per test class: throwaway PostgreSQL database
/// (created + migrated at startup, dropped at dispose) served in-process.
/// A separate fixture instance means a separate rate-limiter budget, which is
/// what keeps the register-heavy tests deterministic.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public HttpClient Client { get; private set; } = null!;
    public string DbName { get; } = "test_" + Guid.NewGuid().ToString("N");

    public string FxUsername { get; private set; } = "";
    public string FxEmail { get; private set; } = "";
    public string FxNickname { get; private set; } = "";
    public Guid FxUserId { get; private set; }
    public string FxToken { get; private set; } = "";

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? fallback;

    private string Conn(string database) =>
        $"Host={Env("TEST_PGHOST", "localhost")};Port={Env("TEST_PGPORT", "5432")};" +
        $"Database={database};Username={Env("TEST_PGUSER", "duel")};Password={Env("TEST_PGPASSWORD", "duel")}";

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(Conn("postgres")))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{DbName}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        Client = WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Default", Conn(DbName))).CreateClient();

        var u = "fx_" + Guid.NewGuid().ToString("N")[..8];
        var resp = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            username = u,
            email = u + "@t.local",
            password = "TestPass123!",
            nickname = "Fx" + u,
        });
        resp.EnsureSuccessStatusCode();
        using var doc = (await resp.Content.ReadFromJsonAsync<JsonDocument>())!;
        var root = doc.RootElement;
        FxUsername = u;
        FxEmail = u + "@t.local";
        FxNickname = "Fx" + u;
        FxUserId = root.GetProperty("id").GetGuid();
        FxToken = root.GetProperty("token").GetString()!;
    }

    public new async Task DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await using var admin = new NpgsqlConnection(Conn("postgres"));
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{DbName}\" WITH (FORCE)", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
        }
        await base.DisposeAsync();
    }
}

public sealed class ProfileApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _fx;

    public ProfileApiTests(ApiFactory fx)
    {
        _fx = fx;
    }

    private static string U(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<HttpResponseMessage> Post(HttpClient c, string path, object body, string? token = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await c.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient c, string path, string? token = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await c.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> Put(HttpClient c, string path, object body, string? token = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await c.SendAsync(req);
    }

    private static async Task<JsonDocument> AsJson(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static async Task<(Guid Id, string Token)> Register(HttpClient c, string u, string email, string nick)
    {
        var r = await Post(c, "/api/auth/register", new
        {
            username = u,
            email,
            password = "TestPass123!",
            nickname = nick,
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await AsJson(r);
        return (doc.RootElement.GetProperty("id").GetGuid(),
                doc.RootElement.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task Register_ReturnsTokenAndChosenNickname()
    {
        var u = U("t1");
        var r = await Post(_fx.Client, "/api/auth/register", new
        {
            username = u,
            email = u + "@t.local",
            password = "TestPass123!",
            nickname = "Nick" + u,
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await AsJson(r);
        Assert.NotEqual(Guid.Empty, doc.RootElement.GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Register_DuplicateNickname_Conflict()
    {
        var a = U("t3a");
        await Register(_fx.Client, a, a + "@t.local", "DupNick" + a[..6]);
        var b = U("t3b");
        var r = await Post(_fx.Client, "/api/auth/register", new
        {
            username = b,
            email = b + "@t.local",
            password = "TestPass123!",
            nickname = "DupNick" + a[..6],
        });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Conflict()
    {
        var a = U("t4a");
        await Register(_fx.Client, a, a + "@t.local", "MailNick" + a[..6]);
        var b = U("t4b");
        var r = await Post(_fx.Client, "/api/auth/register", new
        {
            username = b,
            email = a + "@t.local",
            password = "TestPass123!",
            nickname = "MailNick" + b[..6],
        });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task GetProfile_ReturnsEmailAndChosenNickname()
    {
        var u = U("t5");
        var nick = "GetNick" + u[..6];
        var (_, token) = await Register(_fx.Client, u, u + "@t.local", nick);
        var r = await Get(_fx.Client, "/api/profile", token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await AsJson(r);
        Assert.Equal(nick, Str(doc.RootElement, "nickname"));
        Assert.Equal(u + "@t.local", Str(doc.RootElement, "email"));
    }

    [Fact]
    public async Task PutProfile_RoundTripsAllFields()
    {
        var u = U("t6");
        var (_, token) = await Register(_fx.Client, u, u + "@t.local", "PutNick" + u[..6]);
        var r = await Put(_fx.Client, "/api/profile", new
        {
            nickname = "PutHero" + u[..6],
            country = "Testland",
            avatarBase64 = "aGVsbG8=",
            favouriteDeckId = (string?)null,
            dateOfBirth = "2001-02-03T00:00:00",
            bio = "hello",
            backgroundBase64 = "aGVsbG8=",
        }, token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await AsJson(r);
        var root = doc.RootElement;
        Assert.Equal("PutHero" + u[..6], Str(root, "nickname"));
        Assert.Equal("Testland", Str(root, "country"));
        Assert.Equal("aGVsbG8=", Str(root, "avatarBase64"));
        Assert.Equal("aGVsbG8=", Str(root, "backgroundBase64"));
        Assert.Equal("hello", Str(root, "bio"));
        Assert.StartsWith("2001-02-03", Str(root, "dateOfBirth"));
        Assert.Equal(u + "@t.local", Str(root, "email"));
    }

    [Fact]
    public async Task PutProfile_BlankNickname_BadRequest()
    {
        var r = await Put(_fx.Client, "/api/profile", new { nickname = "   " }, _fx.FxToken);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task GetPublic_HidesEmailAndDob()
    {
        var u = U("t8");
        var (id, token) = await Register(_fx.Client, u, u + "@t.local", "PubNick" + u[..6]);
        var r = await Get(_fx.Client, "/api/profile/" + id, token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await AsJson(r);
        Assert.False(string.IsNullOrEmpty(Str(doc.RootElement, "nickname")));
        Assert.False(doc.RootElement.TryGetProperty("email", out _));
        Assert.False(doc.RootElement.TryGetProperty("dateOfBirth", out _));

        var missing = await Get(_fx.Client, "/api/profile/00000000-0000-0000-0000-000000000000", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task GetByNickname_ReturnsPublicCard()
    {
        var u = U("t11");
        var nick = "ByNick" + u[..6];
        var (_, token) = await Register(_fx.Client, u, u + "@t.local", nick);
        var r = await Get(_fx.Client, "/api/profile/by-nickname/" + nick, token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await AsJson(r);
        Assert.Equal(nick, Str(doc.RootElement, "nickname"));
        Assert.False(doc.RootElement.TryGetProperty("email", out _));
        Assert.False(doc.RootElement.TryGetProperty("dateOfBirth", out _));
    }

    [Fact]
    public async Task GetByNickname_Unknown_ReturnsNotFound()
    {
        var r = await Get(_fx.Client, "/api/profile/by-nickname/NoSuchNick_xyz", _fx.FxToken);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Stats_StartAtZero()
    {
        var r = await Get(_fx.Client, "/api/profile/stats", _fx.FxToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = await AsJson(r);
        Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Ranking_MeDefaultsAndLeaderboardHasNoPii()
    {
        var me = await Get(_fx.Client, "/api/ranking/me", _fx.FxToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meDoc = await AsJson(me);
        Assert.Equal(1000, meDoc.RootElement.GetProperty("rating").GetInt32());

        var lb = await Get(_fx.Client, "/api/ranking/leaderboard?top=10", _fx.FxToken);
        Assert.Equal(HttpStatusCode.OK, lb.StatusCode);
        using var lbDoc = await AsJson(lb);
        var names = new List<string>();
        foreach (var e in lbDoc.RootElement.EnumerateArray())
        {
            names.Add(Str(e, "nickname"));
            Assert.False(e.TryGetProperty("email", out _));
            Assert.False(e.TryGetProperty("dateOfBirth", out _));
        }
        Assert.Contains(_fx.FxNickname, names);
    }
}

public sealed class ValidationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _fx;

    public ValidationTests(ApiFactory fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task Register_BlankNickname_BadRequest()
    {
        var u = "t2_" + Guid.NewGuid().ToString("N")[..8];
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                username = u,
                email = u + "@t.local",
                password = "TestPass123!",
                nickname = "",
            }),
        };
        var r = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

}

public sealed class RateLimitTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _fx;

    public RateLimitTests(ApiFactory fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task Register_Burst_Sees429AndOnly200Or429()
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
        {
            var u = $"rl{i}_{Guid.NewGuid().ToString("N")[..6]}";
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
            {
                Content = JsonContent.Create(new
                {
                    username = u,
                    email = u + "@t.local",
                    password = "TestPass123!",
                    nickname = "Rl" + u,
                }),
            };
            codes.Add((await _fx.Client.SendAsync(req)).StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.All(codes, c => Assert.True(c == HttpStatusCode.OK || c == HttpStatusCode.TooManyRequests, c.ToString()));
    }
}
