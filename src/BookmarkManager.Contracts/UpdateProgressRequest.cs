using System.ComponentModel.DataAnnotations;

namespace BookmarkManager.Contracts;

public sealed class UpdateProgressRequest
{
    [Range(0, int.MaxValue)]
    public double ChaptersRead { get; set; }
}
