namespace BookmarkManager.Api.IntegrationTests;

public abstract class IntegrationTestBase : IDisposable
{
    private bool _disposed;

    // xUnit instantiates a fresh test class per test method and disposes it, so an instance-level
    // factory gives every test its own isolated host and SQLite database file.
    protected IntegrationTestWebApplicationFactory Factory { get; } = new();

    public void Dispose()
    {
        if (!_disposed)
        {
            Factory.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
