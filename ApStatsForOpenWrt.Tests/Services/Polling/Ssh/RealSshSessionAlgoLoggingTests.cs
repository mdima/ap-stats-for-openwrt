using ApStatsForOpenWrt.Services.Polling.Ssh;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Services.Polling.Ssh;

/// <summary>
/// M-SEC-08 (Plan 16-02) unit tests pinning the algo-negotiation visibility log shapes
/// and the SHA-1 family detection rule.
///
/// <para>
/// These drive the <see cref="RealSshSession.EmitM_SEC_08_Logs"/> internal static helper
/// directly with synthetic algorithm inputs (the SSH.NET ConnectionInfo properties cannot
/// be faked cheaply, hence the seam). InternalsVisibleTo("ApStatsForOpenWrt.Tests") is
/// granted in ApStatsForOpenWrt.csproj.
/// </para>
///
/// <para>
/// Templates asserted VERBATIM from CONTEXT.md Decision 1 ("M-SEC-06 TOFU + M-SEC-08
/// algo-negotiation log line shapes"). SHA-1 detection rule covers both the substring
/// branch (sha1 case-insensitive) and the explicit ssh-rsa HostKey branch.
/// </para>
/// </summary>
[Trait("Category", "SecurityFix")]
public sealed class RealSshSessionAlgoLoggingTests
{
    private const string InfoTemplate =
        "M-SEC-08: SSH algorithms negotiated: NodeId={NodeId} Hostname={Hostname} Kex={Kex} HostKey={HostKey} Encryption={Encryption} Mac={Mac}";

    private const string WarnTemplate =
        "M-SEC-08: SSH algorithms negotiated includes SHA-1 family: NodeId={NodeId} Hostname={Hostname} ShaFamilyMembers={Members}";

    private static (List<CapturedLog> info, List<CapturedLog> warn) Emit(
        string? kex, string? hostKey, string? encryption, string? mac,
        int nodeId = 42, string hostname = "test-ap-1")
    {
        var logger = new CapturingLogger();
        RealSshSession.EmitM_SEC_08_Logs(logger, nodeId, hostname, kex, hostKey, encryption, mac);
        var info = logger.Records.FindAll(r => r.Level == LogLevel.Information);
        var warn = logger.Records.FindAll(r => r.Level == LogLevel.Warning);
        return (info, warn);
    }

    [Theory]
    [InlineData("curve25519-sha256", "ssh-ed25519", "aes256-gcm@openssh.com", "hmac-sha2-256-etm@openssh.com")]
    [InlineData("ecdh-sha2-nistp256", "rsa-sha2-512", "chacha20-poly1305@openssh.com", "hmac-sha2-512")]
    public void ModernAlgos_OnlyInformationFires(string kex, string hostKey, string enc, string mac)
    {
        var (info, warn) = Emit(kex, hostKey, enc, mac);

        Assert.Single(info);
        Assert.Empty(warn);

        var rec = info[0];
        Assert.Equal(42, rec.Args["NodeId"]);
        Assert.Equal("test-ap-1", rec.Args["Hostname"]);
        Assert.Equal(kex, rec.Args["Kex"]);
        Assert.Equal(hostKey, rec.Args["HostKey"]);
        Assert.Equal(enc, rec.Args["Encryption"]);
        Assert.Equal(mac, rec.Args["Mac"]);
    }

    [Fact]
    public void HmacSha1Mac_WarningFires_MembersNamesMacOnly()
    {
        var (info, warn) = Emit("curve25519-sha256", "ssh-ed25519", "aes256-gcm@openssh.com", "hmac-sha1");

        Assert.Single(info);
        Assert.Single(warn);
        Assert.Equal("Mac=hmac-sha1", warn[0].Args["Members"]);
    }

    [Fact]
    public void SshRsaHostKey_WarningFires_ViaExplicitBranch()
    {
        // Critical edge case: literal "ssh-rsa" does NOT contain substring "sha1" but MUST
        // trigger via the explicit ssh-rsa HostKey branch.
        var (info, warn) = Emit("curve25519-sha256", "ssh-rsa", "aes256-gcm@openssh.com", "hmac-sha2-256-etm@openssh.com");

        Assert.Single(info);
        Assert.Single(warn);
        Assert.Equal("HostKey=ssh-rsa", warn[0].Args["Members"]);
    }

    [Fact]
    public void DhGroup14Sha1Kex_WarningFires_ViaSubstring()
    {
        var (info, warn) = Emit("diffie-hellman-group14-sha1", "ssh-ed25519", "aes256-gcm@openssh.com", "hmac-sha2-256-etm@openssh.com");

        Assert.Single(info);
        Assert.Single(warn);
        Assert.Equal("Kex=diffie-hellman-group14-sha1", warn[0].Args["Members"]);
    }

    [Fact]
    public void MultipleSlotsTrigger_MembersListsAllInDeclarationOrder()
    {
        var (info, warn) = Emit("diffie-hellman-group14-sha1", "ssh-rsa", "aes256-gcm@openssh.com", "hmac-sha1");

        Assert.Single(info);
        Assert.Single(warn);
        // Kex first, HostKey second, Encryption skipped, Mac last — slot declaration order.
        Assert.Equal("Kex=diffie-hellman-group14-sha1,HostKey=ssh-rsa,Mac=hmac-sha1", warn[0].Args["Members"]);
    }

    [Fact]
    public void CaseInsensitiveSubstring_Triggers()
    {
        var (info, warn) = Emit("curve25519-sha256", "ssh-ed25519", "aes256-gcm@openssh.com", "HMAC-SHA1");

        Assert.Single(info);
        Assert.Single(warn);
        Assert.Equal("Mac=HMAC-SHA1", warn[0].Args["Members"]);
    }

    [Fact]
    public void VerbatimInformationTemplate_AssertEqual()
    {
        var (info, _) = Emit("curve25519-sha256", "ssh-ed25519", "aes256-gcm@openssh.com", "hmac-sha2-256-etm@openssh.com");

        Assert.Single(info);
        Assert.Equal(
            "M-SEC-08: SSH algorithms negotiated: NodeId={NodeId} Hostname={Hostname} Kex={Kex} HostKey={HostKey} Encryption={Encryption} Mac={Mac}",
            info[0].Template);
    }

    [Fact]
    public void VerbatimWarningTemplate_AssertEqual()
    {
        var (_, warn) = Emit("curve25519-sha256", "ssh-ed25519", "aes256-gcm@openssh.com", "hmac-sha1");

        Assert.Single(warn);
        Assert.Equal(
            "M-SEC-08: SSH algorithms negotiated includes SHA-1 family: NodeId={NodeId} Hostname={Hostname} ShaFamilyMembers={Members}",
            warn[0].Template);
    }

    // ── Capture sink ───────────────────────────────────────────────────────────

    private sealed record CapturedLog(
        LogLevel Level, string Template, IReadOnlyDictionary<string, object?> Args);

    /// <summary>
    /// Minimal <see cref="ILogger"/> that captures every <c>Log(...)</c> call's level,
    /// message template (the <c>{OriginalFormat}</c> entry in the structured state), and
    /// the named structured arguments. <c>LoggerExtensions.LogInformation</c>/<c>LogWarning</c>
    /// build the state as an <see cref="IReadOnlyList{T}"/> of
    /// <see cref="KeyValuePair{TKey,TValue}"/> where the last entry is
    /// <c>{OriginalFormat}</c> → the template string.
    /// </summary>
    private sealed class CapturingLogger : ILogger<RealSshSession>
    {
        public List<CapturedLog> Records { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var template = "";
            var args = new Dictionary<string, object?>();

            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kvp in kvps)
                {
                    if (kvp.Key == "{OriginalFormat}")
                        template = kvp.Value as string ?? "";
                    else
                        args[kvp.Key] = kvp.Value;
                }
            }

            Records.Add(new CapturedLog(logLevel, template, args));
        }
    }
}
