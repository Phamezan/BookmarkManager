using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BookmarkManager.Contracts;

public sealed class TrackLibraryEntryRequest
{
    [Required]
    public Guid ParentId { get; set; }

    [Required]
    public string Provider { get; set; } = string.Empty;

    [Required]
    public string ProviderId { get; set; } = string.Empty;

    [Required]
    public string Title { get; set; } = string.Empty;

    [Required]
    public LibraryMediaType MediaType { get; set; }

    public string? CoverImageUrl { get; set; }

    public string? LatestChapter { get; set; }

    [Required]
    public string SourceUrl { get; set; } = string.Empty;

    public List<string> Genres { get; set; } = [];

    [Range(0, int.MaxValue)]
    public double ChaptersRead { get; set; }

    [Required]
    public string Status { get; set; } = "Reading";
}
