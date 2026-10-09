namespace SonOG.Buchhaltung.Core.Models;

public sealed class ParsedStatement
{
    public string Title { get; init; } = "";
    public int Year { get; init; }
    public int Month { get; init; }
    public decimal? OpeningBalance { get; init; }
    public decimal? ClosingBalance { get; init; }
    public List<Booking> Bookings { get; init; } = new();

    public decimal Sum => Bookings.Sum(b => b.Amount);

    /// <summary>Anfangssaldo + Summe der Buchungen = Schlusssaldo. Null, wenn Salden nicht gelesen wurden.</summary>
    public bool? BalanceOk =>
        OpeningBalance is null || ClosingBalance is null ? null : OpeningBalance + Sum == ClosingBalance;

    public DateOnly? LastBookingDate => Bookings.Count == 0 ? null : Bookings.Max(b => b.Date);
}
