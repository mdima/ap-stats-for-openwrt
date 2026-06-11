using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Playwright;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Smoke;

/// <summary>
/// M-HRD-04 D-05 / D-06 — the machine-verified CSP gate. A headless-Chromium browser
/// drives the PLAIN-HTTP containerized app at its Testcontainers-mapped host port and
/// proves two things the source-side <c>CspScriptHashTests</c> (Plan 21-03) cannot see:
///
///  1. The Blazor SignalR circuit connects over a real WebSocket (no long-polling
///     fallback) under the tightened <c>connect-src 'self'</c> — proven by observing a
///     <c>_blazor</c> WebSocket OPEN event (Pitfall 6: 'self' suffices same-origin LAN-HTTP).
///  2. ZERO CSP violations fire: no "Content Security Policy" console errors AND an
///     empty <c>securitypolicyviolation</c> collection captured via an init script —
///     proving the rendered inline-boot-script bytes match the pinned
///     <c>sha256-N+XgxVzS7xHbt2VJ63Smp/xmf8x7iq10qnQZ1+ljDiY=</c> hash end-to-end
///     (catches any source→rendered byte divergence the LF-normalized source hash test
///     cannot — the .gitattributes LF pin is what makes them agree).
///
/// Scope is the CSP gate ONLY (D-06): NOT a general E2E harness — no login/dashboard
/// browser flows. Infra is the exact analog of <see cref="DockerComposeRoundtripSmokeTests"/>
/// (same fixture, same random-mapped-port BuildContainer, same /health wait strategy),
/// EXCEPT this test deliberately does NOT set <c>Polling__GateMode=AlwaysLive</c> —
/// opening the page in a REAL browser is what negotiates the circuit, which is the whole
/// point of this gate. It navigates to "/Nodes" (an InteractiveServer page), NOT "/health"
/// and NOT "/": on a fresh container GET "/" 302-redirects to the static-SSR
/// "/setup/password" wizard step (which carries [ExcludeFromInteractiveRouting] → no
/// circuit), whereas "/Nodes" renders InteractiveServer and negotiates a real WebSocket.
///
/// Requires Docker (the container) AND a provisioned Chromium binary
/// (`playwright.ps1 install` chromium — CI smoke-tests job step + the local user_setup
/// step). The Microsoft.Playwright NuGet package ships only the managed driver.
/// </summary>
[Collection("DockerSmoke")]
[Trait("Category", "Smoke")]
[Trait("RequiresDocker", "true")]
public sealed class CspBrowserSmokeTests(DockerSmokeFixture fixture) : IClassFixture<DockerSmokeFixture>
{
    private readonly DockerSmokeFixture _fixture = fixture;

    [Fact]
    public async Task PlainHttp_BlazorCircuit_ConnectsOverWebSocket_WithZeroCspViolations()
    {
        var ct = TestContext.Current.CancellationToken;

        var container = BuildContainer();
        await container.StartAsync(ct);
        try
        {
            var baseUrl = $"http://localhost:{container.GetMappedPublicPort(8080)}";

            using var pw = await Playwright.CreateAsync();
            await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();

            // CSP violations surface as console errors ("Refused to execute inline script
            // because it violates the following Content Security Policy directive…").
            var cspConsoleViolations = new List<string>();
            page.Console += (_, msg) =>
            {
                if (msg.Type == "error" && msg.Text.Contains("Content Security Policy"))
                {
                    cspConsoleViolations.Add(msg.Text);
                }
            };

            // The securitypolicyviolation DOM event is the authoritative signal — capture
            // it into a window array via an init script (runs before page scripts).
            await page.AddInitScriptAsync(
                "window.__csp=[];document.addEventListener('securitypolicyviolation'," +
                "e=>window.__csp.push(e.violatedDirective+': '+e.blockedURI));");

            // Positive transport proof: a _blazor WebSocket OPEN event (NOT a header sniff).
            // If Blazor falls back to long-polling, no WebSocket opens and this stays false.
            var wsConnected = false;
            page.WebSocket += (_, ws) =>
            {
                if (ws.Url.Contains("_blazor"))
                {
                    wsConnected = true;
                }
            };

            // Navigate to "/Nodes" (the InteractiveServer Nodes Index), NOT "/". On a FRESH
            // container the first-run WizardRedirectMiddleware 302-redirects GET "/" to the
            // static-SSR "/setup/password" page (zero nodes + no password + not dismissed),
            // and ALL /setup/* and /Login pages carry [ExcludeFromInteractiveRouting] → static
            // SSR → NO Blazor circuit, so no _blazor WebSocket ever opens there. "/Nodes" is an
            // ordinary InteractiveServer page (no ExcludeFromInteractiveRouting), reachable
            // anonymously while no UI password is set, and the wizard redirect only fires on the
            // exact "/" path — so it is the minimal route that negotiates a real circuit. The
            // inline anti-FOUC boot script (whose hash the CSP pins) renders on every page
            // including this one, so the CSP gate is exercised identically.
            await page.GotoAsync(baseUrl + "/Nodes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            // A2 / Pitfall 4 (MEDIUM confidence): the _blazor WebSocket open event may not have
            // fired by the time NetworkIdle resolves — the circuit negotiate handshake can
            // complete a beat later. Allow a brief BOUNDED poll for the flag before asserting;
            // the positive proof remains an actual WebSocket open event, never a header sniff.
            // CSP violations (if any) would already be captured by this point.
            var wsDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!wsConnected && DateTime.UtcNow < wsDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }

            var cspFromDom = await page.EvaluateAsync<string[]>("window.__csp");

            // On failure these messages surface the captured violations (which directive + the
            // browser-computed hash it expected) so a future source→rendered hash drift prints
            // the actionable detail instead of a bare "collection was not empty".
            Assert.True(cspConsoleViolations.Count == 0,
                "CSP console violations: " + string.Join(" || ", cspConsoleViolations));
            Assert.True(cspFromDom.Length == 0,
                "securitypolicyviolation events: " + string.Join(" || ", cspFromDom));
            Assert.True(wsConnected, "Blazor circuit did not connect over WebSocket (long-polling fallback?).");
        }
        finally
        {
#pragma warning disable xUnit1051 // deliberate None — teardown must run even if the test's token is cancelled
            await container.StopAsync(CancellationToken.None);
#pragma warning restore xUnit1051
            await container.DisposeAsync();
        }
    }

    // Exact infra analog of DockerComposeRoundtripSmokeTests.BuildContainer() — random
    // mapped host port (load-bearing per Pitfall 4: the browser runs on the host/CI and
    // must reach the MAPPED port, not the container-internal 8080), same volume mounts,
    // same /health wait strategy. DELIBERATELY OMITS Polling__GateMode=AlwaysLive: the
    // real browser opening "/" negotiates the Blazor circuit, which is exactly what this
    // gate proves — forcing AlwaysLive would defeat the WebSocket-connect assertion.
    private IContainer BuildContainer() =>
        new ContainerBuilder("apstatsforopenwrt:smoke")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithVolumeMount(_fixture.DataVolume, "/data")
            .WithVolumeMount(_fixture.LogsVolume, "/logs")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
                r.ForPath("/health").ForPort(8080).ForStatusCode(HttpStatusCode.OK)))
            .Build();
}
