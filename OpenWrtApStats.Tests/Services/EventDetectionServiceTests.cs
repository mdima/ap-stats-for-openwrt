using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWrtApStats.Data.Models;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services;
using OpenWrtApStats.Services.Polling;
using Xunit;

namespace OpenWrtApStats.Tests.Services;

/// <summary>
/// Phase 8 Plan 08-07 Task 1 — exercises <see cref="EventDetectionService"/> against
/// fake repos + a real <see cref="ToastService"/> wired through a hand-rolled
/// <c>FakeGate : INotificationsGate</c>. Locks the D-04 / D-05 / D-06 / D-07 transition
/// rules from 08-CONTEXT.md and the LOCKED toast templates from 08-UI-SPEC.md
/// §Toast copy templates.
///
/// Test stack: xUnit v3 + bUnit 2.7.2 + Microsoft.Testing.Extensions.CodeCoverage (NEVER coverlet).
/// Coverage gate per D-18: ≥ 80% line + ≥ 80% branch on EventDetectionService.cs.
/// </summary>
public sealed class EventDetectionServiceTests
{
    private sealed class FakeGate : INotificationsGate
    {
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// In-memory <see cref="IStationRepo"/> under test control. Records call counts so
    /// Test 2 can assert the bootstrap MAC scan happens EXACTLY once.
    /// </summary>
    private sealed class FakeStationRepo : IStationRepo
    {
        public List<WLANStation> AllStations { get; set; } = new();
        public List<string> SeedMacs { get; set; } = new();
        public int GetAllMacsAsyncCallCount { get; private set; }
        public int GetAllAsyncCallCount { get; private set; }

        public Task<IReadOnlyList<WLANStation>> GetAllAsync(CancellationToken ct = default)
        {
            GetAllAsyncCallCount++;
            return Task.FromResult<IReadOnlyList<WLANStation>>(AllStations.ToList());
        }

        public Task<IReadOnlyList<string>> GetAllMacsAsync(CancellationToken ct = default)
        {
            GetAllMacsAsyncCallCount++;
            return Task.FromResult<IReadOnlyList<string>>(SeedMacs.ToList());
        }

        public Task<WLANStation?> GetByMacAsync(string macAddress, CancellationToken ct = default)
            => Task.FromResult<WLANStation?>(null);

        public Task SetNameAsync(string macAddress, string? name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteByMacAsync(string macAddress, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertCycleAsync(OpenWrtApStats.Data.DatabaseContext db, IEnumerable<WLANStation> live, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>In-memory <see cref="INodeRepo"/> under test control.</summary>
    private sealed class FakeNodeRepo : INodeRepo
    {
        public List<WiFiNode> AllNodes { get; set; } = new();

        public Task<IReadOnlyList<WiFiNode>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WiFiNode>>(AllNodes.ToList());

        public Task<WiFiNode?> GetByIdAsync(int id, CancellationToken ct = default) => Task.FromResult<WiFiNode?>(null);
        public Task AddAsync(WiFiNode node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateMetadataAsync(int id, string hostname, string username, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdatePasswordAsync(int id, string encryptedPassword, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllHostKeyFingerprintsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHostKeyFingerprintAsync(int nodeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AnyEncryptedPasswordsAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(AllNodes.Count > 0);
        public Task MarkAllOfflineAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAllOfflineAsync(OpenWrtApStats.Data.DatabaseContext db, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateOnlineAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, DateTime lastPoll, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHostKeyFingerprintAsync(OpenWrtApStats.Data.DatabaseContext db, int nodeId, string fingerprint, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Harness : IDisposable
    {
        public PollNotifier Notifier { get; } = new();
        public FakeGate Gate { get; } = new();
        public FakeNodeRepo NodeRepo { get; } = new();
        public FakeStationRepo StationRepo { get; } = new();
        public ToastService Toasts { get; }
        public List<ToastEntry> Captured { get; } = new();
        public ServiceProvider Sp { get; }

        public Harness()
        {
            var sc = new ServiceCollection();
            sc.AddSingleton<INodeRepo>(NodeRepo);
            sc.AddSingleton<IStationRepo>(StationRepo);
            Sp = sc.BuildServiceProvider();

            Toasts = new ToastService(Gate, NullLogger<ToastService>.Instance);
            Toasts.OnShow += entry =>
            {
                Captured.Add(entry);
                return Task.CompletedTask;
            };
        }

        public EventDetectionService Build() =>
            new(Notifier, Toasts, Gate, Sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EventDetectionService>.Instance);

        public Task RaiseAsync(int total = 1, int online = 1, int failed = 0, int stations = 0) =>
            Notifier.RaiseAsync(new PollSnapshot(total, online, failed, stations, DateTime.UtcNow));

        public void Dispose() => Sp.Dispose();
    }

    private static WiFiNode Node(int id, string hostname, Status status) =>
        new() { Id = id, Hostname = hostname, Username = "root", Password = "", Status = status, LastPollDate = DateTime.UtcNow };

    private static WLANStation Station(string mac, string? name = null, string? hostname = null) =>
        new() { MacAddress = mac, Name = name, Hostname = hostname, Status = Status.Online, LastPollDate = DateTime.UtcNow };

    // 1. Constructor subscribes to PollNotifier.Cycled. -----------------------------------------
    [Fact]
    public async Task Constructor_SubscribesToCycled()
    {
        using var h = new Harness();
        using var svc = h.Build();

        // If subscription wired up, raising the event reaches the service and yields a path
        // through DiffAndEmitAsync. With one Online node, no transitions and no MACs, no toast
        // is emitted — but GetAllMacsAsync IS called (bootstrap), proving the subscription.
        await h.RaiseAsync();

        Assert.Equal(1, h.StationRepo.GetAllMacsAsyncCallCount);
    }

    // 2. Bootstrap MAC scan happens EXACTLY once across N cycles (D-06 lazy seeding). ----------
    [Fact]
    public async Task FirstCycle_TriggersBootstrap_OnceOnly()
    {
        using var h = new Harness();
        h.StationRepo.SeedMacs = new() { "AA:BB:CC:DD:EE:FF" };
        using var svc = h.Build();

        await h.RaiseAsync();
        await h.RaiseAsync();
        await h.RaiseAsync();

        Assert.Equal(1, h.StationRepo.GetAllMacsAsyncCallCount);
    }

    // 3. D-10 gate — notifications disabled → NO toasts emitted. -------------------------------
    [Fact]
    public async Task GateClosed_EmitsNoToasts_RegardlessOfChanges()
    {
        using var h = new Harness();
        h.Gate.IsEnabled = false;
        h.NodeRepo.AllNodes = new()
        {
            Node(1, "ap-kitchen", Status.Online),
            Node(2, "ap-attic", Status.Offline),    // would normally fire AP→Offline toast
        };
        h.StationRepo.AllStations = new()
        {
            Station("00:11:22:33:44:55"),           // would normally fire new-station toast
        };
        using var svc = h.Build();

        await h.RaiseAsync();

        Assert.Empty(h.Captured);
        // Defense-in-depth: bootstrap also did NOT run (early-exit BEFORE the diff per D-10).
        Assert.Equal(0, h.StationRepo.GetAllMacsAsyncCallCount);
    }

    // 4. D-04 — Online → Offline transition fires "Access point offline" warning. --------------
    [Fact]
    public async Task NodeTransitionsToOffline_EmitsWarningToast()
    {
        using var h = new Harness();
        // Cycle 1: ap is Online. Cycle 2: same ap flips to Offline.
        h.NodeRepo.AllNodes = new() { Node(7, "ap-kitchen", Status.Online) };
        using var svc = h.Build();
        await h.RaiseAsync();
        h.Captured.Clear();    // discard any cycle-1 noise

        h.NodeRepo.AllNodes = new() { Node(7, "ap-kitchen", Status.Offline) };
        await h.RaiseAsync();

        var toast = Assert.Single(h.Captured);
        Assert.Equal("Access point offline", toast.Title);
        Assert.Equal("warning", toast.Severity);
        Assert.Contains("ap-kitchen", toast.Message);
    }

    // 5. D-05 — Offline → Online transition fires "Access point online" success. ---------------
    [Fact]
    public async Task NodeTransitionsToOnline_EmitsSuccessToast()
    {
        using var h = new Harness();
        h.NodeRepo.AllNodes = new() { Node(9, "ap-attic", Status.Offline) };
        using var svc = h.Build();
        await h.RaiseAsync();
        h.Captured.Clear();

        h.NodeRepo.AllNodes = new() { Node(9, "ap-attic", Status.Online) };
        await h.RaiseAsync();

        var toast = Assert.Single(h.Captured);
        Assert.Equal("Access point online", toast.Title);
        Assert.Equal("success", toast.Severity);
        Assert.Contains("ap-attic", toast.Message);
        Assert.Contains("reachable", toast.Message);
    }

    // 6. D-06 — New MAC not in bootstrap set → "New station" info; same MAC again → no re-toast.
    [Fact]
    public async Task NewMac_EmitsInfoToast_OnlyFirstTime()
    {
        using var h = new Harness();
        h.StationRepo.SeedMacs = new() { "AA:AA:AA:AA:AA:AA" };  // pre-known
        h.StationRepo.AllStations = new()
        {
            Station("AA:AA:AA:AA:AA:AA"),                       // known — no toast
            Station("BB:BB:BB:BB:BB:BB", name: "Phone"),        // new — toast
        };
        using var svc = h.Build();

        await h.RaiseAsync();
        var toast = Assert.Single(h.Captured);
        Assert.Equal("New station", toast.Title);
        Assert.Equal("info", toast.Severity);
        Assert.Contains("BB:BB:BB:BB:BB:BB", toast.Message);
        Assert.Contains("Phone", toast.Message);

        // Cycle 2: same stations — no new toast.
        h.Captured.Clear();
        await h.RaiseAsync();
        Assert.Empty(h.Captured);
    }

    // 7. D-07 — exactly 3 consecutive failed cycles fires "Polling failure"; 4th, 5th do NOT
    //    re-toast; counter resets on a successful cycle and re-arms for another 3-fail run. ----
    [Fact]
    public async Task PersistentFailure_TogglesOnExactlyThirdFailure_ResetsAfterSuccess()
    {
        using var h = new Harness();
        var node = Node(3, "ap-garage", Status.Offline);
        h.NodeRepo.AllNodes = new() { node };
        using var svc = h.Build();

        // Cycle 1, 2 — Offline but counter is 1, 2 — no toast yet.
        await h.RaiseAsync();
        h.Captured.RemoveAll(t => t.Title == "Access point offline"); // ignore the AP→Offline that fires once on cycle 1
        await h.RaiseAsync();
        Assert.Empty(h.Captured);

        // Cycle 3 — counter hits exactly 3 — ONE toast fires.
        await h.RaiseAsync();
        var toast = Assert.Single(h.Captured);
        Assert.Equal("Polling failure", toast.Title);
        Assert.Equal("danger", toast.Severity);
        Assert.Contains("ap-garage", toast.Message);
        Assert.Contains("3 consecutive", toast.Message);

        // Cycle 4, 5 — still failing, NO re-toast.
        h.Captured.Clear();
        await h.RaiseAsync();
        await h.RaiseAsync();
        Assert.Empty(h.Captured);

        // Cycle 6 — node recovers (Offline → Online). Resets counter; the AP→Online toast
        // fires (D-05), but that's the success toast, not the polling-failure toast.
        h.NodeRepo.AllNodes = new() { Node(3, "ap-garage", Status.Online) };
        await h.RaiseAsync();
        Assert.Single(h.Captured, t => t.Title == "Access point online");
        Assert.DoesNotContain(h.Captured, t => t.Title == "Polling failure");

        // Cycles 7, 8, 9 — three more failures — toast fires AGAIN on the third (counter re-armed).
        h.Captured.Clear();
        h.NodeRepo.AllNodes = new() { Node(3, "ap-garage", Status.Offline) };
        await h.RaiseAsync();
        await h.RaiseAsync();
        // Cycle 9 — third consecutive failure since the reset.
        h.Captured.Clear();   // strip the AP→Offline that fired on cycle 7
        await h.RaiseAsync();
        Assert.Single(h.Captured, t => t.Title == "Polling failure");
    }

    // 8. Architectural — EventDetectionService NEVER references IDbContextFactory directly.
    //    PER-04: only repositories may inject IDbContextFactory. Plan 08-07 hard constraint #1.
    [Fact]
    public void Type_DoesNotInjectIDbContextFactory_Directly()
    {
        var t = typeof(EventDetectionService);

        // Constructor parameters
        foreach (var ctor in t.GetConstructors())
        {
            foreach (var p in ctor.GetParameters())
            {
                Assert.False(MentionsDbContextFactory(p.ParameterType),
                    $"EventDetectionService ctor injects {p.ParameterType.FullName} — forbidden by PER-04.");
            }
        }

        // Fields
        var fields = t.GetFields(System.Reflection.BindingFlags.NonPublic
                                | System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.Instance);
        foreach (var f in fields)
        {
            Assert.False(MentionsDbContextFactory(f.FieldType),
                $"EventDetectionService holds field {f.Name} of {f.FieldType.FullName} — forbidden by PER-04.");
        }

        static bool MentionsDbContextFactory(Type type)
        {
            if (!type.IsGenericType) return false;
            var gen = type.GetGenericTypeDefinition();
            // System literal name to avoid runtime EFCore reference in this test.
            return gen.FullName?.StartsWith("Microsoft.EntityFrameworkCore.IDbContextFactory", StringComparison.Ordinal) == true;
        }
    }
}
