using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeUsageWidget;

public sealed record UsageWindow(double Utilization, DateTimeOffset? ResetsAt)
{
    public double Used => Math.Clamp(Utilization, 0, 100);
}

public sealed class UsageSnapshot
{
    public UsageWindow? FiveHour { get; set; }
    public UsageWindow? SevenDay { get; set; }
    public UsageWindow? SevenDayOpus { get; set; }
    public UsageWindow? SevenDaySonnet { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

public sealed class FetchResult
{
    public UsageSnapshot? Snapshot { get; init; }
    public string? Error { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public bool TokenExpired { get; init; }
    public bool NeedsLogin { get; init; }
}

/// <summary>
/// Reads the OAuth token Claude Code stores locally and queries the same usage
/// endpoint that powers Claude Code's /usage command. The token is never refreshed
/// here: refreshing would rotate it and could log Claude Code out.
/// </summary>
public sealed class UsageService
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<FetchResult> FetchAsync()
    {
        var (token, tokenError) = ReadToken();
        if (token is null)
            return new FetchResult { Error = tokenError, NeedsLogin = true };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.UserAgent.ParseAdd("claude-usage-widget/1.0");

            using var res = await Http.SendAsync(req);

            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = res.Headers.RetryAfter?.Delta
                    ?? (res.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromMinutes(10);
                return new FetchResult
                {
                    Error = $"요청 제한 — {Math.Ceiling(retry.TotalMinutes)}분 후 재시도",
                    RetryAfter = retry,
                };
            }

            if (res.StatusCode == HttpStatusCode.Unauthorized)
                return new FetchResult { Error = TokenExpiredMessage, TokenExpired = true, NeedsLogin = true };

            if (!res.IsSuccessStatusCode)
                return new FetchResult { Error = $"API 오류 ({(int)res.StatusCode})" };

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return new FetchResult
            {
                Snapshot = new UsageSnapshot
                {
                    FiveHour = ParseWindow(root, "five_hour"),
                    SevenDay = ParseWindow(root, "seven_day"),
                    SevenDayOpus = ParseWindow(root, "seven_day_opus"),
                    SevenDaySonnet = ParseWindow(root, "seven_day_sonnet"),
                    FetchedAt = DateTimeOffset.Now,
                },
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new FetchResult { Error = "네트워크 오류" };
        }
        catch (JsonException)
        {
            return new FetchResult { Error = "응답 형식 오류" };
        }
    }

    private const string TokenExpiredMessage = "토큰 만료 — 여기를 눌러 다시 로그인";
    private const string LoginNeeded = "로그인 필요 — 여기를 눌러 로그인";

    public static string CredentialsDir
    {
        get
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrEmpty(configDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : configDir;
        }
    }

    public const string CredentialsFileName = ".credentials.json";

    private static (string? Token, string? Error) ReadToken()
    {
        var path = Path.Combine(CredentialsDir, CredentialsFileName);

        if (!File.Exists(path))
            return (null, LoginNeeded);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || !oauth.TryGetProperty("accessToken", out var tokenEl)
                || tokenEl.GetString() is not { Length: > 0 } token)
                return (null, LoginNeeded);

            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number
                && DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()) < DateTimeOffset.UtcNow)
                return (null, TokenExpiredMessage);

            return (token, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, "로그인 정보 읽기 실패");
        }
    }

    private static UsageWindow? ParseWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
            return null;
        if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number)
            return null;

        DateTimeOffset? resetsAt = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var d))
            resetsAt = d;

        return new UsageWindow(u.GetDouble(), resetsAt);
    }
}
