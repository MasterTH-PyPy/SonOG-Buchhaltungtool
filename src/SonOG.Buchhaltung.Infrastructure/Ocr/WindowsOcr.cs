using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SonOG.Buchhaltung.Infrastructure.Ocr;

/// <summary>
/// Texterkennung für eingescannte PDFs mit den in Windows 10/11 eingebauten Funktionen (Windows.Data.Pdf zum Rendern,
/// Windows.Media.Ocr zum Erkennen). Es muss nichts installiert werden, aber das Windows-Sprachpaket "Deutsch" muss vorhanden sein.
/// </summary>
public static class WindowsOcr
{
    private static OcrEngine? _engine;
    private static string? _engineError;
    private static readonly object Gate = new();

    /// <summary>Null, wenn die OCR nutzbar ist, sonst eine Erklärung.</summary>
    public static string? Unavailable()
    {
        Engine();
        return _engineError;
    }

    private static OcrEngine? Engine()
    {
        lock (Gate)
        {
            if (_engine is not null || _engineError is not null) return _engine;
            try
            {
                _engine = OcrEngine.TryCreateFromLanguage(new Language("de-DE")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine is null)
                    _engineError = "Windows-Texterkennung nicht verfügbar: Sprachpaket \"Deutsch\" installieren (Einstellungen > Zeit und Sprache > Sprache und Region).";
            }
            catch (Exception ex)
            {
                _engineError = "Windows-Texterkennung nicht verfügbar: " + ex.Message;
            }
            return _engine;
        }
    }

    /// <summary>Liest die ersten Seiten eines PDFs per OCR. Wirft eine Exception, wenn die OCR nicht verfügbar ist.</summary>
    public static string ReadPdf(string path, int maxPages = 3)
    {
        var engine = Engine() ?? throw new InvalidOperationException(_engineError);
        return Task.Run(() => ReadPdfAsync(engine, path, maxPages)).GetAwaiter().GetResult();
    }

    private static async Task<string> ReadPdfAsync(OcrEngine engine, string path, int maxPages)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var doc = await PdfDocument.LoadFromFileAsync(file);
        var sb = new System.Text.StringBuilder();
        uint pages = Math.Min(doc.PageCount, (uint)maxPages);
        for (uint i = 0; i < pages; i++)
        {
            using var page = doc.GetPage(i);
            // Seitengröße ist in 1/96 Zoll; Faktor 3 ergibt ca. 290 dpi, die OCR-Engine erlaubt bis MaxImageDimension.
            double scale = Math.Min(3.0, OcrEngine.MaxImageDimension / Math.Max(page.Size.Width, page.Size.Height));
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)(page.Size.Width * scale) });
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            sb.AppendLine(result.Text);
        }
        return sb.ToString();
    }
}
