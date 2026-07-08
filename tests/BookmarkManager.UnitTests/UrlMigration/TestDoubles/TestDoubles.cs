using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests.UrlMigration.TestDoubles;

public sealed class InMemoryAiTaggingSettingsService : AiTaggingSettingsService
{
    private readonly AiTaggingSettingsDto _settings;

    public InMemoryAiTaggingSettingsService(AiTaggingSettingsDto settings)
        : base(NullLogger<AiTaggingSettingsService>.Instance, "unused-path.json")
    {
        _settings = settings;
    }

    public override Task<AiTaggingSettingsDto> GetAsync(CancellationToken cancellationToken)
        => Task.FromResult(_settings);
}

public sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public SingleClientFactory(HttpClient client)
    {
        _client = client;
    }

    public HttpClient CreateClient(string name) => _client;
}

public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responseFactory;

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _responseFactory(request);
}

public sealed class RoutingHttpClientFactory : IHttpClientFactory
{
    private readonly IReadOnlyDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes;

    public RoutingHttpClientFactory(IReadOnlyDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> routes)
    {
        _routes = routes;
    }

    public HttpClient CreateClient(string name)
    {
        if (!_routes.TryGetValue(name, out var responder))
            throw new InvalidOperationException($"No stub route registered for HttpClient '{name}'.");

        return new HttpClient(new StubHandler(responder));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
