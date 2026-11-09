using System.Globalization;

namespace Polaris.Core;

/// <summary>Numbers, sizes and times as a Slovak reader expects them (decimal comma), independent of the system language.</summary>
public static class Format
{
    public static string Gb(long bytes) => Decimal(bytes / 1e9, 1) + " GB";

    public static string Decimal(double value, int places) =>
        value.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).Replace('.', ',');

    public static string Seconds(double seconds) => Decimal(seconds, 1) + " s";

    public static string Duration(TimeSpan time)
    {
        if (time.TotalSeconds < 60)
        {
            return Seconds(time.TotalSeconds);
        }

        if (time.TotalMinutes < 60)
        {
            return $"{(int)time.TotalMinutes} min {time.Seconds} s";
        }

        return $"{(int)time.TotalHours} h {time.Minutes} min";
    }

    public static string Local(DateTimeOffset time) =>
        time.ToLocalTime().ToString("d. M. yyyy H:mm:ss", CultureInfo.InvariantCulture);

    public static string YesNo(bool value) => value ? "áno" : "nie";

    /// <summary>"1 štart", "2 štarty", "5 štartov".</summary>
    public static string Count(long n, string one, string few, string many) =>
        n == 1 ? $"1 {one}" : n is >= 2 and <= 4 ? $"{n} {few}" : $"{n} {many}";
}
