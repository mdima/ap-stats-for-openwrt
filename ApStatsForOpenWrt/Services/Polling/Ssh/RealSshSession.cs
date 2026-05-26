using Microsoft.Extensions.Logging;
using ApStatsForOpenWrt.Data.Models;
using ApStatsForOpenWrt.Services.Polling.Exceptions;
using Renci.SshNet;
using Renci.SshNet.Common;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace ApStatsForOpenWrt.Services.Polling.Ssh;

/// <summary>
/// Production <see cref="ISshSession"/>. Wraps SSH.NET 2025.1.0 <see cref="SshClient"/>. Wires
/// <see cref="SshClient.HostKeyReceived"/> for D-02 (TOFU on null expectedFingerprint) and D-03
/// (mismatch translates to <see cref="HostKeyMismatchException"/>). Always disposes inside
/// <c>try/catch</c> to mitigate the SSH.NET dispose-hang (Pitfall 5; verified against SSH.NET
/// issues #1275, #122, #355).
/// </summary>
public sealed class RealSshSession(ILogger<RealSshSession> log) : ISshSession
{
    // M-SEC-08 / plan 16-02 — _log is now actively used (algo-negotiation visibility).
    // M-SEC-06 / plan 16-03 — _log also used for TOFU-capture Warning.
    private readonly ILogger<RealSshSession> _log = log;
    private SshClient? _client;
    private string? _expectedFingerprint;
    private Action<string>? _onAccepted;
    private HostKeyMismatchException? _capturedMismatch;

    public async Task ConnectAsync(WiFiNode node, string plaintextPassword, string? expectedFingerprint,
        Action<string>? onHostKeyAccepted, CancellationToken ct)
    {
        _expectedFingerprint = expectedFingerprint;
        _onAccepted = onHostKeyAccepted;
        _capturedMismatch = null;

        var connInfo = new ConnectionInfo(
            node.Hostname,
            node.Username,
            new PasswordAuthenticationMethod(node.Username, plaintextPassword))
        {
            Timeout = TimeSpan.FromSeconds(5)        // POLL-04 belt
        };

        // Pitfall 1: extend the algorithm lists (does not replace).
        SshAlgorithmFallbackPolicy.ExtendForOldDropbear(connInfo);

        _client = new SshClient(connInfo);
        _client.HostKeyReceived += OnHostKeyReceived;

        try
        {
            // SSH.NET's Connect is blocking; wrap in Task.Run + linked CTS observation.
            await Task.Run(() => _client.Connect(), ct);

            // If our HostKeyReceived handler captured a mismatch (D-03), surface it.
            if (_capturedMismatch is not null) throw _capturedMismatch;

            // M-SEC-08 / plan 16-02 — algo-negotiation visibility logs (see helper below).
            var ci = _client.ConnectionInfo;
            EmitM_SEC_08_Logs(
                _log, node.Id, node.Hostname,
                ci.CurrentKeyExchangeAlgorithm, ci.CurrentHostKeyAlgorithm,
                ci.CurrentClientEncryption, ci.CurrentClientHmacAlgorithm);
        }
        catch (SshConnectionException) when (_capturedMismatch is not null)
        {
            // SSH.NET wrapped/swallowed the in-handler throw; surface the captured exception.
            throw _capturedMismatch;
        }
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        // SHA256 base64 (no padding, no prefix) — matches `ssh` CLI fingerprint format.
        var actual = e.FingerPrintSHA256;

        if (_expectedFingerprint is null)
        {
            // D-02: first-connect TOFU. Accept the key; capture for orchestrator-side persistence.
            e.CanTrust = true;
            _onAccepted?.Invoke(actual);
        }
        else if (string.Equals(_expectedFingerprint, actual, StringComparison.Ordinal))
        {
            // Match — proceed.
            e.CanTrust = true;
        }
        else
        {
            // D-03: mismatch — refuse the connection.
            e.CanTrust = false;
            _capturedMismatch = new HostKeyMismatchException(_expectedFingerprint, actual);
        }
    }

    public Task<string> RunCommandAsync(string command, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var cmd = _client!.RunCommand(command);
            return cmd.Result;
        }, ct);
    }

    public Task<(int ExitCode, string StdOut, string StdErr)> RunCommandWithExitCodeAsync(string command, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var cmd = _client!.CreateCommand(command);
            cmd.Execute();
            return (cmd.ExitStatus ?? 0, cmd.Result, cmd.Error);
        }, ct);
    }

    public ValueTask DisposeAsync()
    {
        // Pitfall 5 — dispose-hang mitigation. Both Disconnect() and Dispose() can hang; swallow.
        try { _client?.Disconnect(); } catch { /* swallow — SSH.NET issue #1275 */ }
        try { _client?.Dispose();    } catch { /* swallow */ }
        _client = null;
        return ValueTask.CompletedTask;
    }

    // M-SEC-08 / plan 16-02 — SHA-1 detection rule per CONTEXT.md Decision 1 (SHA-1
    // detection edge case (ssh-rsa)):
    //   - substring 'sha1' (case-insensitive) catches hmac-sha1, hmac-sha1-96,
    //     hmac-sha1-etm, diffie-hellman-group14-sha1, and any future SHA-1 variant.
    //   - explicit string-equal 'ssh-rsa' catches the RSA-with-SHA-1 host-key shape
    //     whose literal name does NOT contain the substring 'sha1'.
    // The two conditions are OR'd. Slot triggers if EITHER fires.
    private static bool IsSha1Family(string slot, string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Contains("sha1", StringComparison.OrdinalIgnoreCase)) return true;
        // Explicit ssh-rsa branch — only applies to the HostKey slot. Other slots
        // can never legitimately equal 'ssh-rsa' so the guard is per-slot to keep
        // the rule audit-clear.
        if (string.Equals(slot, "HostKey", StringComparison.Ordinal) &&
            string.Equals(value, "ssh-rsa", StringComparison.Ordinal)) return true;
        return false;
    }

    // M-SEC-08 / plan 16-02 — algo-negotiation visibility logs. internal static so the
    // unit tests can drive it with synthetic algorithm inputs WITHOUT a real SshClient /
    // network (the SSH.NET ConnectionInfo properties cannot be faked cheaply). CONTEXT.md
    // Decision 1 pinned shapes; M-SEC-NN: prefix-tag matches Phase 15 M-SEC-04 / M-SEC-10
    // convention + Phase 16 sibling 16-01 / 16-03. Information fires first (full algo set),
    // then the conditional SHA-1 Warning supplement.
    internal static void EmitM_SEC_08_Logs(
        ILogger log, int nodeId, string hostname,
        string? kex, string? hostKey, string? encryption, string? mac)
    {
        var kexV = kex ?? "<unknown>";
        var hostKeyV = hostKey ?? "<unknown>";
        var encryptionV = encryption ?? "<unknown>";
        var macV = mac ?? "<unknown>";

        log.LogInformation(
            "M-SEC-08: SSH algorithms negotiated: NodeId={NodeId} Hostname={Hostname} Kex={Kex} HostKey={HostKey} Encryption={Encryption} Mac={Mac}",
            nodeId, hostname, kexV, hostKeyV, encryptionV, macV);

        // SHA-1 family supplement (CONTEXT.md Decision 1). Warning fires when ANY slot
        // triggers IsSha1Family. Members render: comma-separated Slot=Value pairs in
        // declaration order (Kex, HostKey, Encryption, Mac).
        var triggered = new List<string>(4);
        if (IsSha1Family("Kex", kexV))               triggered.Add($"Kex={kexV}");
        if (IsSha1Family("HostKey", hostKeyV))       triggered.Add($"HostKey={hostKeyV}");
        if (IsSha1Family("Encryption", encryptionV)) triggered.Add($"Encryption={encryptionV}");
        if (IsSha1Family("Mac", macV))               triggered.Add($"Mac={macV}");

        if (triggered.Count > 0)
        {
            log.LogWarning(
                "M-SEC-08: SSH algorithms negotiated includes SHA-1 family: NodeId={NodeId} Hostname={Hostname} ShaFamilyMembers={Members}",
                nodeId, hostname, string.Join(",", triggered));
        }
    }
}
