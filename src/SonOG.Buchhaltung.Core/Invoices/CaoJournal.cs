namespace SonOG.Buchhaltung.Core.Invoices;

/// <summary>Eine Rechnung aus der CAO-Faktura-Tabelle JOURNAL (VRENUM, KUN_NUM, KUN_NAME1, BSUMME, RDATUM, Anschrift).</summary>
public sealed record CaoRechnung(string Nummer, string Kundennummer, string Name, decimal? Brutto, DateOnly? Datum)
{
    public string Name2 { get; init; } = "";
    public string Strasse { get; init; } = "";
    public string Plz { get; init; } = "";
    public string Ort { get; init; } = "";
}

/// <summary>Ergänzt die aus den PDFs gelesenen Rechnungen um die Angaben aus CAO (Kunde, Betrag, Datum).</summary>
public static class CaoAbgleich
{
    /// <summary>
    /// CAO-Angaben gehen vor: Kundennummer, Kundenname (erste Empfängerzeile), Bruttobetrag und Rechnungsdatum.
    /// Was in CAO leer ist, bleibt wie aus dem PDF gelesen.
    /// </summary>
    public static InvoiceRecord Anwenden(InvoiceRecord r, CaoRechnung c)
    {
        var recipient = r.Recipient;
        if (c.Name.Trim().Length > 0)
        {
            // Name aus CAO als erste Zeile, die übrige Anschrift aus dem PDF (doppelte Namenszeile weglassen)
            var rest = r.Recipient.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !string.Equals(l, c.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            recipient = string.Join("\n", new[] { c.Name.Trim() }.Concat(rest));
        }
        return r with
        {
            Kundennummer = c.Kundennummer.Trim().Length > 0 ? c.Kundennummer.Trim() : r.Kundennummer,
            Recipient = recipient,
            GrossAmount = c.Brutto ?? r.GrossAmount,
            Date = c.Datum ?? r.Date,
            AusCao = true,
            CaoKunde = c,
        };
    }

    /// <summary>Wendet alle passenden CAO-Rechnungen an. Liefert die Anzahl der ergänzten Rechnungen.</summary>
    public static int Anwenden(List<InvoiceRecord> rechnungen, IReadOnlyDictionary<string, CaoRechnung> cao)
    {
        int n = 0;
        for (int i = 0; i < rechnungen.Count; i++)
        {
            if (!cao.TryGetValue(rechnungen[i].Number, out var c)) continue;
            rechnungen[i] = Anwenden(rechnungen[i], c);
            n++;
        }
        return n;
    }
}
