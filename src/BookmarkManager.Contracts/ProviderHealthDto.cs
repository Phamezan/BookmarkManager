using System;

namespace BookmarkManager.Contracts;

public sealed class ProviderHealthDto
{
    public string ProviderName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public DateTime? LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public string? LastError { get; set; }
    public int CacheHits { get; set; }
    public int NetworkCalls { get; set; }
}
