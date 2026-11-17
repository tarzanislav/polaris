using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Polaris.Windows;

/// <summary>How the PC belongs to a network of Windows computers.</summary>
public enum JoinState
{
    Unknown,
    Unjoined,
    Workgroup,
    Domain,
}

/// <summary>The workgroup of the PC (the name its neighbours in the network see it under, as Samba on the server). Read
/// and changed through the network setup of Windows, the same calls the dialog "Názov počítača" uses.</summary>
public static unsafe partial class Workgroups
{
    public static (string Name, JoinState State) Current()
    {
        var code = NetGetJoinInformation(null, out var buffer, out var status);
        if (code != 0)
        {
            throw new SystemCallException("Zistenie pracovnej skupiny", code);
        }

        try
        {
            var name = buffer == 0 ? "" : new string((char*)buffer);
            var state = status switch
            {
                1 => JoinState.Unjoined,
                2 => JoinState.Workgroup,
                3 => JoinState.Domain,
                _ => JoinState.Unknown,
            };
            return (name, state);
        }
        finally
        {
            if (buffer != 0)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

    /// <summary>Puts the PC into the workgroup. A PC in a domain is never taken out of it – that is a decision for a
    /// person, not for a recipe.</summary>
    public static void Join(string name)
    {
        if (Current().State == JoinState.Domain)
        {
            throw new InvalidOperationException("PC je v doméne – recept ho z domény nevyberie.");
        }

        // no NETSETUP_JOIN_DOMAIN among the options: the name is a workgroup
        var code = NetJoinDomain(null, name, null, null, null, 0);
        if (code != 0)
        {
            throw new SystemCallException($"Pripojenie do pracovnej skupiny {name}", code);
        }
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetGetJoinInformation(string? server, out nint name, out int status);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetJoinDomain(string? server, string domain, string? accountOu, string? account, string? password, uint options);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buffer);
}

/// <summary>The time zone of the PC by the name Windows keeps it under (e.g. "Central Europe Standard Time"). The rules
/// of the zone (bias, summer time) come from the zone's own key in the registry of Windows, never from a recipe.</summary>
public static unsafe partial class TimeZones
{
    public const string TimeZonePrivilege = "SeTimeZonePrivilege";
    private const string Zones = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones";

    // DYNAMIC_TIME_ZONE_INFORMATION: offsets of its fields (432 bytes with the padding at the end)
    private const int Size = 432;
    private const int OffStandardName = 4;
    private const int OffStandardDate = 68;
    private const int OffStandardBias = 84;
    private const int OffDaylightName = 88;
    private const int OffDaylightDate = 152;
    private const int OffDaylightBias = 168;
    private const int OffKeyName = 172;
    private const int NameChars = 32;
    private const int KeyChars = 128;

    public static string Current()
    {
        var info = stackalloc byte[Size];
        new Span<byte>(info, Size).Clear();
        if (GetDynamicTimeZoneInformation(info) == uint.MaxValue)
        {
            throw new SystemCallException("Zistenie časového pásma", Marshal.GetLastPInvokeError());
        }

        return Text(new ReadOnlySpan<byte>(info + OffKeyName, KeyChars * 2));
    }

    /// <summary>The names of all zones this Windows knows.</summary>
    public static IReadOnlyList<string> Known()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var zones = root.OpenSubKey(Zones);
        return zones?.GetSubKeyNames() ?? [];
    }

    public static void Set(string id)
    {
        // the exact spelling Windows uses (the recipe may differ in capitals)
        var keyName = Known().FirstOrDefault(n => string.Equals(n, id.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Windows nepozná časové pásmo „{id}“.");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var zone = root.OpenSubKey(Zones + "\\" + keyName)
            ?? throw new InvalidOperationException($"Windows nepozná časové pásmo „{id}“.");

        // REG_TZI_FORMAT: Bias, StandardBias, DaylightBias, StandardDate, DaylightDate (44 bytes)
        if (zone.GetValue("TZI") is not byte[] { Length: 44 } tzi)
        {
            throw new InvalidOperationException($"Časové pásmo „{id}“ nemá v registri platné pravidlá.");
        }

        var info = new byte[Size];
        BinaryPrimitives.WriteInt32LittleEndian(info, BinaryPrimitives.ReadInt32LittleEndian(tzi));
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(OffStandardBias), BinaryPrimitives.ReadInt32LittleEndian(tzi.AsSpan(4)));
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(OffDaylightBias), BinaryPrimitives.ReadInt32LittleEndian(tzi.AsSpan(8)));
        tzi.AsSpan(12, 16).CopyTo(info.AsSpan(OffStandardDate));
        tzi.AsSpan(28, 16).CopyTo(info.AsSpan(OffDaylightDate));
        Put(info.AsSpan(OffStandardName, NameChars * 2), zone.GetValue("Std") as string ?? keyName);
        Put(info.AsSpan(OffDaylightName, NameChars * 2), zone.GetValue("Dlt") as string ?? keyName);
        Put(info.AsSpan(OffKeyName, KeyChars * 2), keyName);
        // DynamicDaylightTimeDisabled stays 0: Windows follows the rules of the zone over the years

        Privileges.Enable(TimeZonePrivilege);
        fixed (byte* p = info)
        {
            Check.Win32(SetDynamicTimeZoneInformation(p), $"Nastavenie časového pásma {keyName}");
        }
    }

    private static string Text(ReadOnlySpan<byte> utf16)
    {
        var chars = MemoryMarshal.Cast<byte, char>(utf16);
        var end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    /// <summary>A name into a fixed field of characters, cut to leave the closing zero.</summary>
    private static void Put(Span<byte> field, string text)
    {
        field.Clear();
        var chars = MemoryMarshal.Cast<byte, char>(field);
        text.AsSpan(0, Math.Min(text.Length, chars.Length - 1)).CopyTo(chars);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetDynamicTimeZoneInformation(byte* info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDynamicTimeZoneInformation(byte* info);
}
