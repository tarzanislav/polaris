using System.Runtime.Versioning;
using Polaris.Recipes;
using Polaris.Windows;

namespace Polaris.Agent;

/// <summary>The workgroup and the time zone of the PC. Names are compared without regard to capitals, as Windows does.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComputerHandler : IElementHandler
{
    public bool CanHandle(Element element) => element is ComputerElement;

    public Observation Observe(Element element)
    {
        var c = (ComputerElement)element;
        var differences = new List<string>();
        if (c.Workgroup is { Length: > 0 } wanted)
        {
            var (name, state) = Workgroups.Current();
            if (state == JoinState.Domain)
            {
                differences.Add($"PC je v doméne {name} – recept ho z domény nevyberie");
            }
            else if (state != JoinState.Workgroup || !Same(name, wanted))
            {
                differences.Add($"pracovná skupina je {(name.Length > 0 ? name : "(žiadna)")}, má byť {wanted}");
            }
        }

        if (c.TimeZone is { Length: > 0 } zone)
        {
            var now = TimeZones.Current();
            if (!Same(now, zone))
            {
                differences.Add($"časové pásmo je {now}, má byť {zone}");
            }
        }

        if (c.SystemDriveLabel is { Length: > 0 } label && !string.Equals(SystemDrive.VolumeLabel, label.Trim(), StringComparison.Ordinal))
        {
            differences.Add($"systémový disk sa volá „{SystemDrive.VolumeLabel}“, má byť {label.Trim()}");
        }

        return differences.Count == 0 ? new Observation(Observed.Present) : new Observation(Observed.Different, string.Join("; ", differences));
    }

    public void Apply(Element element)
    {
        var c = (ComputerElement)element;
        if (!c.Present)
        {
            throw new InvalidOperationException("Nastavenia počítača sa neodstraňujú.");
        }

        // each part on its own: a failing time zone must not keep the workgroup back, nor the other way round
        var failures = new List<string>();
        if (c.TimeZone is { Length: > 0 } zone && !Same(TimeZones.Current(), zone))
        {
            Try(() => TimeZones.Set(zone), failures);
        }

        if (c.Workgroup is { Length: > 0 } wanted)
        {
            var (name, state) = Workgroups.Current();
            if (state != JoinState.Workgroup || !Same(name, wanted))
            {
                Try(() => Workgroups.Join(wanted.ToUpperInvariant()), failures);
            }
        }

        if (c.SystemDriveLabel is { Length: > 0 } label && !string.Equals(SystemDrive.VolumeLabel, label.Trim(), StringComparison.Ordinal))
        {
            Try(() => SystemDrive.VolumeLabel = label.Trim(), failures);
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", failures));
        }
    }

    private static DriveInfo SystemDrive => new(Path.GetPathRoot(Environment.SystemDirectory)!);

    private static void Try(Action action, List<string> failures)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is InvalidOperationException or Polaris.Windows.SystemCallException or UnauthorizedAccessException or IOException)
        {
            failures.Add(e.Message);
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
