using SonOG.Buchhaltung.Core.Accounting;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Tests;

/// <summary>Erkennung des Personenkontos aus dem Buchungstext (erfundene Namen, Fälle aus der Praxis nachgebildet).</summary>
public class PersonenkontoErkennungTests
{
    private static PersonAccount P(int konto, string name, PersonenArt art, int nutzung = 0, params string[] such) =>
        new() { Konto = konto, Name = name, Art = art, Nutzung = nutzung, Suchbegriffe = such.ToList() };

    private static readonly List<PersonAccount> Konten = new()
    {
        P(70001, "ADN", PersonenArt.Kreditor),
        P(20200, "ADN", PersonenArt.Debitor),
        P(70077, "Toom Baumarkt GmbH", PersonenArt.Kreditor),
        P(70019, "Amazon", PersonenArt.Kreditor),
        P(70134, "Der Dübelladen Inh. Andreas Sommerfeld", PersonenArt.Kreditor),
        P(15100, "Max Beispielmann", PersonenArt.Debitor),
        P(15101, "Nord Kera GmbH", PersonenArt.Debitor),
        P(15102, "Keramik Nord GmbH", PersonenArt.Debitor),
        P(15103, "Holz-Fabrik UG", PersonenArt.Debitor),
        P(15104, "Holzcompany - die Holzexperten GmbH", PersonenArt.Debitor),
        P(15239, "XY-Hausverwaltung", PersonenArt.Debitor, nutzung: 0),
        P(15240, "XY-Hausverwaltung", PersonenArt.Debitor, nutzung: 12),
        P(15360, "EH-Hausverwaltung", PersonenArt.Debitor),
        P(15222, "QZ Seminare Inhaber: Thomas Kramerow", PersonenArt.Debitor),
        P(15300, "Stadtentwicklung Musterstadt GmbH", PersonenArt.Debitor, 0, "SEG MUSTERSTADT"),
        P(15178, "Ferienhausvermietung Holger Sinclaro", PersonenArt.Debitor),
        P(15225, "Immobilienverwaltung Gohrbach", PersonenArt.Debitor),
        P(70052, "Mobilfunk Nord", PersonenArt.Kreditor),
        P(70053, "Mobilfunk Süd", PersonenArt.Kreditor),
        P(70054, "Mobilfunk Ost", PersonenArt.Kreditor),
    };

    private static PersonTreffer? F(string text, decimal betrag) => new PersonenkontoErkennung(Konten).Finden(text, betrag);

    [Theory]
    [InlineData("ADN DISTRIBUTION GMBH RE 1025075484 KDNR 4711", -119.0, 70001)] // Abbuchung → Lieferant
    [InlineData("ADN DISTRIBUTION GMBH GUTSCHRIFT 99", 50.0, 20200)]             // Eingang → Kunde (gleicher Name)
    [InlineData("TOOM BM SAGT DANKE//DARMSTADT/DE 2026-09-14", -16.06, 70077)]  // nur der markante Namensteil
    [InlineData("toom Baumarkt GmbH 1234 Griesheim", -42.0, 70077)]
    [InlineData("AMAZON PAYMENTS EUROPE S.C.A. 305-1234567-1234567", -59.5, 70019)]
    [InlineData("AMAZON PAYMENTS EUROPE ERSTATTUNG 305-1234567-1234567", 20.0, 70019)] // Erstattung: Lieferant trotz Eingang
    [InlineData("BEISPIELMANN MAX RE 202634001", 119.0, 15100)]
    [InlineData("Nord Kera GmbH Re 202634002", 238.0, 15101)]                   // ganzes Wort schlägt Wortanfang (Keramik)
    [InlineData("Holz-Fabrik UG Re 202634003", 100.0, 15103)]                   // "Holz" ist kein Treffer für "Holzcompany"
    [InlineData("HOLZCOMPANY GMBH LOHNABRECHNUNG", 80.0, 15104)]                // Firma: erstes markantes Wort genügt
    [InlineData("XY-Hausverwaltung Miete Oktober", 500.0, 15240)]               // gleichnamig: häufiger genutzt
    [InlineData("EH-Hausverwaltung Miete Oktober", 500.0, 15360)]               // Kürzel aus zwei Buchstaben entscheidet
    [InlineData("QZ SEMINARE RE 202634004", 300.0, 15222)]                      // "Inhaber" → Firma, Nachnamen-Regel gilt nicht
    [InlineData("SEG MUSTERSTADT RE 202634005", 400.0, 15300)]                  // Suchbegriff
    [InlineData("HOLGER SINCLARO RE 202634006", 450.0, 15178)]                  // letztes Namenswort genügt
    [InlineData("GOHRBACH MIETE RE 202634007", 380.0, 15225)]
    public void Recognizes_account(string text, double betrag, int erwartet)
    {
        var t = F(text, (decimal)betrag);
        Assert.NotNull(t);
        Assert.Equal(erwartet, t!.Konto.Konto);
        Assert.False(string.IsNullOrEmpty(t.Grund));
    }

    [Theory]
    [InlineData("Andreas Gundlach Re-Nr.: 1003047114", -20.0)]   // Vorname weiter hinten im Namen reicht nicht
    [InlineData("Max Mustertext Überweisung", 50.0)]             // Kunde: Vorname allein reicht nicht
    [InlineData("MOBILFUNK RECHNUNG 10/2026", -39.99)]           // Wort in mehreren Kontennamen → mehrdeutig
    [InlineData("ADN DISTRIBUTION", 0.0)]                         // ohne Richtung: zwei gleich gute ADN-Konten
    [InlineData("Lastschrift Versicherung", -10.0)]
    public void Gives_no_suggestion_when_unsure(string text, double betrag) => Assert.Null(F(text, (decimal)betrag));

    // ------------------------------------------------------------------ Vorschlag

    private static (AccountingSettings S, PersonAccountDirectory Dir) Umgebung()
    {
        var s = new AccountingSettings { Regeln = new() }; // ohne Standardregeln, damit nur die Erkennung zählt
        var dir = new PersonAccountDirectory();
        dir.Konten.AddRange(Konten.Select(k => new PersonAccount { Konto = k.Konto, Name = k.Name, Art = k.Art, Nutzung = k.Nutzung, Suchbegriffe = k.Suchbegriffe.ToList() }));
        dir.Get(70077)!.StandardSachkonto = 3030;
        return (s, dir);
    }

    [Fact]
    public void Customer_payment_without_invoice_number_clears_debtor()
    {
        var (s, dir) = Umgebung();
        var b = new Booking { Date = new DateOnly(2026, 10, 1), Amount = 500m, Text = "XY-Hausverwaltung Miete Oktober", Number = "2026-0100", Category = BookingCategory.EingangSonstige };
        var k = new KontierungsVorschlag(s, dir).Vorschlagen(b);
        Assert.Equal((15240, false, KontierungsQuelle.Regel), (k.Personenkonto, k.RechnungEinbuchen, k.Quelle));
        Assert.Contains("gleicht den offenen Posten aus", k.Hinweis);
        Assert.DoesNotContain(Buchungssaetze.Pruefen(b.With(k), s, dir), p => p.Stufe == PruefStufe.Fehler);
    }

    [Fact]
    public void Template_for_the_person_account_wins_over_standard_account()
    {
        var (s, dir) = Umgebung();
        s.Regeln.Add(new KontierungsRegel
        {
            Name = "Taxpool: Toom Baumarkt GmbH Beleg-Nr.:", TaxpoolId = "x1", Personenkonto = 70077, Sachkonto = 3400, BuSchluessel = "",
            Richtung = Richtung.Ausgang, RechnungEinbuchen = true,
        });
        var b = new Booking { Date = new DateOnly(2026, 10, 2), Amount = -16.06m, Text = "TOOM BM SAGT DANKE//DARMSTADT/DE", Number = "2026-0101", Category = BookingCategory.AusgabeSonstige };
        var k = new KontierungsVorschlag(s, dir).Vorschlagen(b);
        Assert.Equal((70077, true, 3400, KontierungsQuelle.Regel), (k.Personenkonto, k.RechnungEinbuchen, k.Zeilen[0].Sachkonto, k.Quelle));
        Assert.Contains("Vorlage", k.Hinweis);

        // Eingang beim selben Lieferanten (Erstattung): Vorlage gilt nur für Ausgänge → Standardkonto
        b = new Booking { Date = new DateOnly(2026, 10, 3), Amount = 5m, Text = "TOOM BM ERSTATTUNG", Number = "2026-0102", Category = BookingCategory.EingangSonstige };
        Assert.Equal(3030, new KontierungsVorschlag(s, dir).Vorschlagen(b).Zeilen[0].Sachkonto);
    }

    [Fact]
    public void Customer_template_text_is_used_for_payment_and_sollstellung()
    {
        var (s, dir) = Umgebung();
        s.Regeln.Add(new KontierungsRegel
        {
            Name = "Taxpool: Immobilienverwaltung Gohrbach Re-Nr.:", TaxpoolId = "g1", Personenkonto = 15225, Sachkonto = 8401,
            Richtung = Richtung.Eingang, RechnungEinbuchen = true, Buchungstext = "Immobilienverwaltung Gohrbach Re-Nr.:",
        });
        // Zahlungseingang: OP-Ausgleich, Text aus der Vorlage mit der eigenen Rechnungsnummer
        var b = new Booking { Date = new DateOnly(2026, 10, 5), Amount = 380m, Text = "GOHRBACH MIETE RE 202634007", Number = "2026-0110",
            Category = BookingCategory.Kundenzahlung, OwnInvoiceNumbers = { "202634007" } };
        var k = new KontierungsVorschlag(s, dir).Vorschlagen(b);
        Assert.Equal((15225, false, "Immobilienverwaltung Gohrbach Re-Nr.: 202634007", "202634007"),
            (k.Personenkonto, k.RechnungEinbuchen, k.Zeilen[0].Text, k.Zeilen[0].Rechnungsnummer));

        // Sollstellung: Erlöskonto und Text aus der Vorlage
        var r = new SonOG.Buchhaltung.Core.Invoices.InvoiceRecord("202634007", "x.pdf", 380m, "Immobilienverwaltung Gohrbach\nWeg 1", new DateOnly(2026, 9, 30));
        var ks = Sollstellung.KontierungFuer(r, s, dir);
        Assert.Equal((15225, 8401, "Immobilienverwaltung Gohrbach Re-Nr.: 202634007"), (ks.Personenkonto, ks.Zeilen[0].Sachkonto, ks.Zeilen[0].Text));
    }

    [Fact]
    public void Supplier_with_standard_account_is_booked_in_and_out()
    {
        var (s, dir) = Umgebung();
        var b = new Booking { Date = new DateOnly(2026, 10, 2), Amount = -16.06m, Text = "TOOM BM SAGT DANKE//DARMSTADT/DE", Number = "2026-0101", Category = BookingCategory.AusgabeSonstige };
        var k = new KontierungsVorschlag(s, dir).Vorschlagen(b);
        Assert.Equal((70077, true, 3030), (k.Personenkonto, k.RechnungEinbuchen, k.Zeilen[0].Sachkonto));
        Assert.Contains("TOOM", k.Hinweis);
    }
}

public class RechnungsnummerTests
{
    private static Booking B(string text, params string[] belege) => new() { Text = text, ReceiptFiles = belege.ToList(), Amount = -1m };

    [Theory]
    [InlineData("ADN Bel. v. 11.09.2026 Avis vom 23.09.2026 KD48252 RG1026190417 17", "1026190417")]
    [InlineData("LIEFERANT GMBH RE-NR.: 69307919 KD 4711", "69307919")]
    [InlineData("Muster AG Rechnung 2026-0815 vom 01.10.", "2026-0815")]
    [InlineData("SOFTWARE LTD INVOICE INV-2026-4711", "INV-2026-4711")]
    [InlineData("REWE SAGT DANKE 12345", "")]          // "RE" am Wortanfang ist keine Rechnung
    [InlineData("Lastschrift Versicherung", "")]
    public void Number_from_bank_text(string text, string erwartet) => Assert.Equal(erwartet, Rechnungsnummer.Aus(B(text)));

    [Fact]
    public void Amazon_invoice_number_from_receipt_file()
    {
        Assert.Equal("DE66OD6DABEI", Rechnungsnummer.Aus(B("AMAZON PAYMENTS 305-1234567-1234567", @"C:\Belege\DE66OD6DABEI.pdf")));
        Assert.Equal("", Rechnungsnummer.Aus(B("AMAZON PAYMENTS 305-1234567-1234567", @"C:\Belege\Bestellübersicht.pdf")));
    }

    [Theory]
    [InlineData("ADN Re-Nr.:", "1026190417", "ADN Re-Nr.: 1026190417")]
    [InlineData("Toom Baumarkt GmbH Beleg-Nr.:", "8402", "Toom Baumarkt GmbH Beleg-Nr.: 8402")]
    [InlineData("Lieferant Re-Nr..", "77", "Lieferant Re-Nr.. 77")]
    [InlineData("Lieferant Re.Nr.:", "77", "Lieferant Re.Nr.: 77")]
    [InlineData("Lieferant Re-Nr:", "77", "Lieferant Re-Nr: 77")]
    [InlineData("Telefon", "77", "Telefon")]                 // kein Platzhalter → unverändert
    [InlineData("ADN Re-Nr.:", "", "ADN Re-Nr.:")]           // keine Nummer gefunden → unverändert
    public void Inserts_number_after_placeholder(string text, string nr, string erwartet) =>
        Assert.Equal(erwartet, Rechnungsnummer.Einsetzen(text, nr));

    [Fact]
    public void Long_text_is_shortened_in_front_never_the_number()
    {
        var t = Rechnungsnummer.Einsetzen("!!PV!! Heiner Projektgesellschaft mbH & Co. KG Waldstraße Re-Nr.:", "1026190417");
        Assert.True(t.Length <= 60);
        Assert.EndsWith(" 1026190417", t);
    }

    [Fact]
    public void Template_text_gets_number_and_belegfeld2()
    {
        var s = new AccountingSettings { Regeln = new() };
        var dir = new PersonAccountDirectory();
        dir.Konten.Add(new PersonAccount { Konto = 70001, Name = "ADN", Art = PersonenArt.Kreditor });
        s.Regeln.Add(new KontierungsRegel { Name = "Taxpool: ADN", TaxpoolId = "a", Personenkonto = 70001, Sachkonto = 4964, BuSchluessel = "9",
            Richtung = Richtung.Ausgang, RechnungEinbuchen = true, Buchungstext = "ADN Re-Nr.:" });
        var b = new Booking { Date = new DateOnly(2026, 9, 28), Amount = -156.13m, Number = "2026-0047", Category = BookingCategory.AusgabeSonstige,
            Text = "ADN Bel. v. 11.09.2026 Avis vom 23.09.2026 KD48252 RG1026190417 17" };
        b.Kontierung = new KontierungsVorschlag(s, dir).Vorschlagen(b);
        var z = b.Kontierung.Zeilen.Single();
        Assert.Equal(("ADN Re-Nr.: 1026190417", "1026190417", 4964), (z.Text, z.Rechnungsnummer, z.Sachkonto));
        var rows = Buchungssaetze.AusKontoauszug(b, s);
        Assert.Equal(("ADN Re-Nr.: 1026190417", "2026-0047", "1026190417"), (rows[0].Buchungstext, rows[0].Belegfeld1, rows[0].Belegfeld2));
    }
}
