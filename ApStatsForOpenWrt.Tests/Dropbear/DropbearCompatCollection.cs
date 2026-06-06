using Xunit;

namespace ApStatsForOpenWrt.Tests.Dropbear;

/// <summary>
/// Serializes the DropbearCompat fixture consumption so two test classes do not race on the
/// dual Dropbear image build (legacy + current). Mirrors <c>Tests/Smoke/DockerSmokeCollection.cs</c>
/// 1:1 with a renamed collection. Phase 22 / Plan 22-03 / M-FLD-01 / D-07.
///
/// <c>DisableParallelization = true</c> is required: the two negotiation+round-trip facts must not
/// race on the shared <see cref="DropbearCompatFixture"/> image build / container startup.
/// </summary>
[CollectionDefinition("DropbearCompat", DisableParallelization = true)]
public sealed class DropbearCompatCollection;
