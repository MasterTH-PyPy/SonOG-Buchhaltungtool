using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>Ein Buchungssatz im DATEV-Format (EXTF Buchungsstapel). Umsatz immer positiv, Richtung über Soll/Haben.</summary>
public sealed class DatevBuchung
{
    public decimal Umsatz { get; set; }

    /// <summary>'S' = Soll, 'H' = Haben - bezogen auf <see cref="Konto"/>.</summary>
    public char SollHaben { get; set; } = 'S';

    public int Konto { get; set; }
    public int Gegenkonto { get; set; }
    public string BuSchluessel { get; set; } = "";
    public DateOnly Belegdatum { get; set; }
    public string Belegfeld1 { get; set; } = "";
    public string Belegfeld2 { get; set; } = "";
    public string Buchungstext { get; set; } = "";

    public override string ToString() =>
        $"{Belegdatum:dd.MM.yyyy} {Konto} {SollHaben} {Umsatz:N2} an {Gegenkonto} BU {BuSchluessel} [{Belegfeld1}/{Belegfeld2}] {Buchungstext}";
}

public sealed class DatevKopf
{
    public int Berater { get; set; } = 1001;
    public int Mandant { get; set; } = 1;
    public DateOnly WirtschaftsjahrBeginn { get; set; }
    public int SachkontenLaenge { get; set; } = 4;
    public DateOnly DatumVon { get; set; }
    public DateOnly DatumBis { get; set; }
    public string Bezeichnung { get; set; } = "";
    public string Diktatkuerzel { get; set; } = "";
    public string Herkunft { get; set; } = "RE";
    public bool Festschreibung { get; set; }
    public string Kontenrahmen { get; set; } = "03";
    public DateTime ErzeugtAm { get; set; } = DateTime.Now;
}

/// <summary>
/// Schreibt einen DATEV-Buchungsstapel (EXTF, Version 700, Formatversion 13, 125 Spalten), wie ihn Taxpool,
/// DATEV und viele andere Programme importieren: Semikolon getrennt, Texte in Anführungszeichen, Windows-1252.
/// </summary>
public static class DatevBuchungsstapel
{
    public static readonly string[] Spalten =
    {
        "Umsatz (ohne Soll/Haben-Kz)", "Soll/Haben-Kennzeichen", "WKZ Umsatz", "Kurs", "Basis-Umsatz", "WKZ Basis-Umsatz",
        "Konto", "Gegenkonto (ohne BU-Schlüssel)", "BU-Schlüssel", "Belegdatum", "Belegfeld 1", "Belegfeld 2", "Skonto",
        "Buchungstext", "Postensperre", "Diverse Adressnummer", "Geschäftspartnerbank", "Sachverhalt", "Zinssperre", "Beleglink",
        "Beleginfo - Art 1", "Beleginfo - Inhalt 1", "Beleginfo - Art 2", "Beleginfo - Inhalt 2", "Beleginfo - Art 3", "Beleginfo - Inhalt 3",
        "Beleginfo - Art 4", "Beleginfo - Inhalt 4", "Beleginfo - Art 5", "Beleginfo - Inhalt 5", "Beleginfo - Art 6", "Beleginfo - Inhalt 6",
        "Beleginfo - Art 7", "Beleginfo - Inhalt 7", "Beleginfo - Art 8", "Beleginfo - Inhalt 8",
        "KOST1 - Kostenstelle", "KOST2 - Kostenstelle", "Kost-Menge", "EU-Land u. UStID (Bestimmung)", "EU-Steuersatz (Bestimmung)",
        "Abw. Versteuerungsart", "Sachverhalt L+L", "Funktionsergänzung L+L", "BU 49 Hauptfunktionstyp", "BU 49 Hauptfunktionsnummer",
        "BU 49 Funktionsergänzung",
        "Zusatzinformation - Art 1", "Zusatzinformation- Inhalt 1", "Zusatzinformation - Art 2", "Zusatzinformation- Inhalt 2",
        "Zusatzinformation - Art 3", "Zusatzinformation- Inhalt 3", "Zusatzinformation - Art 4", "Zusatzinformation- Inhalt 4",
        "Zusatzinformation - Art 5", "Zusatzinformation- Inhalt 5", "Zusatzinformation - Art 6", "Zusatzinformation- Inhalt 6",
        "Zusatzinformation - Art 7", "Zusatzinformation- Inhalt 7", "Zusatzinformation - Art 8", "Zusatzinformation- Inhalt 8",
        "Zusatzinformation - Art 9", "Zusatzinformation- Inhalt 9", "Zusatzinformation - Art 10", "Zusatzinformation- Inhalt 10",
        "Zusatzinformation - Art 11", "Zusatzinformation- Inhalt 11", "Zusatzinformation - Art 12", "Zusatzinformation- Inhalt 12",
        "Zusatzinformation - Art 13", "Zusatzinformation- Inhalt 13", "Zusatzinformation - Art 14", "Zusatzinformation- Inhalt 14",
        "Zusatzinformation - Art 15", "Zusatzinformation- Inhalt 15", "Zusatzinformation - Art 16", "Zusatzinformation- Inhalt 16",
        "Zusatzinformation - Art 17", "Zusatzinformation- Inhalt 17", "Zusatzinformation - Art 18", "Zusatzinformation- Inhalt 18",
        "Zusatzinformation - Art 19", "Zusatzinformation- Inhalt 19", "Zusatzinformation - Art 20", "Zusatzinformation- Inhalt 20",
        "Stück", "Gewicht", "Zahlweise", "Forderungsart", "Veranlagungsjahr", "Zugeordnete Fälligkeit", "Skontotyp", "Auftragsnummer",
        "Buchungstyp", "USt-Schlüssel (Anzahlungen)", "EU-Land (Anzahlungen)", "Sachverhalt L+L (Anzahlungen)", "EU-Steuersatz (Anzahlungen)",
        "Erlöskonto (Anzahlungen)", "Herkunft-Kz", "Buchungs GUID", "KOST-Datum", "SEPA-Mandatsreferenz", "Skontosperre",
        "Gesellschaftername", "Beteiligtennummer", "Identifikationsnummer", "Zeichnernummer", "Postensperre bis",
        "Bezeichnung SoBil-Sachverhalt", "Kennzeichen SoBil-Buchung", "Festschreibung", "Leistungsdatum", "Datum Zuord. Steuerperiode",
        "Fälligkeit", "Generalumkehr (GU)", "Steuersatz", "Land", "Abrechnungsreferenz", "BVV-Position",
        "EU-Land u. UStID (Ursprung)", "EU-Steuersatz (Ursprung)", "Abw. Skontokonto",
    };

    private static readonly Regex BelegfeldErlaubt = new(@"[^A-Za-z0-9$&%*+\-/]", RegexOptions.Compiled);

    public static Encoding Ansi
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
    }

    /// <summary>Dateiname nach DATEV-Vorgabe: EXTF_&lt;Name&gt;.csv.</summary>
    public static string Dateiname(string name) => "EXTF_" + Regex.Replace(name, @"[^\w\-]+", "_") + ".csv";

    public static string Erzeugen(DatevKopf kopf, IReadOnlyList<DatevBuchung> buchungen)
    {
        var sb = new StringBuilder();
        sb.Append(KopfZeile(kopf)).Append("\r\n");
        sb.Append(string.Join(";", Spalten)).Append("\r\n");
        foreach (var b in buchungen) sb.Append(Zeile(b)).Append("\r\n");
        return sb.ToString();
    }

    public static void Schreiben(string pfad, DatevKopf kopf, IReadOnlyList<DatevBuchung> buchungen)
    {
        var dir = Path.GetDirectoryName(pfad);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(pfad, Erzeugen(kopf, buchungen), Ansi);
    }

    public static string KopfZeile(DatevKopf k)
    {
        var f = new string[31];
        f[0] = Q("EXTF");
        f[1] = "700";
        f[2] = "21";
        f[3] = Q("Buchungsstapel");
        f[4] = "13";
        f[5] = k.ErzeugtAm.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        f[6] = "";
        f[7] = Q(Max(k.Herkunft, 2));
        f[8] = Q("SonOG Buchhaltung");
        f[9] = Q("");
        f[10] = k.Berater.ToString(CultureInfo.InvariantCulture);
        f[11] = k.Mandant.ToString(CultureInfo.InvariantCulture);
        f[12] = k.WirtschaftsjahrBeginn.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        f[13] = k.SachkontenLaenge.ToString(CultureInfo.InvariantCulture);
        f[14] = k.DatumVon.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        f[15] = k.DatumBis.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        f[16] = Q(Max(k.Bezeichnung, 30));
        f[17] = Q(Max(k.Diktatkuerzel, 2));
        f[18] = "1"; // Finanzbuchführung
        f[19] = "0"; // Rechnungslegungszweck
        f[20] = k.Festschreibung ? "1" : "0";
        f[21] = Q("EUR");
        f[22] = "";
        f[23] = Q("");
        f[24] = "";
        f[25] = "";
        f[26] = Q(k.Kontenrahmen);
        f[27] = "";
        f[28] = "";
        f[29] = Q("");
        f[30] = Q("");
        return string.Join(";", f);
    }

    public static string Zeile(DatevBuchung b)
    {
        var f = new string[Spalten.Length];
        Array.Fill(f, "");
        f[0] = Betrag(b.Umsatz);
        f[1] = Q(b.SollHaben.ToString());
        f[2] = Q("EUR");
        f[6] = b.Konto.ToString(CultureInfo.InvariantCulture);
        f[7] = b.Gegenkonto.ToString(CultureInfo.InvariantCulture);
        f[8] = Q(Max(b.BuSchluessel.Trim(), 4));
        f[9] = b.Belegdatum.ToString("ddMM", CultureInfo.InvariantCulture);
        f[10] = Q(Belegfeld(b.Belegfeld1, 36));
        f[11] = Q(Belegfeld(b.Belegfeld2, 12));
        f[13] = Q(Max(Regex.Replace(b.Buchungstext, @"\s+", " ").Trim(), 60));
        return string.Join(";", f);
    }

    public static string Betrag(decimal v) =>
        Math.Round(Math.Abs(v), 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    public static string Belegfeld(string s, int max) => Max(BelegfeldErlaubt.Replace(s ?? "", ""), max);

    private static string Max(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
