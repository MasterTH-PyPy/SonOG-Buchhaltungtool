using System.ComponentModel;
using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.App;

/// <summary>Zeile im Grid: Anzeigewerte einer Buchung.</summary>
internal sealed class BookingRow
{
    public BookingRow(Booking booking) => Booking = booking;

    [Browsable(false)]
    public Booking Booking { get; }

    public string Nr => Booking.Number;
    public string Datum => Booking.Date.ToString("dd.MM.yyyy");
    public decimal Betrag => Booking.Amount;
    public string Kategorie => Booking.Category.Text();

    public string Status
    {
        get
        {
            if (Booking.Beleglos) return "beleglos";
            if (Booking.Match is { } m) return m.Status.Text() + (m.Accepted && m.Status != MatchStatus.Ok ? " (bestätigt)" : "");
            if (Booking.Category.IsAmazon()) return Booking.ReceiptFiles.Count > 0 ? "Beleg gefunden" : "Beleg fehlt";
            return "";
        }
    }

    public string Rechnungen
    {
        get
        {
            if (Booking.AmazonOrder.Length > 0) return Booking.AmazonOrder;
            if (Booking.Match is { Invoices.Count: > 0 } m) return string.Join(", ", m.Invoices.Select(i => i.Number));
            return string.Join(", ", Booking.OwnInvoiceNumbers);
        }
    }

    public string Hinweis =>
        string.Join(" ", new[] { Booking.Beleglos ? Booking.BelegloGrund : null, Booking.Match?.Note, Booking.ReceiptNote }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Buchungstext => Booking.Text;

    /// <summary>Muss die Buchung noch geprüft werden?</summary>
    [Browsable(false)]
    public bool NeedsReview =>
        !Booking.Beleglos && (Booking.Match is { NeedsReview: true } || (Booking.Category.IsAmazon() && Booking.ReceiptFiles.Count == 0));

    [Browsable(false)]
    public Color BackColor
    {
        get
        {
            if (Booking.Beleglos) return Color.FromArgb(0xE2, 0xEF, 0xDA);
            if (Booking.Category.IsAmazon()) return Booking.ReceiptFiles.Count > 0 ? Color.Empty : Color.FromArgb(0xF8, 0xCB, 0xAD);
            if (Booking.Match is not { } m || m.Status == MatchStatus.Ok || m.Accepted) return Color.Empty;
            return m.Status is MatchStatus.EmpfaengerPruefen or MatchStatus.ZahlendreherVorschlag or MatchStatus.VorschlagUeberBetrag
                ? Color.FromArgb(0xFF, 0xF2, 0xCC)
                : Color.FromArgb(0xF8, 0xCB, 0xAD);
        }
    }
}
