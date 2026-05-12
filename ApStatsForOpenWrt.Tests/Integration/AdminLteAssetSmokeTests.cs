using Xunit;

namespace OpenWrtApStats.Tests.Integration;

/// <summary>
/// D-20 binding: the 5-checkpoint smoke spike (RESEARCH §Pitfall 6).
/// This file owns checkpoint #1 — every vendored static asset must return 200.
///
/// <para>
/// Checkpoint #2 (sidebar collapse) is asserted at MainLayoutTests.cs structural level
/// (full E2E sidebar collapse needs a real JS runtime — bUnit lacks one; manual smoke
/// at phase close-out covers the JS lifecycle).
/// </para>
///
/// <para>
/// Checkpoint #3 (modal opens from Blazor button click) is at ConfirmationModalTests.cs.
/// </para>
///
/// <para>
/// Checkpoints #4 (login form posts via static SSR) and #5 (antiforgery survives
/// sign-in roundtrip) are deferred to Plan 03-03 (which lands Login.razor and the
/// AuthGateTests integration suite).
/// </para>
/// </summary>
[Collection("Integration")]
public sealed class AdminLteAssetSmokeTests(SmokeFactoryFixture factory) : IClassFixture<SmokeFactoryFixture>
{
    private readonly SmokeFactoryFixture _factory = factory;

    [Theory]
    [InlineData("/lib/adminlte/css/adminlte.min.css")]
    [InlineData("/lib/adminlte/js/adminlte.min.js")]
    [InlineData("/lib/bootstrap-icons/bootstrap-icons.min.css")]
    [InlineData("/lib/source-sans-3/index.css")]
    public async Task StaticAsset_Returns200(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient();
        var response = await client.GetAsync(path, ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.False(string.IsNullOrEmpty(body), $"{path} returned an empty body");
    }

    [Fact]
    public async Task AdminLteCss_ContainsExpectedBanner()
    {
        // Sanity check: verify we vendored the actual upstream file, not an empty stub.
        var ct = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient();
        var body = await client.GetStringAsync("/lib/adminlte/css/adminlte.min.css", ct);
        Assert.Contains("AdminLTE", body);  // the AdminLTE banner mentions the project by name
    }

    [Fact]
    public async Task RootRoute_Returns200()
    {
        // The placeholder Components/Pages/Index.razor (Plan 03-01 Task 2.G) renders at /.
        // This is checkpoint-zero — the host actually boots and serves a Razor page.
        // (Plan 03-03 will add the auth gate; until then, /  is anonymous-OK because
        // UIPasswordHash is null on the in-memory DB by default.)
        var ct = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/", ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
