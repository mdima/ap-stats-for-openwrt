using Microsoft.Extensions.Logging;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Services.Polling.Exceptions;
using Renci.SshNet;
using Renci.SshNet.Common;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace OpenWrtApStats.Services.Polling.Ssh;

/// <summary>
/// Production <see cref="ISshSession"/>. Wraps SSH.NET 2025.1.0 <see cref="SshClient"/>. Wires
/// <see cref="SshClient.HostKeyReceived"/> for D-02 (TOFU on null expectedFingerprint) and D-03
/// (mismatch translates to <see cref="HostKeyMismatchException"/>). Always disposes inside
/// <c>try/catch</c> to mitigate the SSH.NET dispose-hang (Pitfall 5; verified against SSH.NET
/// issues #1275, #122, #355).
/// </summary>
public sealed class RealSshSession(ILogger<RealSshSession> log) : ISshSession
{
#pragma warning disable IDE0052, CS0414 // Held for diagnostic logging in future revisions
    private readonly ILogger<RealSshSession> _log = log;
#pragma warning restore IDE0052, CS0414
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
}
