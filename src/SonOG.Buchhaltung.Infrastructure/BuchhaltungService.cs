using SonOG.Buchhaltung.Core.Classification;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Numbering;
using SonOG.Buchhaltung.Core.Parsing;
using SonOG.Buchhaltung.Core.Rules;
using SonOG.Buchhaltung.Infrastructure.Export;
using SonOG.Buchhaltung.Infrastructure.Pdf;
using SonOG.Buchhaltung.Infrastructure.Scanning;

namespace SonOG.Buchhaltung.Infrastructure;

public sealed class ServiceOptions
{
    public string StatementPath { get; set; } = "";
    public string? InvoiceFolder { get; set; }
    public string? AmazonFolder { get; set; }
    public string OutputFolder { get; set; } = "";
    public DateOnly? OpenFrom { get; set; }
}

/// <summary>Ergebnis des Einlesens. Es wurde noch nichts geschrieben (Vorschau / Dry-Run).</summary>
public sealed class BuchhaltungSession
{
    public ParsedStatement Statement { get; init; } = null!;
    public InvoiceIndex Invoices { get; init; } = null!;
    public ReconcileResult Reconcile { get; init; } = null!;
    public AppRules Rules { get; init; } = null!;
    public string StatementKey { get; init; } = "";
    public int Year => Statement.Year;
    public int FirstNumber { get; set; }
    public List<string> Warnings { get; } = new();
}

public sealed class ExportResult
{
    public string StatementPdf { get; set; } = "";
    public string Excel { get; set; } = "";
    public string? PrintPackage { get; set; }
    public string ReceiptFolder { get; set; } = "";
    public int ReceiptCount { get; set; }
    public string FirstNumber { get; set; } = "";
    public string LastNumber { get; set; } = "";
}

/// <summary>Verbindet Einlesen, Abgleich, Nummernvergabe und Ausgabe. Die UI ruft nur diese Klasse auf.</summary>
public sealed class BuchhaltungService
{
    private readonly string _dataDir;

    public BuchhaltungService(string? dataDir = null)
    {
        _dataDir = dataDir ?? AppSettings.DataDir;
        Directory.CreateDirectory(_dataDir);
    }

    public string RulesPath => Path.Combine(_dataDir, "regeln.json");
    public string StatePath => Path.Combine(_dataDir, "nummern.json");

    // ---------------------------------------------------------------------------------------------
    // Einlesen (schreibt nichts außer dem Cache und - beim ersten Start - einer Standard-regeln.json)
    // ---------------------------------------------------------------------------------------------

    public BuchhaltungSession Load(ServiceOptions o, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(o.StatementPath)) throw new FileNotFoundException("Kontoauszug nicht gefunden.", o.StatementPath);

        var rules = AppRules.Load(RulesPath);
        var layout = new StatementLayout();

        progress?.Report("Kontoauszug lesen ...");
        var pages = PdfWordReader.Read(o.StatementPath, columnBreaks: new[] { layout.ColumnBreakX });
        var statement = StatementParser.Parse(pages, layout);
        if (statement.Bookings.Count == 0)
            throw new InvalidDataException("Im Kontoauszug wurden keine Buchungen gefunden. Layout oder PDF prüfen.");

        var classifier = new BookingClassifier(rules, statement.Year);
        foreach (var b in statement.Bookings)
        {
            classifier.Classify(b);
            rules.ApplyBeleglos(b);
        }

        var key = Path.GetFileNameWithoutExtension(o.StatementPath);
        var state = NumberingState.Load(StatePath);
        var first = state.PeekStart(statement.Year, key);
        AssignNumbers(statement, rules, first);

        var warnings = new List<string>();
        if (statement.BalanceOk == false)
            warnings.Add($"Saldo stimmt nicht: {statement.OpeningBalance:N2} + {statement.Sum:N2} ≠ {statement.ClosingBalance:N2}. Wurden alle Buchungen gelesen?");
        if (statement.BalanceOk is null)
            warnings.Add("Anfangs-/Schlusssaldo nicht gefunden - Saldo-Prüfung nicht möglich.");

        var scanner = new DocumentScanner(Path.Combine(_dataDir, "cache"));

        var index = new InvoiceIndex();
        if (!string.IsNullOrWhiteSpace(o.InvoiceFolder) && Directory.Exists(o.InvoiceFolder))
        {
            progress?.Report("Rechnungsordner lesen ...");
            var scan = scanner.ScanInvoices(o.InvoiceFolder, rules, progress, ct);
            foreach (var r in scan.Items) index.Add(r);
            if (scan.SkippedFiles > 0) warnings.Add($"{scan.SkippedFiles} PDF(s) im Rechnungsordner ohne erkennbare Rechnungsnummer im Dateinamen übersprungen.");
            if (index.Duplicates.Count > 0) warnings.Add($"{index.Duplicates.Count} Rechnungsnummer(n) kommen in mehreren Dateien vor.");
            var unreadable = index.All.Count(i => i.GrossAmount is null);
            if (unreadable > 0) warnings.Add($"Bei {unreadable} Rechnung(en) konnte der Betrag nicht gelesen werden - Regeln im Abschnitt \"rechnung\" in regeln.json anpassen.");
            warnings.AddRange(scan.Problems.Take(5).Select(p => "Rechnung nicht lesbar: " + p));
        }
        else if (statement.Bookings.Any(b => b.Category.IsCustomerPayment()))
        {
            warnings.Add("Kein Rechnungsordner angegeben - Kundenzahlungen können nicht abgeglichen werden.");
        }

        progress?.Report("Zahlungen abgleichen ...");
        var reconciler = new PaymentReconciler(index, statement.Year, state.PaidExcluding(key));
        var reconcile = reconciler.Run(statement.Bookings, o.OpenFrom, statement.LastBookingDate);

        if (!string.IsNullOrWhiteSpace(o.AmazonFolder) && Directory.Exists(o.AmazonFolder))
        {
            progress?.Report("Amazon-Belege lesen ...");
            var receipts = scanner.ScanReceipts(o.AmazonFolder, progress, ct);
            AmazonMatcher.Assign(statement.Bookings, receipts.Items);
            warnings.AddRange(receipts.Problems.Take(5).Select(p => "Beleg nicht lesbar: " + p));
        }
        else
        {
            foreach (var b in statement.Bookings.Where(b => b.Category.IsAmazon() && !b.Beleglos))
                b.ReceiptNote = "Kein Amazon-Belegordner angegeben";
        }

        var session = new BuchhaltungSession
        {
            Statement = statement,
            Invoices = index,
            Reconcile = reconcile,
            Rules = rules,
            StatementKey = key,
            FirstNumber = first,
        };
        session.Warnings.AddRange(warnings);
        return session;
    }

    private static void AssignNumbers(ParsedStatement statement, AppRules rules, int first)
    {
        for (int i = 0; i < statement.Bookings.Count; i++)
            statement.Bookings[i].Number = NumberFormat.Format(rules.NummernFormat, statement.Year, first + i);
    }

    // ---------------------------------------------------------------------------------------------
    // Manuelle Eingriffe
    // ---------------------------------------------------------------------------------------------

    public void AcceptSuggestion(Booking booking) => booking.Match?.Accept(booking);

    public void AttachReceipt(Booking booking, string pdfPath)
    {
        if (!booking.ReceiptFiles.Contains(pdfPath)) booking.ReceiptFiles.Add(pdfPath);
        booking.ReceiptNote = "";
    }

    /// <summary>Gibt die Nummern des Auszugs frei, damit er neu nummeriert werden kann.</summary>
    public bool ReleaseNumbers(BuchhaltungSession session)
    {
        var state = NumberingState.Load(StatePath);
        var released = state.Release(session.Year, session.StatementKey);
        if (released) state.Save(StatePath);
        return released;
    }

    // ---------------------------------------------------------------------------------------------
    // Ausgabe
    // ---------------------------------------------------------------------------------------------

    public ExportResult Export(BuchhaltungSession session, ServiceOptions o, bool buildPrintPackage, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(o.OutputFolder)) throw new InvalidOperationException("Kein Ausgabeordner angegeben.");
        Directory.CreateDirectory(o.OutputFolder);

        var st = session.Statement;
        var bookings = st.Bookings;

        // Nummern endgültig vergeben (idempotent: derselbe Auszug bekommt dieselben Nummern wieder)
        var state = NumberingState.Load(StatePath);
        var start = state.Commit(session.Year, session.StatementKey, bookings.Count);
        if (start != session.FirstNumber)
        {
            AssignNumbers(st, session.Rules, start);
            session.FirstNumber = start;
        }

        var stem = Path.GetFileNameWithoutExtension(o.StatementPath);
        var result = new ExportResult
        {
            StatementPdf = Path.Combine(o.OutputFolder, stem + "_nummeriert.pdf"),
            Excel = Path.Combine(o.OutputFolder, stem + "_zuordnung.xlsx"),
            ReceiptFolder = Path.Combine(o.OutputFolder, "Belege"),
            FirstNumber = bookings[0].Number,
            LastNumber = bookings[^1].Number,
        };

        progress?.Report("Kontoauszug stempeln ...");
        PdfStamper.StampStatement(o.StatementPath, result.StatementPdf, bookings);

        // Belege stempeln (Kopien mit Nummer im Dateinamen)
        Directory.CreateDirectory(result.ReceiptFolder);
        var stampedByBooking = new List<(Booking Booking, string File)>();
        foreach (var b in bookings)
        {
            int k = 1;
            foreach (var src in b.ReceiptFiles.Distinct())
            {
                if (!File.Exists(src)) continue;
                var name = SafeFileName($"{b.Number} {k}_{Path.GetFileName(src)}");
                var dst = Path.Combine(result.ReceiptFolder, name);
                progress?.Report($"Beleg stempeln: {name}");
                PdfStamper.StampReceipt(src, dst, b.Number);
                stampedByBooking.Add((b, dst));
                k++;
            }
        }
        result.ReceiptCount = stampedByBooking.Count;

        progress?.Report("Excel schreiben ...");
        ExcelExporter.Write(result.Excel, st, bookings, session.Reconcile.OpenInvoices);

        if (buildPrintPackage)
        {
            progress?.Report("Druckpaket zusammenstellen ...");
            result.PrintPackage = Path.Combine(o.OutputFolder, stem + "_druckpaket.pdf");
            var order = new List<string> { result.StatementPdf };
            order.AddRange(stampedByBooking.OrderBy(x => bookings.IndexOf(x.Booking)).Select(x => x.File));
            PdfStamper.Merge(order, result.PrintPackage);
        }

        // Erst jetzt, nach erfolgreicher Ausgabe: Nummern und bezahlte Rechnungen dauerhaft vermerken.
        foreach (var b in bookings.Where(b => b.Match is { CountsAsPaid: true }))
            foreach (var inv in b.Match!.Invoices)
                state.MarkPaid(inv.Number, b.Number, session.StatementKey);
        state.Save(StatePath);

        return result;
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
