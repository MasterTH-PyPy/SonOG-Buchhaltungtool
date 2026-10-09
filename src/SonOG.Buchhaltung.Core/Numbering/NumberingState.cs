using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonOG.Buchhaltung.Core.Numbering;

public static class NumberFormat
{
    private static readonly Regex NRx = new(@"\{n(?::(?<f>[0#]+))?\}", RegexOptions.Compiled);

    /// <summary>Formatiert die laufende Nummer. Platzhalter {year} und {n} bzw. {n:0000}.</summary>
    public static string Format(string pattern, int year, int n) =>
        NRx.Replace(pattern.Replace("{year}", year.ToString(CultureInfo.InvariantCulture)),
            m => n.ToString(m.Groups["f"].Success ? m.Groups["f"].Value : "0", CultureInfo.InvariantCulture));
}

/// <summary>
/// Merkt sich, welche laufenden Nummern bereits vergeben wurden. Ein Auszug bekommt beim erneuten Lauf
/// dieselben Nummern wieder (idempotent). Außerdem wird gespeichert, welche Rechnungen schon bezahlt wurden.
/// </summary>
public sealed class NumberingState
{
    public sealed class NumberRange
    {
        public int Start { get; set; }
        public int Count { get; set; }
    }

    public sealed class YearCounter
    {
        public int Last { get; set; }
        public Dictionary<string, NumberRange> Statements { get; set; } = new();
    }

    public sealed class PaidEntry
    {
        public string Booking { get; set; } = "";
        public string Statement { get; set; } = "";
    }

    public Dictionary<int, YearCounter> Years { get; set; } = new();

    /// <summary>Rechnungsnummer -> Buchung (und Auszug), mit der sie bezahlt wurde.</summary>
    public Dictionary<string, PaidEntry> PaidInvoices { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static NumberingState Load(string path)
    {
        if (!File.Exists(path)) return new NumberingState();
        return JsonSerializer.Deserialize<NumberingState>(File.ReadAllText(path), Json) ?? new NumberingState();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Erste Nummer, die dieser Auszug bekäme, ohne etwas zu speichern (für die Vorschau).</summary>
    public int PeekStart(int year, string statementKey)
    {
        if (Years.TryGetValue(year, out var y))
        {
            if (y.Statements.TryGetValue(statementKey, out var existing)) return existing.Start;
            return y.Last + 1;
        }
        return 1;
    }

    /// <summary>Vergibt den Nummernbereich endgültig. Wiederholung mit gleicher Anzahl liefert denselben Start.</summary>
    public int Commit(int year, string statementKey, int count)
    {
        if (!Years.TryGetValue(year, out var y))
            Years[year] = y = new YearCounter();

        if (y.Statements.TryGetValue(statementKey, out var existing))
        {
            if (existing.Count != count)
                throw new InvalidOperationException(
                    $"Der Auszug '{statementKey}' wurde bereits mit {existing.Count} Buchungen nummeriert (jetzt {count}). " +
                    "Nummern des Auszugs zuerst freigeben, falls er neu nummeriert werden soll.");
            return existing.Start;
        }

        int start = y.Last + 1;
        y.Statements[statementKey] = new NumberRange { Start = start, Count = count };
        y.Last = start + count - 1;
        return start;
    }

    /// <summary>Gibt die Nummern eines Auszugs frei. Nur der zuletzt vergebene Bereich lässt den Zähler zurückgehen.</summary>
    public bool Release(int year, string statementKey)
    {
        if (!Years.TryGetValue(year, out var y) || !y.Statements.TryGetValue(statementKey, out var r)) return false;
        y.Statements.Remove(statementKey);
        if (r.Start + r.Count - 1 == y.Last) y.Last = r.Start - 1;
        foreach (var key in PaidInvoices.Where(kv => kv.Value.Statement == statementKey).Select(kv => kv.Key).ToList())
            PaidInvoices.Remove(key);
        return true;
    }

    /// <summary>Bereits bezahlte Rechnungen aus anderen Auszügen (Rechnungsnummer -> Buchungsnummer).</summary>
    public IReadOnlyDictionary<string, string> PaidExcluding(string statementKey) =>
        PaidInvoices.Where(kv => kv.Value.Statement != statementKey).ToDictionary(kv => kv.Key, kv => kv.Value.Booking);

    public void MarkPaid(string invoiceNumber, string bookingNumber, string statementKey) =>
        PaidInvoices[invoiceNumber] = new PaidEntry { Booking = bookingNumber, Statement = statementKey };
}
