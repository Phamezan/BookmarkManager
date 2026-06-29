using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BookmarkManager.Api.Services;

public class TagExtractorService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "a", "of", "to", "for", "in", "on", "at", "with", "is", "was", "were", "or", "but", 
        "not", "this", "that", "these", "those", "then", "there", "their", "them", "what", "which", 
        "who", "how", "why", "where", "when", "watch", "read", "view", "online", "free", "home", 
        "page", "website", "web", "site", "com", "net", "org", "www", "http", "https"
    };

    public List<string> ExtractTags(string title, string? url)
    {
        var suggestions = new List<string>();

        // 1. Rule-based category mapping
        var combinedText = (title + " " + (url ?? "")).ToLower();
        if (combinedText.Contains("anime") || combinedText.Contains("miruro") || combinedText.Contains("crunchyroll") || combinedText.Contains("episode") || combinedText.Contains("watch"))
        {
            suggestions.Add("Anime");
        }
        if (combinedText.Contains("manga") || combinedText.Contains("chapter") || combinedText.Contains("read") || combinedText.Contains("mangadex"))
        {
            suggestions.Add("Manga");
        }
        if (combinedText.Contains("github") || combinedText.Contains("gitlab") || combinedText.Contains("develop") || combinedText.Contains("code") || combinedText.Contains("programming") || combinedText.Contains("api") || combinedText.Contains("stack-overflow"))
        {
            suggestions.Add("Development");
        }
        if (combinedText.Contains("news") || combinedText.Contains("bbc") || combinedText.Contains("cnn") || combinedText.Contains("times") || combinedText.Contains("post"))
        {
            suggestions.Add("News");
        }
        if (combinedText.Contains("shop") || combinedText.Contains("amazon") || combinedText.Contains("ebay") || combinedText.Contains("buy") || combinedText.Contains("price"))
        {
            suggestions.Add("Shopping");
        }

        // 2. Token extraction from title
        var wordRegex = new Regex(@"\b[a-zA-Z]{3,15}\b");
        var matches = wordRegex.Matches(title);
        var keywords = matches
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w))
            .GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(g.Key.ToLower()))
            .Take(5)
            .ToList();

        foreach (var keyword in keywords)
        {
            if (suggestions.Count >= 5) break;
            if (!suggestions.Contains(keyword, StringComparer.OrdinalIgnoreCase))
            {
                suggestions.Add(keyword);
            }
        }

        return suggestions.Take(5).ToList();
    }
}
