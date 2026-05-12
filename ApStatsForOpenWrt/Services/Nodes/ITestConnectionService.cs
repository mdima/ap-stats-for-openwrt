namespace OpenWrtApStats.Services.Nodes;

/// <summary>
/// NODE-02 / D-07 seam: drives an SSH probe against a hostname/username/plaintext-password
/// triple, returns <see cref="TestConnectionOutcome"/>. Read-only (no DB writes — D-07).
/// </summary>
public interface ITestConnectionService
{
    Task<TestConnectionOutcome> TestAsync(
        string hostname,
        string username,
        string plaintextPassword,
        int? existingNodeId,
        CancellationToken ct);
}
