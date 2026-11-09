using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Polaris.Core;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LabState))]
internal sealed partial class StateJson : JsonSerializerContext;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJson : JsonSerializerContext;

/// <summary>Text forms of the state and the journal (no reflection – works in a natively compiled program).</summary>
public static class Serialization
{
    public static string ToJson(LabState state) => JsonSerializer.Serialize(state, StateJson.Default.LabState);

    public static LabState StateFromJson(string json) =>
        JsonSerializer.Deserialize(json, StateJson.Default.LabState) ?? throw new InvalidDataException("Prázdny stav.");

    public static string ToLine(JournalEntry entry) => JsonSerializer.Serialize(entry, JournalJson.Default.JournalEntry);

    public static JournalEntry EntryFromLine(string line) =>
        JsonSerializer.Deserialize(line, JournalJson.Default.JournalEntry) ?? throw new InvalidDataException("Prázdny záznam.");

    /// <summary>Reads a journal; a line cut off by a power loss (always the last one) is skipped, not fatal.</summary>
    public static List<JournalEntry> ReadJournal(string text)
    {
        var list = new List<JournalEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                list.Add(EntryFromLine(line));
            }
            catch (JsonException)
            {
                // a line written only partly before a power loss
            }
        }

        return list;
    }

    public static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
