using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Reading, comparing and writing one value under a root key – shared by the machine part of the registry and
/// the template of profiles.</summary>
[SupportedOSPlatform("windows")]
public static class RegistryValues
{
    public static Observation Observe(RegistryKey root, string path, string name, bool present, RegistryKind kindWanted, string data)
    {
        using var key = root.OpenSubKey(path);
        if (key is null)
        {
            return new Observation(Observed.Absent, "kľúč neexistuje");
        }

        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return new Observation(Observed.Absent, "hodnota neexistuje");
        }

        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var now = RegistryData.Show(Readable(value));
        if (!present)
        {
            return new Observation(Observed.Present, "je " + Short(now));
        }

        var wanted = RegistryData.Parse(kindWanted, data);
        return Same(kind, value, kindWanted, wanted)
            ? new Observation(Observed.Present)
            : new Observation(Observed.Different, $"je {Short(now)} ({Kind(kind)}), má byť {Short(RegistryData.Show(wanted))} ({kindWanted})");
    }

    public static void Apply(RegistryKey root, string path, string name, bool present, RegistryKind kind, string data)
    {
        if (present)
        {
            var wanted = RegistryData.Parse(kind, data) ?? throw new InvalidDataException($"Neplatné údaje „{data}“ pre {kind}.");
            using var key = root.CreateSubKey(path, writable: true);
            key.SetValue(name, ToRegistry(wanted), Map(kind));
        }
        else
        {
            using var key = root.OpenSubKey(path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static bool Same(RegistryValueKind kind, object? value, RegistryKind wantedKind, object? wanted)
    {
        if (kind != Map(wantedKind) || value is null || wanted is null)
        {
            return false;
        }

        return (value, wanted) switch
        {
            (int v, uint w) => unchecked((uint)v) == w,
            (long v, ulong w) => unchecked((ulong)v) == w,
            (string[] v, string[] w) => v.SequenceEqual(w, StringComparer.Ordinal),
            (byte[] v, byte[] w) => v.AsSpan().SequenceEqual(w),
            (string v, string w) => string.Equals(v, w, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static object ToRegistry(object wanted) => wanted switch
    {
        uint d => unchecked((int)d),
        ulong q => unchecked((long)q),
        _ => wanted,
    };

    private static object? Readable(object? value) => value switch
    {
        int d => unchecked((uint)d),
        long q => unchecked((ulong)q),
        _ => value,
    };

    private static RegistryValueKind Map(RegistryKind kind) => kind switch
    {
        RegistryKind.ExpandString => RegistryValueKind.ExpandString,
        RegistryKind.DWord => RegistryValueKind.DWord,
        RegistryKind.QWord => RegistryValueKind.QWord,
        RegistryKind.MultiString => RegistryValueKind.MultiString,
        RegistryKind.Binary => RegistryValueKind.Binary,
        _ => RegistryValueKind.String,
    };

    private static string Kind(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.ExpandString => "ExpandString",
        RegistryValueKind.DWord => "DWord",
        RegistryValueKind.QWord => "QWord",
        RegistryValueKind.MultiString => "MultiString",
        RegistryValueKind.Binary => "Binary",
        _ => "String",
    };

    private static string Short(string text) => text.Length <= 60 ? text : text[..57] + "…";
}

/// <summary>Values in the machine part of the registry (always the 64-bit view). Removing a value never deletes keys.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryHandler : IElementHandler
{
    public bool CanHandle(Element element) => element is RegistryValueElement;

    public Observation Observe(Element element)
    {
        var r = (RegistryValueElement)element;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return RegistryValues.Observe(root, Sub(r), r.Name, r.Present, r.Kind, r.Data);
    }

    public void Apply(Element element)
    {
        var r = (RegistryValueElement)element;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        try
        {
            RegistryValues.Apply(root, Sub(r), r.Name, r.Present, r.Kind, r.Data);
        }
        catch (UnauthorizedAccessException) when (IsPolicy(Sub(r)))
        {
            // a policy some protection of Windows (UCPD) lets only Windows write: through the local group policy, the way
            // gpedit.msc does it – Windows itself then sets the value
            new LocalPolicy().Change(user: false, Sub(r), r.Name, r.Present, r.Kind, r.Data);
        }
    }

    public static bool IsPolicy(string sub) => sub.StartsWith(@"SOFTWARE\Policies\", StringComparison.OrdinalIgnoreCase);

    private static string Sub(RegistryValueElement r) =>
        RegistryPath.UnderLocalMachine(r.Path) ?? throw new InvalidDataException($"Cesta {r.Path} nie je v HKLM.");
}
