using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>In welche Richtung eine Regel greift.</summary>
public enum Richtung
{
    Alle,
    /// <summary>Nur Abbuchungen (Betrag negativ).</summary>
    Ausgang,
    /// <summary>Nur Gutschriften auf dem Konto (Betrag positiv).</summary>
    Eingang,
}

/// <summary>
/// Kontierungsregel oder -vorlage. Eine Regel mit Stichwort oder Kategorie wird automatisch vorgeschlagen;
/// ohne beides ist sie nur eine Vorlage, die man in der Oberfläche von Hand auf eine Buchung anwendet
/// (z. B. aus Taxpool übernommene Buchungsvorlagen).
/// </summary>
public sealed class KontierungsRegel
{
    public string Name { get; set; } = "";

    /// <summary>Text im Buchungstext (Groß/klein egal). Mehrere Alternativen mit | trennen, z. B. "AMAZON|AMZN".</summary>
    public string Stichwort { get; set; } = "";

    /// <summary>Kategorie aus Schritt 1 (z. B. "Bankgebuehren", "AmazonAusgabe"); leer = jede.</summary>
    public string Kategorie { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Richtung Richtung { get; set; } = Richtung.Alle;

    /// <summary>Sachkonto (Aufwand, Erlös, Privat ...). 0 = Standardkonto des Personenkontos verwenden.</summary>
    public int Sachkonto { get; set; }

    /// <summary>BU-/Steuerschlüssel, z. B. "9" (19 % Vorsteuer). Bei Automatikkonten leer lassen.</summary>
    public string BuSchluessel { get; set; } = "";

    /// <summary>Buchungstext; leer = aus dem Kontoauszug.</summary>
    public string Buchungstext { get; set; } = "";

    /// <summary>Festes Personenkonto (Kreditor/Debitor); 0 = keins bzw. über <see cref="PersonenkontoSuchen"/>.</summary>
    public int Personenkonto { get; set; }

    /// <summary>Personenkonto über die Suchbegriffe der Personenkonten im Buchungstext finden.</summary>
    public bool PersonenkontoSuchen { get; set; }

    /// <summary>Beleg erst auf dem Personenkonto einbuchen und dann mit der Zahlung ausbuchen (Doppik-Weg).</summary>
    public bool RechnungEinbuchen { get; set; } = true;

    /// <summary>
    /// Aufteilung (Splitvorlage): je Teil Sachkonto, BU und Anteil. Der Buchungsbetrag wird nach den Anteilen verteilt
    /// (z. B. 0,7 / 0,3); der Rundungsrest kommt auf den letzten Teil. Leer = eine Zeile mit <see cref="Sachkonto"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RegelAnteil>? Aufteilung { get; set; }

    /// <summary>Herkunft aus Taxpool (GUID der Buchungsvorlage); beim erneuten Einlesen wird die Regel aktualisiert.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaxpoolId { get; set; }

    [JsonIgnore]
    public bool IstAutomatisch => Stichwort.Trim().Length > 0 || Kategorie.Trim().Length > 0;

    public bool Passt(string text, string kategorie, decimal betrag)
    {
        if (!IstAutomatisch) return false;
        if (Richtung == Richtung.Ausgang && betrag >= 0) return false;
        if (Richtung == Richtung.Eingang && betrag <= 0) return false;
        if (Kategorie.Trim().Length > 0 && !string.Equals(Kategorie.Trim(), kategorie, StringComparison.OrdinalIgnoreCase)) return false;
        if (Stichwort.Trim().Length == 0) return true;
        return Stichwort.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString() =>
        (Name.Length > 0 ? Name : (Stichwort.Length > 0 ? Stichwort : $"Konto {Sachkonto}")) +
        (IstAutomatisch ? "  [auto: " + (Stichwort.Length > 0 ? Stichwort : Kategorie) + "]" : "");
}

/// <summary>Ein Teil einer Splitvorlage.</summary>
public sealed class RegelAnteil
{
    public int Sachkonto { get; set; }
    public string BuSchluessel { get; set; } = "";
    /// <summary>Anteil am Buchungsbetrag (z. B. 0,7).</summary>
    public decimal Anteil { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// Einstellungen für die Buchhaltung (buchhaltung.json): Kontenrahmen, Bankkonto, Nummernkreise der Personenkonten,
/// Kontierungsregeln und Vorlagen. Die Standardwerte gehen von SKR03 aus.
/// </summary>
public sealed class AccountingSettings
{
    /// <summary>"03" = SKR03, "04" = SKR04.</summary>
    public string Kontenrahmen { get; set; } = "03";

    public int SachkontenLaenge { get; set; } = 4;

    /// <summary>Sachkonto der Bank (SKR03: 1200, SKR04: 1800).</summary>
    public int Bankkonto { get; set; } = 1200;

    public int Beraternummer { get; set; } = 1001;
    public int Mandantennummer { get; set; } = 1;
    public int WirtschaftsjahrBeginnMonat { get; set; } = 1;

    public int DebitorenVon { get; set; } = 10000;
    public int DebitorenBis { get; set; } = 69999;
    public int KreditorenVon { get; set; } = 70000;
    public int KreditorenBis { get; set; } = 99999;

    /// <summary>Erlöskonto für die Sollstellung der Ausgangsrechnungen (SKR03 8400 = Erlöse 19 % USt, Automatikkonto).</summary>
    public int SollstellungErloeskonto { get; set; } = 8400;
    public string SollstellungBuSchluessel { get; set; } = "";

    /// <summary>
    /// Beim Ausgleich offener Posten (Zahlung auf ein Personenkonto mit Rechnungsnummer) steht die Rechnungsnummer
    /// in Belegfeld 1 (dort sucht die Buchhaltung den offenen Posten) und die laufende Nummer in Belegfeld 2.
    /// Sonst steht die laufende Nummer immer in Belegfeld 1.
    /// </summary>
    public bool RechnungsnummerInBelegfeld1BeiAusgleich { get; set; } = true;

    /// <summary>Herkunftskennzeichen im DATEV-Kopf (2 Zeichen).</summary>
    public string Herkunft { get; set; } = "RE";

    /// <summary>Diktatkürzel im DATEV-Kopf (2 Zeichen).</summary>
    public string Diktatkuerzel { get; set; } = "KA";

    /// <summary>Buchungen beim Import festschreiben. Standard: nein, damit man den Import erst prüfen kann.</summary>
    public bool Festschreiben { get; set; }

    /// <summary>Automatische Regeln (mit Stichwort/Kategorie) und Vorlagen (ohne). Reihenfolge = Priorität.</summary>
    public List<KontierungsRegel> Regeln { get; set; } = new()
    {
        new KontierungsRegel
        {
            Name = "Amazon Einkauf", Stichwort = "AMAZON", Richtung = Richtung.Ausgang, Sachkonto = 3400,
            PersonenkontoSuchen = true, RechnungEinbuchen = true,
        },
        new KontierungsRegel
        {
            Name = "Amazon Erstattung", Stichwort = "AMAZON", Richtung = Richtung.Eingang, Sachkonto = 3400,
            PersonenkontoSuchen = true, RechnungEinbuchen = true,
        },
        new KontierungsRegel
        {
            Name = "Bankentgelt", Kategorie = "Bankgebuehren", Sachkonto = 6855, RechnungEinbuchen = false,
        },
        new KontierungsRegel { Name = "Privatentnahme", Sachkonto = 1800, RechnungEinbuchen = false },
        new KontierungsRegel { Name = "Privateinlage", Sachkonto = 1890, RechnungEinbuchen = false },
    };

    /// <summary>Kontenbezeichnungen (z. B. aus dem Taxpool-Kontenplan eingelesen), nur für die Anzeige und Prüfung.</summary>
    public Dictionary<int, string> Kontenplan { get; set; } = new();

    /// <summary>Automatikkonten aus Taxpool (Steuer steckt im Konto, kein BU-Schlüssel setzen).</summary>
    public List<int> Automatikkonten { get; set; } = new();

    /// <summary>
    /// Voreingestellter Steuerschlüssel je Sachkonto aus Taxpool, Format "VSt|USt" (z. B. "9|3" bei 19 %).
    /// Wird vorgeschlagen, wenn ein Konto von Hand eingetragen wird, und bei der Prüfung herangezogen.
    /// </summary>
    public Dictionary<int, string> KontoSteuerschluessel { get; set; } = new();

    /// <summary>Taxpool-Datensicherung (*.dbb) oder Tabellenordner, aus dem Vorlagen, Konten und Personenkonten gelesen werden.</summary>
    public string TaxpoolDatei { get; set; } = "";

    /// <summary>Stand der zuletzt gelesenen Taxpool-Datei (Änderungszeit), für das automatische Aktualisieren.</summary>
    public DateTime? TaxpoolDateiStand { get; set; }

    [JsonIgnore]
    public int PersonenkontenLaenge => SachkontenLaenge + 1;

    public bool IstPersonenkonto(int konto) =>
        (konto >= DebitorenVon && konto <= DebitorenBis) || (konto >= KreditorenVon && konto <= KreditorenBis);

    public bool IstSachkonto(int konto) => konto > 0 && konto.ToString().Length <= SachkontenLaenge;

    public string KontoName(int konto) => Kontenplan.TryGetValue(konto, out var n) ? n : "";

    public bool IstAutomatikkonto(int konto) => Automatikkonten.Contains(konto);

    /// <summary>BU-Schlüssel, den Taxpool für das Konto voreingestellt hat ("" = keiner/Automatikkonto/unbekannt).</summary>
    public string BuVorschlag(int konto, bool ausgabe)
    {
        if (IstAutomatikkonto(konto) || !KontoSteuerschluessel.TryGetValue(konto, out var v)) return "";
        var p = v.Split('|');
        return (ausgabe ? p.ElementAtOrDefault(0) : p.ElementAtOrDefault(1)) ?? "";
    }

    /// <summary>Beginn des Wirtschaftsjahres, in das das Datum fällt.</summary>
    public DateOnly WirtschaftsjahrBeginn(DateOnly d)
    {
        int m = Math.Clamp(WirtschaftsjahrBeginnMonat, 1, 12);
        return d.Month >= m ? new DateOnly(d.Year, m, 1) : new DateOnly(d.Year - 1, m, 1);
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AccountingSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var def = new AccountingSettings();
            def.Save(path);
            return def;
        }
        return JsonSerializer.Deserialize<AccountingSettings>(File.ReadAllText(path), Json) ?? new AccountingSettings();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
