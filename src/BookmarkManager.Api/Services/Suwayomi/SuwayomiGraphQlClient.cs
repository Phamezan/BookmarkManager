using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkManager.Api.Services.Suwayomi;

public sealed class SuwayomiException : Exception
{
    public SuwayomiException(string message) : base(message) { }
    public SuwayomiException(string message, Exception inner) : base(message, inner) { }
}

public sealed record SuwayomiSource(string Id, string Name, string Lang);
public sealed record SuwayomiServerStatus(string Version, IReadOnlyList<SuwayomiSource> Sources);
public sealed record SuwayomiManga(int Id, string Title, bool InLibrary, string? RealUrl, string? ThumbnailUrl);
public sealed record SuwayomiChapter(int Id, double ChapterNumber, bool IsRead);
public sealed record SuwayomiMangaAndChapters(SuwayomiManga Manga, IReadOnlyList<SuwayomiChapter> Chapters);
public sealed record SuwayomiThumbnail(Stream Content, string ContentType);

/// <summary>
/// The subset of Suwayomi's GraphQL API the import feature needs. Operations mirror the proven
/// spike (fetchSourceManga / updateManga / fetchMangaAndChapters / updateChapters). Treats any
/// GraphQL <c>errors</c> payload as a failure.
/// </summary>
public interface ISuwayomiClient
{
    Task<SuwayomiServerStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<SuwayomiManga>> SearchAsync(string sourceId, string query, CancellationToken ct);
    Task<SuwayomiMangaAndChapters> GetMangaAndChaptersAsync(int mangaId, CancellationToken ct);
    Task AddToLibraryAsync(int mangaId, CancellationToken ct);
    Task RemoveFromLibraryAsync(int mangaId, CancellationToken ct);
    Task MarkChaptersReadAsync(IReadOnlyList<int> chapterIds, CancellationToken ct);
    Task<SuwayomiThumbnail> GetThumbnailAsync(int mangaId, CancellationToken ct);
}

public sealed class SuwayomiGraphQlClient : ISuwayomiClient
{
    public const string HttpClientName = "Suwayomi";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private const string StatusQuery =
        "query { aboutServer { version } sources { nodes { id name lang } } }";

    private const string SearchQuery =
        "mutation($s:LongString!,$q:String!){ fetchSourceManga(input:{source:$s,type:SEARCH,page:1,query:$q}){ mangas{ id title inLibrary } } }";

    private const string MangaAndChaptersQuery =
        "mutation($id:Int!){ fetchMangaAndChapters(input:{id:$id, fetchManga:true, fetchChapters:true}){ manga{ id title realUrl thumbnailUrl } chapters{ id chapterNumber isRead } } }";

    private const string AddToLibraryQuery =
        "mutation($id:Int!){ updateManga(input:{id:$id, patch:{inLibrary:true}}){ manga{ id } } }";

    private const string RemoveFromLibraryQuery =
        "mutation($id:Int!){ updateManga(input:{id:$id, patch:{inLibrary:false}}){ manga{ id } } }";

    private const string MarkReadQuery =
        "mutation($ids:[Int!]!){ updateChapters(input:{ids:$ids, patch:{isRead:true}}){ chapters{ id } } }";

    private readonly HttpClient _http;

    public SuwayomiGraphQlClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<SuwayomiServerStatus> GetStatusAsync(CancellationToken ct)
    {
        var data = await PostAsync<StatusData>(StatusQuery, new Dictionary<string, object?>(), ct).ConfigureAwait(false);
        var sources = (data.Sources?.Nodes ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .Select(s => new SuwayomiSource(s.Id, s.Name, s.Lang))
            .ToList();
        return new SuwayomiServerStatus(data.AboutServer?.Version ?? string.Empty, sources);
    }

    public async Task<IReadOnlyList<SuwayomiManga>> SearchAsync(string sourceId, string query, CancellationToken ct)
    {
        var data = await PostAsync<SearchData>(SearchQuery,
            new Dictionary<string, object?> { ["s"] = sourceId, ["q"] = query }, ct).ConfigureAwait(false);
        return (data.FetchSourceManga?.Mangas ?? [])
            .Select(m => new SuwayomiManga(m.Id, m.Title, m.InLibrary, m.RealUrl, m.ThumbnailUrl))
            .ToList();
    }

    public async Task<SuwayomiMangaAndChapters> GetMangaAndChaptersAsync(int mangaId, CancellationToken ct)
    {
        var data = await PostAsync<MangaAndChaptersData>(MangaAndChaptersQuery,
            new Dictionary<string, object?> { ["id"] = mangaId }, ct).ConfigureAwait(false);
        var node = data.FetchMangaAndChapters ?? throw new SuwayomiException($"Suwayomi returned no manga for id {mangaId}.");
        var manga = node.Manga;
        var chapters = (node.Chapters ?? [])
            .Select(c => new SuwayomiChapter(c.Id, c.ChapterNumber, c.IsRead))
            .ToList();
        return new SuwayomiMangaAndChapters(
            new SuwayomiManga(manga.Id, manga.Title, manga.InLibrary, manga.RealUrl, manga.ThumbnailUrl),
            chapters);
    }

    public Task AddToLibraryAsync(int mangaId, CancellationToken ct)
        => PostAsync<UpdateMangaData>(AddToLibraryQuery, new Dictionary<string, object?> { ["id"] = mangaId }, ct);

    public Task RemoveFromLibraryAsync(int mangaId, CancellationToken ct)
        => PostAsync<UpdateMangaData>(RemoveFromLibraryQuery, new Dictionary<string, object?> { ["id"] = mangaId }, ct);

    public async Task MarkChaptersReadAsync(IReadOnlyList<int> chapterIds, CancellationToken ct)
    {
        if (chapterIds.Count == 0)
        {
            return;
        }

        await PostAsync<UpdateChaptersData>(MarkReadQuery,
            new Dictionary<string, object?> { ["ids"] = chapterIds }, ct).ConfigureAwait(false);
    }

    public async Task<SuwayomiThumbnail> GetThumbnailAsync(int mangaId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/v1/manga/{mangaId}/thumbnail", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SuwayomiException($"Suwayomi thumbnail request failed ({(int)response.StatusCode}).");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var buffer = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return new SuwayomiThumbnail(buffer, contentType);
    }

    private async Task<T> PostAsync<T>(string query, object variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/graphql")
        {
            Content = JsonContent.Create(new { query, variables }, options: JsonOptions)
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SuwayomiException("Suwayomi could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SuwayomiException("Suwayomi did not respond in time.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new SuwayomiException($"Suwayomi returned HTTP {(int)response.StatusCode}.");
            }

            GqlEnvelope<T>? envelope;
            try
            {
                envelope = await response.Content.ReadFromJsonAsync<GqlEnvelope<T>>(JsonOptions, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new SuwayomiException("Suwayomi returned an unreadable response.", ex);
            }

            if (envelope is null)
            {
                throw new SuwayomiException("Suwayomi returned an empty response.");
            }

            if (envelope.Errors is { Count: > 0 })
            {
                var message = envelope.Errors.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Message))?.Message;
                throw new SuwayomiException(message ?? "Suwayomi reported a GraphQL error.");
            }

            if (envelope.Data is null)
            {
                throw new SuwayomiException("Suwayomi returned no data.");
            }

            return envelope.Data;
        }
    }

    private sealed class GqlEnvelope<T>
    {
        public T? Data { get; set; }
        public List<GqlError>? Errors { get; set; }
    }

    private sealed class GqlError
    {
        public string? Message { get; set; }
    }

    private sealed class StatusData
    {
        public AboutServerModel? AboutServer { get; set; }
        public SourcesModel? Sources { get; set; }
    }

    private sealed class AboutServerModel
    {
        public string? Version { get; set; }
    }

    private sealed class SourcesModel
    {
        public List<SourceModel>? Nodes { get; set; }
    }

    private sealed class SourceModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
    }

    private sealed class SearchData
    {
        public FetchSourceMangaModel? FetchSourceManga { get; set; }
    }

    private sealed class FetchSourceMangaModel
    {
        public List<MangaModel>? Mangas { get; set; }
    }

    private sealed class MangaAndChaptersData
    {
        public FetchMangaAndChaptersModel? FetchMangaAndChapters { get; set; }
    }

    private sealed class FetchMangaAndChaptersModel
    {
        public MangaModel Manga { get; set; } = new();
        public List<ChapterModel>? Chapters { get; set; }
    }

    private sealed class MangaModel
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public bool InLibrary { get; set; }
        public string? RealUrl { get; set; }
        public string? ThumbnailUrl { get; set; }
    }

    private sealed class ChapterModel
    {
        public int Id { get; set; }
        public double ChapterNumber { get; set; }
        public bool IsRead { get; set; }
    }

    private sealed class UpdateMangaData
    {
        public object? UpdateManga { get; set; }
    }

    private sealed class UpdateChaptersData
    {
        public object? UpdateChapters { get; set; }
    }
}
