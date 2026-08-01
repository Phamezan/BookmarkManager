using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Tls;

/// <summary>
/// Caches a PEM-backed TLS certificate and transparently reloads it from disk when the underlying
/// files change, so a scheduled certificate renewal (e.g. Tailscale/Let's Encrypt) is picked up
/// without restarting the process. <see cref="Current"/> is read on every TLS handshake and must stay
/// cheap; the actual file I/O is throttled to at most once per <c>checkInterval</c>.
/// </summary>
internal sealed class ReloadingCertificateProvider : IDisposable
{
    private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(60);

    private readonly string _certPath;
    private readonly string _keyPath;
    private readonly ILogger<ReloadingCertificateProvider> _logger;
    private readonly TimeSpan _checkInterval;

    private X509Certificate2? _current;
    private DateTime _lastCertWriteUtc;
    private DateTime _lastKeyWriteUtc;

    // Environment.TickCount64 deadline for the next allowed file-stat check. Gated with
    // Interlocked.CompareExchange so only one thread per interval pays the File I/O cost; every other
    // caller on the hot TLS-handshake path returns the cached certificate immediately.
    private long _nextCheckTicks;

    private int _disposed;

    public ReloadingCertificateProvider(
        string certPath,
        string keyPath,
        ILogger<ReloadingCertificateProvider> logger,
        TimeSpan? checkInterval = null)
    {
        _certPath = certPath;
        _keyPath = keyPath;
        _logger = logger;
        _checkInterval = checkInterval ?? DefaultCheckInterval;
        _nextCheckTicks = Environment.TickCount64 + (long)_checkInterval.TotalMilliseconds;

        try
        {
            var initial = PemCertificateLoader.Load(_certPath, _keyPath);
            _lastCertWriteUtc = File.GetLastWriteTimeUtc(_certPath);
            _lastKeyWriteUtc = File.GetLastWriteTimeUtc(_keyPath);
            Volatile.Write(ref _current, initial);
        }
        catch (Exception ex)
        {
            // No cert at all at startup is a loud failure: leave Current null so Kestrel's
            // ServerCertificateSelector returns null and the https endpoint fails the handshake,
            // instead of silently pretending everything is fine.
            _logger.LogWarning(ex, "Failed to load initial TLS certificate from {CertPath}", _certPath);
        }
    }

    public X509Certificate2? Current
    {
        get
        {
            MaybeReload();
            return Volatile.Read(ref _current);
        }
    }

    private void MaybeReload()
    {
        var now = Environment.TickCount64;
        var next = Interlocked.Read(ref _nextCheckTicks);
        if (now < next)
        {
            return;
        }

        var deadline = now + (long)_checkInterval.TotalMilliseconds;
        if (Interlocked.CompareExchange(ref _nextCheckTicks, deadline, next) != next)
        {
            // Another thread already won the gate for this interval; nothing more to do here.
            return;
        }

        ReloadIfChanged();
    }

    private void ReloadIfChanged()
    {
        try
        {
            var certWriteUtc = File.GetLastWriteTimeUtc(_certPath);
            var keyWriteUtc = File.GetLastWriteTimeUtc(_keyPath);
            if (certWriteUtc == _lastCertWriteUtc && keyWriteUtc == _lastKeyWriteUtc)
            {
                return;
            }

            var reloaded = PemCertificateLoader.Load(_certPath, _keyPath);
            var previous = Volatile.Read(ref _current);
            _lastCertWriteUtc = certWriteUtc;
            _lastKeyWriteUtc = keyWriteUtc;
            Volatile.Write(ref _current, reloaded);
            previous?.Dispose();

            _logger.LogInformation(
                "Reloaded TLS certificate {Subject}, valid until {NotAfter}",
                reloaded.Subject,
                reloaded.NotAfter);
        }
        catch (Exception ex)
        {
            // A renewal in progress can leave a half-written or truncated PEM file on disk
            // momentarily. That must never take the https endpoint down, so we keep serving whatever
            // certificate is already cached and simply retry on the next check interval.
            _logger.LogWarning(
                ex,
                "Failed to reload TLS certificate from {CertPath}; keeping previous certificate",
                _certPath);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Read(ref _current)?.Dispose();
    }
}
