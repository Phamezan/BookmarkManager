using System;
using System.Security.Cryptography;
using System.Text;
using BookmarkManager.Api.Data;

namespace BookmarkManager.Api.Services.Embedding;

/// <summary>Builds the canonical text embedded for a catalog entry and hashes it so callers can
/// re-embed only when the text actually changes. Shared by the sync ingestion path and the
/// backfill worker so both agree on what "the current text" is.</summary>
public static class LibraryEmbeddingText
{
    /// <summary>Composes the embed text as <c>Title\nAlternateTitles\nGenres\nSynopsis</c>. Alternate
    /// titles (e.g. "Shadow Monarch", "Na Honjaman Level Up") are embedded alongside the main title so
    /// searches that use an alias still hit the entry. Missing parts collapse to empty lines rather than
    /// being dropped so the layout stays stable for hashing.</summary>
    public static string Build(LibraryCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Join(
            '\n',
            entry.Title ?? string.Empty,
            entry.AlternateTitles ?? string.Empty,
            entry.Genres ?? string.Empty,
            entry.Synopsis ?? string.Empty);
    }

    /// <summary>SHA256 hex (lowercase) of the embed text.</summary>
    public static string Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>The stored freshness hash for an entry: the embed-text hash bound to the active model tag,
    /// so a row counts as up-to-date only when both its text AND the embedding model still match. Switching
    /// models (see <see cref="EmbeddingConstants.ModelTag"/>) changes every row's hash and triggers a
    /// full re-embed by the backfill worker.</summary>
    public static string SourceHash(LibraryCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Hash(EmbeddingConstants.ModelTag + '\n' + Build(entry));
    }
}
