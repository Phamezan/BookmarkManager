namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// Feature-level Library settings (section <c>"Library"</c>). Provider toggles live in
/// <see cref="LibraryProviderOptions"/>; this class carries the lifecycle/scheduling knobs - the
/// kill switch, the idle-unload timeout shared by the ONNX models and the vector cache, and the
/// nightly background-run window (same schedule/time-zone shape as <c>Backup</c>).
/// </summary>
public sealed class LibraryOptions
{
    public const string SectionName = "Library";

    /// <summary>Master kill switch. When false the background workers (catalog sync, embedding
    /// backfill, model warmup) are not registered and the Library endpoints return 503.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Minutes of no in-flight inference before the embedding/reranker ONNX sessions and the
    /// vector cache are released so GC can reclaim the memory. Reloaded on the next use.</summary>
    public int ModelIdleUnloadMinutes { get; init; } = 15;

    /// <summary>Local time of the nightly catalog sync + embedding backfill window (same format as
    /// <c>Backup:ScheduleTime</c>).</summary>
    public string BackgroundScheduleTime { get; init; } = "04:00";

    /// <summary>IANA/Windows time-zone id the schedule time is interpreted in (same handling as
    /// <c>Backup:TimeZoneId</c>).</summary>
    public string TimeZoneId { get; init; } = "Europe/Berlin";
}
