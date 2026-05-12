using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ApStatsForOpenWrt.Services.Gating;
using ApStatsForOpenWrt.Services.Settings;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// ROADMAP success #2 + #3 + D-07 JSON shape + D-11 auth bypass coverage for the /health
/// endpoint. Drives the production Program.cs registrations end-to-end via SmokeFactoryFixture.
/// </summary>
[Collection("Integration")]
public sealed class HealthEndpointTests(SmokeFactoryFixture factory) : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Fact]
    public async Task Health_Returns200_AndDoesNotIncrementCircuitTracker()
    {
        // ROADMAP success #2 — 100 /health hits with zero browser circuits leaves count at 0.
        // CLAUDE.md Convention: "/health is plain HTTP — structurally cannot trigger CircuitHandler."
        var ct = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient();

        var tracker = _factory.Services.GetRequiredService<CircuitTracker>();
        Assert.Equal(GateState.Paused, tracker.Current);

        for (var i = 0; i < 100; i++)
        {
            var response = await client.GetAsync("/health", ct);
            // Status code is 200 (Healthy) OR 503 (one probe failed) — but NEVER 401/302/500.
            Assert.True(
                response.StatusCode == HttpStatusCode.OK ||
                response.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"/health returned unexpected status {response.StatusCode} on iteration {i}");
        }

        Assert.Equal(GateState.Paused, tracker.Current);
    }

    [Fact]
    public async Task Health_ResponseBody_IsJsonWithExpectedShape()
    {
        // D-07 — { status, components: { db, dataProtection }, lastPollAge }
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.CreateClient().GetAsync("/health", ct);

        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("status", out var status));
        Assert.True(status.GetString() is "Healthy" or "Unhealthy");

        Assert.True(doc.RootElement.TryGetProperty("components", out var components));
        Assert.True(components.TryGetProperty("db", out var db));
        Assert.True(db.GetString() is "ok" or "fail");
        Assert.True(components.TryGetProperty("dataProtection", out var dp));
        Assert.True(dp.GetString() is "ok" or "fail");

        Assert.True(doc.RootElement.TryGetProperty("lastPollAge", out var lastPollAge));
        // null on cold boot (no successful cycle yet); else compact human format `Ns` or `NmMMs`.
        Assert.True(
            lastPollAge.ValueKind == JsonValueKind.Null ||
            (lastPollAge.ValueKind == JsonValueKind.String &&
             System.Text.RegularExpressions.Regex.IsMatch(lastPollAge.GetString()!, @"^(\d+s|\d+m\d{2}s)$")));

        // gateState MUST NOT be present (D-07 rejected addition).
        Assert.False(doc.RootElement.TryGetProperty("gateState", out _));
    }

    [Fact]
    public async Task Health_RegisteredBeforeUseAuthentication_ReachableWithoutAuth()
    {
        // D-11 + ROADMAP success #3. Set a UI password via the fixture's seed path; then hit
        // /health with NO auth cookie and expect 200 (or 503 on a real probe failure) — NEVER
        // 401 or 302.
        var ct = TestContext.Current.CancellationToken;
        await SeedUIPasswordAsync(ct);

        try
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,    // catch the 302 -> /Login if D-11 regresses.
            });
            var response = await client.GetAsync("/health", ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.OK ||
                response.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"/health was {response.StatusCode}; expected 200 or 503 (D-11 regression — auth caught /health)");
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);          // 302
        }
        finally
        {
            // Cleanup so sibling tests in the shared fixture see a null hash again.
            using var scope = _factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            await settings.SetUIPasswordHashAsync(null, ct);
        }
    }

    private async Task SeedUIPasswordAsync(CancellationToken ct)
    {
        // W-new-3 — concrete body mirroring AuthGateTests.cs lines 49-52. The project's auth
        // tests use SetUIPasswordHashAsync directly with a literal hash string — the gate
        // engages on hash != null, the literal value is irrelevant for the bypass assertion.
        // No PasswordHasher is required for this test (the auth pipeline only checks presence).
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        await settings.SetUIPasswordHashAsync("AQAAA-fake-hash", ct);
    }
}

/// <summary>
/// W7 / B2 — separated into its own class so it can take <see cref="SerilogCapturingFixture"/>
/// (which installs the TestCorrelator sink). The other three Health tests stay on
/// <see cref="SmokeFactoryFixture"/> for shared-lifetime efficiency.
/// </summary>
[Collection("Integration")]
public sealed class HealthSerilogFilterTests(SerilogCapturingFixture factory)
    : IClassFixture<SerilogCapturingFixture>
{
    private readonly SerilogCapturingFixture _factory = factory;

    [Fact]
    public async Task Health_NotIncludedInRequestLog()
    {
        // W7 / B2 — D-10 runtime assertion via the fixture's InMemorySink. The Serilog
        // filter expression `p.ToString() == "\"/health\""` is fragile (doubled-quote literal);
        // a runtime regression would NOT be caught by the static `grep -q Filter.ByExcluding`
        // gate alone. This test exercises the pipeline at runtime to catch that regression.
        //
        // POSITIVE CONTROL: hit "/" first and assert at least one event with a non-/health
        // RequestPath was captured. Without the positive control, an empty `leaked` enumerable
        // could be vacuously empty (wrong sink wiring, missing async flush, etc.) and the test
        // would pass even if the production filter regressed — which is the OPPOSITE of W7's
        // intent.
        //
        // NOTE: round-1 of this test used Serilog.Sinks.TestCorrelator's AsyncLocal-context
        // capture API; empirically, that context did not survive the HttpClient -> TestServer
        // transport hop (events emitted inside the request handler ran in a different async
        // chain), so the positive control was vacuously empty. The package reference is kept in
        // the .csproj for future use; this test routes through the fixture-owned InMemorySink
        // (which captures events regardless of AsyncLocal context).
        var ct = TestContext.Current.CancellationToken;
        _factory.ClearCaptured();

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // (1) POSITIVE CONTROL — hit "/"; UseSerilogRequestLogging in the production pipeline
        //     emits a "HTTP {Method} {RequestPath} ... " event with the RequestPath property set.
        var rootResponse = await client.GetAsync("/", ct);
        // Don't assert on status code — wizard redirect / login / 200 are all valid; we
        // only need the request-log event to fire.
        _ = rootResponse;

        // (2) Hit /health — production filter MUST drop this from the request log.
        var healthResponse = await client.GetAsync("/health", ct);
        Assert.True(
            healthResponse.StatusCode == HttpStatusCode.OK ||
            healthResponse.StatusCode == HttpStatusCode.ServiceUnavailable);

        // Allow the async sink one tick to flush.
        await Task.Delay(100, ct);

        var captured = _factory.CapturedEvents;

        // (3) Positive-control assertion — pipeline IS capturing events (defeats vacuous-empty).
        //     Look for any event with a RequestPath property whose value is NOT /health.
        var rootEvents = captured.Where(e =>
            e.Properties.TryGetValue("RequestPath", out var p) &&
            !p.ToString().Contains("/health", StringComparison.Ordinal));
        Assert.NotEmpty(rootEvents);

        // (4) The actual W7 / D-10 assertion — no event with RequestPath containing "/health".
        var leaked = captured.Where(e =>
            e.Properties.TryGetValue("RequestPath", out var p) &&
            p.ToString().Contains("/health", StringComparison.Ordinal));
        Assert.Empty(leaked);
    }
}
