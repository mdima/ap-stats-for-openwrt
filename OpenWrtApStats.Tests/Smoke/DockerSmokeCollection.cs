using Xunit;

namespace OpenWrtApStats.Tests.Smoke;

/// <summary>
/// Serializes Docker-smoke fixture consumption so two smoke test classes do not race
/// on the image build or the named-volume creation. Mirrors the IntegrationCollection
/// pattern from <c>OpenWrtApStats.Tests/Integration/IntegrationCollection.cs</c>.
/// Phase 7 / D-06 / RESEARCH Finding #5.
/// </summary>
[CollectionDefinition("DockerSmoke", DisableParallelization = true)]
public sealed class DockerSmokeCollection;
