using Xunit;

namespace ApStatsForOpenWrt.Tests.Integration;

/// <summary>
/// Disables parallelization across all integration test classes that build a
/// <c>WebApplicationFactory&lt;Program&gt;</c>. The factory's Serilog bootstrap calls
/// <c>ReloadableLogger.Freeze()</c> the first time a host is built; concurrent factory
/// instances racing through that path throw <c>InvalidOperationException: The logger is
/// already frozen</c> and <c>The entry point exited without ever building an IHost</c>.
/// Wrapping every integration class in this collection serializes them within the test
/// process while still letting unit tests run in parallel.
/// </summary>
[CollectionDefinition("Integration", DisableParallelization = true)]
public sealed class IntegrationCollection;
