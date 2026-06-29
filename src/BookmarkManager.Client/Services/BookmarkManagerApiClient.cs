using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkManager.Client.Services;

public interface IBookmarkManagerApiClient
{
    Task<T?> GetAsync<T>(string uri, CancellationToken cancellationToken = default);
    Task<T?> SendAsync<T>(HttpMethod method, string uri, object? body = null, CancellationToken cancellationToken = default);
    Task SendAsync(HttpMethod method, string uri, object? body = null, CancellationToken cancellationToken = default);
}

public sealed class BookmarkManagerApiClient : IBookmarkManagerApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _httpClient;

    public BookmarkManagerApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<T?> GetAsync<T>(string uri, CancellationToken cancellationToken = default)
    {
        return SendAsync<T>(HttpMethod.Get, uri, cancellationToken: cancellationToken);
    }

    public async Task<T?> SendAsync<T>(HttpMethod method, string uri, object? body = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(method, uri, body);
        using var response = await SendRequestAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return default;

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    public async Task SendAsync(HttpMethod method, string uri, object? body = null, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(method, uri, body);
        using var response = await SendRequestAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string uri,
        object? body)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return request;
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiNetworkException("The API could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiNetworkException("The API request timed out.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);
        var title = problem?.Title ?? response.ReasonPhrase ?? "API request failed";
        var detail = problem?.Detail;
        var code = problem?.Code;
        var errors = problem?.Errors ?? new Dictionary<string, string[]>();

        throw response.StatusCode switch
        {
            HttpStatusCode.BadRequest when errors.Count > 0 => new ApiValidationException(response.StatusCode, title, detail, code, errors),
            HttpStatusCode.Unauthorized => new ApiAuthenticationException(response.StatusCode, title, detail, code),
            HttpStatusCode.Forbidden => new ApiAuthorizationException(response.StatusCode, title, detail, code),
            HttpStatusCode.Conflict => new ApiConflictException(response.StatusCode, title, detail, code),
            _ => new ApiException(response.StatusCode, title, detail, code, errors),
        };
    }

    private static async Task<ApiProblemDetails?> ReadProblemDetailsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
            return null;

        try
        {
            return await response.Content.ReadFromJsonAsync<ApiProblemDetails>(JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class ApiProblemDetails
    {
        public string? Title { get; set; }
        public string? Detail { get; set; }
        public string? Code { get; set; }
        public Dictionary<string, string[]> Errors { get; set; } = [];
    }
}
