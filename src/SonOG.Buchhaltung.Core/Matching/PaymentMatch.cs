using SonOG.Buchhaltung.Core.Invoices;

namespace SonOG.Buchhaltung.Core.Matching;

public enum MatchStatus
{
    /// <summary>Rechnung gefunden, Betrag stimmt auf den Cent, Zahler passt zum Rechnungsempfänger.</summary>
    Ok,
    /// <summary>Betrag stimmt, aber der Zahler passt nicht zum Rechnungsempfänger (z. B. Hausverwaltung zahlt).</summary>
    EmpfaengerPruefen,
    /// <summary>Einzelrechnung gefunden, aber der gezahlte Betrag weicht ab.</summary>
    BetragWeichtAb,
    /// <summary>Mehrere Rechnungen in einer Überweisung, Summe stimmt nicht.</summary>
    SummeStimmtNicht,
    /// <summary>Nummer oder Betrag passt nur mit einem Zahlendreher - Vorschlag, bitte prüfen.</summary>
    ZahlendreherVorschlag,
    /// <summary>Genannte Rechnungsnummer liegt nicht im Rechnungsordner.</summary>
    RechnungNichtImOrdner,
    /// <summary>Der Rechnungsbetrag konnte nicht aus dem PDF gelesen werden.</summary>
    BetragNichtLesbar,
    /// <summary>Rechnung ist laut früherem Lauf oder früherer Buchung schon bezahlt.</summary>
    BereitsBezahlt,
    /// <summary>Eingang ohne Rechnungsnummer, Betrag und Name passen aber zu genau einer offenen Rechnung.</summary>
    VorschlagUeberBetrag,
    /// <summary>Eingang ohne erkennbaren Rechnungsbezug.</summary>
    KeineRechnungsnummer,
}

public static class MatchStatusExtensions
{
    public static string Text(this MatchStatus s) => s switch
    {
        MatchStatus.Ok => "OK",
        MatchStatus.EmpfaengerPruefen => "Empfänger prüfen",
        MatchStatus.BetragWeichtAb => "Betrag weicht ab",
        MatchStatus.SummeStimmtNicht => "Summe stimmt nicht",
        MatchStatus.ZahlendreherVorschlag => "Zahlendreher-Vorschlag",
        MatchStatus.RechnungNichtImOrdner => "Rechnung nicht im Ordner",
        MatchStatus.BetragNichtLesbar => "Rechnungsbetrag nicht lesbar",
        MatchStatus.BereitsBezahlt => "Bereits bezahlt",
        MatchStatus.VorschlagUeberBetrag => "Vorschlag über Betrag/Name",
        MatchStatus.KeineRechnungsnummer => "Keine Rechnungsnummer",
        _ => s.ToString(),
    };
}

public sealed class PaymentMatch
{
    public MatchStatus Status { get; set; }

    /// <summary>Gefundene (oder vorgeschlagene) Rechnungen.</summary>
    public List<InvoiceRecord> Invoices { get; set; } = new();

    public decimal? InvoiceSum { get; set; }

    /// <summary>Zahlung minus Rechnungssumme.</summary>
    public decimal? Difference { get; set; }

    public string Note { get; set; } = "";

    /// <summary>Soll die Rechnung als Beleg gestempelt und ins Druckpaket übernommen werden? Bei Vorschlägen erst nach Bestätigung.</summary>
    public bool AttachInvoices { get; set; }

    public bool NeedsReview => Status != MatchStatus.Ok && !Accepted;

    /// <summary>Vom Anwender bestätigt (z. B. ein Zahlendreher-Vorschlag).</summary>
    public bool Accepted { get; set; }

    /// <summary>Gilt die Rechnung damit als bezahlt (wird beim Export im Bezahlt-Verzeichnis vermerkt)?</summary>
    public bool CountsAsPaid => Status is MatchStatus.Ok or MatchStatus.EmpfaengerPruefen || Accepted;

    /// <summary>Bestätigt den Abgleich: Rechnungen werden als Beleg übernommen und gelten als bezahlt.</summary>
    public void Accept(Models.Booking booking)
    {
        Accepted = true;
        AttachInvoices = true;
        foreach (var inv in Invoices)
            if (!booking.ReceiptFiles.Contains(inv.FilePath)) booking.ReceiptFiles.Add(inv.FilePath);
    }
}
