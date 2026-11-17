using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Polaris.Agent;

/// <summary>Maintenance: for a while the PC must not fall asleep, so the admin can work on it from afar
/// (<c>polaris-agent nespat 3h</c>). The time is kept in the registry; the service holds the request of Windows.</summary>
public static class Maintenance
{
    public static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    /// <summary>"3h", "90m", "1h30m" → the length; null when the text is not such a length or it is zero or over a day.</summary>
    public static TimeSpan? ParseLength(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Length == 0)
        {
            return null;
        }

        var total = TimeSpan.Zero;
        var number = 0;
        var digits = 0;
        foreach (var c in t)
        {
            if (char.IsAsciiDigit(c))
            {
                if (++digits > 4)
                {
                    return null;
                }

                number = (number * 10) + (c - '0');
            }
            else if ((c is 'h' or 'm') && digits > 0)
            {
                total += c == 'h' ? TimeSpan.FromHours(number) : TimeSpan.FromMinutes(number);
                number = 0;
                digits = 0;
            }
            else
            {
                return null;
            }
        }

        return digits > 0 || total <= TimeSpan.Zero || total > Longest ? null : total;
    }

    public static string Reason(DateTimeOffset until) =>
        $"POLARIS: údržba – PC nezaspí do {until.ToLocalTime().ToString("H:mm", CultureInfo.InvariantCulture)}";
}

/// <summary>The service's side of maintenance: one request of Windows while it lasts, a new one when the time
/// changes, none after it. No Windows here – the request is given in.</summary>
public sealed class MaintenanceHold(Func<string, IDisposable?> request)
{
    private IDisposable? _held;
    private DateTimeOffset? _until;

    public bool Holding => _held is not null;

    /// <summary>One look; returns a note for the journal when maintenance began or ended, otherwise null.</summary>
    public string? Update(DateTimeOffset? until, DateTimeOffset now)
    {
        var active = until is { } u && u > now;
        if (active && _until != until)
        {
            _held?.Dispose();
            _held = request(Maintenance.Reason(until!.Value));
            _until = _held is null ? null : until; // a request Windows refused is asked again at the next look
            return _held is null ? null : Maintenance.Reason(until.Value);
        }

        if (!active && _until is not null)
        {
            _held?.Dispose();
            _held = null;
            _until = null;
            return "POLARIS: údržba skončila – PC môže zaspať podľa nastavenia napájania";
        }

        return null;
    }
}

/// <summary>Where the end of maintenance is kept: HKLM\SOFTWARE\POLARIS\Agent, value NespatDo (UTC).</summary>
[SupportedOSPlatform("windows")]
public static class MaintenanceStore
{
    public const string Key = @"SOFTWARE\POLARIS\Agent";
    public const string Value = "NespatDo";

    public static DateTimeOffset? Until()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        return key?.GetValue(Value) is string text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until)
            ? until
            : null;
    }

    public static void Set(DateTimeOffset? until)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Key);
        if (until is { } u)
        {
            key.SetValue(Value, u.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(Value, throwOnMissingValue: false);
        }
    }
}

/// <summary>The PC stays awake while POLARIS works (a recipe, an installation, drivers): a failure to ask never stops
/// the work.</summary>
public static class Awake
{
    public static IDisposable While(string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new Polaris.Windows.AwakeRequest("POLARIS: " + reason);
            }
            catch (Polaris.Windows.SystemCallException)
            {
                // the work goes on; at worst the PC may fall asleep as it would without POLARIS
            }
        }

        return Nothing.Instance;
    }

    [SupportedOSPlatform("windows")]
    public static IDisposable? Request(string reason)
    {
        try
        {
            return new Polaris.Windows.AwakeRequest(reason);
        }
        catch (Polaris.Windows.SystemCallException)
        {
            return null;
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
