using System.Diagnostics;
using SonOG.Buchhaltung.Infrastructure.Pdf;

namespace SonOG.Buchhaltung.App;

/// <summary>Eine Datei, die im Vorschaubereich angezeigt werden kann (z. B. Kontoauszugsseite oder Beleg).</summary>
public sealed record PreviewItem(string Label, string Path, int Page, bool IsReceipt);

/// <summary>PDF-Anzeige mit Auswahl der Datei, Seitenwechsel, Zoom und "Zuordnung entfernen".</summary>
public sealed class PdfPreviewPanel : UserControl
{
    private readonly ComboBox _files = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly Button _prev = new() { Text = "◀", Width = 32 };
    private readonly Button _next = new() { Text = "▶", Width = 32 };
    private readonly Label _pageLabel = new() { AutoSize = true, Margin = new Padding(4, 7, 4, 0) };
    private readonly Button _zoomOut = new() { Text = "−", Width = 32 };
    private readonly Button _zoomIn = new() { Text = "+", Width = 32 };
    private readonly Button _fit = new() { Text = "Breite", AutoSize = true };
    private readonly Button _open = new() { Text = "Extern öffnen", AutoSize = true };
    private readonly Button _remove = new() { Text = "Zuordnung entfernen", AutoSize = true };
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(90, 90, 90) };
    private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(8, 8) };
    private readonly Label _message = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, AutoSize = false };

    private List<PreviewItem> _items = new();
    private int _page;
    private int _pageCount = 1;
    private double _zoom = 1.0;
    private int _renderId;

    /// <summary>Wird ausgelöst, wenn der Anwender einen Beleg von der Buchung lösen möchte.</summary>
    public event Action<PreviewItem>? RemoveRequested;

    public PdfPreviewPanel()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4) };
        bar.Controls.AddRange(new Control[] { _files, _prev, _pageLabel, _next, _zoomOut, _zoomIn, _fit, _open, _remove });
        _scroll.Controls.Add(_picture);
        _scroll.Controls.Add(_message);
        Controls.Add(_scroll);
        Controls.Add(bar);

        _files.SelectedIndexChanged += (_, _) => { _page = CurrentItem?.Page ?? 0; _ = RenderAsync(); };
        _prev.Click += (_, _) => { if (_page > 0) { _page--; _ = RenderAsync(); } };
        _next.Click += (_, _) => { if (_page < _pageCount - 1) { _page++; _ = RenderAsync(); } };
        _zoomIn.Click += (_, _) => { _zoom = Math.Min(4.0, _zoom + 0.25); _ = RenderAsync(); };
        _zoomOut.Click += (_, _) => { _zoom = Math.Max(0.25, _zoom - 0.25); _ = RenderAsync(); };
        _fit.Click += (_, _) => { _zoom = 1.0; _ = RenderAsync(); };
        _open.Click += (_, _) =>
        {
            if (CurrentItem is { } it)
                try { Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "PDF öffnen"); }
        };
        _remove.Click += (_, _) => { if (CurrentItem is { IsReceipt: true } it) RemoveRequested?.Invoke(it); };
        _scroll.Resize += (_, _) => { if (_picture.Image is not null && _zoom == 1.0) _ = RenderAsync(); };
        SetItems(Array.Empty<PreviewItem>());
    }

    private PreviewItem? CurrentItem => _files.SelectedIndex >= 0 && _files.SelectedIndex < _items.Count ? _items[_files.SelectedIndex] : null;

    public void SetItems(IReadOnlyList<PreviewItem> items)
    {
        _items = items.ToList();
        _files.Items.Clear();
        foreach (var i in _items) _files.Items.Add(i.Label);
        bool any = _items.Count > 0;
        foreach (Control c in new Control[] { _files, _prev, _next, _zoomIn, _zoomOut, _fit, _open, _remove }) c.Enabled = any;
        if (!any)
        {
            ShowMessage("Keine Datei zur markierten Buchung");
            _pageLabel.Text = "";
            return;
        }
        // Beleg anzeigen, wenn vorhanden, sonst die Kontoauszugsseite; löst das Rendern aus
        var firstReceipt = _items.FindIndex(i => i.IsReceipt);
        _files.SelectedIndex = firstReceipt >= 0 ? firstReceipt : 0;
    }

    private void ShowMessage(string text)
    {
        _picture.Image?.Dispose();
        _picture.Image = null;
        _picture.Visible = false;
        _message.Text = text;
        _message.Visible = true;
    }

    private async Task RenderAsync()
    {
        if (CurrentItem is not { } item) return;
        int id = ++_renderId;
        _remove.Enabled = item.IsReceipt;
        try
        {
            int width = (int)Math.Max(300, (_scroll.ClientSize.Width - 30) * _zoom);
            var img = await PdfRenderer.RenderAsync(item.Path, _page, width);
            if (id != _renderId) return; // inzwischen wurde etwas anderes ausgewählt
            _pageCount = img.PageCount;
            using var ms = new MemoryStream(img.Png);
            var bmp = new Bitmap(ms);
            _picture.Image?.Dispose();
            _picture.Image = new Bitmap(bmp);
            bmp.Dispose();
            _picture.Visible = true;
            _message.Visible = false;
            _pageLabel.Text = $"Seite {_page + 1} / {_pageCount}";
        }
        catch (Exception ex)
        {
            if (id != _renderId) return;
            _pageLabel.Text = "";
            ShowMessage("Vorschau nicht möglich: " + ex.Message + "\n(Mit „Extern öffnen“ im PDF-Programm ansehen.)");
        }
    }
}
