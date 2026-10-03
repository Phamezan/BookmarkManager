using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.UrlMigration;
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

/// <summary>File-free rejected-host store so discovery tests do not touch disk.</summary>
public sealed class InMemoryRejectedTargetHostStore : RejectedTargetHostStore
{
    private readonly List<string> _hosts;

    public InMemoryRejectedTargetHostStore(IEnumerable<string>? hosts = null)
        : base(NullLogger<RejectedTargetHostStore>.Instance, "unused-rejected.json")
    {
        _hosts = (hosts ?? []).Select(Normalize).Where(h => !string.IsNullOrEmpty(h)).ToList();
    }

    public override Task<IReadOnlyList<string>> GetHostsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(_hosts.ToList());

    public override Task<bool> AddAsync(string host, CancellationToken cancellationToken)
    {
        var normalized = Normalize(host);
        if (string.IsNullOrEmpty(normalized) || _hosts.Contains(normalized, StringComparer.Ordinal))
            return Task.FromResult(false);
        _hosts.Add(normalized);
        return Task.FromResult(true);
    }

    public override Task<bool> RemoveAsync(string host, CancellationToken cancellationToken)
    {
        var removed = _hosts.RemoveAll(h => string.Equals(h, Normalize(host), StringComparison.Ordinal)) > 0;
        return Task.FromResult(removed);
    }
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
