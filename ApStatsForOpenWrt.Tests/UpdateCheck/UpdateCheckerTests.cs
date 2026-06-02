using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Services.Settings;
using ApStatsForOpenWrt.Services.UpdateCheck;
using Xunit;

namespace ApStatsForOpenWrt.Tests.UpdateCheck;

/// <summary>
/// M-UPD-01 + the checker half of M-UPD-04. UpdateChecker fetches GitHub releases/latest with
/// belt+braces 5s timeouts and a User-Agent, compares via <see cref="System.Version"/>, validates the
/// html_url is an https://github.com/ URL, writes through <c>IAppSettingsService</c>, and raises
/// <c>UpdateNotifier.UpdateAvailable</c> exactly once per N→N+1 advance (transition-fires-once, D-05).
/// Failures (non-200 / network / malformed tag) leave LastCheckedAtUtc unchanged with exactly ONE
/// Warning. The checker NEVER sees a DbContext.
/// </summary>
public sealed class UpdateCheckerTests
{
    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();
        private readonly object _gate = new();

        public List<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get { lock (_gate) return _entries.ToList(); }
        }

        public int WarningCount
        {
            get { lock (_gate) return _entries.Count(e => e.Level == LogLevel.Warning); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((level, formatter(state, exception), exception));
        }
    }

    /// <summary>
    /// Stubbed HttpMessageHandler — returns a fixed status + body, OR throws a supplied exception,
    /// per request. Records the outbound request so tests can assert the User-Agent header.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly Exception? _throw;
        public HttpRequestMessage? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
            => _responder = _ => new HttpResponseMessage(status) { Content = new StringContent(body) };

        public StubHandler(Exception toThrow)
        {
            _throw = toThrow;
            _responder = _ => throw new InvalidOperationException("unreachable");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            if (_throw is not null) throw _throw;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Mutable in-memory IAppSettingsService stub. Records setter invocations so tests can assert
    /// "SetLatestReleaseAsync NOT called" / "only SetLastCheckedAtUtcAsync called".
    /// </summary>
    private sealed class StubAppSettingsService : IAppSettingsService
    {
        public int PollingIntervalSeconds => 30;
        public string? UIPasswordHash => null;
        public bool WizardDismissed => false;
        public int RefreshIntervalSeconds => 3600;
        public IReadOnlyList<string> LocalDomainSuffixes => Array.Empty<string>();
        public int SessionVersion => 0;
        public event Action<IAppSettingsService>? Changed;

        public bool UpdateCheckEnabled { get; set; } = true;
        public bool HistoryEnabled => true;
        public Task SetHistoryEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public string? LatestKnownVersion { get; set; }
        public string? LatestReleaseUrl { get; set; }
        public DateTime? LastCheckedAtUtc { get; set; }

        public int SetLatestReleaseCallCount { get; private set; }
        public int SetLastCheckedAtUtcCallCount { get; private set; }

        public Task SetPollingIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUIPasswordHashAsync(string? hash, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetWizardDismissedAsync(bool dismissed, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRefreshIntervalSecondsAsync(int seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalDomainSuffixesAsync(IReadOnlyList<string> suffixes, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetSessionVersionAsync(int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task BumpSessionVersionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SetUpdateCheckEnabledAsync(bool enabled, CancellationToken ct = default) { UpdateCheckEnabled = enabled; Changed?.Invoke(this); return Task.CompletedTask; }
        public Task SetLatestKnownVersionAsync(string? version, CancellationToken ct = default) { LatestKnownVersion = version; return Task.CompletedTask; }
        public Task SetLatestReleaseUrlAsync(string? url, CancellationToken ct = default) { LatestReleaseUrl = url; return Task.CompletedTask; }
        public Task SetLastCheckedAtUtcAsync(DateTime? checkedAtUtc, CancellationToken ct = default)
        {
            SetLastCheckedAtUtcCallCount++;
            LastCheckedAtUtc = checkedAtUtc;
            return Task.CompletedTask;
        }
        public Task SetLatestReleaseAsync(string version, string url, DateTime checkedAtUtc, CancellationToken ct = default)
        {
            SetLatestReleaseCallCount++;
            LatestKnownVersion = version;
            LatestReleaseUrl = url;
            LastCheckedAtUtc = checkedAtUtc;
            Changed?.Invoke(this);
            return Task.CompletedTask;
        }
        public Task WarmAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── Fixture loading ───────────────────────────────────────────────────────

    private static string LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "UpdateCheck", "Fixtures", name);
        return File.ReadAllText(path);
    }

    private static string MakeBody(string tagName, string htmlUrl)
        => JsonSerializer.Serialize(new { tag_name = tagName, html_url = htmlUrl, prerelease = false });

    private static (UpdateChecker checker, StubAppSettingsService settings, UpdateNotifier notifier, CapturingLogger<UpdateChecker> log, StubHandler handler)
        Build(StubHandler handler, StubAppSettingsService? settings = null)
    {
        settings ??= new StubAppSettingsService();
        var notifier = new UpdateNotifier();
        var log = new CapturingLogger<UpdateChecker>();
        var factory = new StubHttpClientFactory(handler);
        var checker = new UpdateChecker(factory, settings, notifier, log);
        return (checker, settings, notifier, log, handler);
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewerVersion_WritesRelease_AndRaisesOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(HttpStatusCode.OK, LoadFixture("releases-latest-newer.json"));
        var (checker, settings, notifier, _, _) = Build(handler);

        var raised = new List<UpdateInfo>();
        notifier.UpdateAvailable += info => { raised.Add(info); return Task.CompletedTask; };

        await checker.CheckOnceAsync(ct);

        Assert.Equal("9.9.9", settings.LatestKnownVersion);
        Assert.Equal("https://github.com/mdima/ap-stats-for-openwrt/releases/tag/v9.9.9", settings.LatestReleaseUrl);
        Assert.NotNull(settings.LastCheckedAtUtc);
        Assert.Single(raised);
        Assert.Equal(Version.Parse("9.9.9"), raised[0].NewVersion);
        Assert.Equal(1, settings.SetLatestReleaseCallCount);
    }

    [Fact]
    public async Task SameKnownVersion_DoesNotRaise_AndDoesNotReAdvance()
    {
        // D-05 transition-fires-once: LatestKnownVersion already 9.9.9; a second 200 returning 9.9.9
        // must NOT re-advance (no SetLatestReleaseAsync) and must NOT raise.
        var ct = TestContext.Current.CancellationToken;
        var settings = new StubAppSettingsService { LatestKnownVersion = "9.9.9" };
        var handler = new StubHandler(HttpStatusCode.OK, LoadFixture("releases-latest-newer.json"));
        var (checker, _, notifier, _, _) = Build(handler, settings);

        var raisedCount = 0;
        notifier.UpdateAvailable += _ => { raisedCount++; return Task.CompletedTask; };

        await checker.CheckOnceAsync(ct);

        Assert.Equal(0, raisedCount);
        Assert.Equal(0, settings.SetLatestReleaseCallCount);
        // A silent re-check still stamps the timestamp.
        Assert.Equal(1, settings.SetLastCheckedAtUtcCallCount);
        Assert.NotNull(settings.LastCheckedAtUtc);
    }

    [Fact]
    public async Task EqualOrOlderThanAssembly_WritesOnlyTimestamp_NeverRaises()
    {
        var ct = TestContext.Current.CancellationToken;
        // 0.0.1 is older than any real running assembly version.
        var handler = new StubHandler(HttpStatusCode.OK,
            MakeBody("v0.0.1", "https://github.com/mdima/ap-stats-for-openwrt/releases/tag/v0.0.1"));
        var (checker, settings, notifier, _, _) = Build(handler);

        var raisedCount = 0;
        notifier.UpdateAvailable += _ => { raisedCount++; return Task.CompletedTask; };

        await checker.CheckOnceAsync(ct);

        Assert.Equal(0, raisedCount);
        Assert.Equal(0, settings.SetLatestReleaseCallCount);
        Assert.Equal(1, settings.SetLastCheckedAtUtcCallCount);
        Assert.Null(settings.LatestKnownVersion);
        Assert.NotNull(settings.LastCheckedAtUtc);
    }

    [Fact]
    public async Task SystemVersionOrdering_v1_10_IsNewerThan_v1_9()
    {
        // Non-lexicographic compare: against a stored LatestKnownVersion of 1.9.0, the parsed 1.10.0
        // is NEWER. (Assembly version is far older than both, so the assembly>parsed gate passes.)
        var ct = TestContext.Current.CancellationToken;
        var settings = new StubAppSettingsService { LatestKnownVersion = "1.9.0" };
        var handler = new StubHandler(HttpStatusCode.OK,
            MakeBody("v1.10.0", "https://github.com/mdima/ap-stats-for-openwrt/releases/tag/v1.10.0"));
        var (checker, _, notifier, _, _) = Build(handler, settings);

        var raised = new List<UpdateInfo>();
        notifier.UpdateAvailable += info => { raised.Add(info); return Task.CompletedTask; };

        await checker.CheckOnceAsync(ct);

        Assert.Single(raised);
        Assert.Equal(Version.Parse("1.10.0"), raised[0].NewVersion);
        Assert.Equal("1.10.0", settings.LatestKnownVersion);
    }

    [Fact]
    public async Task SystemVersionOrdering_v1_9_IsNotNewerThan_v1_10()
    {
        // The reverse: stored LatestKnownVersion 1.10.0, parsed 1.9.0 → NOT a re-advance.
        // (Both are newer than the running assembly version, so the assembly gate passes; the
        // dedup gate is what must hold here.)
        var ct = TestContext.Current.CancellationToken;
        var settings = new StubAppSettingsService { LatestKnownVersion = "1.10.0" };
        var handler = new StubHandler(HttpStatusCode.OK,
            MakeBody("v1.9.0", "https://github.com/mdima/ap-stats-for-openwrt/releases/tag/v1.9.0"));
        var (checker, _, notifier, _, _) = Build(handler, settings);

        var raisedCount = 0;
        notifier.UpdateAvailable += _ => { raisedCount++; return Task.CompletedTask; };

        await checker.CheckOnceAsync(ct);

        Assert.Equal(0, raisedCount);
        Assert.Equal(0, settings.SetLatestReleaseCallCount);
        Assert.Equal("1.10.0", settings.LatestKnownVersion);
    }

    [Fact]
    public async Task Non200_LeavesTimestampUnchanged_OneWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");
        var (checker, settings, _, log, _) = Build(handler);

        await checker.CheckOnceAsync(ct);

        Assert.Null(settings.LastCheckedAtUtc);
        Assert.Equal(0, settings.SetLastCheckedAtUtcCallCount);
        Assert.Equal(0, settings.SetLatestReleaseCallCount);
        Assert.Equal(1, log.WarningCount);
    }

    [Fact]
    public async Task NetworkException_LeavesTimestampUnchanged_OneWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(new HttpRequestException("no route to host"));
        var (checker, settings, _, log, _) = Build(handler);

        await checker.CheckOnceAsync(ct);

        Assert.Null(settings.LastCheckedAtUtc);
        Assert.Equal(0, settings.SetLastCheckedAtUtcCallCount);
        Assert.Equal(1, log.WarningCount);
    }

    [Fact]
    public async Task MalformedTagName_LeavesTimestampUnchanged_OneWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(HttpStatusCode.OK,
            MakeBody("not-a-version", "https://github.com/mdima/ap-stats-for-openwrt/releases/tag/x"));
        var (checker, settings, _, log, _) = Build(handler);

        await checker.CheckOnceAsync(ct);

        Assert.Null(settings.LastCheckedAtUtc);
        Assert.Equal(0, settings.SetLastCheckedAtUtcCallCount);
        Assert.Equal(0, settings.SetLatestReleaseCallCount);
        Assert.Equal(1, log.WarningCount);
    }

    [Fact]
    public async Task NonGithubUrl_TreatedAsParseFailure_OneWarning()
    {
        // T-17-02-02 output-side guard: a tampered html_url that is not https://github.com/ is a
        // parse failure — never persisted, single Warning, no timestamp bump.
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(HttpStatusCode.OK,
            MakeBody("v9.9.9", "javascript:alert(1)"));
        var (checker, settings, notifier, log, _) = Build(handler);

        var raisedCount = 0;
        notifier.UpdateAvailable += _ => { raisedCount++; return Task.CompletedTask; };

        await checker.CheckOnceAsync(ct);

        Assert.Equal(0, raisedCount);
        Assert.Equal(0, settings.SetLatestReleaseCallCount);
        Assert.Null(settings.LastCheckedAtUtc);
        Assert.Equal(1, log.WarningCount);
    }

    [Fact]
    public async Task SetsUserAgentHeader()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(HttpStatusCode.OK, LoadFixture("releases-latest-newer.json"));
        var (checker, _, _, _, h) = Build(handler);

        await checker.CheckOnceAsync(ct);

        Assert.NotNull(h.LastRequest);
        var ua = h.LastRequest!.Headers.UserAgent.ToString();
        Assert.Contains("ApStatsForOpenWrt/", ua, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrereleaseExcludedFixture_ParsesStableTag_NoPrereleaseSuffix()
    {
        // The /releases/latest contract returns a STABLE tag; pin that the parsed version carries no
        // pre-release suffix (System.Version has no pre-release component — v1.2.3 parses cleanly).
        var ct = TestContext.Current.CancellationToken;
        var settings = new StubAppSettingsService { LatestKnownVersion = "0.0.1" };
        var handler = new StubHandler(HttpStatusCode.OK, LoadFixture("releases-latest-prerelease-excluded.json"));
        var (checker, _, _, _, _) = Build(handler, settings);

        await checker.CheckOnceAsync(ct);

        Assert.Equal("1.2.3", settings.LatestKnownVersion);
        Assert.DoesNotContain("-", settings.LatestKnownVersion!, StringComparison.Ordinal);
    }
}
