using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>The DISM interface of Windows (dismapi.dll) for the running system: the state of optional features, switching
/// them on and off. Called directly, no tool output is read.</summary>
[SupportedOSPlatform("windows")]
internal static partial class Dism
{
    private const string OnlineImage = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";
    private const int LogErrors = 0;
    private const int PackageNone = 0;
    private const int AlreadyInitialized = unchecked((int)0xC0040001);
    private const int RebootRequired = 3010;
    private const int RebootRequiredHresult = unchecked((int)0x80070BC2);
    private const int UnknownFeature = unchecked((int)0x800F080C);

    // DismPackageFeatureState
    public const int NotPresent = 0;
    public const int UninstallPending = 1;
    public const int Staged = 2;
    public const int Removed = 3;
    public const int Installed = 4;
    public const int InstallPending = 5;
    public const int Superseded = 6;
    public const int PartiallyInstalled = 7;

    private static readonly Lock Gate = new();
    private static bool initialized;

    public static int State(string feature) => WithSession(session =>
    {
        Check(DismGetFeatureInfo(session, feature, null, PackageNone, out var info), $"Zistenie súčasti {feature}", feature);
        try
        {
            // DismFeatureInfo is packed to 1 byte: PCWSTR FeatureName, then DismPackageFeatureState FeatureState
            return Marshal.ReadInt32(info, IntPtr.Size);
        }
        finally
        {
            _ = DismDelete(info);
        }
    });

    /// <summary>Switches a feature on or off; true when Windows needs a restart to finish it.</summary>
    public static bool Set(string feature, bool on) => WithSession(session =>
    {
        var code = on
            ? DismEnableFeature(session, feature, null, PackageNone, false, IntPtr.Zero, 0, true, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
            : DismDisableFeature(session, feature, null, false, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (code is RebootRequired or RebootRequiredHresult)
        {
            return true;
        }

        Check(code, (on ? "Zapnutie" : "Vypnutie") + $" súčasti {feature}", feature);
        return false;
    });

    private static T WithSession<T>(Func<uint, T> work)
    {
        lock (Gate)
        {
            if (!initialized)
            {
                var code = DismInitialize(LogErrors, null, null);
                if (code != 0 && code != AlreadyInitialized)
                {
                    Check(code, "Spustenie rozhrania DISM", null);
                }

                initialized = true;
            }

            Check(DismOpenSession(OnlineImage, null, null, out var session), "Otvorenie bežiaceho Windows v DISM", null);
            try
            {
                return work(session);
            }
            finally
            {
                _ = DismCloseSession(session);
            }
        }
    }

    private static void Check(int code, string what, string? feature)
    {
        if (code == 0)
        {
            return;
        }

        if (code == UnknownFeature && feature is not null)
        {
            throw new InvalidOperationException($"Súčasť {feature} na tomto Windows neexistuje.");
        }

        throw new InvalidOperationException($"{what} – DISM vrátil 0x{unchecked((uint)code):X8}. {LastMessage()}".Trim());
    }

    private static string LastMessage()
    {
        if (DismGetLastErrorMessage(out var text) != 0 || text == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(text))?.Trim() ?? "";
        }
        finally
        {
            _ = DismDelete(text);
        }
    }

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);

    [LibraryImport("dismapi.dll")]
    private static partial int DismCloseSession(uint session);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismGetFeatureInfo(uint session, string featureName, string? identifier, int packageIdentifier, out IntPtr featureInfo);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismEnableFeature(
        uint session, string featureName, string? identifier, int packageIdentifier,
        [MarshalAs(UnmanagedType.Bool)] bool limitAccess, IntPtr sourcePaths, uint sourcePathCount,
        [MarshalAs(UnmanagedType.Bool)] bool enableAll, IntPtr cancelEvent, IntPtr progress, IntPtr userData);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismDisableFeature(
        uint session, string featureName, string? packageName, [MarshalAs(UnmanagedType.Bool)] bool removePayload,
        IntPtr cancelEvent, IntPtr progress, IntPtr userData);

    [LibraryImport("dismapi.dll")]
    private static partial int DismGetLastErrorMessage(out IntPtr errorMessage);

    [LibraryImport("dismapi.dll")]
    private static partial int DismDelete(IntPtr structure);
}

/// <summary>Optional features of Windows (Windows Sandbox, Hyper-V …). A change waiting for a restart counts as done – the
/// plan and the report say a restart is needed.</summary>
[SupportedOSPlatform("windows")]
public sealed class FeatureHandler : IElementHandler
{
    public bool CanHandle(Element element) => element is FeatureElement;

    public Observation Observe(Element element)
    {
        var f = (FeatureElement)element;
        return Dism.State(f.Name) switch
        {
            Dism.Installed => new Observation(Observed.Present, "zapnutá"),
            Dism.InstallPending => new Observation(Observed.Present, "zapnutá, dokončí sa po reštarte"),
            Dism.UninstallPending => new Observation(Observed.Absent, "vypnutá, dokončí sa po reštarte"),
            Dism.PartiallyInstalled => new Observation(Observed.Different, "zapnutá len čiastočne"),
            _ => new Observation(Observed.Absent, "vypnutá"),
        };
    }

    public void Apply(Element element)
    {
        var f = (FeatureElement)element;
        Dism.Set(f.Name, f.Present);
    }
}
