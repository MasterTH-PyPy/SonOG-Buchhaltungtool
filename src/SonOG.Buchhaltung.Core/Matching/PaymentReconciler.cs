using System.Globalization;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Util;

namespace SonOG.Buchhaltung.Core.Matching;

public sealed class ReconcileResult
{
    /// <summary>Rechnungen aus dem Ordner ohne erkannten Zahlungseingang (für Mahnungen).</summary>
    public List<InvoiceRecord> OpenInvoices { get; init; } = new();
}

/// <summary>
/// Gleicht Kundenzahlungen aus dem Kontoauszug mit den Ausgangsrechnungen ab:
/// Einzelzahlung (Betrag + Empfänger), Sammelzahlung (Summe), Zahlendreher in Nummer oder Betrag.
/// </summary>
public sealed class PaymentReconciler
{
    private const int MaxPool = 40;
    private const int MaxSolutions = 50;
    private const int MaxNodes = 300_000;

    private readonly InvoiceIndex _index;
    private readonly int _year;
    private readonly Dictionary<string, string> _paid;

    /// <param name="alreadyPaid">Rechnungsnummer -> Buchungsnummer aus früheren Auszügen.</param>
    public PaymentReconciler(InvoiceIndex index, int statementYear, IReadOnlyDictionary<string, string>? alreadyPaid = null)
    {
        _index = index;
        _year = statementYear;
        _paid = alreadyPaid is null ? new() : new Dictionary<string, string>(alreadyPaid);
    }

    public ReconcileResult Run(IEnumerable<Booking> bookings, DateOnly? openFrom = null, DateOnly? openUntil = null)
    {
        foreach (var b in bookings)
        {
            b.Match = MatchOne(b);
            if (b.Category.IsCustomerPayment() || b.Category == BookingCategory.EingangSonstige)
                b.ReceiptFiles.Clear();
            if (b.Match is { AttachInvoices: true })
                foreach (var inv in b.Match.Invoices)
                    if (!b.ReceiptFiles.Contains(inv.FilePath)) b.ReceiptFiles.Add(inv.FilePath);

            if (b.Match is { CountsAsPaid: true })
                foreach (var inv in b.Match.Invoices)
                    _paid[inv.Number] = b.Number;
        }

        var open = _index.All
            .Where(i => !_paid.ContainsKey(i.Number))
            .Where(i => i.Date is null || ((openFrom is null || i.Date >= openFrom) && (openUntil is null || i.Date <= openUntil)))
            .OrderBy(i => i.Number, StringComparer.Ordinal)
            .ToList();
        return new ReconcileResult { OpenInvoices = open };
    }

    /// <summary>Rechnungen, die in diesem Lauf (und früheren Läufen) als bezahlt gelten.</summary>
    public IReadOnlyDictionary<string, string> PaidInvoices => _paid;

    // ---------------------------------------------------------------------------------------------

    private PaymentMatch? MatchOne(Booking b)
    {
        if (b.Beleglos) return null;
        if (b.Category.IsCustomerPayment()) return MatchNumbers(b);
        if (b.Category == BookingCategory.EingangSonstige) return MatchWithoutNumber(b);
        return null;
    }

    private PaymentMatch MatchNumbers(Booking b)
    {
        var found = new List<InvoiceRecord>();
        var missing = new List<string>();
        foreach (var n in b.OwnInvoiceNumbers)
        {
            if (TryResolve(n, b.NumbersDerivedFromShortForm, out var inv))
            {
                if (!found.Contains(inv)) found.Add(inv);
            }
            else missing.Add(n);
        }
        return missing.Count > 0 ? MatchWithMissing(b, found, missing) : MatchAllFound(b, found);
    }

    private bool TryResolve(string number, bool derived, out InvoiceRecord invoice)
    {
        if (_index.TryGet(number, out invoice!)) return true;
        if (derived && number.Length > 4)
        {
            var alt = (_year - 1).ToString(CultureInfo.InvariantCulture) + number[4..];
            if (_index.TryGet(alt, out invoice!)) return true;
        }
        invoice = null!;
        return false;
    }

    private PaymentMatch MatchAllFound(Booking b, List<InvoiceRecord> found)
    {
        var m = new PaymentMatch { Invoices = found, AttachInvoices = true };

        if (found.Any(i => i.GrossAmount is null))
        {
            m.Status = MatchStatus.BetragNichtLesbar;
            m.Note = "Rechnungsbetrag nicht aus dem PDF lesbar: " + string.Join(", ", found.Where(i => i.GrossAmount is null).Select(i => i.Number)) +
                     ". Regeln für den Rechnungsbetrag in regeln.json prüfen.";
            return m;
        }

        decimal sum = found.Sum(i => i.GrossAmount!.Value);
        m.InvoiceSum = sum;
        m.Difference = b.Amount - sum;

        if (sum == b.Amount)
        {
            var already = found.Where(i => _paid.ContainsKey(i.Number)).ToList();
            if (already.Count > 0)
            {
                m.Status = MatchStatus.BereitsBezahlt;
                m.AttachInvoices = false;
                m.Note = "Bereits bezahlt mit Buchung " + string.Join(", ", already.Select(i => $"{_paid[i.Number]} ({i.Number})")) +
                         " - Doppelzahlung?";
                return m;
            }

            if (PayerCheck(b, found) == false)
            {
                m.Status = MatchStatus.EmpfaengerPruefen;
                m.Note = $"Zahler „{b.PayerName}“ passt nicht zum Rechnungsempfänger „{found[0].RecipientFirstLine}“.";
                return m;
            }
            m.Status = MatchStatus.Ok;
            return m;
        }

        string diffText = $"Zahlung {Fmt.Money(b.Amount)} € - Rechnung {Fmt.Money(sum)} € = {Fmt.Money(b.Amount - sum)} €";

        if (found.Count == 1)
        {
            if (NumberVariants.AmountsDifferByTransposition(sum, b.Amount))
            {
                m.Status = MatchStatus.ZahlendreherVorschlag;
                m.Note = $"Betrag sieht nach Zahlendreher aus: Rechnung {Fmt.Money(sum)} €, gezahlt {Fmt.Money(b.Amount)} €.";
                return m;
            }

            var alt = NumberVariantsWithAmount(b, found[0].Number, b.Amount).FirstOrDefault();
            if (alt is not null)
            {
                m.Status = MatchStatus.ZahlendreherVorschlag;
                m.AttachInvoices = false;
                m.Invoices = new List<InvoiceRecord> { alt };
                m.Note = $"Rechnung {found[0].Number} hat {Fmt.Money(sum)} €, gezahlt wurden {Fmt.Money(b.Amount)} €. " +
                         $"Rechnung {alt.Number} ({alt.RecipientFirstLine}) hat genau diesen Betrag - Zahlendreher in der Nummer?";
                return m;
            }

            m.Status = MatchStatus.BetragWeichtAb;
            m.Note = diffText + SkontoHint(b.Amount - sum, sum);

            // Kunde hat evtl. mehrere Rechnungen bezahlt, aber nur eine genannt.
            var extra = FindSubsets(b, found, b.Amount).FirstOrDefault(s => s.Contains(found[0]) && s.Count > 1);
            if (extra is not null) m.Note += " " + DescribeSubset(found, extra);
            return m;
        }

        m.Status = MatchStatus.SummeStimmtNicht;
        m.Note = $"Summe der {found.Count} genannten Rechnungen {Fmt.Money(sum)} € ≠ Zahlung {Fmt.Money(b.Amount)} € " +
                 $"(Differenz {Fmt.Money(b.Amount - sum)} €).";
        var best = FindSubsets(b, found, b.Amount);
        if (best.Count > 0)
            m.Note += " " + DescribeSubset(found, best[0]);
        return m;
    }

    private PaymentMatch MatchWithMissing(Booking b, List<InvoiceRecord> found, List<string> missing)
    {
        var m = new PaymentMatch { Invoices = found.ToList(), AttachInvoices = found.Count > 0 };

        var candidates = missing.Select(n => Candidates(b, n)).ToList();

        if (found.All(i => i.GrossAmount is not null) && candidates.All(c => c.Count > 0))
        {
            var combo = FindCombination(found.Sum(i => i.GrossAmount!.Value), candidates, b.Amount);
            if (combo is not null)
            {
                m.Status = MatchStatus.ZahlendreherVorschlag;
                m.Invoices = found.Concat(combo).ToList();
                m.InvoiceSum = b.Amount;
                m.Difference = 0;
                m.AttachInvoices = false;
                m.Note = "Nummer " + string.Join(", ", missing) + " nicht im Ordner - vermutlich Zahlendreher: " +
                         string.Join(", ", combo.Select(c => c.Number)) + " (Betrag stimmt).";
                return m;
            }
        }

        m.Status = MatchStatus.RechnungNichtImOrdner;
        m.Note = "Nicht im Rechnungsordner: " + string.Join(", ", missing) + ".";
        var similar = candidates.SelectMany(c => c).Take(3).ToList();
        if (similar.Count > 0)
            m.Note += " Ähnliche Nummern: " + string.Join("; ", similar.Select(c =>
                $"{c.Number} ({(c.GrossAmount is null ? "Betrag ?" : Fmt.Money(c.GrossAmount.Value) + " €")}, {c.RecipientFirstLine})")) + ".";
        return m;
    }

    private PaymentMatch? MatchWithoutNumber(Booking b)
    {
        var hits = _index.All
            .Where(i => !_paid.ContainsKey(i.Number) && i.GrossAmount == b.Amount)
            .Where(i => i.RecipientFirstLine.Length > 0 && NameMatcher.ContainsAnyToken(b.Text, i.RecipientFirstLine))
            .Take(2).ToList();

        if (hits.Count == 1)
        {
            return new PaymentMatch
            {
                Status = MatchStatus.VorschlagUeberBetrag,
                Invoices = hits,
                InvoiceSum = hits[0].GrossAmount,
                Difference = 0,
                AttachInvoices = false,
                Note = $"Keine Rechnungsnummer im Verwendungszweck. Betrag und Name passen zu Rechnung {hits[0].Number} ({hits[0].RecipientFirstLine}).",
            };
        }
        return new PaymentMatch
        {
            Status = MatchStatus.KeineRechnungsnummer,
            Note = hits.Count > 1
                ? "Keine Rechnungsnummer; mehrere Rechnungen passen nach Betrag und Name."
                : "Eingang ohne erkennbaren Rechnungsbezug. Falls kein Kundeneingang: in regeln.json als beleglos eintragen.",
        };
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>True/false, ob der Zahler zum Rechnungsempfänger passt. Null, wenn nicht entscheidbar.</summary>
    private static bool? PayerCheck(Booking b, List<InvoiceRecord> invoices)
    {
        if (string.IsNullOrWhiteSpace(b.PayerName)) return null;
        if (invoices.Any(i => string.IsNullOrWhiteSpace(i.Recipient))) return null;
        return invoices.Any(i => NameMatcher.Score(b.PayerName, i.Recipient) >= 0.5);
    }

    private static string SkontoHint(decimal diff, decimal invoiceSum)
    {
        if (diff >= 0 || invoiceSum == 0) return "";
        var pct = Math.Abs(diff) / invoiceSum * 100m;
        return pct is >= 0.5m and <= 4m ? $" (ggf. Skonto, {pct.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',')} %)" : "";
    }

    /// <summary>Rechnungen mit ähnlicher Nummer, die genau den gezahlten Betrag haben und zum Zahler passen.</summary>
    private IEnumerable<InvoiceRecord> NumberVariantsWithAmount(Booking b, string number, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(b.PayerName)) yield break;
        foreach (var v in NumberVariants.Of(number))
        {
            if (_index.TryGet(v, out var c) && c.GrossAmount == amount && !_paid.ContainsKey(c.Number) &&
                NameMatcher.Score(b.PayerName, c.Recipient) >= 0.5)
                yield return c;
        }
    }

    /// <summary>Rechnungen mit ähnlicher Nummer (Zahlendreher, Tippfehler), bei bekanntem Zahler nur passende.</summary>
    private List<InvoiceRecord> Candidates(Booking b, string missingNumber)
    {
        var list = new List<InvoiceRecord>();
        foreach (var v in NumberVariants.Of(missingNumber))
        {
            if (!_index.TryGet(v, out var c) || _paid.ContainsKey(c.Number)) continue;
            if (!string.IsNullOrWhiteSpace(b.PayerName) && c.Recipient.Length > 0 && NameMatcher.Score(b.PayerName, c.Recipient) < 0.5) continue;
            list.Add(c);
            if (list.Count >= 5) break;
        }
        return list;
    }

    private static List<InvoiceRecord>? FindCombination(decimal baseSum, List<List<InvoiceRecord>> candidates, decimal target)
    {
        var chosen = new InvoiceRecord[candidates.Count];
        List<InvoiceRecord>? result = null;

        bool Rec(int i, decimal sum)
        {
            if (i == candidates.Count)
            {
                if (sum != target) return false;
                result = chosen.ToList();
                return true;
            }
            foreach (var c in candidates[i])
            {
                if (c.GrossAmount is null) continue;
                chosen[i] = c;
                if (Rec(i + 1, sum + c.GrossAmount.Value)) return true;
            }
            return false;
        }

        Rec(0, baseSum);
        return result;
    }

    /// <summary>
    /// Sucht Rechnungskombinationen desselben Kunden, deren Summe genau der Zahlung entspricht.
    /// Bevorzugt Lösungen, die sich möglichst wenig von den genannten Rechnungen unterscheiden.
    /// </summary>
    private List<List<InvoiceRecord>> FindSubsets(Booking b, List<InvoiceRecord> named, decimal target)
    {
        var pool = named.Where(i => i.GrossAmount is > 0).ToList();
        var refName = named.Select(n => n.RecipientFirstLine).FirstOrDefault(s => s.Length > 0) ?? "";

        var others = new List<InvoiceRecord>();
        foreach (var inv in _index.All)
        {
            if (pool.Contains(inv) || _paid.ContainsKey(inv.Number) || inv.GrossAmount is null or <= 0) continue;
            bool sameCustomer =
                (b.PayerName.Length > 0 && NameMatcher.Score(b.PayerName, inv.Recipient) >= 0.5) ||
                (refName.Length > 0 && NameMatcher.Score(refName, inv.Recipient) >= 0.5);
            if (sameCustomer) others.Add(inv);
        }

        if (others.Count > MaxPool - pool.Count)
        {
            var anchor = long.TryParse(named[0].Number, out var a) ? a : 0;
            others = others
                .OrderBy(i => long.TryParse(i.Number, out var n) ? Math.Abs(n - anchor) : long.MaxValue)
                .Take(Math.Max(0, MaxPool - pool.Count)).ToList();
        }
        pool.AddRange(others);

        var items = pool
            .Select(i => (inv: i, cents: (long)Math.Round(i.GrossAmount!.Value * 100)))
            .OrderByDescending(x => x.cents).ToList();
        var suffix = new long[items.Count + 1];
        for (int i = items.Count - 1; i >= 0; i--) suffix[i] = suffix[i + 1] + items[i].cents;

        long targetCents = (long)Math.Round(target * 100);
        var solutions = new List<List<InvoiceRecord>>();
        int nodes = 0;

        void Dfs(int idx, long remaining, List<int> pick)
        {
            if (solutions.Count >= MaxSolutions || ++nodes > MaxNodes) return;
            if (remaining == 0)
            {
                if (pick.Count > 0) solutions.Add(pick.Select(p => items[p].inv).ToList());
                return;
            }
            if (idx == items.Count || suffix[idx] < remaining) return;
            if (items[idx].cents <= remaining)
            {
                pick.Add(idx);
                Dfs(idx + 1, remaining - items[idx].cents, pick);
                pick.RemoveAt(pick.Count - 1);
            }
            Dfs(idx + 1, remaining, pick);
        }

        Dfs(0, targetCents, new List<int>());

        return solutions
            .OrderBy(s => s.Count(i => !named.Contains(i)) + named.Count(i => !s.Contains(i)))
            .ThenBy(s => s.Count)
            .Take(2).ToList();
    }

    private static string DescribeSubset(List<InvoiceRecord> named, List<InvoiceRecord> subset)
    {
        var added = subset.Where(i => !named.Contains(i)).Select(i => i.Number).ToList();
        var removed = named.Where(i => !subset.Contains(i)).Select(i => i.Number).ToList();
        var parts = new List<string>();
        if (removed.Count > 0) parts.Add("ohne " + string.Join(", ", removed));
        if (added.Count > 0) parts.Add("zusätzlich " + string.Join(", ", added));
        return parts.Count == 0
            ? "Würde stimmen mit: " + string.Join(", ", subset.Select(i => i.Number)) + "."
            : "Würde stimmen " + string.Join(" und ", parts) + ".";
    }
}
