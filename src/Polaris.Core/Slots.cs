namespace Polaris.Core;

/// <summary>One of the three session layers. Windows always runs from one of them; the other two wait clean.</summary>
public enum Slot
{
    A = 0,
    B = 1,
    C = 2,
}

public static class Slots
{
    public static IReadOnlyList<Slot> All { get; } = [Slot.A, Slot.B, Slot.C];

    /// <summary>The order of the rotation: A → B → C → A.</summary>
    public static Slot After(Slot slot) => (Slot)(((int)slot + 1) % 3);

    public static string FileName(Slot slot) => $"relacia-{slot}.vhdx";

    public static string Description(Slot slot) => $"POLARIS vrstva {slot}";
}
