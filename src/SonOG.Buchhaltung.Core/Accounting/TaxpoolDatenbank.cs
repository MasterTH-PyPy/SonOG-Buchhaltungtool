using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>Eine Tabelle aus der Taxpool-Datenbank (Format mit Kennung "YMRA").</summary>
public sealed class TaxpoolTabelle
{
    public string Name { get; init; } = "";

    /// <summary>Feldnamen; je Feld der interne Name und die Beschriftung (z. B. "Ab Jahr" / "Gültig ab Jahr").</summary>
    public List<(string Name, string Label)> Felder { get; } = new();

    public List<string[]> Zeilen { get; } = new();

    /// <summary>Datensatz-GUID je Zeile (gleiche Reihenfolge wie <see cref="Zeilen"/>).</summary>
    public List<string> Guids { get; } = new();

    /// <summary>Index des Feldes über Name oder Beschriftung (Groß/klein egal), -1 wenn nicht vorhanden.</summary>
    public int Index(string feld) =>
        Felder.FindIndex(f => string.Equals(f.Name, feld, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(f.Label, feld, StringComparison.OrdinalIgnoreCase));

    public static string Wert(string[] zeile, int index) => index >= 0 && index < zeile.Length ? zeile[index] : "";
}

/// <summary>
/// Liest die Datenbank von Taxpool-Buchhalter, nur lesend:
/// <list type="bullet">
/// <item>Datensicherung (*.dbb, Kennung "!TP_SFX!" am Ende): zlib-komprimierte Tabellen mit Inhaltsverzeichnis am Dateiende.</item>
/// <item>Einzelne Tabellendatei (*.dbd) oder ein Ordner mit *.dbd-Dateien.</item>
/// </list>
/// Tabellenaufbau: Kopf mit Felddefinitionen ("YMRA", 2, 0, Beschriftung ... Name), danach Datensätze
/// (Kennung F3 EE 27 04, GUID, 0, Feldanzahl, je Feld eine Zeichenkette mit vorangestellter Länge, UTF-8).
/// </summary>
public static class TaxpoolDatenbank
{
    private static readonly byte[] Ymra = "YMRA"u8.ToArray();
    /// <summary>Länge des Kopfes einer Felddefinition: "YMRA", Version (int32, 2 bzw. 3), 0 (int32).</summary>
    private const int FeldKopfLaenge = 12;
    private static readonly byte[] SatzKennung = { 0xF3, 0xEE, 0x27, 0x04 };
    private static readonly byte[] SfxKennung = "!TP_SFX!"u8.ToArray();
    private static readonly Regex GuidRx = new(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", RegexOptions.Compiled);

    /// <summary>Datei lesen, ohne Taxpool zu stören (Taxpool darf die Datei geöffnet haben).</summary>
    public static byte[] DateiLesen(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Alle Tabellen aus Datei oder Ordner, Name (ohne Endung) → Inhalt. Sicherungskopien ("*_backup") werden übersprungen.</summary>
    public static Dictionary<string, byte[]> Tabellen(string path)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*.dbd"))
                result.TryAdd(Path.GetFileNameWithoutExtension(f), DateiLesen(f));
            if (result.Count == 0) throw new InvalidDataException("Im Ordner liegen keine Taxpool-Tabellen (*.dbd).");
            return result;
        }

        var data = DateiLesen(path);
        if (IstArchiv(data))
        {
            foreach (var (name, inhalt) in ArchivLesen(data))
            {
                if (name.EndsWith("_backup", StringComparison.OrdinalIgnoreCase)) continue;
                result.TryAdd(Path.GetFileNameWithoutExtension(name), inhalt);
            }
            return result;
        }
        if (data.AsSpan().StartsWith(Ymra))
        {
            result[Path.GetFileNameWithoutExtension(path)] = data;
            return result;
        }
        throw new InvalidDataException(
            "Das Dateiformat wurde nicht erkannt. Bitte die Taxpool-Datensicherung (*.dbb) oder eine Tabellendatei (*.dbd) wählen.");
    }

    public static bool IstArchiv(ReadOnlySpan<byte> data) => data.Length > 64 && data.EndsWith(SfxKennung);

    // ---------------------------------------------------------------------------------------------
    // Datensicherung (*.dbb)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Entpackt die Datensicherung. Am Ende steht ein Inhaltsverzeichnis, je Eintrag:
    /// Versatz, Größe entpackt, Größe gepackt (je int32), Dateiname + Länge, Pfad + Länge, Zeitstempel (8 Byte).
    /// Die Daten selbst liegen hintereinander als zlib-Ströme ab Dateianfang.
    /// </summary>
    public static List<(string Name, byte[] Inhalt)> ArchivLesen(byte[] data)
    {
        var start = VerzeichnisStart(data)
                    ?? throw new InvalidDataException("Inhaltsverzeichnis der Taxpool-Datensicherung nicht gefunden.");
        var list = new List<(string, byte[])>();
        int p = start;
        while (p + 12 < data.Length)
        {
            int off = I32(data, p), usize = I32(data, p + 4), csize = I32(data, p + 8);
            if (off < 0 || csize <= 0 || usize < 0 || (long)off + csize > start) break;
            var name = LaengeDanach(data, p + 12, 260);
            if (name is null) break;
            int q = p + 12 + name.Value.Laenge + 4;
            var pfad = LaengeDanach(data, q, 1024);
            if (pfad is null) break;
            q += pfad.Value.Laenge + 4 + 8; // Pfad, Länge, Zeitstempel

            list.Add((name.Value.Text, Entpacken(data, off, csize, usize)));
            p = q;
        }
        if (list.Count == 0) throw new InvalidDataException("Die Taxpool-Datensicherung enthält keine lesbaren Tabellen.");
        return list;
    }

    /// <summary>Sucht den ersten Verzeichniseintrag (Versatz 0, Dateiname auf .dbd/.dat) im hinteren Teil der Datei.</summary>
    private static int? VerzeichnisStart(byte[] data)
    {
        int from = Math.Max(0, data.Length - 4 * 1024 * 1024);
        for (int p = from; p + 16 < data.Length; p++)
        {
            if (data[p] != 0 || data[p + 1] != 0 || data[p + 2] != 0 || data[p + 3] != 0) continue;
            int usize = I32(data, p + 4), csize = I32(data, p + 8);
            if (usize <= 0 || csize <= 0 || csize >= data.Length || csize > p) continue;
            var name = LaengeDanach(data, p + 12, 260);
            if (name is null || !Regex.IsMatch(name.Value.Text, @"\.(dbd|dat)(_backup)?$", RegexOptions.IgnoreCase)) continue;
            // Der erste zlib-Strom beginnt am Dateianfang
            if (data[0] != 0x78) return null;
            return p;
        }
        return null;
    }

    /// <summary>Zeichenkette, deren Länge als int32 direkt dahinter steht (so speichert die Datensicherung Namen und Pfade).</summary>
    private static (string Text, int Laenge)? LaengeDanach(byte[] data, int start, int max)
    {
        for (int n = 1; n <= max && start + n + 4 <= data.Length; n++)
        {
            byte b = data[start + n - 1];
            if (b < 0x20 && b != 0) return null;
            if (I32(data, start + n) == n)
                return (Encoding.Latin1.GetString(data, start, n), n);
        }
        return null;
    }

    private static byte[] Entpacken(byte[] data, int off, int csize, int usize)
    {
        using var input = new MemoryStream(data, off, csize, writable: false);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Max(usize, 16));
        z.CopyTo(output);
        return output.ToArray();
    }

    // ---------------------------------------------------------------------------------------------
    // Tabelle
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ab Taxpool-Tabellenversion 0x13 ist der Tabelleninhalt gzip-komprimiert: Kopf "YMRA", Version, Flags,
    /// Größe entpackt, Größe gepackt, GUID der Tabelle, int32, 1 Byte, danach der gzip-Strom.
    /// Ältere Tabellen (Version 0x0F) sind unkomprimiert und werden unverändert zurückgegeben.
    /// </summary>
    public static byte[] Entpackt(byte[] o)
    {
        if (o.Length < 40 || !o.AsSpan().StartsWith(Ymra)) return o;
        int guidLen = I32(o, 20);
        int start = guidLen is > 0 and < 128 ? 20 + 4 + guidLen + 4 + 1 : -1;
        if (start < 0 || start + 2 > o.Length || o[start] != 0x1F || o[start + 1] != 0x8B)
        {
            // Lage weicht ab: gzip-Kennung (1F 8B 08) im Kopfbereich suchen
            start = o.AsSpan(0, Math.Min(o.Length, 256)).IndexOf(new byte[] { 0x1F, 0x8B, 0x08 });
            if (start < 0) return o;
        }
        int csize = I32(o, 16);
        int len = csize > 0 && start + csize <= o.Length ? csize : o.Length - start;
        using var input = new MemoryStream(o, start, len, writable: false);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Max(I32(o, 12), 1024));
        gz.CopyTo(output);
        return output.ToArray();
    }

    private static bool IstFeldKopf(byte[] o, int i) =>
        i + FeldKopfLaenge <= o.Length && o.AsSpan(i, 4).SequenceEqual(Ymra) && I32(o, i + 4) is >= 2 and <= 8 && I32(o, i + 8) == 0;

    public static TaxpoolTabelle TabelleLesen(string name, byte[] roh)
    {
        var t = new TaxpoolTabelle { Name = name };
        var o = Entpackt(roh);
        var span = o.AsSpan();

        // Felddefinitionen (Version 2: Name steht direkt vor der nächsten Definition, Version 3: danach folgen noch Füllbytes)
        var defs = new List<int>();
        for (int i = span.IndexOf(Ymra); i >= 0;)
        {
            if (IstFeldKopf(o, i)) defs.Add(i);
            int next = span[(i + 1)..].IndexOf(Ymra);
            i = next < 0 ? -1 : i + 1 + next;
        }
        int ersterSatz = span.IndexOf(SatzKennung);
        for (int k = 0; k < defs.Count; k++)
        {
            int p = defs[k] + FeldKopfLaenge;
            int len = I32(o, p);
            if (len is <= 0 or > 128 || p + 4 + len > o.Length) continue;
            var label = Encoding.UTF8.GetString(o, p + 4, len);
            int end = k + 1 < defs.Count ? defs[k + 1] : NaechsteGrenze(o, defs[k] + FeldKopfLaenge);
            if (ersterSatz > defs[k] && end > ersterSatz) end = ersterSatz;
            // interner Feldname: die letzte Zeichenkette (Länge + druckbarer Text) im Bereich der Definition
            string internerName = label;
            for (int q = p + 4 + len; q + 6 <= end; q++)
            {
                int n = I32(o, q);
                if (n is >= 1 and <= 128 && q + 4 + n <= end && Druckbar(o, q + 4, n))
                {
                    internerName = Encoding.UTF8.GetString(o, q + 4, n);
                    q += 3 + n;
                }
            }
            t.Felder.Add((internerName, label));
        }
        if (t.Felder.Count == 0) throw new InvalidDataException($"Tabelle {name}: keine Felddefinitionen gefunden.");

        // Datensätze
        for (int i = ersterSatz; i >= 0 && i < o.Length;)
        {
            int next = span[(i + 4)..].IndexOf(SatzKennung);
            int satzEnde = next < 0 ? o.Length : i + 4 + next;
            var satz = SatzLesen(o, i, satzEnde, t.Felder.Count);
            if (satz is { } s)
            {
                t.Zeilen.Add(s.Werte);
                t.Guids.Add(s.Guid);
            }
            i = next < 0 ? -1 : satzEnde;
        }
        return t;
    }

    private static (string Guid, string[] Werte)? SatzLesen(byte[] o, int start, int ende, int feldAnzahl)
    {
        // GUID (Länge 36) innerhalb der ersten 80 Byte nach der Kennung
        int g = -1;
        for (int p = start + 4; p < Math.Min(ende - 48, start + 80); p++)
        {
            if (I32(o, p) == 36 && GuidRx.IsMatch(Encoding.ASCII.GetString(o, p + 4, 36))) { g = p; break; }
        }
        if (g < 0) return null;
        int anzahl = I32(o, g + 44);
        if (anzahl <= 0 || anzahl > feldAnzahl) return null;

        var werte = new string[feldAnzahl];
        Array.Fill(werte, "");
        int q = g + 48;
        for (int f = 0; f < anzahl; f++)
        {
            if (q + 4 > ende) return null;
            int n = I32(o, q);
            if (n < 0 || q + 4 + n > ende) return null;
            werte[f] = Encoding.UTF8.GetString(o, q + 4, n);
            q += 4 + n;
        }
        return (Encoding.ASCII.GetString(o, g + 4, 36), werte);
    }

    private static int NaechsteGrenze(byte[] o, int from)
    {
        var span = o.AsSpan(from);
        int a = span.IndexOf(SatzKennung), b = span.IndexOf(Ymra);
        int r = (a, b) switch { (< 0, < 0) => o.Length - from, (< 0, _) => b, (_, < 0) => a, _ => Math.Min(a, b) };
        return from + r;
    }

    private static bool Druckbar(byte[] o, int start, int n)
    {
        for (int i = start; i < start + n; i++) if (o[i] < 0x20 || o[i] >= 0xFE) return false; // 0xFE/0xFF kommen in UTF-8 nicht vor
        return true;
    }

    private static int I32(byte[] o, int p) => p >= 0 && p + 4 <= o.Length ? BinaryPrimitives.ReadInt32LittleEndian(o.AsSpan(p, 4)) : int.MinValue;

    // ---------------------------------------------------------------------------------------------
    // Fachliche Sicht: Vorlagen, Konten, Steuersätze, Personenkonten
    // ---------------------------------------------------------------------------------------------

    public static TaxpoolDaten Lesen(string path)
    {
        var tabellen = Tabellen(path);
        TaxpoolTabelle? Tab(params string[] names)
        {
            foreach (var n in names)
                if (tabellen.TryGetValue(n, out var blob)) return TabelleLesen(n, blob);
            return null;
        }

        var daten = new TaxpoolDaten();
        var vorlagen = Tab("Buchungstexte") ?? throw new InvalidDataException("Die Tabelle mit den Buchungsvorlagen (Buchungstexte) fehlt.");
        daten.Vorlagen.AddRange(Vorlagen(vorlagen));
        if (Tab("Konten2007", "Konten2006") is { } konten) Konten(konten, daten);
        if (Tab("Steuersaetze") is { } steuer) Steuern(steuer, daten);
        if (Tab("debikrediinfo") is { } personen) Personen(personen, daten);
        if (Tab("Buchungen") is { } buchungen) Historie(buchungen, daten);
        return daten;
    }

    /// <summary>Kontofeld "Kontoname\nKontonummer" → Nummer (0 = leer).</summary>
    public static int KontoNummer(string feld)
    {
        var nr = feld.Split('\n').LastOrDefault()?.Trim() ?? "";
        return int.TryParse(nr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;
    }

    /// <summary>Steuerfeld "USt. normal\n2" → Steuercode (2 = normal, 1 = ermäßigt, 0 = ohne, -1 = keiner).</summary>
    public static int SteuerCode(string feld)
    {
        if (feld.Trim().Length == 0) return -1;
        var nr = feld.Split('\n').LastOrDefault()?.Trim() ?? "";
        return int.TryParse(nr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    private static decimal? Dezimal(string s) =>
        decimal.TryParse(s.Trim(), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int Ganz(string s, int def = -1) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : def;

    private static IEnumerable<TaxpoolVorlage> Vorlagen(TaxpoolTabelle t)
    {
        int iBez = t.Index("Bezeichnung"), iSoll = t.Index("Soll-Konto"), iHaben = t.Index("Haben-Konto"), iBetrag = t.Index("Betrag"),
            iAnz = t.Index("Anzeigen"), iAb = t.Index("Ab Jahr"), iBis = t.Index("Bis Jahr"), iSteuer = t.Index("Steuer"),
            iSplit = t.Index("IstSplit"), iParent = t.Index("ParentID"), iId = t.Index("ID"), iMemo = t.Index("Memo"),
            iText = t.Index("Buchungstext"), iBrutto = t.Index("Brutto");
        if (iBez < 0 || iSoll < 0 || iHaben < 0) throw new InvalidDataException("Buchungsvorlagen: Felder Bezeichnung/Soll-Konto/Haben-Konto fehlen.");

        for (int r = 0; r < t.Zeilen.Count; r++)
        {
            var z = t.Zeilen[r];
            string W(int i) => TaxpoolTabelle.Wert(z, i);
            yield return new TaxpoolVorlage
            {
                Guid = t.Guids[r],
                Id = Ganz(W(iId)),
                Bezeichnung = W(iBez).Trim(),
                SollKonto = KontoNummer(W(iSoll)),
                HabenKonto = KontoNummer(W(iHaben)),
                Betrag = Dezimal(W(iBetrag)),
                Anzeigen = W(iAnz) != "0",
                AbJahr = Ganz(W(iAb)),
                BisJahr = Ganz(W(iBis)),
                SteuerCode = SteuerCode(W(iSteuer)),
                SteuerName = W(iSteuer).Split('\n')[0].Trim(),
                IstSplit = W(iSplit) == "1",
                ParentId = Ganz(W(iParent)),
                Buchungstext = W(iText).Trim(),
                Memo = W(iMemo),
                Brutto = W(iBrutto) != "0",
            };
        }
    }

    private static void Konten(TaxpoolTabelle t, TaxpoolDaten d)
    {
        int iNr = t.Index("NUMMER"), iName = t.Index("NAME"), iSt = t.Index("STEUERSATZ_ID"), iAuto = t.Index("AUTOMATIKKONTO"), iAnz = t.Index("ANZEIGEN");
        if (iNr < 0 || iName < 0) return;
        foreach (var z in t.Zeilen)
        {
            var nrText = TaxpoolTabelle.Wert(z, iNr).Trim();
            // Steuer-Systemkonten stehen zusätzlich mit führender Null ("01770") in der Tabelle - übergehen
            if (nrText.Length == 0 || (nrText.Length > 1 && nrText[0] == '0')) continue;
            int nr = Ganz(nrText, 0);
            if (nr <= 0) continue;
            var konto = new TaxpoolKonto
            {
                Nummer = nr,
                Name = TaxpoolTabelle.Wert(z, iName).Trim(),
                SteuerCode = Ganz(TaxpoolTabelle.Wert(z, iSt)),
                Automatik = TaxpoolTabelle.Wert(z, iAuto) == "1",
                Typ = Ganz(TaxpoolTabelle.Wert(z, t.Index("KONTENTYP_ID"))),
                Anzeigen = TaxpoolTabelle.Wert(z, iAnz) != "0",
            };
            // doppelte Nummern: sichtbares Konto mit Namen gewinnt
            if (!d.Konten.TryGetValue(nr, out var alt) || (!alt.Anzeigen && konto.Anzeigen) || (alt.Name.Length == 0 && konto.Name.Length > 0))
                d.Konten[nr] = konto;
        }
    }

    private static void Steuern(TaxpoolTabelle t, TaxpoolDaten d)
    {
        int iId = t.Index("ID"), iKurz = t.Index("KURZBEZEICHNUNG");
        if (iId < 0) return;
        // Steuerschlüssel je Zeitraum: "Steuerschlüssel USt 1..4", "Steuerschlüssel VSt 1..4", gültig ab "STEUERSATZn_GUELTIG_AB"
        foreach (var z in t.Zeilen)
        {
            int id = Ganz(TaxpoolTabelle.Wert(z, iId));
            if (id < 0) continue;
            string Schluessel(string art)
            {
                string best = "";
                var bestAb = DateOnly.MinValue;
                for (int n = 1; n <= 4; n++)
                {
                    var key = TaxpoolTabelle.Wert(z, t.Index($"Steuerschlüssel {art} {n}")).Trim();
                    if (key.Length == 0 || key == "-1") continue;
                    var ab = Datum(TaxpoolTabelle.Wert(z, t.Index($"STEUERSATZ{n}_GUELTIG_AB"))) ?? DateOnly.MinValue;
                    if (ab > DateOnly.FromDateTime(DateTime.Today)) continue;
                    if (best.Length == 0 || ab >= bestAb) { best = key; bestAb = ab; }
                }
                return best;
            }
            d.Steuern[id] = new TaxpoolSteuer
            {
                Id = id,
                Name = TaxpoolTabelle.Wert(z, iKurz).Trim(),
                SchluesselUSt = Schluessel("USt"),
                SchluesselVSt = Schluessel("VSt"),
            };
        }
    }

    /// <summary>Datum im Format "1.7.2020.0.0.0.0".</summary>
    private static DateOnly? Datum(string s)
    {
        var p = s.Split('.');
        if (p.Length < 3) return null;
        return int.TryParse(p[0], out var day) && int.TryParse(p[1], out var m) && int.TryParse(p[2], out var y) && y > 1900 && m is >= 1 and <= 12 && day is >= 1 and <= 31
            ? new DateOnly(y, m, Math.Min(day, DateTime.DaysInMonth(y, m)))
            : null;
    }

    /// <summary>
    /// Wertet die Buchungen aus: wie oft jedes Konto bebucht wurde (z. B. um das tatsächlich genutzte Bankkonto zu finden)
    /// und welche Konten in denselben Buchungen gegenüberstanden (für das übliche Gegenkonto je Personenkonto).
    /// Stornierte Buchungen zählen nicht. Für die Gegenkonten zählen nur die letzten drei Jahre mit Buchungen.
    /// </summary>
    private static void Historie(TaxpoolTabelle t, TaxpoolDaten d)
    {
        int iSoll = Erstes(t, "SOLLKONTO_NUMMER_SICHERUNG", "Soll-Kontonr.", "SOLLKONTO_NUMMER");
        int iHaben = Erstes(t, "HABENKONTO_NUMMER_SICHERUNG", "Haben-Kontonr.", "HABENKONTO_NUMMER");
        int iStorno = t.Index("STORNO"), iDatum = t.Index("BELEGDATUM"), iBetrag = t.Index("BRUTTO_BETRAG");
        if (iSoll < 0 || iHaben < 0) return;

        var paare = new List<(int Soll, int Haben, int Jahr)>();
        foreach (var z in t.Zeilen)
        {
            if (TaxpoolTabelle.Wert(z, iStorno) == "1") continue;
            int soll = Ganz(TaxpoolTabelle.Wert(z, iSoll), 0), haben = Ganz(TaxpoolTabelle.Wert(z, iHaben), 0);
            if (soll <= 0 || haben <= 0 || soll == haben) continue;
            int jahr = Datum(TaxpoolTabelle.Wert(z, iDatum))?.Year ?? 0;
            paare.Add((soll, haben, jahr));
        }
        if (paare.Count == 0) return;
        int bisJahr = paare.Max(p => p.Jahr);
        // Nutzung nur aus den letzten beiden Jahren (z. B. Bank 1200 bis 2010, danach 1210)
        foreach (var (soll, haben, jahr) in paare.Where(p => p.Jahr == 0 || p.Jahr >= bisJahr - 1))
        {
            d.KontoNutzung[soll] = d.KontoNutzung.GetValueOrDefault(soll) + 1;
            d.KontoNutzung[haben] = d.KontoNutzung.GetValueOrDefault(haben) + 1;
        }
        foreach (var (soll, haben, jahr) in paare.Where(p => p.Jahr == 0 || p.Jahr > bisJahr - 3))
        {
            void Zaehle(int person, int gegen)
            {
                if (!d.Gegenkonten.TryGetValue(person, out var map)) d.Gegenkonten[person] = map = new Dictionary<int, int>();
                map[gegen] = map.GetValueOrDefault(gegen) + 1;
            }
            Zaehle(soll, haben);
            Zaehle(haben, soll);
        }
    }

    private static int Erstes(TaxpoolTabelle t, params string[] namen)
    {
        foreach (var n in namen)
        {
            int i = t.Index(n);
            if (i >= 0) return i;
        }
        return -1;
    }

    private static void Personen(TaxpoolTabelle t, TaxpoolDaten d)
    {
        int iKonto = t.Index("Allgemein_DebiKrediKonto"), iFirma = t.Index("Rechnung_Firma"), iName = t.Index("Rechnung_Name"),
            iVorname = t.Index("Rechnung_Vorname"), iMatch = t.Index("Allgemein_Matchcode");
        if (iKonto < 0) return;
        foreach (var z in t.Zeilen)
        {
            int konto = Ganz(TaxpoolTabelle.Wert(z, iKonto), 0);
            if (konto <= 0) continue;
            var name = TaxpoolTabelle.Wert(z, iFirma).Trim();
            if (name.Length == 0)
                name = (TaxpoolTabelle.Wert(z, iVorname).Trim() + " " + TaxpoolTabelle.Wert(z, iName).Trim()).Trim();
            if (name.Length == 0) name = TaxpoolTabelle.Wert(z, iMatch).Trim();
            d.Personen[konto] = name;
        }
    }
}

public sealed class TaxpoolVorlage
{
    /// <summary>Datensatz-GUID in Taxpool (bleibt beim Umbenennen gleich).</summary>
    public string Guid { get; init; } = "";
    public int Id { get; init; }
    public string Bezeichnung { get; init; } = "";
    public int SollKonto { get; init; }
    public int HabenKonto { get; init; }
    /// <summary>Fester Betrag; bei Teilen einer Splitvorlage der Anteil (z. B. 0,7).</summary>
    public decimal? Betrag { get; init; }
    public bool Anzeigen { get; init; } = true;
    public int AbJahr { get; init; } = -1;
    public int BisJahr { get; init; } = -1;
    /// <summary>2 = USt/VSt normal, 1 = ermäßigt, 0 = ohne, -1 = nicht angegeben (dann gilt der Steuersatz des Kontos).</summary>
    public int SteuerCode { get; init; } = -1;
    public string SteuerName { get; init; } = "";
    public bool IstSplit { get; init; }
    public int ParentId { get; init; } = -1;
    public string Buchungstext { get; init; } = "";
    public string Memo { get; init; } = "";
    public bool Brutto { get; init; } = true;

    public override string ToString() => $"{Id} {Bezeichnung} ({SollKonto}/{HabenKonto})";
}

public sealed class TaxpoolKonto
{
    public int Nummer { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Voreingestellter Steuersatz (2 = normal, 1 = ermäßigt, 0 = ohne).</summary>
    public int SteuerCode { get; init; } = -1;
    /// <summary>Automatikkonto: Steuer wird vom Konto selbst gerechnet, kein BU-Schlüssel.</summary>
    public bool Automatik { get; init; }
    public bool Anzeigen { get; init; } = true;
    /// <summary>Kontentyp in Taxpool (1 = Aufwand/Ertrag, 2 = Bestand, 0 = sonstige).</summary>
    public int Typ { get; init; } = -1;
}

public sealed class TaxpoolSteuer
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string SchluesselUSt { get; init; } = "";
    public string SchluesselVSt { get; init; } = "";
}

public sealed class TaxpoolDaten
{
    public List<TaxpoolVorlage> Vorlagen { get; } = new();
    public Dictionary<int, TaxpoolKonto> Konten { get; } = new();
    /// <summary>Steuersätze nach ID. Der Steuercode in Vorlagen/Konten entspricht ID - 1.</summary>
    public Dictionary<int, TaxpoolSteuer> Steuern { get; } = new();
    public Dictionary<int, string> Personen { get; } = new();

    /// <summary>Anzahl Buchungen je Konto in den letzten beiden Jahren mit Buchungen (aus der Buchungstabelle).</summary>
    public Dictionary<int, int> KontoNutzung { get; } = new();

    /// <summary>Konto → (Gegenkonto → Anzahl gemeinsamer Buchungen), letzte drei Jahre.</summary>
    public Dictionary<int, Dictionary<int, int>> Gegenkonten { get; } = new();

    public TaxpoolSteuer? SteuerFuerCode(int code) => code > 0 && Steuern.TryGetValue(code + 1, out var s) ? s : null;
}
