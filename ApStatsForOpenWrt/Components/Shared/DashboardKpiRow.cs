using ApStatsForOpenWrt.Data.Models;

namespace ApStatsForOpenWrt.Components.Shared;

/// <summary>
/// Public projection consumed by <see cref="KpiTileBar"/> — Plan 08-08 (Dashboard
/// reorganization) projects each private <c>Dashboard.DashboardRow</c> into this type so
/// <see cref="KpiTileBar"/> can derive its 4 KPI counts without referencing the Phase 5
/// nested record (which is declared private inside <c>Dashboard.razor</c> @code).
///
/// Bucket rules (must match <see cref="KpiTileBar"/>.Recompute and Phase 5
/// Dashboard.ComputeFilteredRows status filter):
///   - Online  iff <see cref="StationStatus"/> == <see cref="Status.Online"/>
///   - Stale   iff <see cref="StationStatus"/> == <see cref="Status.Offline"/> AND <see cref="IsStale"/>
///   - Offline iff <see cref="StationStatus"/> == <see cref="Status.Offline"/> AND !<see cref="IsStale"/>
///
/// <see cref="Status.Polling"/> stations count toward _total only — they do not surface in
/// dashboard rows in Phase 5 (Polling is a transient mid-cycle state).
/// </summary>
/// <param name="StationStatus">Persisted station status (Online | Offline | Polling).</param>
/// <param name="IsStale">UI-only sub-state derived in the parent from CycleEndUtc + LastPollDate
/// + PollingIntervalSeconds (Phase 5 D-09); only meaningful when StationStatus == Offline.</param>
public sealed record DashboardKpiRow(Status StationStatus, bool IsStale);
