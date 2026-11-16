using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace Polaris.Recipes;

/// <summary>The pupil's side of the School store: one request, one answer over the local pipe of the agent's service.
/// The client lets the service only identify it (never act as it), and sends nothing but the request.</summary>
public static class StoreClient
{
    public const string PipeName = "polaris-obchod";

    /// <summary>An installation may take long (a big program over a slow line).</summary>
    public static readonly TimeSpan InstallLimit = TimeSpan.FromMinutes(45);

    /// <summary>The list is quick – a menu does not wait longer.</summary>
    public static readonly TimeSpan ListLimit = TimeSpan.FromSeconds(15);

    public static StoreAnswer Ask(StoreRequest request, TimeSpan limit, string name = PipeName, int connectMilliseconds = 10000)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(connectMilliseconds);
        }
        catch (TimeoutException)
        {
            return new StoreAnswer { Ok = false, Message = "Školský obchod neodpovedá – služba POLARIS agent nebeží, alebo práve inštaluje iný program. Skús to o chvíľu." };
        }
        catch (IOException e)
        {
            return new StoreAnswer { Ok = false, Message = "Školský obchod nie je dostupný: " + e.Message };
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
