using SonOG.Buchhaltung.Core.Accounting;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Tests;

public class AccountingTests
{
    private static AccountingSettings Settings() => new();

    private static PersonAccountDirectory Dir()
    {
        var d = new PersonAccountDirectory();
        d.Konten.Add(new PersonAccount { Konto = 70001, Name = "Amazon EU", Art = PersonenArt.Kreditor, Suchbegriffe = { "AMAZON" } });
        d.Konten.Add(new PersonAccount { Konto = 10001, Name = "Muster Hausverwaltung GmbH", Art = PersonenArt.Debitor });
        d.Konten.Add(new PersonAccount { Konto = 10002, Name = "Beispiel Bau", Art = PersonenArt.Debitor, Suchbegriffe = { "BEISPIELBAU" } });
        return d;
    }

    private static Booking Amazon(decimal amount, string order = "305-1234567-1234567") => new()
    {
        Date = new DateOnly(2026, 9, 14),
        Type = "Lastschrift",
        Amount = amount,
        Text = "AMAZON PAYMENTS EUROPE " + order,
        Number = "2026-0042",
        Category = amount < 0 ? BookingCategory.AmazonAusgabe : BookingCategory.AmazonErstattung,
        AmazonOrder = order,
    };

    private static InvoiceRecord Inv(string nr, decimal gross, string recipient = "Muster Hausverwaltung GmbH\nWeg 1") =>
        new(nr, nr + ".pdf", gross, recipient, new DateOnly(2026, 9, 2));

    private static Booking Customer(decimal amount, params InvoiceRecord[] inv) => new()
    {
        Date = new DateOnly(2026, 9, 20),
        Type = "Gutschrift",
        Amount = amount,
        Text = "Muster Hausverwaltung GmbH Re " + string.Join(" ", inv.Select(i => i.Number)),
        PayerName = "Muster Hausverwaltung GmbH",
        Number = "2026-0050",
        Category = inv.Length > 1 ? BookingCategory.KundenSammelzahlung : BookingCategory.Kundenzahlung,
        OwnInvoiceNumbers = inv.Select(i => i.Number).ToList(),
        Match = new PaymentMatch { Status = MatchStatus.Ok, Invoices = inv.ToList(), InvoiceSum = inv.Sum(i => i.GrossAmount) },
    };

    // ------------------------------------------------------------------ Vorschlag

    [Fact]
    public void Amazon_payment_is_suggested_on_creditor_with_invoice_posting()
    {
        var b = Amazon(-119.00m);
        var k = new KontierungsVorschlag(Settings(), Dir()).Vorschlagen(b);
        Assert.Equal(70001, k.Personenkonto);
        Assert.True(k.RechnungEinbuchen);
        Assert.Equal(KontierungsQuelle.Regel, k.Quelle);
        var z = Assert.Single(k.Zeilen);
        Assert.Equal(119.00m, z.Betrag);
        Assert.Equal(3400, z.Sachkonto);
    }

    [Fact]
    public void Amazon_without_creditor_in_directory_stays_open()
    {
        var k = new KontierungsVorschlag(Settings(), new PersonAccountDirectory()).Vorschlagen(Amazon(-50m));
        Assert.Null(k.Personenkonto);
        Assert.Equal(KontierungsQuelle.Offen, k.Quelle);
        Assert.Contains("Personenkonto nicht gefunden", k.Hinweis);
    }

    [Fact]
    public void Customer_collective_payment_gets_one_line_per_invoice_on_debtor()
    {
        var b = Customer(300m, Inv("202634001", 100m), Inv("202634002", 200m));
        var k = new KontierungsVorschlag(Settings(), Dir()).Vorschlagen(b);
        Assert.Equal(10001, k.Personenkonto);
        Assert.False(k.RechnungEinbuchen);
        Assert.Equal(KontierungsQuelle.Abgleich, k.Quelle);
        Assert.Equal(2, k.Zeilen.Count);
        Assert.Equal(new[] { "202634001", "202634002" }, k.Zeilen.Select(z => z.Rechnungsnummer).ToArray());
        Assert.Equal(300m, k.Summe);
    }

    [Fact]
    public void Customer_payment_with_unknown_debtor_asks_for_account()
    {
        var b = Customer(100m, Inv("202634001", 100m, "Ganz Andere AG"));
        b.PayerName = "Ganz Andere AG";
        var k = new KontierungsVorschlag(Settings(), Dir()).Vorschlagen(b);
        Assert.Null(k.Personenkonto);
        Assert.Contains("Debitor", k.Hinweis);
    }

    [Fact]
    public void Bank_fee_rule_matches_by_category()
    {
        var b = new Booking { Date = new DateOnly(2026, 9, 30), Type = "Entgeltabrechnung", Amount = -12.5m, Text = "Abrechnung 30.09.2026", Category = BookingCategory.Bankgebuehren, Number = "2026-0060" };
        var k = new KontierungsVorschlag(Settings(), Dir()).Vorschlagen(b);
        Assert.Null(k.Personenkonto);
        Assert.Equal(6855, Assert.Single(k.Zeilen).Sachkonto);
    }

    [Fact]
    public void Templates_without_keyword_never_match_automatically()
    {
        var s = Settings();
        var b = new Booking { Date = new DateOnly(2026, 9, 3), Amount = -40m, Text = "Irgendwas", Category = BookingCategory.AusgabeSonstige, Number = "2026-0001" };
        var k = new KontierungsVorschlag(s, Dir()).Vorschlagen(b);
        Assert.Equal(KontierungsQuelle.Offen, k.Quelle);
        Assert.Equal(0, Assert.Single(k.Zeilen).Sachkonto);
        // ... aber von Hand anwendbar
        var privat = s.Regeln.Single(r => r.Name == "Privatentnahme");
        var k2 = new KontierungsVorschlag(s, Dir()).AusRegel(b, privat);
        Assert.Equal(1800, k2.Zeilen[0].Sachkonto);
        Assert.Null(k2.Personenkonto);
    }

    // ------------------------------------------------------------------ Buchungssätze

    [Fact]
    public void Split_amazon_payment_books_each_line_on_creditor_then_pays_creditor()
    {
        var s = Settings();
        var b = Amazon(-150m);
        b.Kontierung = new Kontierung
        {
            Personenkonto = 70001,
            RechnungEinbuchen = true,
            Zeilen =
            {
                new KontierungsZeile { Betrag = 100m, Sachkonto = 3400, Text = "Material", Belegdatum = new DateOnly(2026, 9, 10) },
                new KontierungsZeile { Betrag = 50m, Sachkonto = 1800, Text = "privat" },
            },
        };
        Assert.DoesNotContain(Buchungssaetze.Pruefen(b, s, Dir()), p => p.Stufe == PruefStufe.Fehler);

        var rows = Buchungssaetze.AusKontoauszug(b, s);
        Assert.Equal(3, rows.Count);
        // Einbuchen: Kreditor Haben an Aufwand / Privat
        Assert.Equal((70001, 'H', 3400, 100m, new DateOnly(2026, 9, 10)), (rows[0].Konto, rows[0].SollHaben, rows[0].Gegenkonto, rows[0].Umsatz, rows[0].Belegdatum));
        Assert.Equal((70001, 'H', 1800, 50m, new DateOnly(2026, 9, 14)), (rows[1].Konto, rows[1].SollHaben, rows[1].Gegenkonto, rows[1].Umsatz, rows[1].Belegdatum));
        // Ausbuchen: Bank Haben an Kreditor (Kreditor Soll)
        Assert.Equal((1200, 'H', 70001, 150m), (rows[2].Konto, rows[2].SollHaben, rows[2].Gegenkonto, rows[2].Umsatz));
        Assert.All(rows, r => Assert.Equal("2026-0042", r.Belegfeld1));
    }

    [Fact]
    public void Amazon_refund_reverses_directions()
    {
        var s = Settings();
        var b = Amazon(20m);
        b.Kontierung = new Kontierung { Personenkonto = 70001, RechnungEinbuchen = true, Zeilen = { new KontierungsZeile { Betrag = 20m, Sachkonto = 3400 } } };
        var rows = Buchungssaetze.AusKontoauszug(b, s);
        Assert.Equal('S', rows[0].SollHaben); // Kreditor Soll an Aufwand (Gutschrift)
        Assert.Equal((1200, 'S', 70001), (rows[1].Konto, rows[1].SollHaben, rows[1].Gegenkonto));
    }

    [Fact]
    public void Customer_payment_clears_open_items_with_invoice_number_in_belegfeld1()
    {
        var s = Settings();
        var b = Customer(300m, Inv("202634001", 100m), Inv("202634002", 200m));
        b.Kontierung = new KontierungsVorschlag(s, Dir()).Vorschlagen(b);
        var rows = Buchungssaetze.AusKontoauszug(b, s);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((1200, 'S', 10001), (r.Konto, r.SollHaben, r.Gegenkonto)));
        Assert.Equal("202634001", rows[0].Belegfeld1);
        Assert.Equal("2026-0050", rows[0].Belegfeld2);

        s.RechnungsnummerInBelegfeld1BeiAusgleich = false;
        rows = Buchungssaetze.AusKontoauszug(b, s);
        Assert.Equal("2026-0050", rows[0].Belegfeld1);
        Assert.Equal("202634001", rows[0].Belegfeld2);
    }

    [Fact]
    public void Direct_booking_without_person_account_goes_against_bank()
    {
        var s = Settings();
        var b = new Booking { Date = new DateOnly(2026, 9, 10), Amount = -1000m, Text = "FINANZAMT", Number = "2026-0003", Beleglos = true };
        b.Kontierung = new Kontierung { Zeilen = { new KontierungsZeile { Betrag = 1000m, Sachkonto = 1780 } } };
        var row = Assert.Single(Buchungssaetze.AusKontoauszug(b, s));
        Assert.Equal((1200, 'H', 1780, 1000m), (row.Konto, row.SollHaben, row.Gegenkonto, row.Umsatz));
    }

    [Fact]
    public void Check_reports_wrong_sum_and_missing_account()
    {
        var s = Settings();
        var b = Amazon(-150m);
        b.Kontierung = new Kontierung { Zeilen = { new KontierungsZeile { Betrag = 100m } } };
        var p = Buchungssaetze.Pruefen(b, s, Dir()).Where(x => x.Stufe == PruefStufe.Fehler).Select(x => x.Text).ToList();
        Assert.Contains(p, t => t.Contains("Summe"));
        Assert.Contains(p, t => t.Contains("Sachkonto fehlt"));

        b.Kontierung = new Kontierung { Personenkonto = 12345678, Zeilen = { new KontierungsZeile { Betrag = 150m } } };
        p = Buchungssaetze.Pruefen(b, s, Dir()).Where(x => x.Stufe == PruefStufe.Fehler).Select(x => x.Text).ToList();
        Assert.Contains(p, t => t.Contains("Nummernkreis"));
    }

    [Fact]
    public void Invoice_date_from_previous_year_falls_back_to_payment_date()
    {
        var s = Settings();
        var b = Amazon(-10m);
        b.Kontierung = new Kontierung { Personenkonto = 70001, RechnungEinbuchen = true, Zeilen = { new KontierungsZeile { Betrag = 10m, Sachkonto = 3400, Belegdatum = new DateOnly(2025, 12, 28) } } };
        Assert.Equal(b.Date, Buchungssaetze.AusKontoauszug(b, s)[0].Belegdatum);
    }

    // ------------------------------------------------------------------ Sollstellung

    [Fact]
    public void Sollstellung_selects_month_and_skips_already_posted()
    {
        var s = Settings();
        var invoices = new[]
        {
            new InvoiceRecord("202634001", "a.pdf", 119m, "Muster Hausverwaltung GmbH\nWeg 1", new DateOnly(2026, 9, 2)),
            new InvoiceRecord("202634002", "b.pdf", 238m, "Beispiel Bau\nStraße 2", new DateOnly(2026, 9, 30)),
            new InvoiceRecord("202634003", "c.pdf", 50m, "Muster Hausverwaltung GmbH", new DateOnly(2026, 10, 1)),
            new InvoiceRecord("202634004", "d.pdf", 50m, "Muster Hausverwaltung GmbH", null),
        };
        var state = new SollstellungState();
        state.Gestellt["202634002"] = new SollstellungState.Eintrag { Monat = "2026-09" };

        var posten = Sollstellung.Vorbereiten(invoices, 2026, 9, s, Dir(), state);
        Assert.Equal(new[] { "202634001", "202634002" }, posten.Select(p => p.Rechnung.Number).ToArray());
        Assert.True(posten[0].Auswahl);
        Assert.False(posten[1].Auswahl);
        Assert.Equal("2026-09", posten[1].BereitsGestellt);
        Assert.Equal(10001, posten[0].Kontierung.Personenkonto);
        Assert.Equal(10002, posten[1].Kontierung.Personenkonto);

        var rows = Buchungssaetze.AusSollstellung(posten[0]);
        var r = Assert.Single(rows);
        Assert.Equal((10001, 'S', 8400, 119m, "202634001", new DateOnly(2026, 9, 2)), (r.Konto, r.SollHaben, r.Gegenkonto, r.Umsatz, r.Belegfeld1, r.Belegdatum));
        Assert.Empty(Buchungssaetze.Pruefen(posten[0], s, Dir()));
        Assert.Single(Sollstellung.OhneDatum(invoices));
    }

    [Fact]
    public void Credit_note_in_sollstellung_is_booked_on_credit_side()
    {
        var inv = new InvoiceRecord("202634009", "g.pdf", -59.5m, "Beispiel Bau", new DateOnly(2026, 9, 5));
        var p = new SollstellungsPosten(inv, Sollstellung.KontierungFuer(inv, Settings(), Dir()));
        var r = Assert.Single(Buchungssaetze.AusSollstellung(p));
        Assert.Equal((10002, 'H', 59.5m), (r.Konto, r.SollHaben, r.Umsatz));
    }

    // ------------------------------------------------------------------ DATEV-Datei

    [Fact]
    public void Datev_file_has_header_125_columns_and_german_formats()
    {
        var rows = new List<DatevBuchung>
        {
            new() { Umsatz = 1234.5m, SollHaben = 'H', Konto = 70001, Gegenkonto = 3400, BuSchluessel = "", Belegdatum = new DateOnly(2026, 9, 3), Belegfeld1 = "2026-0042", Belegfeld2 = "DE66OD6DABEI", Buchungstext = "Amazon \"Kabel\"; Stecker" },
        };
        var kopf = Buchungssaetze.Kopf(rows, Settings(), "Kontoauszug 9/2026");
        kopf.ErzeugtAm = new DateTime(2026, 10, 9, 12, 0, 0, 123);
        var text = DatevBuchungsstapel.Erzeugen(kopf, rows);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("\"EXTF\";700;21;\"Buchungsstapel\";13;20261009120000123;;\"RE\";", lines[0]);
        Assert.Contains(";1001;1;20260101;4;20260903;20260903;\"Kontoauszug 9/2026\";\"KA\";1;0;0;\"EUR\";", lines[0]);
        Assert.Equal(31, lines[0].Split(';').Length);
        Assert.Equal(125, lines[1].Split(';').Length);
        Assert.StartsWith("1234,50;\"H\";\"EUR\";;;;70001;3400;\"\";0309;\"2026-0042\";\"DE66OD6DABEI\";;\"Amazon \"\"Kabel\"\"; Stecker\";", lines[2]);
        Assert.Equal(125, TaxpoolImport.Split(lines[2], ';').Count);
    }

    [Fact]
    public void Belegfeld_removes_forbidden_characters()
    {
        Assert.Equal("RE2026/01-A", DatevBuchungsstapel.Belegfeld("RE 2026/01-A.", 36));
        Assert.Equal("123456789012", DatevBuchungsstapel.Belegfeld("1234567890123", 12));
    }

    [Fact]
    public void Header_refuses_two_fiscal_years()
    {
        var rows = new List<DatevBuchung>
        {
            new() { Umsatz = 1, Konto = 1200, Gegenkonto = 1800, Belegdatum = new DateOnly(2025, 12, 31) },
            new() { Umsatz = 1, Konto = 1200, Gegenkonto = 1800, Belegdatum = new DateOnly(2026, 1, 2) },
        };
        Assert.Throws<InvalidOperationException>(() => Buchungssaetze.Kopf(rows, Settings(), "x"));
    }

    // ------------------------------------------------------------------ Taxpool-Import

    [Fact]
    public void Import_chart_of_accounts_splits_ledger_and_person_accounts()
    {
        var s = Settings();
        var dir = new PersonAccountDirectory();
        var csv = "Konto;Bezeichnung\r\n1200;Bank\r\n3400;Wareneingang 19 % Vorsteuer\r\n10001;Muster GmbH\r\n70001;Amazon EU S.a.r.l.\r\n;leer\r\n";
        var e = TaxpoolImport.Text(csv, s, dir);
        Assert.Equal(2, e.Sachkonten);
        Assert.Equal(2, e.Personenkonten);
        Assert.Equal(1, e.Uebersprungen);
        Assert.Equal("Wareneingang 19 % Vorsteuer", s.KontoName(3400));
        Assert.Equal(PersonenArt.Kreditor, dir.Get(70001)!.Art);
        Assert.Equal(PersonenArt.Debitor, dir.Get(10001)!.Art);
    }

    [Fact]
    public void Import_templates_become_manual_rules_and_keep_user_keywords()
    {
        var s = Settings();
        var csv = "\"Vorlage\";\"Konto\";\"Gegenkonto\";\"BU-Schlüssel\";\"Buchungstext\"\r\n" +
                  "\"Telefon\";\"1200\";\"4920\";\"9\";\"Telefonrechnung\"\r\n" +
                  "\"Amazon Büro\";\"70001\";\"4930\";\"\";\"Bürobedarf\"\r\n";
        var e = TaxpoolImport.Text(csv, s, new PersonAccountDirectory());
        Assert.Equal(2, e.Vorlagen);

        var tel = s.Regeln.Single(r => r.Name == "Taxpool: Telefon");
        Assert.Equal((4920, "9", 0, false, false), (tel.Sachkonto, tel.BuSchluessel, tel.Personenkonto, tel.RechnungEinbuchen, tel.IstAutomatisch));
        var amz = s.Regeln.Single(r => r.Name == "Taxpool: Amazon Büro");
        Assert.Equal((4930, 70001, true), (amz.Sachkonto, amz.Personenkonto, amz.RechnungEinbuchen));

        tel.Stichwort = "TELEKOM";
        TaxpoolImport.Text(csv, s, new PersonAccountDirectory());
        Assert.Equal("TELEKOM", s.Regeln.Single(r => r.Name == "Taxpool: Telefon").Stichwort);
    }

    // ------------------------------------------------------------------ Verzeichnis / Speicher

    [Fact]
    public void Next_free_person_account_and_lookup()
    {
        var s = Settings();
        var dir = Dir();
        Assert.Equal(10003, dir.NextFree(PersonenArt.Debitor, s));
        Assert.Equal(70002, dir.NextFree(PersonenArt.Kreditor, s));
        Assert.Equal(10002, dir.FindDebitor("XYZ", "Zahlung BEISPIELBAU Rechnung")!.Konto);
        Assert.Equal(10001, dir.FindDebitor("Muster Hausverwaltung")!.Konto);
        Assert.Null(dir.FindDebitor("Völlig Fremd"));
    }

    [Fact]
    public void Store_restores_only_matching_bookings()
    {
        var bookings = new List<Booking> { Amazon(-10m), Amazon(-20m) };
        bookings[0].Kontierung = new Kontierung { Quelle = KontierungsQuelle.Manuell, Zeilen = { new KontierungsZeile { Betrag = 10m, Sachkonto = 1800 } } };
        bookings[1].Kontierung = new Kontierung { Zeilen = { new KontierungsZeile { Betrag = 20m, Sachkonto = 3400 } } };
        var path = Path.Combine(Path.GetTempPath(), "sonog-test-" + Guid.NewGuid() + ".json");
        try
        {
            KontierungStore.From(bookings).Save(path);
            var store = KontierungStore.Load(path);
            Assert.Equal(1800, store.Find(0, Amazon(-10m))!.Zeilen[0].Sachkonto);
            Assert.Null(store.Find(1, Amazon(-21m)));
        }
        finally { File.Delete(path); }
    }
}
