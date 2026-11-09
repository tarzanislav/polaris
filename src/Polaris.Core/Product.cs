namespace Polaris.Core;

/// <summary>The name of the product in one place (the working name may still change).</summary>
public static class Product
{
    public const string Name = "POLARIS";

    /// <summary>The stage of development the program belongs to.</summary>
    public const string Stage = "E0";

    public static string Version { get; } =
        typeof(Product).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
