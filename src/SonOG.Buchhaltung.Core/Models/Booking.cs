using SonOG.Buchhaltung.Core.Matching;

namespace SonOG.Buchhaltung.Core.Models;

public enum BookingCategory
{
    AmazonAusgabe,
    AmazonErstattung,
    Kundenzahlung,
    KundenSammelzahlung,
    EingangSonstige,
    AusgabeSonstige,
    Bankgebuehren,
}

public static class BookingCategoryExtensions
{
    public static string Text(this BookingCategory c) => c switch
    {
        BookingCategory.AmazonAusgabe => "Amazon-Ausgabe",
        BookingCategory.AmazonErstattung => "Amazon-Erstattung",
        BookingCategory.Kundenzahlung => "Kundenzahlung",
        BookingCategory.KundenSammelzahlung => "Kundenzahlung (Sammel)",
        BookingCategory.EingangSonstige => "Eingang sonstige",
        BookingCategory.AusgabeSonstige => "Ausgabe sonstige",
        BookingCategory.Bankgebuehren => "Bankgebühren",
        _ => c.ToString(),
    };

    public static bool IsAmazon(this BookingCategory c) => c is BookingCategory.AmazonAusgabe or BookingCategory.AmazonErstattung;
    public static bool IsCustomerPayment(this BookingCategory c) => c is BookingCategory.Kundenzahlung or BookingCategory.KundenSammelzahlung;
}

/// <summary>Eine Buchung aus dem Kontoauszug.</summary>
public sealed class Booking
{
    // Position im PDF (für den Stempel)
    public int PageIndex { get; init; }
    public double Y0 { get; init; }
    public double Y1 { get; init; }

    public DateOnly Date { get; init; }
    public string Type { get; init; } = "";
    public decimal Amount { get; init; }
    public string Text { get; init; } = "";

    /// <summary>Laufende Nummer, z. B. 2026-0042.</summary>
    public string Number { get; set; } = "";

    public BookingCategory Category { get; set; }
    public string AmazonOrder { get; set; } = "";

    /// <summary>Eigene Rechnungsnummern, die im Verwendungszweck erkannt wurden (9-stellig).</summary>
    public List<string> OwnInvoiceNumbers { get; set; } = new();

    /// <summary>True, wenn die Nummern aus Kurzform (5 Stellen, z. B. bei Lastschrift-Einzug) ergänzt wurden.</summary>
    public bool NumbersDerivedFromShortForm { get; set; }

    public string PayerName { get; set; } = "";

    public bool Beleglos { get; set; }
    public string BelegloGrund { get; set; } = "";

    /// <summary>Ergebnis des Rechnungsabgleichs (nur bei Kundenzahlungen / Eingängen).</summary>
    public PaymentMatch? Match { get; set; }

    /// <summary>Belege (PDF-Dateien), die auf diese Buchung gestempelt und ins Druckpaket kommen.</summary>
    public List<string> ReceiptFiles { get; set; } = new();

    public string ReceiptNote { get; set; } = "";
}
