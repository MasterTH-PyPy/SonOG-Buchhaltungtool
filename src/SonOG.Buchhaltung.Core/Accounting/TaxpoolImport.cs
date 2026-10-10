using System.Text;
using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Matching;

namespace SonOG.Buchhaltung.Core.Accounting;

public sealed class TaxpoolImportErgebnis
{
    public int Sachkonten { get; set; }
    public int Personenkonten { get; set; }
    public int Vorlagen { get; set; }
    public int Aktualisiert { get; set; }
    public int Entfernt { get; set; }
    public int Uebersprungen { get; set; }
    public List<string> Hinweise { get; } = new();

    public override string ToString() =>
        $"{Sachkonten} Sachkonten, {Personenkonten} Personenkonten, {Vorlagen} Vorlagen neu" +
        (Aktualisiert > 0 ? $", {Aktualisiert} aktualisiert" : "") +
        (Entfernt > 0 ? $", {Entfernt} entfernt" : "") +
        (Uebersprungen > 0 ? $", {Uebersprungen} übersprungen" : "");
}

/// <summary>
/// Liest Exporte aus Taxpool (CSV/Text, Spalten werden über die Überschrift erkannt):
/// <list type="bullet">
/// <item>Kontenplan bzw. Personenkonten: Spalten Konto + Bezeichnung/Name. Konten in den Personenkonten-Nummernkreisen
/// werden Debitoren/Kreditoren, alle anderen landen im Kontenplan (für Anzeige und Prüfung).</item>
/// <item>Buchungsvorlagen: Spalten Konto + Gegenkonto (+ BU, Buchungstext, Vorlagenname). Daraus werden Vorlagen,
/// die man in Schritt 2 auf eine Buchung anwenden oder mit einem Stichwort zur automatischen Regel machen kann.</item>
/// </list>
/// </summary>
public static class TaxpoolImport
{
    private static readonly string[] KontoNamen = { "konto", "kontonummer", "kontonr", "kto", "ktonr", "sachkonto", "kontono" };
    private static readonly string[] GegenkontoNamen = { "gegenkonto", "gegenkontonummer", "gegenkto", "gkto", "gegenkontonr", "gkonto" };
    private static readonly string[] BezeichnungNamen = { "bezeichnung", "kontobezeichnung", "kontoname", "name", "beschriftung", "firma", "name1" };
    private static readonly string[] VorlageNamen = { "vorlage", "vorlagenname", "vorlagenbezeichnung", "buchungsvorlage", "kurzbezeichnung" };
    private static readonly string[] BuNamen = { "bu", "buschluessel", "buschlussel", "steuerschluessel", "steuerschlussel", "steuercode", "steuer", "ustschluessel", "steuerkennzeichen" };
    private static readonly string[] TextNamen = { "buchungstext", "text", "verwendungszweck" };

    /// <summary>Liest eine Datei (UTF-8 mit BOM oder Windows-1252) und übernimmt den Inhalt.</summary>
    public static TaxpoolImportErgebnis Datei(string path, AccountingSettings s, PersonAccountDirectory dir)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { text = DatevBuchungsstapel.Ansi.GetString(bytes); }
        }
        var e = Text(text, s, dir);
        e.Hinweise.Insert(0, Path.GetFileName(path) + ": " + e);
        return e;
    }

    public static TaxpoolImportErgebnis Text(string content, AccountingSettings s, PersonAccountDirectory dir)
    {
        var result = new TaxpoolImportErgebnis();
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Trim().Length > 0).ToList();
        // DATEV-Exporte haben vor der Überschrift eine Kopfzeile ("EXTF"/"DTVF"): überspringen.
        if (lines.Count > 0 && Regex.IsMatch(lines[0], "^\"?(EXTF|DTVF)\"?[;,]")) lines.RemoveAt(0);
        if (lines.Count < 2)
        {
            result.Hinweise.Add("Datei enthält keine Datenzeilen.");
            return result;
        }

        char sep = new[] { ';', '\t', ',' }.OrderByDescending(c => lines[0].Count(ch => ch == c)).First();
        var header = Split(lines[0], sep).Select(Norm).ToList();
        int Col(string[] names) => header.FindIndex(h => names.Contains(h));
        int cKonto = Col(KontoNamen), cGegen = Col(GegenkontoNamen), cBez = Col(BezeichnungNamen),
            cVorlage = Col(VorlageNamen), cBu = Col(BuNamen), cText = Col(TextNamen);
        // DATEV-Spaltenname "Gegenkonto (ohne BU-Schlüssel)" / "BU-Schlüssel"
        if (cGegen < 0) cGegen = header.FindIndex(h => h.StartsWith("gegenkonto", StringComparison.Ordinal));
        if (cBu < 0) cBu = header.FindIndex(h => h.StartsWith("buschl", StringComparison.Ordinal));

        if (cKonto < 0)
        {
            result.Hinweise.Add("Keine Spalte \"Konto\" gefunden. Erkannte Spalten: " + string.Join(", ", header));
            return result;
        }

        bool vorlagen = cGegen >= 0;
        if (!vorlagen && cBez < 0)
        {
            result.Hinweise.Add("Weder \"Gegenkonto\" (Vorlagen) noch \"Bezeichnung\" (Kontenplan) gefunden. Erkannte Spalten: " + string.Join(", ", header));
            return result;
        }

        foreach (var line in lines.Skip(1))
        {
            var f = Split(line, sep);
            string Get(int c) => c >= 0 && c < f.Count ? f[c].Trim() : "";
            int konto = Zahl(Get(cKonto));
            if (konto <= 0)
            {
                result.Uebersprungen++;
                continue;
            }

            if (!vorlagen)
            {
                var name = Get(cBez);
                if (s.IstPersonenkonto(konto))
                {
                    var art = konto >= s.DebitorenVon && konto <= s.DebitorenBis ? PersonenArt.Debitor : PersonenArt.Kreditor;
                    dir.Upsert(konto, name, art);
                    result.Personenkonten++;
                }
                else
                {
                    s.Kontenplan[konto] = name;
                    result.Sachkonten++;
                }
                continue;
            }

            int gegen = Zahl(Get(cGegen));
            if (gegen <= 0)
            {
                result.Uebersprungen++;
                continue;
            }
            var regel = new KontierungsRegel
            {
                BuSchluessel = Get(cBu),
                Buchungstext = Get(cText),
            };
            if (konto == s.Bankkonto || gegen == s.Bankkonto)
            {
                regel.Sachkonto = konto == s.Bankkonto ? gegen : konto;
                if (s.IstPersonenkonto(regel.Sachkonto))
                {
                    regel.Personenkonto = regel.Sachkonto;
                    regel.Sachkonto = 0;
                    regel.RechnungEinbuchen = false;
                }
                else regel.RechnungEinbuchen = false;
            }
            else if (s.IstPersonenkonto(konto) || s.IstPersonenkonto(gegen))
            {
                regel.Personenkonto = s.IstPersonenkonto(konto) ? konto : gegen;
                regel.Sachkonto = s.IstPersonenkonto(konto) ? gegen : konto;
                regel.RechnungEinbuchen = true;
            }
            else
            {
                regel.Sachkonto = gegen;
                regel.RechnungEinbuchen = false;
                result.Hinweise.Add($"Vorlage {konto}/{gegen}: weder Bank- noch Personenkonto - Gegenkonto {gegen} als Sachkonto übernommen.");
            }

            var vorlagenName = Get(cVorlage);
            if (vorlagenName.Length == 0) vorlagenName = Get(cBez);
            if (vorlagenName.Length == 0) vorlagenName = regel.Buchungstext;
            if (vorlagenName.Length == 0) vorlagenName = $"Taxpool {konto}/{gegen}";
            regel.Name = "Taxpool: " + vorlagenName;

            var existing = s.Regeln.FindIndex(r => r.Name == regel.Name);
            if (existing >= 0)
            {
                // Stichwort/Kategorie, die der Anwender ergänzt hat, bleiben erhalten
                regel.Stichwort = s.Regeln[existing].Stichwort;
                regel.Kategorie = s.Regeln[existing].Kategorie;
                regel.Richtung = s.Regeln[existing].Richtung;
                regel.PersonenkontoSuchen = s.Regeln[existing].PersonenkontoSuchen;
                s.Regeln[existing] = regel;
            }
            else s.Regeln.Add(regel);
            result.Vorlagen++;
        }
        return result;
    }

    private static int Zahl(string s)
    {
        var digits = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.' || c == ' ').Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 9 && int.TryParse(digits, out var n) ? n : 0;
    }

    private static string Norm(string h) => NameMatcher.Normalize(h).Replace(" ", "");

    /// <summary>CSV-Zeile mit Anführungszeichen (doppelte "" = ein ").</summary>
    public static List<string> Split(string line, char sep)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }
}
