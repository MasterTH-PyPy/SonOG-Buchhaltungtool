using System.Text;
using SonOG.Buchhaltung.Core.Accounting;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Infrastructure;

public sealed class TaxpoolExportErgebnis
{
    public string Datei { get; set; } = "";
    public string? PersonenkontenDatei { get; set; }
    /// <summary>DATEV-Stammdaten der Personenkonten, die in Taxpool noch angelegt werden müssen (vor der Buchungsdatei importieren).</summary>
    public string? StammdatenDatei { get; set; }
    public int NeuePersonenkonten { get; set; }
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
            // Nur Bestätigtes und von Hand Bearbeitetes wiederherstellen; reine Vorschläge neu berechnen
            // (das Tool weiß inzwischen vielleicht mehr: neue Regeln, Suchbegriffe, Standardkonten)
            if (saved is { Bestaetigt: false, Quelle: not KontierungsQuelle.Manuell }) saved = null;
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
        KontierungStore.From(st.Bookings, old).Save(path);
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

    /// <summary>Legt Debitoren mit Kundennummer und Anschrift aus CAO an (für Taxpool vorbereitet, Stammdaten gehen mit dem Export mit).</summary>
    public List<PersonAccount> DebitorenAusCaoAnlegen(IEnumerable<CaoRechnung> kunden)
    {
        var list = kunden.Select(c => Sollstellung.DebitorAusCao(c, Personen)).ToList();
        SavePersonen();
        return list;
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

        // Eingangsrechnungen (Beleg auf einem Lieferanten einbuchen) bekommen eine eigene EK-Belegnummer
        var path = KontierungStore.PathFor(_dataDir, statementKey);
        var alt = KontierungStore.Load(path);
        var (taxpoolEk, ekHinweis) = EkStandAusTaxpool();
        var ek = EkNummernVergeben(st, alt, taxpoolEk);

        var rows = st.Bookings.SelectMany(b => Buchungssaetze.AusKontoauszug(b, Settings, ek.GetValueOrDefault(b.Number))).ToList();
        var name = "Kontoauszug_" + statementKey;
        var kopf = Buchungssaetze.Kopf(rows, Settings, Bezeichnung(st.Title.Length > 0 ? "Auszug " + st.Title : name));
        var result = Schreiben(outputFolder, name, kopf, rows);
        result.Belege = st.Bookings.Count;
        result.Hinweise.AddRange(pruefungen);

        var store = KontierungStore.From(st.Bookings, alt);
        foreach (var (k, v) in ek) store.EkNummern[k] = v;
        store.Exporte.Add(new KontierungStore.ExportEintrag { Am = DateTime.Now, Datei = result.Datei, Buchungssaetze = rows.Count });
        store.Save(path);
        SaveSettings(); // EK-Zähler
        if (ek.Count > 0)
            result.Hinweise.Add(new Pruefung(PruefStufe.Hinweis, "EK", $"Eingangsrechnungen {ek.Values.First()} bis {ek.Values.Last()} - {ekHinweis}"));
        return result;
    }

    /// <summary>
    /// Liest bei jedem Export den aktuellen EK-Stand aus der Taxpool-Datei (in Taxpool von Hand erfasste Belege zählen mit).
    /// Liefert die höchste Nummer je Jahr und einen Hinweis, wie aktuell die Prüfung ist.
    /// </summary>
    private (Dictionary<int, int> MaxJeJahr, string Hinweis) EkStandAusTaxpool()
    {
        var path = Settings.TaxpoolDatei;
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path)))
            return (new(), "EK-Stand NICHT in Taxpool geprüft (keine Taxpool-Datei gemerkt) - weitergezählt ab dem letzten bekannten Stand");
        try
        {
            var d = TaxpoolDatenbank.Lesen(path);
            var max = d.EkMax.Where(x => x.Key.Kreis == Settings.EkBelegkreis).ToDictionary(x => x.Key.Jahr, x => x.Value);
            foreach (var (j, m) in max) Settings.EkTaxpoolStand[j] = m;
            var stand = Stand(path).ToLocalTime();
            return (max, $"EK-Stand geprüft in {Path.GetFileName(path)} (Stand {stand:dd.MM.yyyy HH:mm}; danach in Taxpool erfasste Belege sind dort nicht enthalten)");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (new(), "EK-Stand NICHT in Taxpool geprüft (" + ex.Message + ") - weitergezählt ab dem letzten bekannten Stand");
        }
    }

    /// <summary>
    /// EK-Belegnummer je Eingangsrechnung (Personenkonto ist ein Lieferant, Beleg wird eingebucht). Schon vergebene
    /// Nummern desselben Auszugs werden wiederverwendet, neue zählen je Jahr ab dem höchsten bekannten Stand weiter.
    /// </summary>
    private Dictionary<string, string> EkNummernVergeben(ParsedStatement st, KontierungStore alt, Dictionary<int, int> taxpoolMax)
    {
        var result = new Dictionary<string, string>();
        var eingang = st.Bookings.Where(IstEingangsrechnung).ToList();
        foreach (var b in eingang)
        {
            int jahr = b.Date.Year;
            int inTaxpool = taxpoolMax.GetValueOrDefault(jahr);
            // Nummer aus einem früheren Export desselben Auszugs nur wiederverwenden, wenn Taxpool sie noch nicht kennt
            // und sie nicht unter einer von Hand gesetzten nächsten Nummer liegt
            if (alt.EkNummern.TryGetValue(b.Number, out var vorhanden) && EkZahl(vorhanden) is int vn && vn > inTaxpool &&
                vn >= Settings.EkAbHand.GetValueOrDefault(jahr))
            {
                result[b.Number] = vorhanden;
                continue;
            }
            int n = Math.Max(inTaxpool, Settings.EkLetzteNummer.GetValueOrDefault(jahr)) + 1;
            Settings.EkLetzteNummer[jahr] = n;
            result[b.Number] = $"{Settings.EkBelegkreis} {n}".Trim();
        }
        return result;
    }

    private bool IstEingangsrechnung(Booking b) =>
        b.Kontierung is { RechnungEinbuchen: true, Personenkonto: int pk } && pk >= Settings.KreditorenVon && pk <= Settings.KreditorenBis;

    /// <summary>Nächste EK-Nummer für die Anzeige (Taxpool-Stand des letzten Lesens und Zähler des Tools).</summary>
    public int EkNaechste(int jahr) => Settings.EkNaechste(jahr);

    /// <summary>Nächste EK-Nummer von Hand setzen und speichern.</summary>
    public void EkNaechsteSetzen(int jahr, int naechste)
    {
        Settings.EkNaechsteSetzen(jahr, naechste);
        SaveSettings();
    }

    private static int? EkZahl(string ek) =>
        System.Text.RegularExpressions.Regex.Match(ek, @"(\d+)\s*$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

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

        // Neue Kunden aus CAO jetzt als Debitor anlegen; ihre Stammdaten gehen als eigene Datei mit
        var neu = Sollstellung.FehlendeDebitoren(auswahl, Settings, Personen);
        if (neu.Count > 0) DebitorenAusCaoAnlegen(neu);

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
        var list = new List<TaxpoolImportErgebnis>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".csv" or ".txt") list.Add(TaxpoolImport.Datei(f, Settings, Personen));
            else list.Add(ImportTaxpoolDatenbankIntern(f));
        }
        SaveSettings();
        SavePersonen();
        return list;
    }

    /// <summary>
    /// Liest Vorlagen, Kontenplan, Steuerschlüssel und Personenkonten direkt aus der Taxpool-Datensicherung (*.dbb),
    /// einer Tabellendatei (*.dbd) oder einem Ordner mit Tabellen. Nur lesend; der Pfad wird für das automatische
    /// Aktualisieren gemerkt.
    /// </summary>
    public TaxpoolImportErgebnis ImportTaxpoolDatenbank(string path)
    {
        var e = ImportTaxpoolDatenbankIntern(path);
        SaveSettings();
        SavePersonen();
        return e;
    }

    private TaxpoolImportErgebnis ImportTaxpoolDatenbankIntern(string path)
    {
        var daten = TaxpoolDatenbank.Lesen(path);
        var e = TaxpoolVorlagenImport.Uebernehmen(daten, Settings, Personen);
        Settings.TaxpoolDatei = path;
        Settings.TaxpoolDateiStand = Stand(path);
        Settings.TaxpoolLeseStand = AccountingSettings.AktuellerTaxpoolLeseStand;
        e.Hinweise.Insert(0, Path.GetFileName(path) + ": " + e);
        return e;
    }

    /// <summary>
    /// Liest die gemerkte Taxpool-Datei neu, wenn sie sich seit dem letzten Lesen geändert hat.
    /// Liefert null, wenn nichts zu tun war; Fehler werden als Text zurückgegeben statt geworfen.
    /// </summary>
    public string? TaxpoolAktualisieren()
    {
        var path = Settings.TaxpoolDatei;
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path))) return null;
        var stand = Stand(path);
        bool toolNeuer = Settings.TaxpoolLeseStand < AccountingSettings.AktuellerTaxpoolLeseStand;
        if (!toolNeuer && Settings.TaxpoolDateiStand is { } alt && stand <= alt) return null;
        try
        {
            var e = ImportTaxpoolDatenbank(path);
            return "Taxpool-Vorlagen aktualisiert: " + e;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return "Taxpool-Datei konnte nicht gelesen werden: " + ex.Message;
        }
    }

    private static DateTime Stand(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.dbd").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max()
            : File.GetLastWriteTimeUtc(path);

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
            var sb = new StringBuilder("Konto;Name;Art;Neu;Strasse;PLZ;Ort\r\n");
            static string C(string? s) => (s ?? "").Replace(";", ",");
            foreach (var k in used)
            {
                var p = Personen.Get(k);
                var art = p?.Art.ToString() ?? (k >= Settings.DebitorenVon && k <= Settings.DebitorenBis ? "Debitor" : "Kreditor");
                sb.Append(k).Append(';').Append(C(p?.Name)).Append(';').Append(art).Append(';').Append(p?.NeuFuerTaxpool == true ? "ja" : "")
                  .Append(';').Append(C(p?.Strasse)).Append(';').Append(C(p?.Plz)).Append(';').Append(C(p?.Ort)).Append("\r\n");
            }
            File.WriteAllText(result.PersonenkontenDatei, sb.ToString(), DatevBuchungsstapel.Ansi);

            // In Taxpool noch nicht vorhandene Konten (z. B. neue Kunden aus CAO) als DATEV-Stammdaten
            var neu = used.Select(Personen.Get).Where(p => p is { NeuFuerTaxpool: true }).Select(p => p!).ToList();
            if (neu.Count > 0)
            {
                result.StammdatenDatei = Path.Combine(outputFolder, DatevPersonenkonten.Dateiname(name));
                File.WriteAllText(result.StammdatenDatei, DatevPersonenkonten.Erzeugen(kopf, neu), DatevBuchungsstapel.Ansi);
                result.NeuePersonenkonten = neu.Count;
                result.Hinweise.Add(new Pruefung(PruefStufe.Hinweis, "Personenkonten",
                    $"{neu.Count} neue(s) Personenkonto/-konten ({string.Join(", ", neu.Select(p => p.Konto))}) - zuerst " +
                    $"{Path.GetFileName(result.StammdatenDatei)} in Taxpool importieren (Stammdaten Debitoren/Kreditoren), dann die Buchungen."));
            }
        }
        return result;
    }
}
