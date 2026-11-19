using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Executor of the pins of Start for new profiles: the files of Start from the trezor of the account go into the
/// template of profiles (each checked by its fingerprint in the trezor). The rule (from which account) is kept, so a trezor
/// saved again brings its pins into the template at once (trezor ulozit). What the template had before is kept beside
/// (.povodny) and put back when the recipe takes the element away.</summary>
[SupportedOSPlatform("windows")]
public sealed class StartLayoutHandler(ProfileVault? vault = null, string? templateFolder = null, string ruleKey = StartLayoutHandler.DefaultRuleKey) : IElementHandler
{
    public const string DefaultRuleKey = @"SOFTWARE\POLARIS\Agent";
    public const string RuleValue = "StartZTrezoru";
    public const string Original = ".povodny";

    /// <summary>POLARIS wrote the template already (what was there before is kept beside, if anything was).</summary>
    public const string KeptValue = "StartPovodnyOdlozeny";

    private readonly ProfileVault _vault = vault ?? new ProfileVault();

    public bool CanHandle(Element element) => element is StartLayoutElement;

    public Observation Observe(Element element)
    {
        var l = (StartLayoutElement)element;
        var from = l.From.Trim();
        var rule = Rule();
        if (!l.Present)
        {
            return rule is null && !Kept()
                ? new Observation(Observed.Absent)
                : new Observation(Observed.Present, rule is null ? "v predlohe je rozloženie Štartu" : $"z trezoru konta {rule}");
        }

        if (!string.Equals(rule, from, StringComparison.OrdinalIgnoreCase))
        {
            return new Observation(rule is null ? Observed.Absent : Observed.Different, rule is null ? "" : $"je z trezoru konta {rule}");
        }

        if (_vault.Manifest(from) is null)
        {
            return new Observation(Observed.Present, $"konto {from} ešte nemá trezor – rozloženie sa prevezme pri uložení trezoru");
        }

        var source = Source(from);
        if (source.Count == 0)
        {
            return new Observation(Observed.Different, NoLayout(from));
        }

        var folder = TemplateFolder();
        foreach (var (path, data) in source)
        {
            var file = Path.Combine(folder, Path.GetFileName(path));
            if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(data))
            {
                return new Observation(Observed.Different, $"v predlohe profilov nie je rozloženie z trezoru ({Path.GetFileName(path)})");
            }
        }

        return new Observation(Observed.Present, $"z trezoru konta {from}");
    }

    public void Apply(Element element)
    {
        var l = (StartLayoutElement)element;
        if (!l.Present)
        {
            PutBackOriginals();
            SetRule(null);
            return;
        }

        var from = l.From.Trim();
        SetRule(from);
        if (_vault.Manifest(from) is not null)
        {
            Copy(from);
        }
    }

    /// <summary>After a trezor was saved: its pins into the template when a recipe says so. A line for the admin, or null.</summary>
    public string? AfterSave(string account)
    {
        if (!string.Equals(Rule(), account, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var files = Copy(account);
            return $"Pripnuté v Štarte z tohto profilu dostane aj každý nový profil ({files} {(files == 1 ? "súbor" : "súbory")}).";
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return "Rozloženie Štartu pre nové profily sa neprevzalo: " + e.Message;
        }
    }

    private int Copy(string from)
    {
        var source = Source(from);
        if (source.Count == 0)
        {
            throw new InvalidOperationException(NoLayout(from));
        }

        var folder = TemplateFolder();
        Directory.CreateDirectory(folder);
        if (!Kept())
        {
            // what Windows or the admin had there before POLARIS, once
            foreach (var file in Template())
            {
                File.Copy(file, file + Original, overwrite: true);
            }

            SetKept(true);
        }

        foreach (var (path, data) in source)
        {
            var file = Path.Combine(folder, Path.GetFileName(path));
            File.WriteAllBytes(file + ".novy", data);
            File.Move(file + ".novy", file, overwrite: true);
        }

        return source.Count;
    }

    private void PutBackOriginals()
    {
        foreach (var file in Template())
        {
            File.Delete(file);
        }

        foreach (var original in Originals())
        {
            File.Move(original, original[..^Original.Length], overwrite: true);
        }

        SetKept(false);
    }

    private List<(string Path, byte[] Data)> Source(string from) =>
        _vault.Files(from, StartLayoutRules.IsLayoutFile, StartLayoutRules.Largest);

    private static string NoLayout(string from) =>
        $"v trezore konta {from} nie je súbor pripnutých programov Štartu ({StartLayoutRules.Folder}\\start2.bin) – Windows ho "
        + "možno ukladá inak; nahlás to, POLARIS nič nehádže do predlohy";

    private string TemplateFolder() =>
        Path.Combine(templateFolder ?? Path.GetDirectoryName(ProfileTemplateHandler.DefaultHiveFile())!, StartLayoutRules.Folder);

    private List<string> Template()
    {
        var folder = TemplateFolder();
        return Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder).Where(f => StartLayoutRules.IsLayoutFile(Path.Combine(StartLayoutRules.Folder, Path.GetFileName(f))))]
            : [];
    }

    private List<string> Originals()
    {
        var folder = TemplateFolder();
        return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "start*.bin" + Original)] : [];
    }

    private bool Kept()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ruleKey);
        return key?.GetValue(KeptValue) is not null;
    }

    private void SetKept(bool kept)
    {
        using var key = Registry.LocalMachine.CreateSubKey(ruleKey, writable: true);
        if (kept)
        {
            key.SetValue(KeptValue, 1, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(KeptValue, throwOnMissingValue: false);
        }
    }

    private string? Rule()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ruleKey);
        return key?.GetValue(RuleValue) as string is { Length: > 0 } account ? account : null;
    }

    private void SetRule(string? account)
    {
        using var key = Registry.LocalMachine.CreateSubKey(ruleKey, writable: true);
        if (account is null)
        {
            key.DeleteValue(RuleValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(RuleValue, account, RegistryValueKind.String);
        }
    }
}
