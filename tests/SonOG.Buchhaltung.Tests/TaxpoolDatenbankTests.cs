using System.IO.Compression;
using System.Text;
using SonOG.Buchhaltung.Core.Accounting;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Tests;

/// <summary>
/// Tests mit synthetischen Dateien im Format der Taxpool-Datenbank (Tabellen "YMRA", Datensicherung "!TP_SFX!").
/// Der Aufbau ist an einer echten Datensicherung abgelesen; hier stehen nur erfundene Daten.
/// </summary>
public class TaxpoolDatenbankTests
{
    // ------------------------------------------------------------------ Dateien bauen

    private static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write(b.Length);
        w.Write(b);
    }

    /// <summary>Tabelle: Kopf, Felddefinitionen (Beschriftung ... Name), Datensätze (Kennung, GUID, 0, Feldanzahl, Werte).</summary>
    internal static byte[] Tabelle((string Name, string Label)[] felder, params string[][] zeilen)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("YMRA"u8.ToArray());
        w.Write(15);
        w.Write(new byte[8]);
        Str(w, Guid.NewGuid().ToString().ToUpperInvariant());
        w.Write(new byte[20]);
        foreach (var (name, label) in felder)
        {
            w.Write("YMRA"u8.ToArray());
            w.Write(2);
            w.Write(0);
            Str(w, label);
            w.Write(new byte[] { 0, 0, 0, 1, 8, 0, 0, 0 });
            w.Write(new byte[60]);
            Str(w, name);
        }
        int nr = 0;
        foreach (var z in zeilen)
        {
            w.Write(new byte[] { 0xF3, 0xEE, 0x27, 0x04 });
            w.Write(new byte[] { 3, 0x2E, 1, 0, 0, (byte)nr++, 0, 0, 0, 1, 0, 0 });
            w.Write(new byte[26]); // Zeitstempel o. Ä.
            Str(w, Guid.NewGuid().ToString().ToUpperInvariant());
            w.Write(0);
            w.Write(z.Length);
            foreach (var v in z) Str(w, v);
        }
        w.Write("YMRA"u8.ToArray()); // Index-Bereich am Ende
        w.Write(new byte[12]);
        return ms.ToArray();
    }

    /// <summary>Datensicherung: zlib-Ströme hintereinander, Inhaltsverzeichnis, Kennung.</summary>
    internal static byte[] Archiv(params (string Name, byte[] Inhalt)[] dateien)
    {
        using var ms = new MemoryStream();
        var eintraege = new List<(int Off, int USize, int CSize, string Name)>();
        foreach (var (name, inhalt) in dateien)
        {
            int off = (int)ms.Position;
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(inhalt);
            eintraege.Add((off, inhalt.Length, (int)ms.Position - off, name));
        }
        using var w = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true);
        foreach (var e in eintraege)
        {
            w.Write(e.Off);
            w.Write(e.USize);
            w.Write(e.CSize);
            var nb = Encoding.Latin1.GetBytes(e.Name);
            w.Write(nb);
            w.Write(nb.Length);
            var pb = Encoding.Latin1.GetBytes(@"C:\Temp\tp-b-1\" + e.Name);
            w.Write(pb);
            w.Write(pb.Length);
            w.Write(DateTime.Now.ToFileTime());
        }
        w.Write(0);
        w.Write(eintraege.Count);
        var basis = Encoding.Latin1.GetBytes(@"X:\Buchhaltung\Taxpool-Buchhalter\");
        w.Write(basis);
        w.Write(basis.Length);
        w.Write("1.12!TP_SFX!"u8.ToArray());
        w.Flush();
        return ms.ToArray();
    }

    private static readonly (string, string)[] VorlagenFelder =
    {
        ("Bezeichnung", "Bezeichnung"), ("Soll-Konto", "Soll-Konto"), ("Haben-Konto", "Haben-Konto"), ("Betrag", "Betrag"),
        ("Anzeigen", "Anzeigen"), ("Ab Jahr", "Gültig ab Jahr"), ("Bis Jahr", "Gültig bis Jahr"), ("Steuer", "Steuer"),
        ("IstSplit", "IstSplit"), ("ParentID", "ParentID"), ("ChildID", "ChildID"), ("ID", "ID"), ("Buchungstext", "Buchungstext"),
    };

    // Bezeichnung, Soll, Haben, Betrag, Anzeigen, Ab, Bis, Steuer, IstSplit, ParentID, ChildID, ID, Buchungstext
    private static string[] V(string bez, string soll, string haben, string steuer = "", string betrag = "", string anz = "1",
        string split = "0", string parent = "-1", string id = "1", string text = "", string bis = "") =>
        new[] { bez, soll, haben, betrag, anz, "2007", bis, steuer, split, parent, "-1", id, text };

    private static byte[] VorlagenTabelle() => Tabelle(VorlagenFelder,
        V("Telefon", "Telefon\n4920", "", id: "1"),
        V("Wareneingang 19% VSt.", "Wareneingang 19 % Vorsteuer\n3400", "", id: "2"),
        V("Zeitschriften", "Zeitschriften, Bücher\n4940", "", steuer: "USt. ermäßigt\n1", id: "3"),
        V("Erlöse 19 %", "", "Erlöse 19 % USt\n8400", id: "4"),
        V("Kunde Muster Re-Nr.", "Bank\n1200", "Erlöse 19 % USt\n8400", steuer: "USt. normal\n2", id: "5", text: "Kunde Muster Re-Nr.: "),
        V("Porto", "Porto\n4910", "", steuer: "Ohne USt.\n0", id: "6"),
        V("Bewirtung 70/30", "", "Bank\n1200", betrag: "1.00", split: "1", id: "10"),
        V("Bewirtung 70%", "Bewirtungskosten\n4650", "Bank\n1200", steuer: "USt. normal\n2", betrag: "0.70", split: "1", parent: "12", id: "11"),
        V("Bewirtung 30%", "Nicht abzugsfähige Bewirtungskosten\n4654", "Bank\n1200", steuer: "USt. normal\n2", betrag: "0.30", split: "1", parent: "12", id: "12"),
        V("KFZ-1%", "Unentgeltliche Wertabgaben\n1880", "", split: "1", id: "20"),
        V("Anteil", "", "Verwendung 19 %\n8921", betrag: "0.8", split: "1", parent: "22", id: "21"),
        V("Anteil ohne", "", "Verwendung ohne\n8924", betrag: "0.2", split: "1", parent: "22", id: "22"),
        V("Ausgeblendet", "Telefon\n4920", "", anz: "0", id: "30"),
        V("Abgelaufen", "Telefon\n4920", "", bis: "2019", id: "31"),
        V("Amazon über Kreditor", "Bürobedarf\n4930", "Amazon\n70001", id: "32"));

    private static byte[] KontenTabelle() => Tabelle(
        new[] { ("ID", "ID"), ("NAME", "NAME"), ("NUMMER", "NUMMER"), ("STEUERSATZ_ID", "STEUERSATZ_ID"), ("ANZEIGEN", "ANZEIGEN"), ("AUTOMATIKKONTO", "AUTOMATIKKONTO") },
        new[] { "1", "Bank", "1200", "0", "1", "0" },
        new[] { "2", "Wareneingang 19 % Vorsteuer", "3400", "2", "1", "1" },
        new[] { "3", "Telefon", "4920", "2", "1", "0" },
        new[] { "4", "Zeitschriften, Bücher", "4940", "1", "1", "0" },
        new[] { "5", "Erlöse 19 % USt", "8400", "2", "1", "1" },
        new[] { "6", "Bewirtungskosten", "4650", "2", "1", "0" },
        new[] { "7", "Nicht abzugsfähige Bewirtungskosten", "4654", "0", "1", "0" },
        new[] { "8", "Umsatzsteuer", "01770", "", "1", "1" }, // Systemkonto mit führender Null
        new[] { "9", "Bürobedarf", "4930", "2", "1", "0" },
        new[] { "10", "Porto", "4910", "0", "1", "0" });

    private static byte[] SteuerTabelle() => Tabelle(
        new[] { ("ID", "ID"), ("KURZBEZEICHNUNG", "KURZBEZEICHNUNG"), ("STEUERSATZ1_GUELTIG_AB", "STEUERSATZ1_GUELTIG_AB"),
                ("Steuerschlüssel USt 1", "Steuerschlüssel USt 1"), ("Steuerschlüssel VSt 1", "Steuerschlüssel VSt 1") },
        new[] { "1", "Ohne USt.", "1.4.1998.0.0.0.0", "", "" },
        new[] { "2", "USt. ermäßigt", "1.1.2007.0.0.0.0", "2", "8" },
        new[] { "3", "USt. normal", "1.1.2007.0.0.0.0", "3", "9" });

    private static byte[] PersonenTabelle() => Tabelle(
        new[] { ("Allgemein_DebiKrediKonto", "Allgemein_DebiKrediKonto"), ("Rechnung_Firma", "Rechnung_Firma") },
        new[] { "10000", "Debitor *Mustermann*" },
        new[] { "70001", "Amazon EU" });

    private static string TempArchiv()
    {
        var path = Path.Combine(Path.GetTempPath(), "sonog-tp-" + Guid.NewGuid() + ".dbb");
        File.WriteAllBytes(path, Archiv(
            ("Buchungstexte.dbd", VorlagenTabelle()),
            ("Buchungstexte.dbd_backup", Tabelle(VorlagenFelder, V("ALT", "Alt\n4000", ""))),
            ("Konten2007.dbd", KontenTabelle()),
            ("Steuersaetze.dbd", SteuerTabelle()),
            ("debikrediinfo.dbd", PersonenTabelle())));
        return path;
    }

    // ------------------------------------------------------------------ Lesen

    [Fact]
    public void Table_reads_labels_names_values_and_guids()
    {
        var t = TaxpoolDatenbank.TabelleLesen("Buchungstexte", VorlagenTabelle());
        Assert.Equal(13, t.Felder.Count);
        Assert.Equal(("Ab Jahr", "Gültig ab Jahr"), t.Felder[5]);
        Assert.Equal(5, t.Index("Gültig ab Jahr"));
        Assert.Equal(5, t.Index("ab jahr"));
        Assert.Equal(15, t.Zeilen.Count);
        Assert.Equal(15, t.Guids.Count);
        Assert.Equal("Zeitschriften, Bücher\n4940", t.Zeilen[2][1]);
        Assert.Equal(4940, TaxpoolDatenbank.KontoNummer(t.Zeilen[2][1]));
        Assert.Equal(1, TaxpoolDatenbank.SteuerCode(t.Zeilen[2][7]));
        Assert.Equal(-1, TaxpoolDatenbank.SteuerCode(""));
    }

    [Fact]
    public void Archive_is_unpacked_and_backups_are_skipped()
    {
        var path = TempArchiv();
        try
        {
            var tabs = TaxpoolDatenbank.Tabellen(path);
            Assert.Equal(new[] { "Buchungstexte", "debikrediinfo", "Konten2007", "Steuersaetze" }, tabs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray());
            var d = TaxpoolDatenbank.Lesen(path);
            Assert.Equal(15, d.Vorlagen.Count);
            Assert.DoesNotContain(d.Vorlagen, v => v.Bezeichnung == "ALT");
            Assert.False(d.Konten.ContainsKey(1770)); // Systemkonto "01770" übergangen
            Assert.True(d.Konten[3400].Automatik);
            Assert.Equal("9", d.SteuerFuerCode(2)!.SchluesselVSt);
            Assert.Equal("2", d.SteuerFuerCode(1)!.SchluesselUSt);
            Assert.Equal("Amazon EU", d.Personen[70001]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Single_table_file_and_unknown_format()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonog-tp-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Buchungstexte.dbd"), VorlagenTabelle());
            Assert.Equal(15, TaxpoolDatenbank.Lesen(dir).Vorlagen.Count);                                  // Ordner
            Assert.Equal(15, TaxpoolDatenbank.Lesen(Path.Combine(dir, "Buchungstexte.dbd")).Vorlagen.Count); // einzelne Datei
            File.WriteAllText(Path.Combine(dir, "x.db0"), "irgendwas");
            Assert.Throws<InvalidDataException>(() => TaxpoolDatenbank.Lesen(Path.Combine(dir, "x.db0")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ Übernehmen

    private static (AccountingSettings S, PersonAccountDirectory Dir, TaxpoolImportErgebnis E, TaxpoolDaten D) Import()
    {
        var path = TempArchiv();
        try
        {
            var d = TaxpoolDatenbank.Lesen(path);
            var s = new AccountingSettings();
            var dir = new PersonAccountDirectory();
            return (s, dir, TaxpoolVorlagenImport.Uebernehmen(d, s, dir, 2026), d);
        }
        finally { File.Delete(path); }
    }

    private static KontierungsRegel R(AccountingSettings s, string name) => s.Regeln.Single(r => r.Name == TaxpoolVorlagenImport.Praefix + name);

    [Fact]
    public void Templates_become_rules_with_tax_keys_from_taxpool()
    {
        var (s, _, e, _) = Import();
        Assert.Equal((4920, "9", Richtung.Ausgang, false), (R(s, "Telefon").Sachkonto, R(s, "Telefon").BuSchluessel, R(s, "Telefon").Richtung, R(s, "Telefon").IstAutomatisch));
        Assert.Equal("", R(s, "Wareneingang 19% VSt.").BuSchluessel);   // Automatikkonto
        Assert.Equal("8", R(s, "Zeitschriften").BuSchluessel);          // ermäßigt laut Vorlage
        Assert.Equal((8400, "", Richtung.Eingang), (R(s, "Erlöse 19 %").Sachkonto, R(s, "Erlöse 19 %").BuSchluessel, R(s, "Erlöse 19 %").Richtung));
        Assert.Equal((8400, Richtung.Eingang, "Kunde Muster Re-Nr.:"), (R(s, "Kunde Muster Re-Nr.").Sachkonto, R(s, "Kunde Muster Re-Nr.").Richtung, R(s, "Kunde Muster Re-Nr.").Buchungstext));
        Assert.Equal("", R(s, "Porto").BuSchluessel);                   // "Ohne USt." in der Vorlage

        var amz = R(s, "Amazon über Kreditor");
        Assert.Equal((70001, 4930, true, Richtung.Ausgang, "9"), (amz.Personenkonto, amz.Sachkonto, amz.RechnungEinbuchen, amz.Richtung, amz.BuSchluessel));

        Assert.DoesNotContain(s.Regeln, r => r.Name.Contains("Ausgeblendet") || r.Name.Contains("Abgelaufen") || r.Name.Contains("KFZ-1%"));
        Assert.Contains(e.Hinweise, h => h.Contains("KFZ-1%") && h.Contains("Umbuchung"));
        Assert.Contains(e.Hinweise, h => h.Contains("2 in Taxpool ausgeblendete"));
        Assert.Equal(8, e.Vorlagen);
    }

    [Fact]
    public void Split_template_distributes_amount_and_rounding_rest()
    {
        var (s, dir, _, _) = Import();
        var split = R(s, "Bewirtung 70/30");
        Assert.Equal(Richtung.Ausgang, split.Richtung);
        Assert.Equal(new[] { (4650, 0.70m, "9"), (4654, 0.30m, "9") }, split.Aufteilung!.Select(a => (a.Sachkonto, a.Anteil, a.BuSchluessel)).ToArray());

        var b = new Booking { Date = new DateOnly(2026, 9, 1), Amount = -100.01m, Text = "Restaurant", Number = "2026-0001" };
        var k = new KontierungsVorschlag(s, dir).AusRegel(b, split);
        Assert.Equal(new[] { (70.01m, 4650), (30.00m, 4654) }, k.Zeilen.Select(z => (z.Betrag, z.Sachkonto)).ToArray());
        Assert.Equal(100.01m, k.Summe);
        Assert.Equal(KontierungsQuelle.Regel, k.Quelle);
        Assert.DoesNotContain(Buchungssaetze.Pruefen(b.With(k), s, dir), p => p.Stufe == PruefStufe.Fehler);
    }

    [Fact]
    public void Chart_of_accounts_person_accounts_and_tax_defaults_are_taken_over()
    {
        var (s, dir, e, _) = Import();
        Assert.Equal("Telefon", s.KontoName(4920));
        Assert.Contains(3400, s.Automatikkonten);
        Assert.Contains(8400, s.Automatikkonten);
        Assert.Equal("9", s.BuVorschlag(4920, ausgabe: true));
        Assert.Equal("3", s.BuVorschlag(4920, ausgabe: false));
        Assert.Equal("", s.BuVorschlag(3400, ausgabe: true));
        Assert.Equal("", s.BuVorschlag(4654, ausgabe: true));
        Assert.Equal(PersonenArt.Kreditor, dir.Get(70001)!.Art);
        Assert.Equal(PersonenArt.Debitor, dir.Get(10000)!.Art);
        Assert.Equal(2, e.Personenkonten);
    }

    [Fact]
    public void Reimport_keeps_user_keywords_and_removes_deleted_templates()
    {
        var path = TempArchiv();
        try
        {
            var s = new AccountingSettings();
            var dir = new PersonAccountDirectory();
            var d = TaxpoolDatenbank.Lesen(path);
            TaxpoolVorlagenImport.Uebernehmen(d, s, dir, 2026);
            R(s, "Telefon").Stichwort = "TELEKOM";
            var porto = R(s, "Porto");

            // Vorlage "Porto" in Taxpool gelöscht, "Telefon" umbenannt (gleiche GUID)
            var tel = d.Vorlagen.Single(v => v.Bezeichnung == "Telefon");
            var d2 = new TaxpoolDaten();
            d2.Vorlagen.AddRange(d.Vorlagen.Where(v => v.Bezeichnung is not ("Porto" or "Telefon")));
            d2.Vorlagen.Add(new TaxpoolVorlage { Guid = tel.Guid, Id = tel.Id, Bezeichnung = "Telefon und Handy", SollKonto = 4920 });
            foreach (var (k, v) in d.Konten) d2.Konten[k] = v;
            foreach (var (k, v) in d.Steuern) d2.Steuern[k] = v;

            var e = TaxpoolVorlagenImport.Uebernehmen(d2, s, dir, 2026);
            Assert.Equal(1, e.Entfernt);
            Assert.DoesNotContain(porto, s.Regeln);
            var umbenannt = R(s, "Telefon und Handy");
            Assert.Equal("TELEKOM", umbenannt.Stichwort);
            Assert.Equal(1, s.Regeln.Count(r => r.TaxpoolId == tel.Guid));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Checks_warn_about_tax_keys_on_automatic_and_missing_keys()
    {
        var (s, dir, _, _) = Import();
        var b = new Booking { Date = new DateOnly(2026, 9, 1), Amount = -50m, Text = "x", Number = "2026-0001" };
        b.Kontierung = new Kontierung { Zeilen = { new KontierungsZeile { Betrag = 50m, Sachkonto = 3400, BuSchluessel = "9" } } };
        Assert.Contains(Buchungssaetze.Pruefen(b, s, dir), p => p.Stufe == PruefStufe.Hinweis && p.Text.Contains("Automatikkonto"));
        b.Kontierung = new Kontierung { Zeilen = { new KontierungsZeile { Betrag = 50m, Sachkonto = 4920 } } };
        Assert.Contains(Buchungssaetze.Pruefen(b, s, dir), p => p.Stufe == PruefStufe.Hinweis && p.Text.Contains("BU-Schlüssel 9"));
    }
}

internal static class BookingTestExtensions
{
    public static Booking With(this Booking b, Kontierung k)
    {
        b.Kontierung = k;
        return b;
    }
}
