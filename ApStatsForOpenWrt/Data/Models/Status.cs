namespace ApStatsForOpenWrt.Data.Models;

/// <summary>
/// Lifecycle status of a polled entity (node or station). Stored on disk as <see cref="int"/>
/// per CONTEXT D-08 — EF default; no value converter is registered.
/// </summary>
public enum Status
{
    Online,
    Offline,
    Polling
}
