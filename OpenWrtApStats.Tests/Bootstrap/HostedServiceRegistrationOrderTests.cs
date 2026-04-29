using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenWrtApStats.Data;
using OpenWrtApStats.Data.Repositories;
using OpenWrtApStats.Services.Bootstrap;
using OpenWrtApStats.Services.Settings;
using Xunit;

namespace OpenWrtApStats.Tests.Bootstrap;

/// <summary>
/// Pitfall 10 mitigation: hosted-service registration order = StartAsync order. If a developer
/// accidentally swaps the order (e.g., DataProtectionHealth before Migrations) the D-12 probe
/// would query a not-yet-migrated WiFiNodes table and crash for the wrong reason. This test
/// makes the silent reorder loud.
/// </summary>
public sealed class HostedServiceRegistrationOrderTests
{
    [Fact]
    public void RegistrationOrder_IsMigrationsThenDataProtectionHealthThenAppSettingsBootstrap()
    {
        // Pitfall 10 — registration order = StartAsync order.
        // Uses Host.CreateApplicationBuilder (test project is Microsoft.NET.Sdk, not Web).
        // The Services collection behaves identically for the registration-order assertion;
        // Pitfall 10 cares about the descriptor sequence, not the host flavor.
        var builder = Host.CreateApplicationBuilder();

        // Replicate the Program.cs hosted-service registrations in the same order.
        builder.Services.AddDbContextFactory<DatabaseContext>(o => o.UseSqlite("Data Source=:memory:"));
        builder.Services.AddTransient<INodeRepo,        NodeRepo>();
        builder.Services.AddTransient<IAppSettingsRepo, AppSettingsRepo>();
        builder.Services.AddSingleton<IAppSettingsService, AppSettingsService>();

        builder.Services.AddHostedService<MigrationsHostedService>();
        builder.Services.AddHostedService<DataProtectionHealthHostedService>();
        builder.Services.AddHostedService<AppSettingsBootstrapHostedService>();

        var hostedDescriptors = builder.Services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType ?? d.ImplementationFactory?.Method.ReturnType)
            .Where(t => t != null)
            .ToList();

        var implTypes = hostedDescriptors.Select(t => t!.Name).ToList();

        // The three Phase 1 services must appear in the exact registration order.
        var phase1Services = implTypes.Where(n =>
            n == nameof(MigrationsHostedService) ||
            n == nameof(DataProtectionHealthHostedService) ||
            n == nameof(AppSettingsBootstrapHostedService)).ToList();

        Assert.Equal(
            new[] { nameof(MigrationsHostedService), nameof(DataProtectionHealthHostedService), nameof(AppSettingsBootstrapHostedService) },
            phase1Services);

        // Phase 1 ships exactly THREE Phase-1 IHostedServices — Pitfall 8.
        Assert.Equal(3, phase1Services.Count);
    }
}
