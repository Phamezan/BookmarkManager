using System.Net;
using System.Net.Http.Headers;
using BookmarkManager.Client.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkManagerApiClientTests
{
    [Fact]
    public async Task ProblemDetailsResponse_BecomesApiValidationException_WithErrors()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.BadRequest,
            """{"title":"Validation failed","detail":"Label is required.","code":"VALIDATION","errors":{"label":["Label is required."]}}""",
            "application/problem+json");
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
        var api = new BookmarkManagerApiClient(client);

        var ex = await Assert.ThrowsAsync<ApiValidationException>(
            () => api.SendAsync(HttpMethod.Post, "api/sample", new { label = "" }));

        Assert.Equal("Validation failed", ex.Title);
        Assert.Equal("Label is required.", ex.Detail);
        Assert.Contains(ex.ValidationErrors, pair => pair.Key == "label");
    }

    [Fact]
    public async Task SendAndReceiveRoundTripSucceeds()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"id":"abc","name":"test"}""", "application/json");
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
        var api = new BookmarkManagerApiClient(client);

        var result = await api.SendAsync<SampleResponse>(HttpMethod.Post, "api/sample", new { label = "demo" });

        Assert.NotNull(result);
        Assert.Equal("abc", result!.Id);
        Assert.Equal("test", result.Name);
    }

    [Fact]
    public async Task GetReturnsDeserializedResponse()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"id":"x","name":"y"}""", "application/json");
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
        var api = new BookmarkManagerApiClient(client);

        var result = await api.GetAsync<SampleResponse>("api/sample/abc");

        Assert.NotNull(result);
        Assert.Equal("x", result!.Id);
        Assert.Equal("y", result.Name);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string? _contentType;

        public CapturingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "", string? contentType = null)
        {
            _status = status;
            _body = body;
            _contentType = contentType;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(_status);
            if (_contentType is not null)
            {
                response.Content = new StringContent(_body);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            }
            else if (!string.IsNullOrEmpty(_body))
            {
                response.Content = new StringContent(_body);
            }

            return Task.FromResult(response);
        }
    }

    private sealed record SampleResponse(string Id, string Name);
}
