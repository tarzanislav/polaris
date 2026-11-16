using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Polaris.Web;

/// <summary>The centre's own certificate: ECDSA P-256, self-signed (no authority needed – the PCs pin its key). Kept as
/// two PEM files; the private one is readable only by its owner on Linux.</summary>
public static class Certificates
{
    public static X509Certificate2 Create(IEnumerable<string> names, DateTimeOffset now, int years = 10)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=POLARIS centrum", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(years));
    }

    public static void Save(X509Certificate2 certificate, string certificateFile, string keyFile)
    {
        File.WriteAllText(certificateFile, certificate.ExportCertificatePem());
        using var key = certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Certifikát nemá súkromný kľúč.");
        var pem = key.ExportPkcs8PrivateKeyPem();
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(keyFile, pem);
        }
        else
        {
            using var stream = new FileStream(keyFile, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var writer = new StreamWriter(stream);
            writer.Write(pem);
        }
    }

    /// <summary>Loads the pair for the server. Exported and loaded again as PKCS#12, because the TLS of Windows cannot use a
    /// key that lives only in memory.</summary>
    public static X509Certificate2 Load(string certificateFile, string keyFile)
    {
        using var pem = X509Certificate2.CreateFromPemFile(certificateFile, keyFile);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }
}
