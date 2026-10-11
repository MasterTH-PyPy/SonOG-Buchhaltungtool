using System.Text.Json;
using SonOG.Buchhaltung.Core.Invoices;

namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>Merkt sich, welche Ausgangsrechnungen schon sollgestellt (an die Buchhaltung übergeben) wurden.</summary>
public sealed class SollstellungState
{
    public sealed class Eintrag
    {
        public string Monat { get; set; } = "";
        public string Datei { get; set; } = "";
        public DateTime Am { get; set; }
        public int Debitor { get; set; }
    }

    /// <summary>Rechnungsnummer → wann und wohin sie exportiert wurde.</summary>
    public Dictionary<string, Eintrag> Gestellt { get; set; } = new();

    public static SollstellungState Load(string path)
    {
        if (!File.Exists(path)) return new SollstellungState();
        return JsonSerializer.Deserialize<SollstellungState>(File.ReadAllText(path), AccountingSettings.Json) ?? new SollstellungState();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, AccountingSettings.Json));
    }

    /// <summary>Nimmt die Sollstellung eines Monats zurück (z. B. wenn der Import in Taxpool gelöscht wurde).</summary>
    public int Freigeben(string monat)
    {
        var keys = Gestellt.Where(kv => kv.Value.Monat == monat).Select(kv => kv.Key).ToList();
        foreach (var k in keys) Gestellt.Remove(k);
        return keys.Count;
    }

    public static string MonatKey(int year, int month) => $"{year:0000}-{month:00}";
}

/// <summary>Stellt die Ausgangsrechnungen eines Monats zusammen (getrennt vom Kontoauszug, damit sich nichts überschneidet).</summary>
public static class Sollstellung
{
    public static List<SollstellungsPosten> Vorbereiten(
        IEnumerable<InvoiceRecord> rechnungen, int year, int month,
        AccountingSettings s, PersonAccountDirectory dir, SollstellungState state)
    {
        var list = new List<SollstellungsPosten>();
        foreach (var r in rechnungen.Where(r => r.Date is { } d && d.Year == year && d.Month == month)
                     .OrderBy(r => r.Date).ThenBy(r => r.Number, StringComparer.Ordinal))
        {
            var p = new SollstellungsPosten(r, KontierungFuer(r, s, dir));
            if (state.Gestellt.TryGetValue(r.Number, out var e))
            {
                p.BereitsGestellt = e.Monat;
                p.Auswahl = false;
            }
            list.Add(p);
        }
        return list;
    }

    public static Kontierung KontierungFuer(InvoiceRecord r, AccountingSettings s, PersonAccountDirectory dir)
    {
        // Kundennummer auf der Rechnung = Debitorenkonto (Taxpool KdNr); der Empfängername ist nur Rückfall für alte Rechnungen
        var perKdNr = int.TryParse(r.Kundennummer, out var kdnr) ? dir.Konten.FirstOrDefault(k => k.Konto == kdnr && k.Art == PersonenArt.Debitor) : null;
        var debitor = perKdNr ?? dir.FindDebitor(r.RecipientFirstLine, r.Recipient);
        var name = debitor?.Name is { Length: > 0 } n ? n : r.RecipientFirstLine;
        int erloes = debitor is { StandardSachkonto: > 0 } ? debitor.StandardSachkonto : s.SollstellungErloeskonto;
        string bu = debitor is { StandardSachkonto: > 0 } ? debitor.StandardBuSchluessel : s.SollstellungBuSchluessel;
        // Vorlage des Kunden (z. B. aus Taxpool "Immobilienverwaltung Gohr Re-Nr.:" an 8400) geht vor: Erlöskonto und Text
        var vorlage = debitor is null ? null : KontierungsVorschlag.VorlageFuer(s, debitor.Konto, Richtung.Eingang, name);
        if (vorlage is { Sachkonto: > 0, RechnungEinbuchen: true })
        {
            erloes = vorlage.Sachkonto;
            bu = vorlage.BuSchluessel;
        }
        var vorlagenText = debitor is null ? null : KontierungsVorschlag.KundenText(s, debitor.Konto, r.Number, name);
        return new Kontierung
        {
            Personenkonto = debitor?.Konto,
            RechnungEinbuchen = true,
            Quelle = debitor is null ? KontierungsQuelle.Offen : KontierungsQuelle.Abgleich,
            Hinweis = debitor is null
                ? (r.Kundennummer.Length > 0 ? $"Kundennummer {r.Kundennummer} ist kein Debitor" : "Debitor fehlt")
                : perKdNr is not null ? ""
                : r.Kundennummer.Length > 0 ? $"Kundennummer {r.Kundennummer} ist kein Debitor - Debitor über den Namen gefunden, bitte prüfen"
                : "Keine Kundennummer auf der Rechnung - Debitor über den Namen gefunden, bitte prüfen",
            Zeilen =
            {
                new KontierungsZeile
                {
                    Betrag = Math.Abs(r.GrossAmount ?? 0),
                    Sachkonto = erloes,
                    BuSchluessel = bu,
                    Rechnungsnummer = r.Number,
                    Belegdatum = r.Date,
                    Text = vorlagenText ?? KontierungsVorschlag.Cut($"{name} Re-Nr.: {r.Number}"),
                },
            },
        };
    }

    /// <summary>Rechnungen ohne lesbares Datum (können keinem Monat zugeordnet werden).</summary>
    public static List<InvoiceRecord> OhneDatum(IEnumerable<InvoiceRecord> rechnungen) => rechnungen.Where(r => r.Date is null).ToList();
}
