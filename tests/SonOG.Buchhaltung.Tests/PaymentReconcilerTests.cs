using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Rules;

namespace SonOG.Buchhaltung.Tests;

public class PaymentReconcilerTests
{
    private static ReconcileResult Run(InvoiceIndex idx, int year, IReadOnlyDictionary<string, string>? paid, DateOnly? openFrom, params Booking[] bookings)
    {
        for (int i = 0; i < bookings.Length; i++) bookings[i].Number = $"{year}-{i + 1:0000}";
        return new PaymentReconciler(idx, year, paid).Run(bookings, openFrom);
    }

    private static ReconcileResult Run(InvoiceIndex idx, params Booking[] bookings) => Run(idx, 2026, null, null, bookings);

    // ---- Einzelzahlung -----------------------------------------------------------------------

    [Fact]
    public void Exact_single_payment_is_ok_and_attaches_invoice()
    {
        var idx = Fx.Index(Fx.Inv("202634552", 254.36m, "Erika Beispiel\nMusterweg 1"));
        var b = Fx.Payment(254.36m, "Erika Beispiel Rechnungsnummer: 202634552");

        Run(idx, b);

        Assert.Equal(MatchStatus.Ok, b.Match!.Status);
        Assert.Equal(new[] { "202634552.pdf" }, b.ReceiptFiles.ToArray());
    }

    [Fact]
    public void Amount_difference_is_shown_with_skonto_hint()
    {
        var idx = Fx.Index(Fx.Inv("202634552", 100.00m, "Erika Beispiel"));
        var b = Fx.Payment(97.00m, "Erika Beispiel Rechnungsnummer: 202634552");

        Run(idx, b);

        Assert.Equal(MatchStatus.BetragWeichtAb, b.Match!.Status);
        Assert.Equal(-3.00m, b.Match.Difference);
        Assert.Contains("Skonto", b.Match.Note);
    }

    [Fact]
    public void Payer_that_does_not_fit_recipient_asks_for_review()
    {
        var idx = Fx.Index(Fx.Inv("202634552", 100.00m, "Erika Beispiel"));
        var b = Fx.Payment(100.00m, "Hausverwaltung Nord GmbH Rechnungsnummer: 202634552");

        Run(idx, b);

        Assert.Equal(MatchStatus.EmpfaengerPruefen, b.Match!.Status);
        Assert.Contains("Hausverwaltung Nord", b.Match.Note);
    }

    [Fact]
    public void Unknown_payer_does_not_block_ok()
    {
        var idx = Fx.Index(Fx.Inv("202634552", 100.00m, "Erika Beispiel"));
        var b = Fx.Payment(100.00m, "202634552");

        Run(idx, b);

        Assert.Equal(MatchStatus.Ok, b.Match!.Status);
    }

    [Fact]
    public void Same_amount_for_other_customer_is_not_confused()
    {
        var idx = Fx.Index(
            Fx.Inv("202634100", 395.68m, "Anna Alpha"),
            Fx.Inv("202634010", 395.68m, "Bernd Beta"));
        var right = Fx.Payment(395.68m, "Anna Alpha Rechnungsnummer: 202634100");
        var wrong = Fx.Payment(395.68m, "Anna Alpha Rechnungsnummer: 202634010");

        Run(idx, right, wrong);

        Assert.Equal(MatchStatus.Ok, right.Match!.Status);
        Assert.Equal(MatchStatus.EmpfaengerPruefen, wrong.Match!.Status);
    }

    [Fact]
    public void Unreadable_invoice_amount_is_reported()
    {
        var idx = Fx.Index(Fx.Inv("202634552", null, "Erika Beispiel"));
        var b = Fx.Payment(100.00m, "Erika Beispiel Rechnungsnummer: 202634552");

        Run(idx, b);

        Assert.Equal(MatchStatus.BetragNichtLesbar, b.Match!.Status);
    }

    // ---- Sammelzahlung -----------------------------------------------------------------------

    [Fact]
    public void Collective_payment_is_ok_when_sum_matches()
    {
        var idx = Fx.Index(
            Fx.Inv("202634001", 10.00m, "Erika Beispiel"),
            Fx.Inv("202634002", 20.00m, "Erika Beispiel"),
            Fx.Inv("202634003", 5.50m, "Erika Beispiel"));
        var b = Fx.Payment(30.00m, "Erika Beispiel Re.Nr. 202634001, Re.Nr. 202634002");

        Run(idx, b);

        Assert.Equal(MatchStatus.Ok, b.Match!.Status);
        Assert.Equal(30.00m, b.Match.InvoiceSum);
        Assert.Equal(2, b.ReceiptFiles.Count);
    }

    [Fact]
    public void Collective_mismatch_shows_the_combination_that_would_fit()
    {
        var idx = Fx.Index(
            Fx.Inv("202634001", 50.00m, "Erika Beispiel"),
            Fx.Inv("202634002", 30.00m, "Erika Beispiel"),
            Fx.Inv("202634003", 20.00m, "Erika Beispiel"));
        var b = Fx.Payment(70.00m, "Erika Beispiel Re.Nr. 202634001, Re.Nr. 202634002");

        Run(idx, b);

        Assert.Equal(MatchStatus.SummeStimmtNicht, b.Match!.Status);
        Assert.Equal(80.00m, b.Match.InvoiceSum);
        Assert.Contains("ohne 202634002", b.Match.Note);
        Assert.Contains("zusätzlich 202634003", b.Match.Note);
    }

    [Fact]
    public void Single_named_invoice_but_two_paid_suggests_the_other()
    {
        var idx = Fx.Index(
            Fx.Inv("202634001", 50.00m, "Erika Beispiel"),
            Fx.Inv("202634003", 20.00m, "Erika Beispiel"));
        var b = Fx.Payment(70.00m, "Erika Beispiel Rechnungsnummer: 202634001");

        Run(idx, b);

        Assert.Equal(MatchStatus.BetragWeichtAb, b.Match!.Status);
        Assert.Contains("zusätzlich 202634003", b.Match.Note);
    }

    [Fact]
    public void Invoices_of_other_customers_are_not_used_for_the_suggestion()
    {
        var idx = Fx.Index(
            Fx.Inv("202634001", 50.00m, "Erika Beispiel"),
            Fx.Inv("202634003", 20.00m, "Max Mustermann"));
        var b = Fx.Payment(70.00m, "Erika Beispiel Rechnungsnummer: 202634001");

        Run(idx, b);

        Assert.DoesNotContain("202634003", b.Match!.Note);
    }

    // ---- Zahlendreher ------------------------------------------------------------------------

    [Fact]
    public void Transposed_invoice_number_is_suggested_when_amount_fits()
    {
        var idx = Fx.Index(Fx.Inv("202634695", 142.80m, "Erika Beispiel"));
        var b = Fx.Payment(142.80m, "Erika Beispiel Rechnungsnummer: 202634659");

        Run(idx, b);

        Assert.Equal(MatchStatus.ZahlendreherVorschlag, b.Match!.Status);
        Assert.Equal("202634695", b.Match.Invoices[0].Number);
        Assert.False(b.Match.AttachInvoices);
        Assert.Empty(b.ReceiptFiles);
    }

    [Fact]
    public void Transposed_number_in_collective_payment_is_resolved()
    {
        var idx = Fx.Index(
            Fx.Inv("202634001", 10.00m, "Erika Beispiel"),
            Fx.Inv("202634695", 20.00m, "Erika Beispiel"));
        var b = Fx.Payment(30.00m, "Erika Beispiel Re.Nr. 202634001, Re.Nr. 202634659");

        Run(idx, b);

        Assert.Equal(MatchStatus.ZahlendreherVorschlag, b.Match!.Status);
        Assert.Equal(2, b.Match.Invoices.Count);
        Assert.Contains("202634695", b.Match.Note);
    }

    [Fact]
    public void Missing_invoice_without_similar_number_is_reported()
    {
        var idx = Fx.Index(Fx.Inv("202634001", 10.00m, "Erika Beispiel"));
        var b = Fx.Payment(10.00m, "Erika Beispiel Rechnungsnummer: 202639999");

        Run(idx, b);

        Assert.Equal(MatchStatus.RechnungNichtImOrdner, b.Match!.Status);
        Assert.Contains("202639999", b.Match.Note);
    }

    [Fact]
    public void Transposed_amount_is_flagged_but_invoice_is_attached()
    {
        var idx = Fx.Index(Fx.Inv("202634552", 45.90m, "Erika Beispiel"));
        var b = Fx.Payment(54.90m, "Erika Beispiel Rechnungsnummer: 202634552");

        Run(idx, b);

        Assert.Equal(MatchStatus.ZahlendreherVorschlag, b.Match!.Status);
        Assert.True(b.Match.AttachInvoices);
        Assert.Equal(1, b.ReceiptFiles.Count);
    }

    [Fact]
    public void Wrong_number_of_other_customer_is_corrected_via_amount_and_name()
    {
        var idx = Fx.Index(
            Fx.Inv("202634100", 100.00m, "Anna Alpha"),
            Fx.Inv("202634010", 150.00m, "Bernd Beta"));
        var b = Fx.Payment(150.00m, "Bernd Beta Rechnungsnummer: 202634100");

        Run(idx, b);

        Assert.Equal(MatchStatus.ZahlendreherVorschlag, b.Match!.Status);
        Assert.Equal("202634010", b.Match.Invoices[0].Number);
        Assert.False(b.Match.AttachInvoices);
    }

    // ---- bereits bezahlt ---------------------------------------------------------------------

    [Fact]
    public void Invoice_paid_in_earlier_statement_is_flagged()
    {
        var idx = Fx.Index(Fx.Inv("202634552", 100.00m, "Erika Beispiel"));
        var b = Fx.Payment(100.00m, "Erika Beispiel Rechnungsnummer: 202634552");
        var paid = new Dictionary<string, string> { ["202634552"] = "2026-0003" };

        Run(idx, 2026, paid, null, b);

        Assert.Equal(MatchStatus.BereitsBezahlt, b.Match!.Status);
        Assert.Contains("2026-0003", b.Match.Note);
        Assert.Empty(b.ReceiptFiles);
    }

    [Fact]
    public void Same_invoice_paid_twice_in_one_statement_is_flagged_on_second()
    {
        var idx = Fx.Index(Fx.Inv("202634552", 100.00m, "Erika Beispiel"));
        var first = Fx.Payment(100.00m, "Erika Beispiel Rechnungsnummer: 202634552");
        var second = Fx.Payment(100.00m, "Erika Beispiel Rechnungsnummer: 202634552");

        Run(idx, first, second);

        Assert.Equal(MatchStatus.Ok, first.Match!.Status);
        Assert.Equal(MatchStatus.BereitsBezahlt, second.Match!.Status);
        Assert.Contains("2026-0001", second.Match.Note);
    }

    // ---- ohne Rechnungsnummer ----------------------------------------------------------------

    [Fact]
    public void Income_without_number_is_suggested_when_amount_and_name_are_unique()
    {
        var idx = Fx.Index(Fx.Inv("202634552", 77.77m, "Erika Beispiel"));
        var b = Fx.Payment(77.77m, "Zahlung Beispiel Erika Danke");

        Run(idx, b);

        Assert.Equal(MatchStatus.VorschlagUeberBetrag, b.Match!.Status);
        Assert.False(b.Match.AttachInvoices);
    }

    [Fact]
    public void Income_without_any_reference_is_flagged()
    {
        var b = Fx.Payment(99.00m, "Stadtwerke Einspeisung 08/2026");

        Run(Fx.Index(), b);

        Assert.Equal(MatchStatus.KeineRechnungsnummer, b.Match!.Status);
    }

    [Fact]
    public void Beleglos_income_is_not_checked()
    {
        var rules = new AppRules();
        rules.BelegloseStichwoerter.Add(new KeywordRule { Stichwort = "STADTWERKE", Grund = "Einspeisung" });
        var b = Fx.Classify("GutschriftÜberweisung", 99.00m, "Stadtwerke Einspeisung 08/2026", rules: rules);

        Run(Fx.Index(), b);

        Assert.Null(b.Match);
    }

    [Fact]
    public void Expenses_are_ignored()
    {
        var b = Fx.Classify("Lastschrift", -10.00m, "Mobilfunk");
        Run(Fx.Index(), b);
        Assert.Null(b.Match);
    }

    // ---- Lastschrift-Einzug & Jahreswechsel --------------------------------------------------

    [Fact]
    public void Direct_debit_short_numbers_find_invoices_of_previous_year()
    {
        var idx = Fx.Index(Fx.Inv("202634138", 25.90m, "Beispiel-Verlag GmbH"));
        var b = Fx.Classify("Lastsch-Einzug Online", 25.90m, "Beispiel-Verlag abcd1234 SVWZ+Re-Nr. 34138 DATUM 05.01.2027, 12.48 UHR", year: 2027);

        Run(idx, 2027, null, null, b);

        Assert.Equal(MatchStatus.Ok, b.Match!.Status);
        Assert.Equal("202634138", b.Match.Invoices[0].Number);
    }

    // ---- offene Posten -----------------------------------------------------------------------

    [Fact]
    public void Open_invoices_are_those_without_payment()
    {
        var idx = Fx.Index(
            Fx.Inv("202634001", 10.00m, "A Alpha"),
            Fx.Inv("202634002", 20.00m, "B Beta"),
            Fx.Inv("202634003", 30.00m, "C Gamma"));
        var b = Fx.Payment(10.00m, "A Alpha Rechnungsnummer: 202634001");

        var res = Run(idx, b);

        Assert.Equal(new[] { "202634002", "202634003" }, res.OpenInvoices.Select(i => i.Number).ToArray());
    }

    [Fact]
    public void Open_invoices_respect_start_date_and_ledger()
    {
        var idx = Fx.Index(
            Fx.Inv("202534001", 10.00m, "A Alpha", new DateOnly(2025, 3, 1)),
            Fx.Inv("202634002", 20.00m, "B Beta", new DateOnly(2026, 3, 1)),
            Fx.Inv("202634003", 30.00m, "C Gamma", new DateOnly(2026, 4, 1)));
        var paid = new Dictionary<string, string> { ["202634003"] = "2026-0001" };

        var res = Run(idx, 2026, paid, new DateOnly(2026, 1, 1));

        Assert.Equal(new[] { "202634002" }, res.OpenInvoices.Select(i => i.Number).ToArray());
    }

    // ---- Amazon ------------------------------------------------------------------------------

    [Fact]
    public void Amazon_bookings_get_receipts_by_order_number()
    {
        var charge = Fx.Classify("Lastschrift", -50.34m, "AMAZON PAYMENTS EUROPE S.C.A. 305-7892042-7991539 AMZN Mkt DE");
        var missing = Fx.Classify("Lastschrift", -8.50m, "AMAZON BUSINESS EU SARL 305-0682748-5941962 AMZNBusiness");
        var receipts = new[]
        {
            new ReceiptDocument("a.pdf", new[] { "305-7892042-7991539" }, ReceiptKind.Rechnung, 50.34m),
            new ReceiptDocument("b.pdf", new[] { "305-1111111-2222222" }, ReceiptKind.Rechnung, 9.99m),
        };

        AmazonMatcher.Assign(new[] { charge, missing }, receipts);

        Assert.Equal(new[] { "a.pdf" }, charge.ReceiptFiles.ToArray());
        Assert.Empty(missing.ReceiptFiles);
        Assert.Contains("305-0682748-5941962", missing.ReceiptNote);
    }

    private static ReceiptDocument Doc(string file, string order, ReceiptKind kind, decimal? amount) =>
        new(file, new[] { order }, kind, amount);

    [Fact]
    public void Amazon_order_with_two_invoices_and_two_charges_is_matched_by_amount()
    {
        const string o = "305-1000000-0000001";
        var b1 = Fx.Classify("Lastschrift", -15.99m, $"AMAZON BUSINESS EU SARL {o} AMZNBusiness");
        var b2 = Fx.Classify("Lastschrift", -8.50m, $"AMAZON BUSINESS EU SARL {o} AMZNBusiness");
        var docs = new[]
        {
            Doc("x/" + o + ".pdf", o, ReceiptKind.Bestelluebersicht, 24.49m),
            Doc("x/DEA.pdf", o, ReceiptKind.Rechnung, 8.50m),
            Doc("x/DEB.pdf", o, ReceiptKind.Rechnung, 15.99m),
        };

        var res = AmazonMatcher.Assign(new[] { b1, b2 }, docs);

        Assert.Equal(new[] { "x/DEB.pdf" }, b1.ReceiptFiles.ToArray());
        Assert.Equal(new[] { "x/DEA.pdf" }, b2.ReceiptFiles.ToArray());
        Assert.Empty(res.UnusedDocuments);
    }

    [Fact]
    public void Amazon_refund_booking_gets_the_credit_note_not_the_original_invoice()
    {
        const string o = "305-1000000-0000002";
        var refund = Fx.Classify("Gutschrift", 20.38m, $"AMAZON PAYMENTS EUROPE S.C.A. {o} AMZN Mkt DE");
        var docs = new[]
        {
            Doc("x/INV.pdf", o, ReceiptKind.Rechnung, 20.38m),
            Doc("x/CRN.pdf", o, ReceiptKind.Gutschrift, -20.38m),
        };

        var res = AmazonMatcher.Assign(new[] { refund }, docs);

        Assert.Equal(new[] { "x/CRN.pdf" }, refund.ReceiptFiles.ToArray());
        Assert.Single(res.UnusedDocuments);
    }

    [Fact]
    public void Overview_page_alone_is_not_a_receipt()
    {
        const string o = "305-1000000-0000003";
        var b = Fx.Classify("Lastschrift", -19.90m, $"AMAZON PAYMENTS EUROPE S.C.A. {o} AMZN Mkt DE");

        AmazonMatcher.Assign(new[] { b }, new[] { Doc("x/" + o + ".pdf", o, ReceiptKind.Bestelluebersicht, 19.90m) });

        Assert.Empty(b.ReceiptFiles);
        Assert.Contains("Bestellübersicht", b.ReceiptNote);
    }

    [Fact]
    public void Seller_invoice_without_readable_amount_is_used_when_overview_amount_matches()
    {
        const string o = "305-1000000-0000004";
        var b = Fx.Classify("Lastschrift", -19.90m, $"AMAZON PAYMENTS EUROPE S.C.A. {o} AMZN Mkt DE");
        var docs = new[]
        {
            Doc("x/" + o + ".pdf", o, ReceiptKind.Bestelluebersicht, 19.90m),
            Doc("x/XRE-1.pdf", o, ReceiptKind.Rechnung, null),
        };

        AmazonMatcher.Assign(new[] { b }, docs);

        Assert.Equal(new[] { "x/XRE-1.pdf" }, b.ReceiptFiles.ToArray());
        Assert.Contains("prüfen", b.ReceiptNote);
    }

    [Fact]
    public void Wrong_amount_is_reported_with_available_amounts()
    {
        const string o = "305-1000000-0000005";
        var b = Fx.Classify("Lastschrift", -30.00m, $"AMAZON PAYMENTS EUROPE S.C.A. {o} AMZN Mkt DE");

        AmazonMatcher.Assign(new[] { b }, new[] { Doc("x/A.pdf", o, ReceiptKind.Rechnung, 13.00m) });

        Assert.Empty(b.ReceiptFiles);
        Assert.Contains("13,00", b.ReceiptNote);
    }

    [Fact]
    public void Same_export_twice_does_not_duplicate_receipts()
    {
        const string o = "305-1000000-0000006";
        var b = Fx.Classify("Lastschrift", -5.00m, $"AMAZON BUSINESS EU SARL {o} AMZNBusiness");
        var docs = new[]
        {
            Doc("w1/DEX.pdf", o, ReceiptKind.Rechnung, 5.00m),
            Doc("w2/DEX.pdf", o, ReceiptKind.Rechnung, 5.00m),
        };

        var res = AmazonMatcher.Assign(new[] { b }, docs);

        Assert.Single(b.ReceiptFiles);
        Assert.Empty(res.UnusedDocuments);
    }

    [Fact]
    public void Receipt_kind_and_amount_are_read_from_text()
    {
        var inv = AmazonMatcher.Analyze("Rechnung Seite 1 von 1 Amazon Business EU ... Rechnungssumme 15,99 € ... Zahlbetrag 15,99 €", "DE1.pdf");
        Assert.Equal((ReceiptKind.Rechnung, 15.99m), inv);

        var crn = AmazonMatcher.Analyze("Rechnungskorrektur Seite 1 von 2 ... Zahlbetrag -20,05 €", "DE2.pdf");
        Assert.Equal((ReceiptKind.Gutschrift, -20.05m), crn);

        var ov = AmazonMatcher.Analyze("Übersicht zur Bestellung #305-1000000-0000001 Gesamtbestellwert:  1.078,69 EUR", "305-1000000-0000001.pdf");
        Assert.Equal((ReceiptKind.Bestelluebersicht, 1078.69m), ov);

        var seller = AmazonMatcher.Analyze("Rechnung Beleg-Nr: XRE-1 ... Gesamt Netto 16,72 € Gesamt Brutto 19,90 €", "XRE-1.pdf");
        Assert.Equal((ReceiptKind.Rechnung, 19.90m), seller);
    }

    [Fact]
    public void Merchant_keywords_skip_legal_forms_and_noise()
    {
        var k = MerchantKeywords.Extract("Musterfirma GmbH SEPA Lastschrift Rechnung 4711 DE12 3456");
        Assert.Equal(new[] { "Musterfirma" }, k.ToArray());
        var two = MerchantKeywords.Extract("Beispiel-Verlag Online Shop abcd1234");
        Assert.Equal(new[] { "Beispiel-Verlag", "Shop" }, two.ToArray());
    }

    [Fact]
    public void Reference_numbers_are_read_from_booking_text()
    {
        var r = MerchantKeywords.ReferenceNumbers("Musterfirma Rechnung RE-2026-0815 vom 01.09. Kundennr 12");
        Assert.Equal(new[] { "RE-2026-0815" }, r.ToArray());
    }

    [Fact]
    public void Amounts_are_found_in_german_format()
    {
        var a = AmazonMatcher.FindAmounts("Summe 1.234,56 EUR, MwSt 19,00 und 1.234,56 sowie Tel 0123,4");
        Assert.Equal(new[] { 1234.56m, 19.00m }, a.ToArray());
    }

    [Fact]
    public void Order_numbers_are_found_in_receipt_text()
    {
        var found = AmazonMatcher.FindOrderNumbers("Bestellnummer 305-7892042-7991539 vom 29.09.2026, Retoure 305-7892042-7991539 und 303-0331003-9067513");
        Assert.Equal(2, found.Count);
    }
}

public class AcceptSuggestionTests
{
    [Fact]
    public void Accepting_a_suggestion_attaches_invoice_and_marks_it_paid()
    {
        var idx = Fx.Index(Fx.Inv("202634695", 142.80m, "Erika Beispiel"));
        var b = Fx.Payment(142.80m, "Erika Beispiel Rechnungsnummer: 202634659");
        b.Number = "2026-0001";
        var rec = new PaymentReconciler(idx, 2026);
        rec.Run(new[] { b });

        Assert.False(b.Match!.CountsAsPaid);
        Assert.True(b.Match.NeedsReview);

        b.Match.Accept(b);

        Assert.True(b.Match.CountsAsPaid);
        Assert.False(b.Match.NeedsReview);
        Assert.Equal(new[] { "202634695.pdf" }, b.ReceiptFiles.ToArray());
    }
}
