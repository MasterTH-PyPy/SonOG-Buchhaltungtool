using SonOG.Buchhaltung.Core.Classification;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Numbering;
using SonOG.Buchhaltung.Core.Parsing;
using SonOG.Buchhaltung.Core.Rules;
using SonOG.Buchhaltung.Infrastructure.Export;
using SonOG.Buchhaltung.Infrastructure.Mail;
using SonOG.Buchhaltung.Infrastructure.Pdf;
using SonOG.Buchhaltung.Infrastructure.Scanning;

namespace SonOG.Buchhaltung.Infrastructure;

public sealed class ServiceOptions
{
    public string StatementPath { get; set; } = "";
    public string? InvoiceFolder { get; set; }
    public string? EingangFolder { get; set; }
    public string OutputFolder { get; set; } = "";
    public DateOnly? OpenFrom { get; set; }
    public List<MailAccount> MailAccounts { get; set; } = new();
    public bool FetchMail { get; set; }
    public int NumberFrom { get; set; } = 1;
    public int MailDaysBefore { get; set; } = 60;
    public int MailDaysAfter { get; set; } = 30;
}

/// <summary>Ergebnis des Einlesens. Es wurde noch nichts geschrieben (Vorschau / Dry-Run).</summary>
public sealed class BuchhaltungSession
{
    public ParsedStatement Statement { get; init; } = null!;
    public InvoiceIndex Invoices { get; set; } = null!;
    public ReconcileResult Reconcile { get; set; } = null!;
    public string StatementPath { get; init; } = "";
    /// <summary>Stabile Schlüssel der Buchungen für gespeicherte manuelle Zuordnungen.</summary>
    public Dictionary<Booking, string> ManualKeys { get; init; } = new();
    /// <summary>Warnungen, die nur vom Kontoauszug abhängen (Saldo, Nummernkreis); die übrigen entstehen beim Dokumentenabgleich.</summary>
    public List<string> BaseWarnings { get; } = new();
    public AppRules Rules { get; init; } = null!;
    public string StatementKey { get; init; } = "";
    public int Year => Statement.Year;
    public int FirstNumber { get; set; }
    public int NumberFrom { get; init; } = 1;
    public int NumberTo { get; init; } = int.MaxValue;
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
        Taxpool = new TaxpoolService(_dataDir);
    }

    /// <summary>Schritt 2/3 (Kontieren, Taxpool-Importdatei) und Sollstellung.</summary>
    public TaxpoolService Taxpool { get; }

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
        int numFrom = Math.Max(1, o.NumberFrom), numTo = int.MaxValue;
        var first = state.PeekStart(statement.Year, key, numFrom);
        AssignNumbers(statement, rules, first);

        var warnings = new List<string>();
        if ((long)first + statement.Bookings.Count - 1 > numTo)
            warnings.Add($"Nummernkreis zu klein: ab {first} bis {numTo} passen nur {Math.Max(0, numTo - first + 1)} Nummern, der Auszug hat {statement.Bookings.Count} Buchungen. " +
                         "Der Export wird verweigert, bitte den Nummernkreis (von/bis) anpassen.");
        if (statement.BalanceOk == false)
            warnings.Add($"Saldo stimmt nicht: {statement.OpeningBalance:N2} + {statement.Sum:N2} ≠ {statement.ClosingBalance:N2}. Wurden alle Buchungen gelesen?");
        if (statement.BalanceOk is null)
            warnings.Add("Anfangs-/Schlusssaldo nicht gefunden - Saldo-Prüfung nicht möglich.");

        var session = new BuchhaltungSession
        {
            Statement = statement,
            Rules = rules,
            StatementKey = key,
            StatementPath = o.StatementPath,
            FirstNumber = first,
            NumberFrom = numFrom,
            NumberTo = numTo,
            ManualKeys = ManualAssignments.Keys(statement.Bookings),
        };
        session.BaseWarnings.AddRange(warnings);
        Rematch(session, o, progress, ct);
        return session;
    }

    private string ManualPath(BuchhaltungSession s) => Path.Combine(_dataDir, "zuordnungen", s.StatementKey + ".json");

    /// <summary>
    /// Liest Rechnungsordner, Eingangsordner und Mails neu ein und gleicht neu ab - ohne den Kontoauszug neu zu lesen und ohne die
    /// Nummern anzutasten. Manuelle Zuordnungen (gespeichert je Auszug) werden danach wieder angewendet.
    /// </summary>
    public void Rematch(BuchhaltungSession session, ServiceOptions o, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var statement = session.Statement;
        var rules = session.Rules;
        var key = session.StatementKey;
        var state = NumberingState.Load(StatePath);
        var warnings = session.Warnings;
        warnings.Clear();
        warnings.AddRange(session.BaseWarnings);

        foreach (var b in statement.Bookings)
        {
            b.Match = null;
            b.ReceiptFiles = new List<string>();
            b.ReceiptNote = "";
        }

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

        MailReceiptFinder.DaysBefore = Math.Clamp(o.MailDaysBefore, 0, 730);
        MailReceiptFinder.DaysAfter = Math.Clamp(o.MailDaysAfter, 0, 730);
        var mailLog = new List<string>();
        var mailProblems = new List<string>();
        var mailDir = MailReceiptFinder.MailFolder(_dataDir);
        if (o.FetchMail && o.MailAccounts.Count > 0)
        {
            // Alle PDF-Anhänge im Zeitraum laden; sie werden danach wie der Eingangsordner nach Inhalt abgeglichen.
            var from = statement.Bookings.Min(b => b.Date).AddDays(-MailReceiptFinder.DaysBefore);
            var to = (statement.LastBookingDate ?? DateOnly.FromDateTime(DateTime.Today)).AddDays(MailReceiptFinder.DaysAfter);
            progress?.Report("Mail-Anhänge laden ...");
            var dl = MailDownloader.Run(o.MailAccounts, from, to, mailDir, mailProblems, mailLog, progress, ct);
            warnings.Add($"Mail: {dl.MailsChecked} Mails geprüft, {dl.Downloaded} PDF-Anhang/Anhänge neu geladen ({dl.AlreadyThere} schon vorhanden).");
        }

        var receiptFolders = new List<string>();
        if (o.FetchMail && Directory.Exists(mailDir) && Directory.EnumerateFiles(mailDir, "*.pdf", SearchOption.AllDirectories).Any())
            receiptFolders.Add(mailDir);
        if (!string.IsNullOrWhiteSpace(o.EingangFolder) && Directory.Exists(o.EingangFolder)) receiptFolders.Add(o.EingangFolder);

        if (receiptFolders.Count > 0)
        {
            progress?.Report("Belege lesen ...");
            var allReceipts = new List<ReceiptDocument>();
            foreach (var folder in receiptFolders)
            {
                var receipts = scanner.ScanReceipts(folder, progress, ct);
                allReceipts.AddRange(receipts.Items);
                warnings.AddRange(receipts.Problems.Take(5).Select(p => "Beleg nicht lesbar: " + p));
            }
            var assigned = AmazonMatcher.Assign(statement.Bookings, allReceipts);
            InboundMatcher.Assign(statement.Bookings, allReceipts);
            var scans = allReceipts.Where(r => r.IsScan).ToList();
            if (scans.Count > 0)
                warnings.Add($"{scans.Count} PDF(s) im Eingangsordner sind Scans ohne Text und können nicht automatisch gelesen werden (OCR fehlt): " +
                             string.Join(", ", scans.Take(3).Select(r => Path.GetFileName(r.FilePath))) + (scans.Count > 3 ? " ..." : ""));
            if (assigned.UnusedDocuments.Count > 0)
                warnings.Add($"{assigned.UnusedDocuments.Count} Beleg(e) im Eingangsordner gehören zu keiner Buchung dieses Auszugs (z. B. andere Monate).");
        }
        else
        {
            foreach (var b in statement.Bookings.Where(b => b.Category.IsAmazon() && !b.Beleglos))
                b.ReceiptNote = "Kein Ordner für Eingangsrechnungen angegeben";
        }

        if (o.FetchMail && o.MailAccounts.Count > 0)
        {
            var logPath = Path.Combine(_dataDir, "mail-protokoll.txt");
            try { File.WriteAllLines(logPath, mailLog.Concat(mailProblems)); } catch (IOException) { }
            warnings.AddRange(mailProblems.Take(5));
            warnings.Add("Mail-Protokoll: " + logPath);
        }


        session.Invoices = index;
        session.Reconcile = reconcile;

        var manual = ManualAssignments.Load(ManualPath(session));
        int applied = manual.Apply(session.ManualKeys);
        if (applied > 0) warnings.Add($"{applied} manuelle Zuordnung(en) wieder angewendet.");
    }

    /// <summary>Speichert den Belegstand der Buchung(en) als manuellen Eingriff, damit er beim erneuten Einlesen bleibt.</summary>
    public void SaveManual(BuchhaltungSession session, params Booking[] bookings)
    {
        var path = ManualPath(session);
        var manual = ManualAssignments.Load(path);
        foreach (var b in bookings)
            if (session.ManualKeys.TryGetValue(b, out var k)) manual.Record(k, b);
        manual.Save(path);
    }

    private static void AssignNumbers(ParsedStatement statement, AppRules rules, int first)
    {
        for (int i = 0; i < statement.Bookings.Count; i++)
            statement.Bookings[i].Number = NumberFormat.Format(rules.NummernFormat, statement.Year, first + i);
    }

    // ---------------------------------------------------------------------------------------------
    // Manuelle Eingriffe
    // ---------------------------------------------------------------------------------------------

    public void AcceptSuggestion(BuchhaltungSession session, Booking booking)
    {
        booking.Match?.Accept(booking);
        SaveManual(session, booking);
    }

    public void AttachReceipt(BuchhaltungSession session, Booking booking, string pdfPath)
    {
        if (!booking.ReceiptFiles.Contains(pdfPath)) booking.ReceiptFiles.Add(pdfPath);
        booking.ReceiptNote = "Manuell zugeordnet";
        SaveManual(session, booking);
    }

    /// <summary>Entfernt einen (z. B. falsch zugeordneten) Beleg von der Buchung und merkt sich das.</summary>
    public void RemoveReceipt(BuchhaltungSession session, Booking booking, string pdfPath)
    {
        booking.ReceiptFiles.RemoveAll(f => string.Equals(f, pdfPath, StringComparison.OrdinalIgnoreCase));
        booking.ReceiptNote = "Beleg manuell entfernt: " + Path.GetFileName(pdfPath);
        SaveManual(session, booking);
    }

    /// <summary>True, wenn die Nummern des Auszugs in Schritt 1 endgültig vergeben wurden (Export erfolgt).</summary>
    public bool NummernEndgueltig(BuchhaltungSession session) =>
        NumberingState.Load(StatePath).IsCommitted(session.Year, session.StatementKey, session.FirstNumber, session.Statement.Bookings.Count);

    /// <summary>Liest den Ordner der Ausgangsrechnungen (für die Sollstellung; nutzt den Cache).</summary>
    public InvoiceIndex ScanInvoices(string folder, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Ordner der Ausgangsrechnungen nicht gefunden: " + folder);
        var rules = AppRules.Load(RulesPath);
        var scanner = new DocumentScanner(Path.Combine(_dataDir, "cache"));
        var scan = scanner.ScanInvoices(folder, rules, progress, ct);
        var index = new InvoiceIndex();
        foreach (var r in scan.Items) index.Add(r);
        return index;
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
        var start = state.Commit(session.Year, session.StatementKey, bookings.Count, session.NumberFrom, session.NumberTo);
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

        // Seiten hinter der letzten Buchungsseite (Anlage "Entgeltabrechnung") gehören zur Entgelt-Buchung:
        // Sie bekommen deren Nummer und werden im Druckpaket direkt hinter der Seite dieser Buchung einsortiert.
        int lastBookingPage = bookings.Max(b => b.PageIndex);
        int pageCount = PdfStamper.PageCount(result.StatementPdf);
        var extraPages = Enumerable.Range(lastBookingPage + 1, Math.Max(0, pageCount - lastBookingPage - 1)).ToList();
        var typeRules = session.Rules.BelegloseTypen.Where(t => t.Typ.Length > 0).ToList();
        var feeBooking = bookings.LastOrDefault(b => typeRules.Any(t => b.Type.StartsWith(t.Typ, StringComparison.OrdinalIgnoreCase)));
        if (extraPages.Count > 0 && feeBooking is not null)
            PdfStamper.StampPageLabels(result.StatementPdf, extraPages.ToDictionary(i => i, _ => feeBooking.Number));

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
            // Rückwärts nach Auszugsseiten: letzte Seite zuerst, danach deren Belege (in Buchungsreihenfolge),
            // dann die vorletzte Seite mit ihren Belegen usw. So liegt der Stapel nach dem Druck richtig herum.
            var items = new List<(string File, int? Page)>();
            if (feeBooking is null) // keine Zuordnung möglich: Anlageseiten wie normale Seiten ganz am Ende des Auszugs, also zuerst
                foreach (var i in extraPages.OrderByDescending(i => i)) items.Add((result.StatementPdf, i));
            foreach (var pageGroup in bookings.GroupBy(b => b.PageIndex).OrderByDescending(g => g.Key))
            {
                items.Add((result.StatementPdf, pageGroup.Key));
                foreach (var x in stampedByBooking.Where(x => x.Booking.PageIndex == pageGroup.Key).OrderBy(x => bookings.IndexOf(x.Booking)))
                    items.Add((x.File, null));
                if (feeBooking is not null && pageGroup.Contains(feeBooking))
                    foreach (var i in extraPages) items.Add((result.StatementPdf, i));
            }
            PdfStamper.MergePages(items, result.PrintPackage);
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
