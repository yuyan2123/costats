using System.Net.Http.Headers;
using System.Text.Json;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Fetches Codex's manual "reset my limits early" credits. OpenAI does not surface this
/// anywhere in the CLI or ChatGPT UI; it's only available via the same internal endpoint
/// the desktop Codex app queries.
/// </summary>
public sealed class CodexResetCreditsFetcher : IDisposable
{
    private const string BaseUrl = "https://chatgpt.com/backend-api/";
    private const string ResetCreditsPath = "wham/rate-limit-reset-credits";

    private readonly HttpClient _httpClient;

    public CodexResetCreditsFetcher()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "costats");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Add("openai-beta", "codex-1");
        _httpClient.DefaultRequestHeaders.Add("originator", "Codex Desktop");
    }

    public async Task<CodexResetCredits?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await LoadCredentialsAsync();
            if (credentials?.AccessToken is null)
            {
                return null;
            }

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

            if (_httpClient.DefaultRequestHeaders.Contains("ChatGPT-Account-Id"))
            {
                _httpClient.DefaultRequestHeaders.Remove("ChatGPT-Account-Id");
            }
            if (!string.IsNullOrEmpty(credentials.AccountId))
            {
                _httpClient.DefaultRequestHeaders.Add("ChatGPT-Account-Id", credentials.AccountId);
            }

            var response = await _httpClient.GetAsync(ResetCreditsPath, cancellationToken);
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

            if (root.TryGetProperty("tokens", out var tokens))
            {
                return new CodexCredentials(
                    tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null,
                    tokens.TryGetProperty("account_id", out var aid) ? aid.GetString() : null);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static CodexResetCredits? ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("available_count", out var countEl) || countEl.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            var availableCount = (int)countEl.GetDouble();
            DateTimeOffset? nextExpiresAt = null;

            if (root.TryGetProperty("credits", out var creditsEl) && creditsEl.ValueKind == JsonValueKind.Array)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var credit in creditsEl.EnumerateArray())
                {
                    var status = credit.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                    if (!string.Equals(status, "available", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!credit.TryGetProperty("expires_at", out var expiresEl) ||
                        !DateTimeOffset.TryParse(expiresEl.GetString(), out var expiresAt))
                    {
                        continue;
                    }

                    if (expiresAt <= now)
                    {
                        continue;
                    }

                    if (nextExpiresAt is null || expiresAt < nextExpiresAt.Value)
                    {
                        nextExpiresAt = expiresAt;
                    }
                }
            }

            return new CodexResetCredits(availableCount, nextExpiresAt);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record CodexCredentials(string? AccessToken, string? AccountId);
}
