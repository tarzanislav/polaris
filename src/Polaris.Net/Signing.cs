using System.Security.Cryptography;

namespace Polaris.Net;

/// <summary>Signatures of tasks and event entries: ECDSA on the P-256 curve (the same family POLARIS uses for releases).
/// A signing key belongs to one console; a verifying key is public. The .NET runtime does the curve maths – nothing is
/// invented here, only how the bytes to sign are laid out.</summary>
public sealed class SigningKey : IDisposable
{
    private readonly ECDsa _key;

    private SigningKey(ECDsa key) => _key = key;

    public static SigningKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static SigningKey FromPrivate(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new SigningKey(key);
    }

    public byte[] ExportPrivate() => _key.ExportPkcs8PrivateKey();

    /// <summary>The private key encrypted by a password (PKCS#8 with AES-256 and PBKDF2-SHA256, 600 000 rounds).</summary>
    public byte[] ExportEncrypted(string password) =>
        _key.ExportEncryptedPkcs8PrivateKey(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));

    /// <summary>Opens a key encrypted by <see cref="ExportEncrypted"/>; a wrong password gives a <see cref="CryptographicException"/>.</summary>
    public static SigningKey FromEncrypted(ReadOnlySpan<byte> encrypted, string password)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportEncryptedPkcs8PrivateKey(password, encrypted, out _);
            return new SigningKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public VerifyingKey Public() => VerifyingKey.FromSubjectPublicKeyInfo(_key.ExportSubjectPublicKeyInfo());

    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    public void Dispose() => _key.Dispose();
}

/// <summary>The public half of a signing key: it can only check a signature, never make one.</summary>
public sealed class VerifyingKey : IDisposable
{
    private readonly ECDsa _key;

    private VerifyingKey(ECDsa key) => _key = key;

    public static VerifyingKey FromSubjectPublicKeyInfo(ReadOnlySpan<byte> spki)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return new VerifyingKey(key);
    }

    public byte[] Export() => _key.ExportSubjectPublicKeyInfo();

    /// <summary>A short name of the key (first bytes of its SHA-256), so a PC can say which key it trusts.</summary>
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Export()).AsSpan(0, 8));

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        _key.VerifyData(data, signature, HashAlgorithmName.SHA256);

    public void Dispose() => _key.Dispose();
}
