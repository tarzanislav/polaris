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

        return differences.Count == 0 ? new Observation(Observed.Present) : new Observation(Observed.Different, string.Join("; ", differences));
    }

    public void Apply(Element element)
    {
        var c = (ComputerElement)element;
        if (!c.Present)
        {
            throw new InvalidOperationException("Nastavenia počítača sa neodstraňujú.");
        }

        // the time zone first: it never needs a restart, and a failing workgroup must not keep it back
        if (c.TimeZone is { Length: > 0 } zone && !Same(TimeZones.Current(), zone))
        {
            TimeZones.Set(zone);
        }

        if (c.Workgroup is { Length: > 0 } wanted)
        {
            var (name, state) = Workgroups.Current();
            if (state != JoinState.Workgroup || !Same(name, wanted))
            {
                Workgroups.Join(wanted.ToUpperInvariant());
            }
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
