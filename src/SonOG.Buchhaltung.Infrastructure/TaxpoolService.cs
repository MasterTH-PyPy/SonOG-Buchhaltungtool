using System.Text;
using SonOG.Buchhaltung.Core.Accounting;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Infrastructure;

public sealed class TaxpoolExportErgebnis
{
    public string Datei { get; set; } = "";
    public string? PersonenkontenDatei { get; set; }
    public int Buchungssaetze { get; set; }
    public int Belege { get; set; }
    public List<Pruefung> Hinweise { get; } = new();
}

/// <summary>
/// Schritt 2 (Kontieren) und Schritt 3 (Importdatei für Taxpool) sowie die Sollstellung der Ausgangsrechnungen.
/// Daten liegen unter %AppData%\SonOG-Buchhaltung: buchhaltung.json (Einstellungen, Regeln, Vorlagen, Kontenplan),
/// personenkonten.json, kontierung\&lt;Auszug&gt;.json und sollstellung.json.
/// </summary>
public sealed class TaxpoolService
{
    private readonly string _dataDir;

    public TaxpoolService(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(_dataDir);
        Reload();
    }

    public string SettingsPath => Path.Combine(_dataDir, "buchhaltung.json");
    public string PersonenPath => Path.Combine(_dataDir, "personenkonten.json");
    public string SollstellungPath => Path.Combine(_dataDir, "sollstellung.json");

    public AccountingSettings Settings { get; private set; } = new();
    public PersonAccountDirectory Personen { get; private set; } = new();

    public void Reload()
    {
        Settings = AccountingSettings.Load(SettingsPath);
        Personen = PersonAccountDirectory.Load(PersonenPath);
    }

    public void SaveSettings() => Settings.Save(SettingsPath);
    public void SavePersonen() => Personen.Save(PersonenPath);

    private KontierungsVorschlag Vorschlager => new(Settings, Personen);

    // ---------------------------------------------------------------------------------------------
    // Schritt 2: Kontieren
    // ---------------------------------------------------------------------------------------------

    /// <summary>Setzt an jeder Buchung die gespeicherte Kontierung oder einen neuen Vorschlag.</summary>
    /// <returns>Anzahl der aus der Datei wiederhergestellten Kontierungen.</returns>
    public int Vorbereiten(ParsedStatement st, string statementKey)
    {
        var store = KontierungStore.Load(KontierungStore.PathFor(_dataDir, statementKey));
        int restored = 0;
        for (int i = 0; i < st.Bookings.Count; i++)
        {
            var b = st.Bookings[i];
            var saved = store.Find(i, b);
            if (saved is not null) restored++;
            b.Kontierung = saved ?? Vorschlager.Vorschlagen(b);
        }
        return restored;
    }

    public Kontierung Vorschlag(Booking b) => Vorschlager.Vorschlagen(b);

    public Kontierung VorlageAnwenden(Booking b, KontierungsRegel regel) => Vorschlager.AusRegel(b, regel);

    /// <summary>Schlägt alle noch nicht bestätigten und nicht von Hand bearbeiteten Buchungen neu vor (z. B. nach neuen Regeln).</summary>
    public int NeuVorschlagen(ParsedStatement st)
    {
        int n = 0;
        foreach (var b in st.Bookings.Where(b => b.Kontierung is null || (!b.Kontierung.Bestaetigt && b.Kontierung.Quelle != KontierungsQuelle.Manuell)))
        {
            b.Kontierung = Vorschlager.Vorschlagen(b);
            n++;
        }
        return n;
    }

    public void Speichern(ParsedStatement st, string statementKey)
    {
        var path = KontierungStore.PathFor(_dataDir, statementKey);
        var old = KontierungStore.Load(path);
        KontierungStore.From(st.Bookings, old.Exporte).Save(path);
    }

    public IReadOnlyList<KontierungStore.ExportEintrag> Exporte(string statementKey) =>
        KontierungStore.Load(KontierungStore.PathFor(_dataDir, statementKey)).Exporte;

    public List<Pruefung> Pruefen(Booking b) => Buchungssaetze.Pruefen(b, Settings, Personen);

    /// <summary>Legt eine Kontierungsregel an (oder ersetzt eine gleichnamige).</summary>
    public void RegelSpeichern(KontierungsRegel regel)
    {
        var i = Settings.Regeln.FindIndex(r => r.Name == regel.Name);
        if (i >= 0) Settings.Regeln[i] = regel;
        else Settings.Regeln.Insert(0, regel); // neue Regeln zuerst, damit sie vor allgemeinen greifen
        SaveSettings();
    }

    public PersonAccount PersonenkontoAnlegen(string name, PersonenArt art, string suchbegriff = "")
    {
        var konto = Personen.NextFree(art, Settings);
        var acc = Personen.Upsert(konto, name, art);
        if (suchbegriff.Trim().Length > 0 && !acc.Suchbegriffe.Contains(suchbegriff.Trim())) acc.Suchbegriffe.Add(suchbegriff.Trim());
        SavePersonen();
        return acc;
    }

    // ---------------------------------------------------------------------------------------------
    // Schritt 3: Importdatei für Taxpool
    // ---------------------------------------------------------------------------------------------

    /// <param name="nummernEndgueltig">Nummern aus Schritt 1 sind vergeben (Export erfolgt) - sonst stimmen die Belegnummern nicht.</param>
    public TaxpoolExportErgebnis ExportKontoauszug(ParsedStatement st, string statementKey, string outputFolder, bool nummernEndgueltig)
    {
        if (!nummernEndgueltig)
            throw new InvalidOperationException("Die Belegnummern dieses Auszugs sind noch nicht endgültig. Bitte zuerst in Schritt 1 exportieren " +
                                                "(Nummern vergeben, Belege stempeln, Ordner drucken), dann die Taxpool-Datei erzeugen.");
        if (string.IsNullOrWhiteSpace(outputFolder)) throw new InvalidOperationException("Kein Ausgabeordner angegeben.");

        var pruefungen = st.Bookings.SelectMany(Pruefen).ToList();
        Abbrechen(pruefungen);

        var rows = st.Bookings.SelectMany(b => Buchungssaetze.AusKontoauszug(b, Settings)).ToList();
        var name = "Kontoauszug_" + statementKey;
        var kopf = Buchungssaetze.Kopf(rows, Settings, Bezeichnung(st.Title.Length > 0 ? "Auszug " + st.Title : name));
        var result = Schreiben(outputFolder, name, kopf, rows);
        result.Belege = st.Bookings.Count;
        result.Hinweise.AddRange(pruefungen);

        var path = KontierungStore.PathFor(_dataDir, statementKey);
        var store = KontierungStore.From(st.Bookings, KontierungStore.Load(path).Exporte);
        store.Exporte.Add(new KontierungStore.ExportEintrag { Am = DateTime.Now, Datei = result.Datei, Buchungssaetze = rows.Count });
        store.Save(path);
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Sollstellung der Ausgangsrechnungen (eigener Teil, eigener Status)
    // ---------------------------------------------------------------------------------------------

    public List<SollstellungsPosten> SollstellungVorbereiten(IEnumerable<InvoiceRecord> rechnungen, int year, int month) =>
        Sollstellung.Vorbereiten(rechnungen, year, month, Settings, Personen, SollstellungState.Load(SollstellungPath));

    public List<Pruefung> Pruefen(SollstellungsPosten p) => Buchungssaetze.Pruefen(p, Settings, Personen);

    public TaxpoolExportErgebnis ExportSollstellung(IReadOnlyList<SollstellungsPosten> posten, int year, int month, string outputFolder)
    {
        if (string.IsNullOrWhiteSpace(outputFolder)) throw new InvalidOperationException("Kein Ausgabeordner angegeben.");
        var state = SollstellungState.Load(SollstellungPath);
        var auswahl = posten.Where(p => p.Auswahl).ToList();
        if (auswahl.Count == 0) throw new InvalidOperationException("Keine Rechnung ausgewählt.");
        var doppelt = auswahl.Where(p => state.Gestellt.ContainsKey(p.Rechnung.Number)).Select(p => p.Rechnung.Number).ToList();
        if (doppelt.Count > 0)
            throw new InvalidOperationException("Diese Rechnungen wurden schon sollgestellt: " + string.Join(", ", doppelt) +
                                                ". Falls der Import in Taxpool gelöscht wurde: Monat freigeben.");

        var pruefungen = auswahl.SelectMany(Pruefen).ToList();
        Abbrechen(pruefungen);

        var rows = auswahl.SelectMany(Buchungssaetze.AusSollstellung).ToList();
        var monat = SollstellungState.MonatKey(year, month);
        var name = "Sollstellung_" + monat;
        var kopf = Buchungssaetze.Kopf(rows, Settings, Bezeichnung($"Ausgangsrechnungen {month:00}/{year}"));
        var result = Schreiben(outputFolder, name, kopf, rows);
        result.Belege = auswahl.Count;
        result.Hinweise.AddRange(pruefungen);

        foreach (var p in auswahl)
        {
            state.Gestellt[p.Rechnung.Number] = new SollstellungState.Eintrag { Monat = monat, Datei = result.Datei, Am = DateTime.Now, Debitor = p.Kontierung.Personenkonto ?? 0 };
            p.BereitsGestellt = monat;
            p.Auswahl = false;
        }
        state.Save(SollstellungPath);
        return result;
    }

    public int SollstellungFreigeben(int year, int month)
    {
        var state = SollstellungState.Load(SollstellungPath);
        var n = state.Freigeben(SollstellungState.MonatKey(year, month));
        state.Save(SollstellungPath);
        return n;
    }

    // ---------------------------------------------------------------------------------------------
    // Daten aus Taxpool übernehmen (Kontenplan, Personenkonten, Buchungsvorlagen)
    // ---------------------------------------------------------------------------------------------

    public List<TaxpoolImportErgebnis> ImportTaxpool(IEnumerable<string> files)
    {
        var list = files.Select(f => TaxpoolImport.Datei(f, Settings, Personen)).ToList();
        SaveSettings();
        SavePersonen();
        return list;
    }

    // ---------------------------------------------------------------------------------------------

    private static void Abbrechen(List<Pruefung> pruefungen)
    {
        var fehler = pruefungen.Where(p => p.Stufe == PruefStufe.Fehler).ToList();
        if (fehler.Count == 0) return;
        var sb = new StringBuilder($"Die Importdatei wurde nicht erzeugt - {fehler.Count} Fehler:\n\n");
        foreach (var f in fehler.Take(15)) sb.Append("• ").Append(f.Nummer).Append(": ").Append(f.Text).Append('\n');
        if (fehler.Count > 15) sb.Append($"... und {fehler.Count - 15} weitere\n");
        throw new InvalidOperationException(sb.ToString());
    }

    private static string Bezeichnung(string s) => s.Length <= 30 ? s : s[..30];

    private TaxpoolExportErgebnis Schreiben(string outputFolder, string name, DatevKopf kopf, List<DatevBuchung> rows)
    {
        Directory.CreateDirectory(outputFolder);
        var result = new TaxpoolExportErgebnis
        {
            Datei = Path.Combine(outputFolder, DatevBuchungsstapel.Dateiname(name)),
            Buchungssaetze = rows.Count,
        };
        DatevBuchungsstapel.Schreiben(result.Datei, kopf, rows);

        // Liste der verwendeten Personenkonten, falls sie in Taxpool noch angelegt werden müssen
        var used = rows.SelectMany(r => new[] { r.Konto, r.Gegenkonto }).Where(Settings.IstPersonenkonto).Distinct().OrderBy(k => k).ToList();
        if (used.Count > 0)
        {
            result.PersonenkontenDatei = Path.Combine(outputFolder, "Personenkonten_" + name + ".csv");
            var sb = new StringBuilder("Konto;Name;Art\r\n");
            foreach (var k in used)
            {
                var p = Personen.Get(k);
                var art = p?.Art.ToString() ?? (k >= Settings.DebitorenVon && k <= Settings.DebitorenBis ? "Debitor" : "Kreditor");
                sb.Append(k).Append(';').Append((p?.Name ?? "").Replace(";", ",")).Append(';').Append(art).Append("\r\n");
            }
            File.WriteAllText(result.PersonenkontenDatei, sb.ToString(), DatevBuchungsstapel.Ansi);
        }
        return result;
    }
}
