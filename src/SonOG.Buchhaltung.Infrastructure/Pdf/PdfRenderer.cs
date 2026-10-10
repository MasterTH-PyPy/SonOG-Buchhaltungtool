using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SonOG.Buchhaltung.Infrastructure.Pdf;

/// <summary>Zeigt PDF-Seiten als PNG-Bild an, mit der in Windows eingebauten PDF-Funktion (keine Zusatzinstallation).</summary>
public static class PdfRenderer
{
    public sealed record PageImage(byte[] Png, int PageCount);

    /// <summary>Rendert eine Seite (0-basiert) auf die angegebene Breite in Pixel.</summary>
    public static Task<PageImage> RenderAsync(string path, int pageIndex, int pixelWidth) =>
        Task.Run(async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            var doc = await PdfDocument.LoadFromFileAsync(file);
            int count = (int)doc.PageCount;
            pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, count - 1));
            using var page = doc.GetPage((uint)pageIndex);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Clamp(pixelWidth, 200, 6000) });
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return new PageImage(bytes, count);
        });
}
