using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Services.Settings;

namespace ApStatsForOpenWrt.Services.UpdateCheck;

/// <summary>
/// M-UPD-01 — the first <c>HttpClient</c> consumer in the codebase. Fetches the upstream GitHub
/// <c>releases/latest</c>, compares the parsed tag against the running assembly version via
/// <see cref="System.Version"/>, persists exclusively through <see cref="IAppSettingsService"/>
/// (never a <c>DbContext</c>), and raises <see cref="UpdateNotifier.UpdateAvailable"/> exactly once
/// on a real N→N+1 advance (transition-fires-once, D-05 — the dedup lives HERE, not in the notifier).
/// <para>
/// HTTP discipline mirrors the SSH "belt + braces" convention (CLAUDE.md): <c>HttpClient.Timeout</c>
/// is 5s AND the call is wrapped in a 5s linked <see cref="CancellationTokenSource"/>. A
/// <c>User-Agent</c> is mandatory (GitHub 403s without one). Repo coordinates are a baked-in internal
/// const (D-06 / L-03 — a fork edits one line). The whole fetch+parse is inside one try/catch: any
/// failure (network / non-200 / parse / non-github URL) logs exactly ONE Serilog Warning and returns
/// WITHOUT touching <c>LastCheckedAtUtc</c> (the timestamp reflects last *successful* ground truth).
/// </para>
/// </summary>
public sealed class UpdateChecker(
    IHttpClientFactory httpClientFactory,
    IAppSettingsService settings,
    UpdateNotifier notifier,
    ILogger<UpdateChecker> log) : IUpdateChecker
{
    // D-06 / L-03 — baked-in repo coordinates. A fork edits these two lines; NOT a config/env surface.
    internal const string RepoOwner = "mdima";
    internal const string RepoName  = "ap-stats-for-openwrt";

    // T-17-02-01 — the const endpoint is https:// (TLS enforced). GET https://api.github.com/repos/{owner}/{repo}/releases/latest.
    internal const string ReleasesLatestUrl =
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    // T-17-02-02 — the html_url MUST be an absolute https://github.com/ URL before it can reach
    // LatestReleaseUrl (defends the Plan-03 badge href against a tampered javascript:/data:/non-github body).
    private const string ReleaseHostName = "github.com";

    // T-17-02-02 — defensive upper bound on the tag string we will even attempt to parse.
    private const int MaxTagLength = 64;

    private const string HttpClientName = "github";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Minimal DTO over the GitHub releases/latest body — only the two fields we consume.</summary>
    private sealed record ReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);

    public async Task CheckOnceAsync(CancellationToken ct = default)
    {
        // 260518-vbs — version read at runtime; NO literal version string anywhere in this file.
        var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version;
        if (currentVersion is null)
        {
            log.LogWarning("Update check skipped: running assembly version is unavailable (GetEntryAssembly returned null).");
            return;
        }

        // Belt + braces: HttpClient.Timeout AND a 5s linked CTS combined with the caller's token
        // (mirrors the SSH ConnectionInfo.Timeout + linked CTS convention — CLAUDE.md).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = RequestTimeout;

            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesLatestUrl);
            // GitHub 403s a missing User-Agent. Version-suffixed so the call is self-identifying.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ApStatsForOpenWrt", currentVersion.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Update check failed: GitHub returned HTTP {StatusCode}. LastCheckedAtUtc left unchanged.",
                    (int)response.StatusCode);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            ReleaseDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<ReleaseDto>(body);
            }
            catch (JsonException)
            {
                log.LogWarning("Update check failed: releases/latest body was not valid JSON. LastCheckedAtUtc left unchanged.");
                return;
            }

            // T-17-02-02 — bound the parse: null/empty/oversized tag is a parse failure (no throw).
            var rawTag = dto?.TagName;
            if (string.IsNullOrWhiteSpace(rawTag) || rawTag.Length > MaxTagLength)
            {
                log.LogWarning("Update check failed: malformed tag_name (null/empty/oversized). LastCheckedAtUtc left unchanged.");
                return;
            }

            // Strip a single leading 'v' (v9.9.9 -> 9.9.9), then System.Version.TryParse (NOT lexicographic).
            var normalized = rawTag.StartsWith('v') || rawTag.StartsWith('V') ? rawTag[1..] : rawTag;
            if (!Version.TryParse(normalized, out var parsedVersion))
            {
                log.LogWarning("Update check failed: malformed tag_name '{Tag}' is not a System.Version. LastCheckedAtUtc left unchanged.",
                    rawTag);
                return;
            }

            // T-17-02-02 — validate html_url is an absolute https://github.com/ URL BEFORE persisting.
            var rawUrl = dto?.HtmlUrl;
            if (string.IsNullOrWhiteSpace(rawUrl)
                || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps
                || !url.Host.Equals(ReleaseHostName, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Update check failed: html_url is not an absolute https://github.com/ URL. LastCheckedAtUtc left unchanged.");
                return;
            }
            var validatedUrl = url.ToString();

            var now = DateTime.UtcNow;

            // If the upstream release is NOT newer than what we are running → success, but only the
            // timestamp moves (no advance, no raise).
            if (parsedVersion <= currentVersion)
            {
                await settings.SetLastCheckedAtUtcAsync(now, ct);
                return;
            }

            // The release IS newer than the running version. Transition-fires-once (D-05): only a
            // strict N→N+1 advance over the prior-stored LatestKnownVersion writes + raises. A null
            // or unparseable prior counts as an advance; a re-check of the same already-known version
            // is a silent timestamp-only write.
            var priorAdvance = settings.LatestKnownVersion is { } prior
                && Version.TryParse(prior, out var priorVersion)
                && parsedVersion <= priorVersion;

            if (priorAdvance)
            {
                // Already known (or older than known) — silent re-check, no raise.
                await settings.SetLastCheckedAtUtcAsync(now, ct);
                return;
            }

            // Real advance — combined 3-value write (single Changed), THEN raise exactly once.
            var versionString = parsedVersion.ToString();
            log.LogInformation("Update available: {Version} discovered at {Url}.", versionString, validatedUrl);
            await settings.SetLatestReleaseAsync(versionString, validatedUrl, now, ct);
            await notifier.RaiseAsync(new UpdateInfo(parsedVersion, validatedUrl));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancellation (host shutdown). Expected — re-throw so the loop's
            // OperationCanceledException-on-shutdown handler observes it.
            throw;
        }
        catch (OperationCanceledException)
        {
            // The 5s timeout CTS fired (the caller's token is NOT cancelled). Treat as a check failure:
            // single Warning, LastCheckedAtUtc untouched.
            log.LogWarning("Update check failed: request timed out after {Seconds}s. LastCheckedAtUtc left unchanged.",
                (int)RequestTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            // Network failure (HttpRequestException) or any other unexpected fault. Single Warning,
            // no timestamp bump, never propagate (the loop relies on CheckOnceAsync not throwing).
            log.LogWarning(ex, "Update check failed: {ExceptionType}. LastCheckedAtUtc left unchanged.",
                ex.GetType().Name);
        }
    }
}
