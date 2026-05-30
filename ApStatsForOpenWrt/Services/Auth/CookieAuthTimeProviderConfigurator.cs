using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace ApStatsForOpenWrt.Services.Auth;

/// <summary>
/// M-SEC-04 / plan 15-04 — binds the DI-resolved <see cref="TimeProvider"/> onto
/// <see cref="CookieAuthenticationOptions.TimeProvider"/> via the canonical
/// <see cref="IPostConfigureOptions{TOptions}"/> seam.
///
/// <para>
/// The lambda inside <c>AddCookie(o =&gt; { ... })</c> in <c>Program.cs</c> runs at
/// registration time with no scope, so it cannot resolve <see cref="TimeProvider"/>
/// from DI directly. <see cref="IPostConfigureOptions{TOptions}"/> runs AFTER the
/// options instance is constructed (and inside the scope where <see cref="TimeProvider"/>
/// is resolvable), so this post-configurator is the canonical wiring shape.
/// </para>
///
/// <para>
/// CONTEXT.md Decision 4 (test seam): production registers <c>TimeProvider.System</c>;
/// <c>SlidingExpirationTests</c> swaps it for a <c>FakeTimeProvider</c> to drive the
/// 7-day sliding-expiration invariant without real-time waits.
/// </para>
/// </summary>
internal sealed class CookieAuthTimeProviderConfigurator(TimeProvider timeProvider)
    : IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
        => options.TimeProvider = timeProvider;
}
