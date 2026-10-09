using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Rules;
using SonOG.Buchhaltung.Infrastructure.Pdf;

namespace SonOG.Buchhaltung.Infrastructure.Scanning;

public sealed class ScanResult<T>
{
    public List<T> Items { get; init; } = new();
    public List<string> Problems { get; init; } = new();
    public int SkippedFiles { get; set; }
}

/// <summary>
/// Liest Ausgangsrechnungen (CAO-Rechnungsordner) und Amazon-Belege ein. Ergebnisse werden pro Datei
/// (Pfad, Änderungsdatum, Größe) zwischengespeichert, damit ein erneuter Lauf nicht alle PDFs neu lesen muss.
/// </summary>
public sealed class DocumentScanner
{
    private sealed class InvoiceCacheEntry
    {
        public long Ticks { get; set; }
        public long Size { get; set; }
        public decimal? Gross { get; set; }
        public string Recipient { get; set; } = "";
        public string? Date { get; set; }
    }

    private sealed class ReceiptCacheEntry
    {
        public long Ticks { get; set; }
        public long Size { get; set; }
        public List<string> Orders { get; set; } = new();
    }

    private readonly string _cacheDir;

    public DocumentScanner(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public ScanResult<InvoiceRecord> ScanInvoices(string folder, AppRules rules, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var numberRx = new Regex(rules.EigeneRechnungsnummerRegex);
        var cachePath = Path.Combine(_cacheDir, "rechnungen.cache.json");
        var cache = LoadCache<InvoiceCacheEntry>(cachePath);
        var fresh = new ConcurrentDictionary<string, InvoiceCacheEntry>();

        var result = new ScanResult<InvoiceRecord>();
        var files = Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories).ToList();
        var records = new ConcurrentBag<InvoiceRecord>();
        var problems = new ConcurrentBag<string>();
        int skipped = 0, done = 0;

        Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, file =>
        {
            var number = InvoiceFileName.TryGetNumber(file, numberRx);
            if (number is null) { Interlocked.Increment(ref skipped); return; }

            var info = new FileInfo(file);
            InvoiceCacheEntry entry;
            if (cache.TryGetValue(file, out var cached) && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Size == info.Length)
            {
                entry = cached;
            }
            else
            {
                try
                {
                    var pages = PdfWordReader.Read(file);
                    var content = InvoiceTextParser.Parse(pages, rules.Rechnung);
                    entry = new InvoiceCacheEntry
                    {
                        Ticks = info.LastWriteTimeUtc.Ticks,
                        Size = info.Length,
                        Gross = content.Gross,
                        Recipient = content.Recipient,
                        Date = content.Date?.ToString("yyyy-MM-dd"),
                    };
                }
                catch (Exception ex)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    return;
                }
            }
            fresh[file] = entry;
            DateOnly? date = entry.Date is not null && DateOnly.TryParse(entry.Date, out var d) ? d : null;
            records.Add(new InvoiceRecord(number, file, entry.Gross, entry.Recipient, date));

            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0) progress?.Report($"Rechnungen lesen: {n} / {files.Count}");
        });

        SaveCache(cachePath, fresh.ToDictionary(kv => kv.Key, kv => kv.Value));

        result.Items.AddRange(records.OrderBy(r => r.Number, StringComparer.Ordinal));
        result.Problems.AddRange(problems);
        result.SkippedFiles = skipped;
        return result;
    }

    public ScanResult<ReceiptDocument> ScanReceipts(string folder, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var cachePath = Path.Combine(_cacheDir, "belege.cache.json");
        var cache = LoadCache<ReceiptCacheEntry>(cachePath);
        var fresh = new ConcurrentDictionary<string, ReceiptCacheEntry>();

        var result = new ScanResult<ReceiptDocument>();
        var files = Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories).ToList();
        var docs = new ConcurrentBag<ReceiptDocument>();
        var problems = new ConcurrentBag<string>();
        int done = 0;

        Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, file =>
        {
            var info = new FileInfo(file);
            ReceiptCacheEntry entry;
            if (cache.TryGetValue(file, out var cached) && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Size == info.Length)
            {
                entry = cached;
            }
            else
            {
                try
                {
                    var text = PdfWordReader.ReadAllText(file);
                    entry = new ReceiptCacheEntry
                    {
                        Ticks = info.LastWriteTimeUtc.Ticks,
                        Size = info.Length,
                        Orders = AmazonMatcher.FindOrderNumbers(text).ToList(),
                    };
                }
                catch (Exception ex)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    return;
                }
            }
            fresh[file] = entry;
            docs.Add(new ReceiptDocument(file, entry.Orders));

            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0) progress?.Report($"Belege lesen: {n} / {files.Count}");
        });

        SaveCache(cachePath, fresh.ToDictionary(kv => kv.Key, kv => kv.Value));
        result.Items.AddRange(docs);
        result.Problems.AddRange(problems);
        return result;
    }

    private static Dictionary<string, T> LoadCache<T>(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path)) ?? new();
        }
        catch (JsonException) { /* defekter Cache wird neu aufgebaut */ }
        catch (IOException) { }
        return new Dictionary<string, T>();
    }

    private static void SaveCache<T>(string path, Dictionary<string, T> cache)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(cache)); }
        catch (IOException) { /* Cache ist optional */ }
    }
}
