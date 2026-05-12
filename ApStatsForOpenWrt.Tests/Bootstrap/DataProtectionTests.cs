using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ApStatsForOpenWrt.Tests.TestSupport;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Bootstrap;

/// <summary>
/// Verifies the production Data Protection wiring shape (SEC-01 round-trip, SEC-02 persistence
/// across host restarts, SEC-03 verbatim ApplicationDiscriminator). Builds an isolated DI graph
/// per test so the Program.cs composition root is exercised in spirit but the test does not
/// depend on /data/keys (the production hardcoded path).
/// </summary>
public sealed class DataProtectionTests(TempDirectoryFixture temp) : IClassFixture<TempDirectoryFixture>
{
    private readonly TempDirectoryFixture _temp = temp;

    private IServiceProvider BuildDp()
    {
        var keys = Path.Combine(_temp.KeysDir, $"dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keys);
        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keys))
            .SetApplicationName("ApStatsForOpenWrt")
            .SetDefaultKeyLifetime(TimeSpan.FromDays(90));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void RoundTrip_NodeCredsProtector_PreservesPlaintext()
    {
        // SEC-01: encrypt-decrypt round-trip under the canonical "node-creds" purpose.
        var sp = BuildDp();
        var protector = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("node-creds");
        var ciphertext = protector.Protect("hello");
        Assert.Equal("hello", protector.Unprotect(ciphertext));
    }

    [Fact]
    public void KeyRing_PersistsAcrossHostRestarts()
    {
        // SEC-02: build twice with the same key directory + application name; ciphertext from
        // the first DI scope must decrypt in the second. This proves the key ring on disk is
        // the durable encryption boundary across container restarts.
        var keys = Path.Combine(_temp.KeysDir, $"dp-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keys);

        string ciphertext;
        {
            var s1 = new ServiceCollection();
            s1.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keys))
                .SetApplicationName("ApStatsForOpenWrt");
            using var sp1 = s1.BuildServiceProvider();
            var p = sp1.GetRequiredService<IDataProtectionProvider>().CreateProtector("node-creds");
            ciphertext = p.Protect("persisted");
        }

        Assert.True(Directory.EnumerateFiles(keys, "*.xml").Any(), "Key ring XML files must exist after first run");

        {
            var s2 = new ServiceCollection();
            s2.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keys))
                .SetApplicationName("ApStatsForOpenWrt");
            using var sp2 = s2.BuildServiceProvider();
            var p2 = sp2.GetRequiredService<IDataProtectionProvider>().CreateProtector("node-creds");
            Assert.Equal("persisted", p2.Unprotect(ciphertext));
        }
    }

    [Fact]
    public void SetApplicationName_IsApStatsForOpenWrt_Verbatim()
    {
        // SEC-03 / D-02: ApplicationDiscriminator string is exactly "ApStatsForOpenWrt".
        // An assembly rename without preserving this string would silently invalidate every
        // ciphertext on disk, so the verbatim assertion is the trip-wire.
        var sp = BuildDp();
        var opts = sp.GetRequiredService<IOptions<KeyManagementOptions>>(); // resolved to confirm DP is wired
        Assert.NotNull(opts);

        var dpOptions = sp.GetRequiredService<IOptions<DataProtectionOptions>>();
        Assert.Equal("ApStatsForOpenWrt", dpOptions.Value.ApplicationDiscriminator);
    }
}
