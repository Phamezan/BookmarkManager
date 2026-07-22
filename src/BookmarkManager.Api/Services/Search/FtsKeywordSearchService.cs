using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Search;

/// <summary>FTS5-backed <see cref="IKeywordSearchService"/>. Queries the <c>LibraryCatalogSearch</c>
/// virtual table created/synced by the <c>AddLibraryCatalogSearchFts</c> migration (triggers keep it
/// in step with <see cref="LibraryCatalogEntry"/> automatically - this service only reads).</summary>
public sealed partial class FtsKeywordSearchService(AppDbContext db) : IKeywordSearchService
{
    /// <summary>Caps how many extracted terms go into one MATCH expression - a chat message pasted in
    /// full could otherwise produce a huge OR clause for no retrieval benefit.</summary>
    private const int MaxTerms = 16;

    /// <summary>Matches runs of letters/digits (any script) - deliberately excludes every FTS5-special
    /// character (<c>" * : - ( )</c>) and word-boundary punctuation, so the rebuilt query below can
    /// never contain an unescaped FTS5 operator or syntax character.</summary>
    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex TermPattern();

    public async Task<IReadOnlyList<(Guid Id, double Bm25)>> SearchAsync(
        string query, int k, CancellationToken cancellationToken)
    {
        if (k <= 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var match = BuildMatchExpression(query);
        if (match is null)
            return [];

        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EntryId, bm25(LibraryCatalogSearch) AS Score
                FROM LibraryCatalogSearch
                WHERE LibraryCatalogSearch MATCH $match
                ORDER BY Score ASC
                LIMIT $k;
                """;
            command.Parameters.Add(new SqliteParameter("$match", match));
            command.Parameters.Add(new SqliteParameter("$k", k));

            var results = new List<(Guid Id, double Bm25)>(k);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                    results.Add((id, reader.GetDouble(1)));
            }

            return results;
        }
        finally
        {
            // Must close through EF's ref-counted Database.CloseConnectionAsync, not the raw
            // DbConnection.CloseAsync - OpenConnectionAsync above went through EF's ref-counted open, and
            // closing the raw connection directly bypasses that ref count. EF can then believe the
            // connection is still open with active statements when it's actually been torn down
            // underneath it, surfacing as an intermittent "unable to delete/modify collation sequence due
            // to active statements" (SQLite error 5) on a shared DbContext under concurrent/repeated use.
            if (wasClosed)
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Tokenizes the raw query and rebuilds a safe FTS5 MATCH expression: each term quoted as
    /// its own phrase (so bareword <c>AND</c>/<c>OR</c>/<c>NEAR</c> and stray <c>" * : -</c> can never
    /// be parsed as FTS5 syntax) and OR-ed together for recall. Returns null when no usable terms
    /// remain (empty/whitespace input, or input that is only punctuation).</summary>
    private static string? BuildMatchExpression(string query)
    {
        var terms = TermPattern().Matches(query)
            .Select(m => m.Value)
            .Where(t => t.Length > 0)
            .Take(MaxTerms)
            .ToList();

        return terms.Count == 0 ? null : string.Join(" OR ", terms.Select(t => $"\"{t}\""));
    }
}
