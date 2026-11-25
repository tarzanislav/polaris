using System.Text;
using Polaris.Json;
using Polaris.Net;

namespace Polaris.Centrum;

/// <summary>What a console of this PC holds: its folder (keys and centrum.txt), and once unlocked the key of the console
/// and the centre. The key is unlocked here with the password typed in the window and never leaves this process.</summary>
public sealed class ConsoleState : IDisposable
{
    private readonly Lock _gate = new();
    private (Uri Address, string Pin)? _server;
    private (string Path, ConsoleKeyFile File, SigningKey Key)? _key;

    public ConsoleState(string folder) => Folder = folder;

    public string Folder { get; }

    public static ConsoleState Unlocked((Uri Address, string Pin) server, string keyPath, ConsoleKeyFile keyFile, SigningKey key) =>
        new(Path.GetDirectoryName(Path.GetFullPath(keyPath)) ?? ".") { _server = server, _key = (keyPath, keyFile, key) };

    /// <summary>The centre and the key when the console is unlocked, otherwise null.</summary>
    public ((Uri Address, string Pin) Server, string KeyPath, ConsoleKeyFile KeyFile, SigningKey Key)? Open()
    {
        lock (_gate)
        {
            return _server is { } server && _key is { } key ? (server, key.Path, key.File, key.Key) : null;
        }
    }

    private List<string> Keys() => Directory.Exists(Folder)
        ? Directory.GetFiles(Folder, "*.kluc").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
        : [];

    private (Uri Address, string Pin)? Centre()
    {
        var file = Path.Combine(Folder, CentreFile.Name);
        return _server ?? (File.Exists(file) ? CentreFile.Parse(File.ReadAllText(file)) : null);
    }

    /// <summary>What the start of the window needs: the keys there are, the centre, whether it is unlocked.</summary>
    public JsonObject Start()
    {
        lock (_gate)
        {
            var centre = Centre();
            return new JsonObject()
                .Add("ok", true)
                .Add("unlocked", _server is not null && _key is not null)
                .Add("keys", JsonArray.Of(Keys(), k => k))
                .Add("centre", centre?.Address.ToString() ?? "")
                .Add("folder", Folder)
                .Add("version", Polaris.Core.Product.Version);
        }
    }

    public JsonObject NewKey(byte[] body)
    {
        var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(body), JsonSyntax.Strict), "nový kľúč");
        var name = f.String("name").Trim();
        var password = f.String("password");
        f.End();
        if (!Commands.KeyNamePattern().IsMatch(name))
        {
            return Fail("Meno konzoly: písmená bez diakritiky, číslice, - a _ (napr. ucitel, notebook1).");
        }

        if (password.Length < Commands.MinPassword)
        {
            return Fail($"Heslo musí mať aspoň {Commands.MinPassword} znakov.");
        }

        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            var privateFile = Path.Combine(Folder, name + ".kluc");
            var publicFile = Path.Combine(Folder, name + ".verejny.json");
            if (File.Exists(privateFile) || File.Exists(publicFile))
            {
                return Fail($"Kľúč {name} už existuje.");
            }

            var (secretKey, publicKey) = RecipeSigning.NewConsoleKey(name, password);
            File.WriteAllText(privateFile, RecipeSigning.ToJson(secretKey));
            File.WriteAllText(publicFile, RecipeSigning.ToJson(publicKey));
            return new JsonObject().Add("ok", true).Add("name", name).Add("publicFile", publicFile)
                .Add("message", $"Kľúč {name} je hotový. Súbor {publicFile} nakopíruj na server a tam spusti: polaris-centrum-nastavit {name}.verejny.json");
        }
    }

    public JsonObject SetCentre(byte[] body)
    {
        var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(body), JsonSyntax.Strict), "centrum");
        var text = f.String("address").Trim() + " " + f.String("pin").Trim();
        f.End();
        if (CentreFile.Parse(text) is not { } centre)
        {
            return Fail("Adresa (napr. 172.16.0.10) alebo odtlačok (64 znakov) nie sú v správnom tvare.");
        }

        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, CentreFile.Name), CentreFile.Line(centre.Address, centre.Pin));
            if (_key is not null)
            {
                _server = centre;
            }

            return new JsonObject().Add("ok", true).Add("centre", centre.Address.ToString());
        }
    }

    public JsonObject Unlock(byte[] body)
    {
        var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(body), JsonSyntax.Strict), "odomknutie");
        var name = f.String("name");
        var password = f.String("password");
        f.End();
        lock (_gate)
        {
            if (!Keys().Contains(name, StringComparer.Ordinal))
            {
                return Fail("Taký kľúč konzoly tu nie je.");
            }

            if (Centre() is not { } centre)
            {
                return Fail("Najprv zadaj centrum (adresu a odtlačok).");
            }

            var path = Path.Combine(Folder, name + ".kluc");
            var file = RecipeSigning.KeyFromJson(File.ReadAllText(path));
            var key = RecipeSigning.OpenPrivate(file, password); // a wrong password throws: the answer says so
            _key?.Key.Dispose();
            _key = (path, file, key);
            _server = centre;
            return new JsonObject().Add("ok", true).Add("console", name);
        }
    }

    private static JsonObject Fail(string message) => new JsonObject().Add("ok", false).Add("message", message);

    public void Dispose()
    {
        lock (_gate)
        {
            _key?.Key.Dispose();
            _key = null;
        }
    }
}
