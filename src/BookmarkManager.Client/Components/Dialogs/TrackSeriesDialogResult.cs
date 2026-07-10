using System;

namespace BookmarkManager.Client.Components.Dialogs;

public sealed record TrackSeriesDialogResult(Guid ParentId, double ChaptersRead, string Tags, string Status);
