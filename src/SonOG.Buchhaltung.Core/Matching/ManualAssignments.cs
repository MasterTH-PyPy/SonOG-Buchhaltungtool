using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Matching;

/// <summary>
/// Merkt sich manuelle Eingriffe (Beleg zugeordnet, Vorschlag bestätigt, Beleg entfernt/abgewählt) je Kontoauszug,
/// damit ein erneutes Einlesen sie nicht überschreibt. Eine Buchung wird über Datum, Betrag, Text und
/// laufende Position bei gleichen Buchungen erkannt (nicht über die Belegnummer, die sich ändern kann).
/// </summary>
public sealed class ManualAssignments
{
    public sealed class Entry
    {
        /// <summary>Vollständige Belegliste der Buchung nach dem Eingriff.</summary>
        public List<string> Files { get; set; } = new();
        public bool Accepted { get; set; }
    }

    public Dictionary<string, Entry> Entries { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ManualAssignments Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ManualAssignments>(File.ReadAllText(path), Json) ?? new ManualAssignments();
        }
        catch (JsonException) { /* defekte Datei: ohne gespeicherte Eingriffe weiterarbeiten */ }
        return new ManualAssignments();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Stabile Schlüssel für alle Buchungen eines Auszugs (gleiche Buchungen werden durchnummeriert).</summary>
    public static Dictionary<Booking, string> Keys(IEnumerable<Booking> bookings)
    {
        var result = new Dictionary<Booking, string>();
        var seen = new Dictionary<string, int>();
        foreach (var b in bookings)
        {
            var text = Regex.Replace(b.Text, @"\s+", " ").Trim().ToUpperInvariant();
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..10];
            var baseKey = $"{b.Date:yyyyMMdd}|{b.Amount:0.00}|{hash}";
            seen[baseKey] = seen.TryGetValue(baseKey, out var n) ? n + 1 : 1;
            result[b] = $"{baseKey}|{seen[baseKey]}";
        }
        return result;
    }

    /// <summary>Hält den aktuellen Belegstand einer Buchung als manuellen Eingriff fest.</summary>
    public void Record(string key, Booking b)
    {
        Entries[key] = new Entry
        {
            Files = b.ReceiptFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Accepted = b.Match?.Accepted == true,
        };
    }

    /// <summary>Wendet gespeicherte Eingriffe auf frisch abgeglichene Buchungen an. Gibt die Zahl der angewendeten Eingriffe zurück.</summary>
    public int Apply(IReadOnlyDictionary<Booking, string> keys)
    {
        int applied = 0;
        foreach (var (b, key) in keys)
        {
            if (!Entries.TryGetValue(key, out var e)) continue;
            if (e.Accepted && b.Match is { Accepted: false } m) m.Accept(b);
            b.ReceiptFiles = e.Files.ToList();
            var missing = e.Files.Where(f => !File.Exists(f)).Select(Path.GetFileName).ToList();
            b.ReceiptNote = missing.Count > 0
                ? "Manuell zugeordnet, aber Datei fehlt: " + string.Join(", ", missing)
                : (e.Files.Count > 0 ? "Manuell zugeordnet" : "Beleg manuell entfernt");
            applied++;
        }
        return applied;
    }
}
