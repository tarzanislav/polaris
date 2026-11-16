using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Net;

namespace Polaris.Agent;

/// <summary>The consoles this PC trusts, kept in HKLM\SOFTWARE\POLARIS\Agent\DoverneKonzoly – a part of the registry only
/// administrators can change (a pupil cannot plant a key there), and a recipe can never touch it.</summary>
[SupportedOSPlatform("windows")]
public static class TrustStore
{
    public const string Key = @"SOFTWARE\POLARIS\Agent\DoverneKonzoly";

    public static List<TrustedConsole> Read()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Key);
        var list = new List<TrustedConsole>();
        if (key is null)
        {
            return list;
        }

        foreach (var name in key.GetValueNames())
        {
            try
            {
                var trusted = RecipeSigning.Trust(RecipeSigning.KeyFromJson(key.GetValue(name) as string ?? ""));
                if (trusted.Fingerprint == name)
                {
                    list.Add(trusted);
                }
            }
            catch (InvalidDataException)
            {
                // a damaged entry never gives trust
            }
        }

        return list;
    }

    public static void Add(ConsoleKeyFile publicKey)
    {
        var trusted = RecipeSigning.Trust(publicKey);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(Key, writable: true);
        var clean = new ConsoleKeyFile { Name = publicKey.Name, Fingerprint = trusted.Fingerprint, PublicKey = publicKey.PublicKey };
        key.SetValue(trusted.Fingerprint, RecipeSigning.ToJson(clean), RegistryValueKind.String);
    }

    public static bool Remove(string fingerprint)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Key, writable: true);
        if (key?.GetValue(fingerprint) is null)
        {
            return false;
        }

        key.DeleteValue(fingerprint);
        return true;
    }
}
