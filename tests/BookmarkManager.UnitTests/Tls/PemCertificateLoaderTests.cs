using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BookmarkManager.Api.Services.Tls;
using Xunit;

namespace BookmarkManager.UnitTests.Tls;

public sealed class PemCertificateLoaderTests
{
    [Fact]
    public void Load_RoundTripsGeneratedPemPair_AndExposesUsablePrivateKey()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("PemCertificateLoaderTests");
        try
        {
            var certPath = Path.Combine(directory.FullName, "cert.pem");
            var keyPath = Path.Combine(directory.FullName, "key.pem");
            WriteSelfSignedPem(certPath, keyPath, "CN=pem-loader-test");

            // Act
            using var loaded = PemCertificateLoader.Load(certPath, keyPath);

            // Assert
            Assert.Equal("CN=pem-loader-test", loaded.Subject);
            Assert.True(loaded.HasPrivateKey);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal static void WriteSelfSignedPem(string certPath, string keyPath, string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
    }
}
