using BookmarkManager.Api.Services.Tls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Tls;

public sealed class ReloadingCertificateProviderTests
{
    [Fact]
    public void Current_ReturnsLoadedCertificate_AfterConstruction()
    {
        // Arrange
        using var fixture = CertificateFixture.Create();

        // Act
        using var provider = new ReloadingCertificateProvider(
            fixture.CertPath,
            fixture.KeyPath,
            NullLogger<ReloadingCertificateProvider>.Instance);

        // Assert
        Assert.NotNull(provider.Current);
        Assert.Equal("CN=test-a", provider.Current!.Subject);
    }

    [Fact]
    public void Current_ReturnsSameInstance_OnRepeatedReadsWithinCheckInterval()
    {
        // Arrange
        using var fixture = CertificateFixture.Create();
        using var provider = new ReloadingCertificateProvider(
            fixture.CertPath,
            fixture.KeyPath,
            NullLogger<ReloadingCertificateProvider>.Instance,
            checkInterval: TimeSpan.FromMinutes(5));

        // Act
        var first = provider.Current;
        var second = provider.Current;

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public void Current_PicksUpReplacedCertificate_AfterCheckIntervalElapsesAndFileChanges()
    {
        // Arrange
        using var fixture = CertificateFixture.Create();
        using var provider = new ReloadingCertificateProvider(
            fixture.CertPath,
            fixture.KeyPath,
            NullLogger<ReloadingCertificateProvider>.Instance,
            checkInterval: TimeSpan.FromMilliseconds(1));
        Assert.NotNull(provider.Current);

        // Act
        fixture.WriteCertificate("CN=test-b");
        Thread.Sleep(5);
        var reloaded = provider.Current;

        // Assert
        Assert.NotNull(reloaded);
        Assert.Equal("CN=test-b", reloaded!.Subject);
    }

    [Fact]
    public void Current_KeepsServingPreviousCertificate_WhenFileBecomesCorrupt()
    {
        // Arrange
        using var fixture = CertificateFixture.Create();
        using var provider = new ReloadingCertificateProvider(
            fixture.CertPath,
            fixture.KeyPath,
            NullLogger<ReloadingCertificateProvider>.Instance,
            checkInterval: TimeSpan.FromMilliseconds(1));
        var original = provider.Current;
        Assert.NotNull(original);
        var originalSubject = original!.Subject;

        // Act
        fixture.CorruptCertificateFile();
        Thread.Sleep(5);
        var afterCorruption = provider.Current;

        // Assert
        Assert.NotNull(afterCorruption);
        Assert.Equal(originalSubject, afterCorruption!.Subject);
    }

    [Fact]
    public void Constructor_DoesNotThrow_AndLeavesCurrentNull_WhenFilesAreMissing()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("ReloadingCertificateProviderTests");
        try
        {
            var certPath = Path.Combine(directory.FullName, "missing-cert.pem");
            var keyPath = Path.Combine(directory.FullName, "missing-key.pem");

            // Act
            using var provider = new ReloadingCertificateProvider(
                certPath,
                keyPath,
                NullLogger<ReloadingCertificateProvider>.Instance);

            // Assert
            Assert.Null(provider.Current);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class CertificateFixture : IDisposable
    {
        private readonly DirectoryInfo _directory;

        private CertificateFixture(DirectoryInfo directory)
        {
            _directory = directory;
            CertPath = Path.Combine(directory.FullName, "lan.pem");
            KeyPath = Path.Combine(directory.FullName, "lan-key.pem");
        }

        public string CertPath { get; }

        public string KeyPath { get; }

        public static CertificateFixture Create()
        {
            var fixture = new CertificateFixture(Directory.CreateTempSubdirectory("ReloadingCertificateProviderTests"));
            fixture.WriteCertificate("CN=test-a");
            return fixture;
        }

        public void WriteCertificate(string subjectName)
        {
            PemCertificateLoaderTests.WriteSelfSignedPem(CertPath, KeyPath, subjectName);

            // Filesystem mtime resolution can be coarser than the time between test steps, so force
            // the write time forward explicitly rather than relying on the clock having ticked.
            var forcedWriteTimeUtc = File.GetLastWriteTimeUtc(CertPath).AddSeconds(1);
            File.SetLastWriteTimeUtc(CertPath, forcedWriteTimeUtc);
            File.SetLastWriteTimeUtc(KeyPath, forcedWriteTimeUtc);
        }

        public void CorruptCertificateFile()
        {
            File.WriteAllText(CertPath, "not a valid pem certificate");
            File.SetLastWriteTimeUtc(CertPath, File.GetLastWriteTimeUtc(CertPath).AddSeconds(1));
        }

        public void Dispose()
        {
            _directory.Delete(recursive: true);
        }
    }
}
