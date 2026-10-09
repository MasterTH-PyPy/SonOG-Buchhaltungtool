using ClosedXML.Excel;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Infrastructure.Export;

/// <summary>Schreibt die Zuordnungsliste (alle Buchungen, offene Rechnungen, Zusammenfassung) als Excel-Datei.</summary>
public static class ExcelExporter
{
    private static readonly string[] Headers =
    {
        "Lfd. Nr.", "Datum", "Betrag EUR", "Typ", "Kategorie", "Belegart", "Abgleich", "Rechnungsnr.", "Rechnungsbetrag",
        "Differenz", "Empfänger laut Rechnung", "Beleg-Dateien", "Hinweis", "Buchungstext",
    };

    public static void Write(string path, ParsedStatement statement, IReadOnlyList<Booking> bookings, IReadOnlyList<InvoiceRecord> openInvoices)
    {
        using var wb = new XLWorkbook();
        WriteBookings(wb.Worksheets.Add("Buchungen"), bookings);
        WriteOpen(wb.Worksheets.Add("Offene Rechnungen"), openInvoices);
        WriteSummary(wb.Worksheets.Add("Zusammenfassung"), statement, bookings, openInvoices);
        wb.SaveAs(path);
    }

    private static void WriteBookings(IXLWorksheet ws, IReadOnlyList<Booking> bookings)
    {
        for (int c = 0; c < Headers.Length; c++) ws.Cell(1, c + 1).Value = Headers[c];
        var head = ws.Range(1, 1, 1, Headers.Length);
        head.Style.Font.Bold = true;
        head.Style.Font.FontColor = XLColor.White;
        head.Style.Fill.BackgroundColor = XLColor.FromHtml("#305496");

        int r = 2;
        foreach (var b in bookings)
        {
            var m = b.Match;
            ws.Cell(r, 1).Value = b.Number;
            ws.Cell(r, 2).Value = b.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 2).Style.DateFormat.Format = "dd.MM.yyyy";
            ws.Cell(r, 3).Value = (double)b.Amount;
            ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00;[Red]-#,##0.00";
            ws.Cell(r, 4).Value = b.Type;
            ws.Cell(r, 5).Value = b.Category.Text();
            ws.Cell(r, 6).Value = b.Beleglos ? "beleglos: " + b.BelegloGrund : "Beleg erforderlich";
            ws.Cell(r, 7).Value = m is null ? "" : m.Status.Text() + (m.Accepted && m.Status != MatchStatus.Ok ? " (bestätigt)" : "");

            var numbers = m is { Invoices.Count: > 0 }
                ? string.Join(", ", m.Invoices.Select(i => i.Number))
                : string.Join(", ", b.OwnInvoiceNumbers);
            if (b.AmazonOrder.Length > 0) numbers = b.AmazonOrder;
            ws.Cell(r, 8).Value = numbers;

            if (m?.InvoiceSum is { } sum)
            {
                ws.Cell(r, 9).Value = (double)sum;
                ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0.00";
            }
            if (m?.Difference is { } diff)
            {
                ws.Cell(r, 10).Value = (double)diff;
                ws.Cell(r, 10).Style.NumberFormat.Format = "#,##0.00;[Red]-#,##0.00";
            }
            ws.Cell(r, 11).Value = m is { Invoices.Count: > 0 } ? m.Invoices[0].RecipientFirstLine : "";
            ws.Cell(r, 12).Value = string.Join("; ", b.ReceiptFiles.Select(Path.GetFileName));
            ws.Cell(r, 13).Value = string.Join(" ", new[] { m?.Note, b.ReceiptNote }.Where(s => !string.IsNullOrWhiteSpace(s)));
            ws.Cell(r, 14).Value = b.Text;

            string? color = b.Beleglos ? "#E2EFDA"
                : m is null ? null
                : m.Status == MatchStatus.Ok || m.Accepted ? null
                : m.Status is MatchStatus.EmpfaengerPruefen or MatchStatus.ZahlendreherVorschlag or MatchStatus.VorschlagUeberBetrag ? "#FFF2CC"
                : "#F8CBAD";
            if (color is null && b.ReceiptNote.Length > 0) color = "#F8CBAD";
            if (color is not null) ws.Range(r, 1, r, Headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml(color);
            r++;
        }

        int[] widths = { 12, 11, 12, 22, 22, 24, 26, 30, 14, 12, 30, 40, 70, 90 };
        for (int c = 0; c < widths.Length; c++) ws.Column(c + 1).Width = widths[c];
        ws.SheetView.FreezeRows(1);
        if (bookings.Count > 0) ws.Range(1, 1, r - 1, Headers.Length).SetAutoFilter();
    }

    private static void WriteOpen(IXLWorksheet ws, IReadOnlyList<InvoiceRecord> open)
    {
        string[] head = { "Rechnungsnr.", "Datum", "Betrag EUR", "Empfänger", "Datei" };
        for (int c = 0; c < head.Length; c++) ws.Cell(1, c + 1).Value = head[c];
        var hr = ws.Range(1, 1, 1, head.Length);
        hr.Style.Font.Bold = true;
        hr.Style.Font.FontColor = XLColor.White;
        hr.Style.Fill.BackgroundColor = XLColor.FromHtml("#305496");

        int r = 2;
        foreach (var i in open)
        {
            ws.Cell(r, 1).Value = i.Number;
            if (i.Date is { } d)
            {
                ws.Cell(r, 2).Value = d.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 2).Style.DateFormat.Format = "dd.MM.yyyy";
            }
            if (i.GrossAmount is { } g)
            {
                ws.Cell(r, 3).Value = (double)g;
                ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
            }
            ws.Cell(r, 4).Value = i.RecipientFirstLine;
            ws.Cell(r, 5).Value = Path.GetFileName(i.FilePath);
            r++;
        }
        int[] widths = { 14, 11, 12, 40, 60 };
        for (int c = 0; c < widths.Length; c++) ws.Column(c + 1).Width = widths[c];
        ws.SheetView.FreezeRows(1);
    }

    private static void WriteSummary(IXLWorksheet ws, ParsedStatement st, IReadOnlyList<Booking> bookings, IReadOnlyList<InvoiceRecord> open)
    {
        int r = 1;
        void Line(string label, string value) { ws.Cell(r, 1).Value = label; ws.Cell(r, 2).Value = value; r++; }

        Line("Kontoauszug", st.Title);
        Line("Buchungen", bookings.Count.ToString());
        if (bookings.Count > 0) Line("Laufende Nummern", $"{bookings[0].Number} bis {bookings[^1].Number}");
        Line("Anfangssaldo", st.OpeningBalance is { } o ? o.ToString("N2") : "?");
        Line("Summe Buchungen", st.Sum.ToString("N2"));
        Line("Schlusssaldo", st.ClosingBalance is { } c ? c.ToString("N2") : "?");
        Line("Saldo-Prüfung", st.BalanceOk switch { true => "OK", false => "ABWEICHUNG", _ => "nicht möglich" });
        r++;
        foreach (var g in bookings.GroupBy(b => b.Category).OrderBy(g => g.Key))
            Line(g.Key.Text(), g.Count().ToString());
        r++;
        Line("beleglos eingebucht", bookings.Count(b => b.Beleglos).ToString());
        Line("Abgleich OK", bookings.Count(b => b.Match is { Status: MatchStatus.Ok }).ToString());
        Line("Abgleich prüfen", bookings.Count(b => b.Match is { NeedsReview: true }).ToString());
        Line("Offene Rechnungen", open.Count.ToString());
        ws.Column(1).Width = 26;
        ws.Column(2).Width = 30;
        ws.Column(1).Style.Font.Bold = true;
    }
}
