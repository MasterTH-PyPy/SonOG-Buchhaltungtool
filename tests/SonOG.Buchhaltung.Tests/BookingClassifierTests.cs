using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Rules;

namespace SonOG.Buchhaltung.Tests;

public class BookingClassifierTests
{
    [Fact]
    public void Amazon_marketplace_charge_has_order_number()
    {
        var b = Fx.Classify("Lastschrift", -50.34m, "AMAZON PAYMENTS EUROPE S.C.A. 305-7892042-7991539 AMZN Mkt DE 1P WEDGTCGRVGI4BS");
        Assert.Equal(BookingCategory.AmazonAusgabe, b.Category);
        Assert.Equal("305-7892042-7991539", b.AmazonOrder);
    }

    [Fact]
    public void Amazon_credit_is_refund()
    {
        var b = Fx.Classify("GutschriftÜberweisung", 12.00m, "AMAZON EU SARL 303-0331003-9067513 Erstattung");
        Assert.Equal(BookingCategory.AmazonErstattung, b.Category);
    }

    [Fact]
    public void Customer_transfer_has_number_and_payer()
    {
        var b = Fx.Payment(8161.60m, "Erika Beispiel Rechnungsnummer: 202634694");
        Assert.Equal(BookingCategory.Kundenzahlung, b.Category);
        Assert.Equal(new[] { "202634694" }, b.OwnInvoiceNumbers.ToArray());
        Assert.Equal("Erika Beispiel", b.PayerName);
    }

    [Fact]
    public void Several_invoice_numbers_make_collective_payment()
    {
        var b = Fx.Payment(99.84m, "Erika Beispiel Re.Nr. 202634663, Re.Nr. 202634664");
        Assert.Equal(BookingCategory.KundenSammelzahlung, b.Category);
        Assert.Equal(2, b.OwnInvoiceNumbers.Count);
        Assert.Equal("Erika Beispiel", b.PayerName);
    }

    [Fact]
    public void Number_after_KNr_text_is_found()
    {
        var b = Fx.Payment(42.92m, "Hans Muster R-Nr. 202634637, K-Nr. KD-005225 vom 08.09.2026 / Wartung 07/26");
        Assert.Equal(new[] { "202634637" }, b.OwnInvoiceNumbers.ToArray());
        Assert.Equal("Hans Muster", b.PayerName);
    }

    [Fact]
    public void Direct_debit_short_numbers_are_expanded_even_when_wrapped()
    {
        var b = Fx.Classify("Lastsch-Einzug Online", 25.90m,
            "Beispiel-Verlag edb4c149 SVWZ+Re-Nr. 3 4138, 34140 DATUM 28.09.2026, 12. 48 UHR PMTINFID-05c7d7078499245b99edb4c149");
        Assert.Equal(BookingCategory.KundenSammelzahlung, b.Category);
        Assert.Equal(new[] { "202634138", "202634140" }, b.OwnInvoiceNumbers.ToArray());
        Assert.True(b.NumbersDerivedFromShortForm);
        Assert.Equal("Beispiel-Verlag", b.PayerName);
    }

    [Fact]
    public void Direct_debit_with_many_numbers_over_two_lines()
    {
        var b = Fx.Classify("Lastsch-Einzug Online", 1414.48m,
            "Muster Bau GmbH f58cd5f1 SVWZ+Re-Nr: 3 4422,34477,34155,34189,34239,34290, 34349,34383,34436,34461,34540,34541 DATUM 28.0 9.2026, 12.53 UHR");
        Assert.Equal(12, b.OwnInvoiceNumbers.Count);
        Assert.Equal("202634422", b.OwnInvoiceNumbers[0]);
        Assert.Equal("202634541", b.OwnInvoiceNumbers[11]);
    }

    [Fact]
    public void Unknown_credit_is_other_income_and_debit_is_other_expense()
    {
        Assert.Equal(BookingCategory.EingangSonstige, Fx.Payment(99m, "Stadtwerke Einspeisung 08/2026").Category);
        Assert.Equal(BookingCategory.AusgabeSonstige, Fx.Classify("Lastschrift", -99m, "Anbieter Mobilfunk").Category);
    }

    [Fact]
    public void Bank_fee_is_recognized_and_beleglos_by_default_rule()
    {
        var b = Fx.Classify("Entgeltabrechnung", -31.93m, "Abrechnung 30.09.2026");
        Assert.Equal(BookingCategory.Bankgebuehren, b.Category);
        Assert.True(b.Beleglos);
    }

    [Fact]
    public void Tax_office_is_beleglos()
    {
        var b = Fx.Classify("Überweisung Online", -4889m, "FA Musterstadt fuer Finanzamt STEUERN 4.889,00 EUR");
        Assert.True(b.Beleglos);
        Assert.Equal("Steuer", b.BelegloGrund);
    }

    [Fact]
    public void Custom_keyword_rule_applies()
    {
        var rules = new AppRules();
        rules.BelegloseStichwoerter.Add(new KeywordRule { Stichwort = "LEASING", Grund = "privat" });
        var b = Fx.Classify("Lastschrift", -300m, "Auto Leasing Rate 09/2026", rules: rules);
        Assert.True(b.Beleglos);
        Assert.Equal("privat", b.BelegloGrund);
    }
}
