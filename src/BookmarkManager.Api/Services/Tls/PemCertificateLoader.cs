using System.Security.Cryptography.X509Certificates;

namespace BookmarkManager.Api.Services.Tls;

/// <summary>
/// Loads a PEM certificate/key pair into an <see cref="X509Certificate2"/> that is actually usable
/// for TLS server authentication.
/// </summary>
internal static class PemCertificateLoader
{
    public static X509Certificate2 Load(string certPath, string keyPath)
    {
        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);

        // CreateFromPemFile produces a certificate backed by an ephemeral private key. On Windows,
        // SChannel cannot use an ephemeral key for TLS server authentication -- handshakes fail with
        // an opaque "no credentials are available" style error. Round-tripping through PKCS#12
        // rehydrates the key into a persisted, exportable form SChannel accepts. This is harmless on
        // Linux/OpenSSL, so we do it unconditionally rather than branching on OS.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }
}
