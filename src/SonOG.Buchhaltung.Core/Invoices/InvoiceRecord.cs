namespace SonOG.Buchhaltung.Core.Invoices;

/// <summary>Eine Ausgangsrechnung aus dem Rechnungsordner.</summary>
public sealed record InvoiceRecord(string Number, string FilePath, decimal? GrossAmount, string Recipient, DateOnly? Date)
{
    /// <summary>Erste nicht leere Zeile des Empfängerblocks (meist der Name).</summary>
    public string RecipientFirstLine =>
        Recipient.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}

public sealed class InvoiceIndex
{
    private readonly Dictionary<string, InvoiceRecord> _byNumber = new();
    private readonly List<string> _duplicates = new();

    public int Count => _byNumber.Count;
    public IEnumerable<InvoiceRecord> All => _byNumber.Values;

    /// <summary>Rechnungsnummern, die in mehreren Dateien vorkommen (z. B. Kopien). Es zählt die erste Datei mit lesbarem Betrag.</summary>
    public IReadOnlyList<string> Duplicates => _duplicates;

    public void Add(InvoiceRecord record)
    {
        if (_byNumber.TryGetValue(record.Number, out var existing))
        {
            _duplicates.Add(record.Number);
            if (existing.GrossAmount is null && record.GrossAmount is not null)
                _byNumber[record.Number] = record;
            return;
        }
        _byNumber[record.Number] = record;
    }

    public bool TryGet(string number, out InvoiceRecord record) => _byNumber.TryGetValue(number, out record!);
}
