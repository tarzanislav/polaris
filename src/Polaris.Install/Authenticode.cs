using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Polaris.Install;

public enum SignatureKind
{
    /// <summary>The file carries no signature of a publisher.</summary>
    Unsigned,

    /// <summary>Signed, the signature fits the file and leads to a root Windows trusts.</summary>
    Valid,

    /// <summary>A signature is there but does not fit, has expired without a time stamp, or is not trusted.</summary>
    Invalid,
}

public sealed record PublisherSignature(SignatureKind Kind, string? Publisher, string Detail);

/// <summary>The publisher's signature of an installer (Authenticode), checked by Windows itself (WinVerifyTrust), and
/// the name of the signer. Offline: revocation lists are not fetched – a school network may have no internet, and the
/// fingerprint of the file is checked anyway.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class Authenticode
{
    private static readonly Guid VerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static PublisherSignature Check(string file)
    {
        var path = Marshal.StringToHGlobalUni(Path.GetFullPath(file));
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = path };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = (nint)(&fileInfo),
                dwStateAction = 1,           // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x1000 | 0x10, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
            };
            var action = VerifyV2;
            var status = WinVerifyTrust(-1, ref action, ref data);
            data.dwStateAction = 2;          // WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(-1, ref action, ref data);
            return unchecked((uint)status) switch
            {
                0 => new PublisherSignature(SignatureKind.Valid, Signer(file), "podpis je platný"),
                0x800B0100 or 0x800B0003 or 0x80092009 => new PublisherSignature(SignatureKind.Unsigned, null, "súbor nemá podpis vydavateľa"),
                0x80096010 => new PublisherSignature(SignatureKind.Invalid, null, "podpis nesedí – súbor bol po podpísaní zmenený"),
                0x800B0101 => new PublisherSignature(SignatureKind.Invalid, Signer(file), "certifikát podpisu vypršal a podpis nemá časovú pečiatku"),
                0x800B0109 => new PublisherSignature(SignatureKind.Invalid, Signer(file), "podpis vedie k nedôveryhodnému koreňu"),
                var other => new PublisherSignature(SignatureKind.Invalid, null, $"podpis neplatí (0x{other:X8}: {Marshal.GetPInvokeErrorMessage((int)other)})"),
            };
        }
        finally
        {
            Marshal.FreeHGlobal(path);
        }
    }

    /// <summary>The simple name of the signer (e.g. "Microsoft Corporation"), or null.</summary>
    private static string? Signer(string file)
    {
        if (CryptQueryObject(1 /* CERT_QUERY_OBJECT_FILE */, Path.GetFullPath(file), 1 << 10 /* PKCS7_SIGNED_EMBED */, 2 /* BINARY */, 0,
            out _, out _, out _, out var store, out var message, out _) == 0)
        {
            return null;
        }

        try
        {
            uint size = 0;
            if (CryptMsgGetParam(message, 7 /* CMSG_SIGNER_CERT_INFO_PARAM */, 0, 0, ref size) == 0)
            {
                return null;
            }

            var info = Marshal.AllocHGlobal((int)size);
            try
            {
                if (CryptMsgGetParam(message, 7, 0, info, ref size) == 0)
                {
                    return null;
                }

                var certificate = CertFindCertificateInStore(store, 0x10001 /* X509 | PKCS7 */, 0, 0xB0000 /* CERT_FIND_SUBJECT_CERT */, info, 0);
                if (certificate == 0)
                {
                    return null;
                }

                try
                {
                    var name = new char[256];
                    fixed (char* n = name)
                    {
                        var length = CertGetNameString(certificate, 4 /* CERT_NAME_SIMPLE_DISPLAY_TYPE */, 0, 0, n, (uint)name.Length);
                        return length > 1 ? new string(n, 0, (int)length - 1) : null;
                    }
                }
                finally
                {
                    _ = CertFreeCertificateContext(certificate);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
        finally
        {
            _ = CryptMsgClose(message);
            _ = CertCloseStore(store, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, ref Guid action, ref WINTRUST_DATA data);

    [LibraryImport("crypt32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CryptQueryObject(uint objectType, string file, uint expectedContent, uint expectedFormat, uint flags,
        out uint encoding, out uint contentType, out uint formatType, out nint store, out nint message, out nint context);

    [LibraryImport("crypt32.dll")]
    private static partial int CryptMsgGetParam(nint message, uint parameter, uint index, nint data, ref uint size);

    [LibraryImport("crypt32.dll")]
    private static partial nint CertFindCertificateInStore(nint store, uint encoding, uint flags, uint type, nint para, nint previous);

    [LibraryImport("crypt32.dll", EntryPoint = "CertGetNameStringW")]
    private static partial uint CertGetNameString(nint certificate, uint type, uint flags, nint typePara, char* name, uint size);

    [LibraryImport("crypt32.dll")]
    private static partial int CertFreeCertificateContext(nint certificate);

    [LibraryImport("crypt32.dll")]
    private static partial int CryptMsgClose(nint message);

    [LibraryImport("crypt32.dll")]
    private static partial int CertCloseStore(nint store, uint flags);
}
