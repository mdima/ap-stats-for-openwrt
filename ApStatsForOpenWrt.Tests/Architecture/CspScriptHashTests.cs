using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApStatsForOpenWrt.Services.Security;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Architecture;

/// <summary>
/// M-HRD-04 (plan 21-03) — source-derived pin on the CSP <c>script-src</c> SHA-256 hash.
///
/// <para>
/// The CSP <c>script-src</c> directive drops <c>'unsafe-inline'</c> in favor of a
/// <c>sha256-</c> hash of the single inline boot <c>&lt;script&gt;</c> in
/// <c>App.razor</c>. This test reads that script from SOURCE, LF-normalizes it, recomputes
/// the hash with the BCL (<see cref="SHA256.HashData(byte[])"/> — never hand-rolled), and
/// asserts the freshly-computed value is present in
/// <see cref="SecurityHeadersMiddleware.ContentSecurityPolicyValue"/>.
/// </para>
///
/// <para>
/// On drift (anyone edits the boot script without re-pinning) the failure message prints the
/// fresh <c>sha256-…</c> string for copy-paste into the const. The LF-normalization here MUST
/// match the normalization the middleware used to compute the pinned hash, and the
/// repo-root <c>.gitattributes</c> (<c>*.razor eol=lf</c>) makes the on-disk bytes identical
/// on Windows and the Linux container build (RESEARCH Pitfall 1). The browser-level proof that
/// this source hash matches the RENDERED bytes lands in the Plan 04 Playwright smoke test.
/// </para>
/// </summary>
[Trait("Category", "SecurityFix")]
public sealed class CspScriptHashTests
{
    /// <summary>
    /// Walks up from the test assembly location to the repo root and resolves the App.razor
    /// source file. The test bin lives at
    /// <c>ApStatsForOpenWrt.Tests/bin/Release/net10.0</c>; the source is at
    /// <c>ApStatsForOpenWrt/Components/App.razor</c> off the repo root.
    /// </summary>
    private static string LocateAppRazor()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ApStatsForOpenWrt", "Components", "App.razor");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate ApStatsForOpenWrt/Components/App.razor walking up from " +
            AppContext.BaseDirectory);
    }

    [Fact]
    public void Csp_ScriptSrcHash_MatchesAppRazorBootScript_MHrd04()
    {
        var razor = File.ReadAllText(LocateAppRazor());

        // Plan 21-04 fix (Rule 1 bug the Playwright CSP gate surfaced): strip Razor `@* *@`
        // comments BEFORE matching. App.razor's header comment contains the literal word
        // "<script>" ("…locks the <link> order … and <script> order (AdminLTE first, then
        // blazor.web.js)"), and that comment has no closing "</script>". A naive
        // `<script>(.*?)</script>` over the raw razor therefore opens at the COMMENT's
        // "<script>" and closes at the real block's "</script>", hashing ~1781 bytes of
        // comment-tail + boot script instead of the ~1078-byte boot script the browser hashes.
        // Stripping comments first makes this extraction match the rendered text node exactly.
        var razorNoComments = Regex.Replace(razor, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

        // Extract the single inline <script>…</script> (no src=) block — the anti-FOUC boot
        // script (App.razor lines ~21-43). Singleline so '.' spans the multi-line body.
        var m = Regex.Match(razorNoComments, @"<script>(?<body>.*?)</script>", RegexOptions.Singleline);
        Assert.True(m.Success, "App.razor inline <script> block not found.");

        // Normalize CRLF/CR → LF so the hash is identical regardless of git checkout state
        // (Windows CRLF vs Linux container LF). Mirrors the middleware's hash computation.
        var body = m.Groups["body"].Value.Replace("\r\n", "\n").Replace("\r", "\n");

        var hash = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        // On drift the message prints the freshly-computed hash for copy-paste into the const
        // (CONTEXT encourages this — a boot-script edit fails the build with the exact new pin
        // instead of a silent CSP break that only manifests in a browser).
        var present = SecurityHeadersMiddleware.ContentSecurityPolicyValue
            .Contains(hash, StringComparison.Ordinal);
        Assert.True(present,
            "CSP script-src hash drift — the App.razor inline boot script changed.\n" +
            "Update SecurityHeadersMiddleware.ContentSecurityPolicyValue script-src to:\n" +
            $"    'self' '{hash}'");
    }
}
