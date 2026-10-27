using System.Security.Cryptography.X509Certificates;

namespace TailControl.Shared.AspNetCore;

/// <summary>
/// Certificatul HTTPS generat de <c>tailscale cert</c> (fisiere PEM). E recitit automat
/// cand fisierele se schimba, deci reinnoirea nu cere repornirea serviciului.
/// </summary>
public sealed class PemCertificateProvider(string certPath, string keyPath)
{
    private readonly Lock _lock = new();
    private X509Certificate2? _current;
    private (DateTime, DateTime)? _loadedVersion;

    public string CertPath { get; } = Path.GetFullPath(certPath);

    public string KeyPath { get; } = Path.GetFullPath(keyPath);

    public X509Certificate2 GetCertificate()
    {
        var version = (File.GetLastWriteTimeUtc(CertPath), File.GetLastWriteTimeUtc(KeyPath));
        lock (_lock)
        {
            if (_current is not null && _loadedVersion == version)
            {
                return _current;
            }

            try
            {
                using var pem = X509Certificate2.CreateFromPemFile(CertPath, KeyPath);

                // Pe Windows, SChannel nu poate folosi cheia efemera din PEM; o trecem prin PKCS#12.
                _current = OperatingSystem.IsWindows()
                    ? X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null)
                    : new X509Certificate2(pem);
                _loadedVersion = version;
            }
            catch (Exception) when (_current is not null)
            {
                // Fisier scris pe jumatate in timpul reinnoirii: pastram certificatul vechi.
            }

            return _current;
        }
    }
}
