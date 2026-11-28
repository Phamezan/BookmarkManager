namespace BookmarkManager.Api.Services.Novels;

public static class NovelPathValidator
{
    public const int MaxPathLength = 512;
    public const int MaxTitleLength = 500;
    public const int MaxUrlLength = 2048;
    public const int MaxQueryLength = 200;

    public static string? ValidatePath(string? value, string fieldName = "path")
    {
        if (string.IsNullOrEmpty(value))
            return $"{fieldName} is required.";
        if (value.Length > MaxPathLength)
            return $"{fieldName} must be at most {MaxPathLength} characters.";
        if (!value.StartsWith('/'))
            return $"{fieldName} must start with '/'.";
        if (value.Contains("//", StringComparison.Ordinal) || value.Contains("://", StringComparison.Ordinal))
            return $"{fieldName} must not contain '//' or '://'.";
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return $"{fieldName} contains invalid characters.";
        return null;
    }

    public static string? ValidateOptionalHttpUrl(string? value, string fieldName = "coverUrl")
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length > MaxUrlLength)
            return $"{fieldName} must be at most {MaxUrlLength} characters.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return $"{fieldName} must be an absolute http or https URL.";
        return null;
    }
}
