using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Values in the machine part of the registry (always the 64-bit view). Removing a value never deletes keys.</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryHandler : IElementHandler
{
    public bool CanHandle(Element element) => element is RegistryValueElement;

    public Observation Observe(Element element)
    {
        var r = (RegistryValueElement)element;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Sub(r));
        if (key is null)
        {
            return new Observation(Observed.Absent, "kľúč neexistuje");
        }

        if (!key.GetValueNames().Contains(r.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new Observation(Observed.Absent, "hodnota neexistuje");
        }

        var kind = key.GetValueKind(r.Name);
        var value = key.GetValue(r.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var now = RegistryData.Show(Readable(value));
        if (!r.Present)
        {
            return new Observation(Observed.Present, "je " + Short(now));
        }

        var wanted = RegistryData.Parse(r.Kind, r.Data);
        return Same(kind, value, r.Kind, wanted)
            ? new Observation(Observed.Present)
            : new Observation(Observed.Different, $"je {Short(now)} ({Kind(kind)}), má byť {Short(RegistryData.Show(wanted))} ({r.Kind})");
    }

    public void Apply(Element element)
    {
        var r = (RegistryValueElement)element;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (r.Present)
        {
            var wanted = RegistryData.Parse(r.Kind, r.Data) ?? throw new InvalidDataException($"Neplatné údaje „{r.Data}“ pre {r.Kind}.");
            using var key = root.CreateSubKey(Sub(r), writable: true);
            key.SetValue(r.Name, ToRegistry(wanted), Map(r.Kind));
        }
        else
        {
            using var key = root.OpenSubKey(Sub(r), writable: true);
            key?.DeleteValue(r.Name, throwOnMissingValue: false);
        }
    }

    private static string Sub(RegistryValueElement r) =>
        RegistryPath.UnderLocalMachine(r.Path) ?? throw new InvalidDataException($"Cesta {r.Path} nie je v HKLM.");

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
