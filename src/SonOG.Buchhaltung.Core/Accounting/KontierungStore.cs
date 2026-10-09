using System.Text.Json;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>
/// Speichert die Kontierungen eines Kontoauszugs (kontierung\&lt;Auszug&gt;.json), damit Änderungen beim nächsten
/// Einlesen erhalten bleiben. Zugeordnet wird über Position, Datum und Betrag der Buchung.
/// </summary>
public sealed class KontierungStore
{
    public sealed class Eintrag
    {
        public int Index { get; set; }
        public DateOnly Datum { get; set; }
        public decimal Betrag { get; set; }
        public string Nummer { get; set; } = "";
        public Kontierung Kontierung { get; set; } = new();
    }

    public sealed class ExportEintrag
    {
        public DateTime Am { get; set; }
        public string Datei { get; set; } = "";
        public int Buchungssaetze { get; set; }
    }

    public List<Eintrag> Eintraege { get; set; } = new();

    /// <summary>Bisherige Exporte nach Taxpool (Warnung bei doppeltem Import).</summary>
    public List<ExportEintrag> Exporte { get; set; } = new();

    public static string PathFor(string dataDir, string statementKey)
    {
        var name = statementKey;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(dataDir, "kontierung", name + ".json");
    }

    public static KontierungStore Load(string path)
    {
        if (!File.Exists(path)) return new KontierungStore();
        return JsonSerializer.Deserialize<KontierungStore>(File.ReadAllText(path), AccountingSettings.Json) ?? new KontierungStore();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, AccountingSettings.Json));
    }

    /// <summary>Gespeicherte Kontierung für die Buchung an Position <paramref name="index"/>, wenn Datum und Betrag passen.</summary>
    public Kontierung? Find(int index, Booking b) =>
        Eintraege.FirstOrDefault(e => e.Index == index && e.Datum == b.Date && e.Betrag == b.Amount)?.Kontierung.Clone();

    public static KontierungStore From(IReadOnlyList<Booking> bookings, IEnumerable<ExportEintrag>? exporte = null)
    {
        var store = new KontierungStore();
        for (int i = 0; i < bookings.Count; i++)
        {
            if (bookings[i].Kontierung is not { } k) continue;
            store.Eintraege.Add(new Eintrag { Index = i, Datum = bookings[i].Date, Betrag = bookings[i].Amount, Nummer = bookings[i].Number, Kontierung = k.Clone() });
        }
        if (exporte is not null) store.Exporte.AddRange(exporte);
        return store;
    }
}
