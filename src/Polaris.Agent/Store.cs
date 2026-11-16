using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Templates of the Sandbox as files on the common desktop (only administrators write there, every user sees them).</summary>
[SupportedOSPlatform("windows")]
public sealed class SandboxHandler(string? folder = null) : IElementHandler
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public bool CanHandle(Element element) => element is SandboxElement;

    public Observation Observe(Element element)
    {
        var s = (SandboxElement)element;
        var file = FileOf(s);
        if (!File.Exists(file))
        {
            return new Observation(Observed.Absent, "šablóna nie je na ploche");
        }

        return File.ReadAllText(file, Utf8) == SandboxConfig.Xml(s)
            ? new Observation(Observed.Present, file)
            : new Observation(Observed.Different, "šablóna má iné nastavenia");
    }

    public void Apply(Element element)
    {
        var s = (SandboxElement)element;
        var file = FileOf(s);
        if (s.Present)
        {
            File.WriteAllText(file, SandboxConfig.Xml(s), Utf8);
        }
        else if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private string FileOf(SandboxElement s) =>
        Path.Combine(folder ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), s.FileName);
}

/// <summary>The catalogue of the School store and the programs installed from it, in HKLM\SOFTWARE\POLARIS\Agent\Obchod
/// (administrators and the agent only).</summary>
[SupportedOSPlatform("windows")]
public sealed class StoreState(string key = StoreState.DefaultKey)
{
    public const string DefaultKey = @"SOFTWARE\POLARIS\Agent\Obchod";

    public StoreElement? Catalogue()
    {
        using var k = Open(key, false);
        return k?.GetValue("Katalog") is string json ? StoreRules.CatalogueFromJson(json) : null;
    }

    public string? CatalogueJson()
    {
        using var k = Open(key, false);
        return k?.GetValue("Katalog") as string;
    }

    public void SetCatalogue(StoreElement? catalogue)
    {
        using var k = Create(key);
        if (catalogue is null)
        {
            k.DeleteValue("Katalog", throwOnMissingValue: false);
        }
        else
        {
            k.SetValue("Katalog", StoreRules.ToJson(catalogue), RegistryValueKind.String);
        }
    }

    public List<StoreInstall> Installed()
    {
        using var k = Open(key + @"\Nainstalovane", false);
        var list = new List<StoreInstall>();
        foreach (var name in k?.GetValueNames() ?? [])
        {
            if (k!.GetValue(name) is string json && StoreRules.InstallFromJson(json) is { } record)
            {
                list.Add(record);
            }
        }

        return list;
    }

    public void Record(StoreInstall install)
    {
        using var k = Create(key + @"\Nainstalovane");
        k.SetValue(install.Item, StoreRules.ToJson(install), RegistryValueKind.String);
    }

    public void Forget(string item)
    {
        using var k = Open(key + @"\Nainstalovane", true);
        k?.DeleteValue(item, throwOnMissingValue: false);
    }

    private static RegistryKey? Open(string path, bool writable)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return root.OpenSubKey(path, writable);
    }

    private static RegistryKey Create(string path)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return root.CreateSubKey(path, writable: true);
    }
}

/// <summary>The store element: writes the catalogue for the agent's service.</summary>
[SupportedOSPlatform("windows")]
public sealed class StoreHandler(StoreState? state = null) : IElementHandler
{
    private readonly StoreState _state = state ?? new StoreState();

    public bool CanHandle(Element element) => element is StoreElement;

    public Observation Observe(Element element)
    {
        var s = (StoreElement)element;
        var now = _state.CatalogueJson();
        if (now is null)
        {
            return new Observation(Observed.Absent, "obchod je zatvorený");
        }

        return !s.Present || now == StoreRules.ToJson(Clean(s))
            ? new Observation(Observed.Present, "obchod je otvorený")
            : new Observation(Observed.Different, "obchod má iný katalóg");
    }

    public void Apply(Element element)
    {
        var s = (StoreElement)element;
        _state.SetCatalogue(s.Present ? Clean(s) : null);
    }

    /// <summary>The catalogue as stored: without notes, ids and the flag of presence of the element itself.</summary>
    private static StoreElement Clean(StoreElement s) => new() { Id = s.Id, Accounts = s.Accounts, MaxPerSession = s.MaxPerSession, Items = s.Items };
}

/// <summary>The work of the store: answers requests of pupils, installs, removes after the session. One lock for the store
/// and the cleaner – never two installations at once, never cleaning during an installation.</summary>
[SupportedOSPlatform("windows")]
public sealed class StoreWork(StoreState state, IElementHandler programs, Action<RunRecord> journal, Func<DateTimeOffset> clock, Func<string, bool> cleaned)
{
    public static readonly Lock Gate = new();

    public StoreAnswer Handle(StoreRequest? request, string account, bool administrator)
    {
        if (request is null)
        {
            return new StoreAnswer { Ok = false, Message = "Nezrozumiteľná požiadavka." };
        }

        lock (Gate)
        {
            var catalogue = state.Catalogue();
            var installed = state.Installed();
            if (request.Command == "list")
            {
                using var reading = (programs as IReadsInBatches)?.ReadOnce(); // one reading of the PC for the whole list
                return new StoreAnswer
                {
                    Ok = catalogue is { Items.Count: > 0 },
                    Message = catalogue is { Items.Count: > 0 } ? "" : "Školský obchod je na tomto PC zatvorený.",
                    Items = catalogue?.Items.Select(i => new StoreOffer
                    {
                        Id = i.Id,
                        Title = i.Title,
                        Description = i.Description,
                        Installed = installed.Any(x => x.Item == i.Id) || programs.Observe(i.Program).State == Observed.Present,
                    }).ToList() ?? [],
                };
            }

            if (request.Command != "install")
            {
                return new StoreAnswer { Ok = false, Message = "Neznámy príkaz." };
            }

            if (!cleaned(account) && catalogue is { Items.Count: > 0 } && catalogue.Accounts.Contains(account, StringComparer.OrdinalIgnoreCase) && !administrator)
            {
                return new StoreAnswer { Ok = false, Message = $"Relácia konta {account} sa nečistí – programy by sa nemali kedy odstrániť. Povedz to správcovi." };
            }

            var item = catalogue?.Items.FirstOrDefault(i => i.Id == request.Item);
            var there = item is not null && !installed.Any(x => x.Item == item.Id) && programs.Observe(item.Program).State == Observed.Present;
            if (StoreRules.Refusal(catalogue, account, administrator, installed, request.Item, there) is { } why)
            {
                return new StoreAnswer { Ok = false, Message = why };
            }

            // recorded first: even a power loss during the installation ends with the program removed after the session
            var record = new StoreInstall { Item = item!.Id, Account = account, InstalledUtc = clock(), Program = item.Program };
            state.Record(record);
            var started = clock();
            var result = new ChangeResult(item.Id, $"Školský obchod: {item.Title} pre {account}", ChangeKind.Install, false, "", 0);
            try
            {
                programs.Apply(item.Program);
                var ok = programs.Observe(item.Program).State == Observed.Present;
                result = result with { Ok = ok, Message = ok ? "nainštalované na túto reláciu" : "inštalácia sa neprejavila" };
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                result = result with { Message = e.Message };
            }

            journal(new RunRecord { StartedUtc = started, FinishedUtc = clock(), Recipe = "(Školský obchod)", Results = [result] });
            return new StoreAnswer
            {
                Ok = result.Ok,
                Message = result.Ok ? $"{item.Title} je nainštalovaný. Po skončení relácie sa sám odstráni." : $"{item.Title} sa nepodarilo nainštalovať: {result.Message}",
            };
        }
    }

    /// <summary>Removes what the store installed for an account; a note for the journal, null when there was nothing.</summary>
    public string? RemoveFor(string account)
    {
        lock (Gate)
        {
            var mine = state.Installed().Where(i => string.Equals(i.Account, account, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0)
            {
                return null;
            }

            var removed = 0;
            var failed = new List<string>();
            foreach (var record in mine)
            {
                var program = record.Program;
                program.Present = false;
                try
                {
                    if (programs.Observe(program).State != Observed.Absent)
                    {
                        programs.Apply(program);
                    }

                    if (programs.Observe(program).State == Observed.Absent)
                    {
                        state.Forget(record.Item);
                        removed++;
                    }
                    else
                    {
                        failed.Add(program.DisplayName);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    failed.Add($"{program.DisplayName} ({e.Message})");
                }
            }

            return $"programy zo Školského obchodu odstránené: {removed}" + (failed.Count > 0 ? $", neodstránené (skúsi sa znova): {string.Join(", ", failed)}" : "");
        }
    }
}

/// <summary>The door of the store for pupils: a named pipe on this PC only. Signed-in users may connect; who they are is
/// taken from Windows (the pipe's client identity), never from the request. Only the catalogue id travels.</summary>
[SupportedOSPlatform("windows")]
public static class StorePipe
{
    public const string Name = "polaris-obchod";
    public static readonly TimeSpan ClientLimit = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(15);

    public static void Serve(StoreWork work, Action<string> log, CancellationToken stop, string name = Name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(
                    name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
                pipe.WaitForConnectionAsync(stop).GetAwaiter().GetResult();
                ServeOne(pipe, work);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
            {
                log("Školský obchod: " + e.Message);
                stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static void ServeOne(NamedPipeServerStream pipe, StoreWork work)
    {
        string? account = null;
        var administrator = true;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                var name = identity.Name;
                var slash = name.IndexOf('\\');
                if (slash > 0 && string.Equals(name[..slash], Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    account = name[(slash + 1)..];
                    administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            account = null; // the client did not let us see who it is
        }

        var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        string? line;
        using (new Timer(_ => pipe.Dispose(), null, RequestLimit, Timeout.InfiniteTimeSpan))
        {
            line = ReadLimited(reader); // a client that connects and says nothing is cut off, the store does not wait for it
        }

        StoreAnswer answer;
        if (account is null)
        {
            answer = new StoreAnswer { Ok = false, Message = "Školský obchod je len pre lokálne kontá tohto PC." };
        }
        else
        {
            try
            {
                administrator = administrator || Profiles.IsAdministrator(account);
                answer = work.Handle(StoreRules.RequestFromJson(line), account, administrator);
            }
            catch (SystemCallException e)
            {
                answer = new StoreAnswer { Ok = false, Message = "Konto sa nedá overiť: " + e.Message };
            }
        }

        writer.WriteLine(StoreRules.ToJson(answer));
        pipe.WaitForPipeDrain();
    }

    private static string? ReadLimited(StreamReader reader)
    {
        var text = new StringBuilder();
        while (text.Length <= StoreRules.MaxRequest)
        {
            var c = reader.Read();
            if (c is -1 or '\n')
            {
                return text.ToString().TrimEnd('\r');
            }

            text.Append((char)c);
        }

        return null;
    }

    /// <summary>The pupil's side: one request, one answer.</summary>
    public static StoreAnswer Ask(StoreRequest request, TimeSpan limit, string name = Name)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(10000);
        }
        catch (TimeoutException)
        {
            return new StoreAnswer { Ok = false, Message = "Školský obchod neodpovedá – služba POLARIS agent nebeží, alebo práve inštaluje iný program. Skús to o chvíľu." };
        }

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        writer.WriteLine(StoreRules.ToJson(request));
        var read = reader.ReadLineAsync();
        if (!read.Wait(limit) || read.Result is not { } line)
        {
            return new StoreAnswer { Ok = false, Message = "Služba neodpovedala včas." };
        }

        return StoreRules.AnswerFromJson(line) ?? new StoreAnswer { Ok = false, Message = "Nezrozumiteľná odpoveď služby." };
    }
}
