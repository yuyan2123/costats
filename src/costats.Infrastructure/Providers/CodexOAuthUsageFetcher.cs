using System.Net.Http.Headers;
using System.Text.Json;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Fetches Codex (OpenAI) usage data via the ChatGPT backend API.
/// This provides accurate utilization percentages directly from OpenAI.
/// </summary>
public sealed class CodexOAuthUsageFetcher : IDisposable
{
    private const string BaseUrl = "https://chatgpt.com/backend-api/";
    private const string UsagePath = "wham/usage";

    private readonly HttpClient _httpClient;

    public CodexOAuthUsageFetcher()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "costats");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<CodexOAuthUsageResult?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await LoadCredentialsAsync();
            if (credentials?.AccessToken is null)
            {
                return null;
            }

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

            // Add account ID header if available
            if (!string.IsNullOrEmpty(credentials.AccountId))
            {
                if (_httpClient.DefaultRequestHeaders.Contains("ChatGPT-Account-Id"))
                {
                    _httpClient.DefaultRequestHeaders.Remove("ChatGPT-Account-Id");
                }
                _httpClient.DefaultRequestHeaders.Add("ChatGPT-Account-Id", credentials.AccountId);
            }

            var response = await _httpClient.GetAsync(UsagePath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseResponse(content);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<CodexCredentials?> LoadCredentialsAsync()
    {
        // Check for CODEX_HOME environment variable first
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        string authPath;

        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            authPath = Path.Combine(codexHome.Trim(), "auth.json");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            authPath = Path.Combine(home, ".codex", "auth.json");
        }

        if (!File.Exists(authPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(authPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try new format with "tokens" object
            if (root.TryGetProperty("tokens", out var tokens))
            {
                return new CodexCredentials(
                    tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null,
                    tokens.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                    tokens.TryGetProperty("account_id", out var aid) ? aid.GetString() : null,
                    tokens.TryGetProperty("id_token", out var idt) ? idt.GetString() : null);
            }

            // Try legacy format with direct OPENAI_API_KEY
            if (root.TryGetProperty("OPENAI_API_KEY", out var apiKey))
            {
                return new CodexCredentials(apiKey.GetString(), null, null, null);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static CodexOAuthUsageResult? ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? planType = null;
            double? creditBalance = null;
            bool hasCredits = false;

            // Parse plan_type
            if (root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
            {
                planType = pt.GetString();
            }

            // Parse rate_limit.
            // OpenAI dropped the 5-hour session limit for some plans and now returns the sole
            // weekly window in "primary_window" with "secondary_window" set to null. Classify each
            // window by its declared length rather than trusting its position, so the weekly window
            // always lands in the weekly slot regardless of which field carries it.
            RateWindow? sessionWindow = null;
            RateWindow? weeklyWindow = null;

            if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
            {
                AssignWindow(ParseWindow(rateLimit, "primary_window"), ref sessionWindow, ref weeklyWindow);
                AssignWindow(ParseWindow(rateLimit, "secondary_window"), ref sessionWindow, ref weeklyWindow);
            }

            // Parse credits
            if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
            {
                if (credits.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True)
                {
                    hasCredits = true;
                }
                if (credits.TryGetProperty("balance", out var bal))
                {
                    if (bal.ValueKind == JsonValueKind.Number)
                    {
                        creditBalance = bal.GetDouble();
                    }
                    else if (bal.ValueKind == JsonValueKind.String && double.TryParse(bal.GetString(), out var balVal))
                    {
                        creditBalance = balVal;
                    }
                }
            }

            return new CodexOAuthUsageResult(
                planType,
                sessionWindow?.UsedPercent,
                sessionWindow?.ResetsAt,
                sessionWindow?.WindowSeconds,
                weeklyWindow?.UsedPercent,
                weeklyWindow?.ResetsAt,
                weeklyWindow?.WindowSeconds,
                hasCredits,
                creditBalance,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    // Session windows are short (hours); the weekly window spans multiple days. Treat anything
    // at least a day long as the weekly limit.
    private const int WeeklyWindowThresholdSeconds = 24 * 60 * 60;

    private static RateWindow? ParseWindow(JsonElement rateLimit, string propertyName)
    {
        // A missing or null window (e.g. "secondary_window": null) is not an object; skip it
        // instead of calling TryGetProperty on it, which throws for non-object elements.
        if (!rateLimit.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double? usedPercent = null;
        if (window.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number)
        {
            usedPercent = up.GetDouble();
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
        }

        int? windowSeconds = null;
        if (window.TryGetProperty("limit_window_seconds", out var lws) && lws.ValueKind == JsonValueKind.Number && lws.TryGetInt32(out var lwsValue))
        {
            windowSeconds = lwsValue;
        }

        if (usedPercent is null && resetsAt is null && windowSeconds is null)
        {
            return null;
        }

        return new RateWindow(usedPercent, resetsAt, windowSeconds);
    }

    private static void AssignWindow(RateWindow? window, ref RateWindow? sessionWindow, ref RateWindow? weeklyWindow)
    {
        if (window is null)
        {
            return;
        }

        // Route by declared length when available so we do not depend on the API's field ordering.
        if (window.WindowSeconds is { } seconds)
        {
            if (seconds >= WeeklyWindowThresholdSeconds)
            {
                weeklyWindow ??= window;
            }
            else
            {
                sessionWindow ??= window;
            }
            return;
        }

        // Length unknown: fall back to positional order (session first, then weekly).
        if (sessionWindow is null)
        {
            sessionWindow = window;
        }
        else
        {
            weeklyWindow ??= window;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record CodexCredentials(
        string? AccessToken,
        string? RefreshToken,
        string? AccountId,
        string? IdToken);

    private sealed record RateWindow(
        double? UsedPercent,
        DateTimeOffset? ResetsAt,
        int? WindowSeconds);
}

public sealed record CodexOAuthUsageResult(
    string? PlanType,
    double? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetsAt,
    int? PrimaryWindowSeconds,
    double? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetsAt,
    int? SecondaryWindowSeconds,
    bool HasCredits,
    double? CreditBalance,
    DateTimeOffset FetchedAt);
