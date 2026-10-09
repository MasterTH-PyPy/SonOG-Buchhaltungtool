using System.Diagnostics;
using System.Globalization;
using SonOG.Buchhaltung.Core.Accounting;
using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Infrastructure;

namespace SonOG.Buchhaltung.App;

/// <summary>
/// Monatliche Sollstellung der Ausgangsrechnungen: Forderung je Rechnung auf dem Debitor einbuchen (Debitor an Erlöse).
/// Läuft getrennt vom Kontoauszug und merkt sich, welche Rechnungen schon übergeben wurden.
/// </summary>
internal sealed class SollstellungPanel : UserControl
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private readonly BuchhaltungService _service;
    private readonly Func<string> _invoiceFolder;
    private readonly Func<string> _outputFolder;
    private readonly Action<string> _status;
    private TaxpoolService Tp => _service.Taxpool;

    private InvoiceIndex? _index;
    private List<SollstellungsPosten> _posten = new();
    private bool _filling;

    private readonly DateTimePicker _dtMonat = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "MM.yyyy", ShowUpDown = true, Width = 90 };
    private readonly Button _btnLaden = new() { Text = "Rechnungen laden", AutoSize = true };
    private readonly Button _btnDebitor = new() { Text = "Debitor anlegen ...", AutoSize = true };
    private readonly Button _btnPersonen = new() { Text = "Personenkonten ...", AutoSize = true };
    private readonly Button _btnFreigeben = new() { Text = "Monat freigeben ...", AutoSize = true };
    private readonly Button _btnExport = new() { Text = "Sollstellung exportieren ...", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly Label _lblInfo = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3), ForeColor = Color.DimGray };
    private readonly DataGridView _grid = new();

    public SollstellungPanel(BuchhaltungService service, Func<string> invoiceFolder, Func<string> outputFolder, Action<string> status)
    {
        _service = service;
        _invoiceFolder = invoiceFolder;
        _outputFolder = outputFolder;
        _status = status;
        Dock = DockStyle.Fill;

        var last = DateTime.Today.AddMonths(-1);
        _dtMonat.Value = new DateTime(last.Year, last.Month, 1);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        top.Controls.Add(new Label { Text = "Monat:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        top.Controls.AddRange(new Control[] { _dtMonat, _btnLaden, _btnDebitor, _btnPersonen, _btnFreigeben, _btnExport, _lblInfo });
        _btnExport.Margin = new Padding(24, 3, 3, 3);

        var help = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 2, 10, 2),
            ForeColor = Color.DimGray,
            Text = "Bucht jede Ausgangsrechnung des Monats als Forderung auf den Debitor (Debitor an Erlöskonto, Rechnungsdatum, Rechnungsnummer als Beleg). " +
                   "Die Zahlung gleicht der Kontoauszug später aus. Schon übergebene Rechnungen sind grau und werden nicht noch einmal exportiert.",
        };

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Auswahl", HeaderText = "Übergeben", FillWeight = 7 });
        void C(string name, string header, float weight, bool ro, bool right = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, ReadOnly = ro, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (right) c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            if (ro) c.DefaultCellStyle.ForeColor = Color.DimGray;
            _grid.Columns.Add(c);
        }
        C("Nr", "Rechnung", 10, true);
        C("Datum", "Datum", 9, true);
        C("Empfaenger", "Empfänger", 22, true);
        C("Brutto", "Brutto", 9, true, right: true);
        C("Debitor", "Debitor", 9, false);
        C("DebitorName", "Debitor-Name", 16, true);
        C("Erloes", "Erlöskonto", 8, false);
        C("Bu", "BU", 5, false);
        C("Status", "Status", 22, true);
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += OnCellChanged;

        Controls.Add(_grid);
        Controls.Add(help);
        Controls.Add(top);

        _btnLaden.Click += async (_, _) => await OnLadenAsync();
        _dtMonat.ValueChanged += (_, _) => { if (_index is not null) Vorbereiten(); };
        _btnDebitor.Click += (_, _) => OnDebitorNeu();
        _btnPersonen.Click += (_, _) => OnPersonen();
        _btnFreigeben.Click += (_, _) => OnFreigeben();
        _btnExport.Click += (_, _) => OnExport();
        UpdateButtons();
    }

    private int Year => _dtMonat.Value.Year;
    private int Month => _dtMonat.Value.Month;

    private async Task OnLadenAsync()
    {
        var folder = _invoiceFolder();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            MessageBox.Show(this, "Bitte in Schritt 1 den Ordner der Ausgangsrechnungen (CAO) angeben.", "Sollstellung", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _btnLaden.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var progress = new Progress<string>(_status);
            _index = await Task.Run(() => _service.ScanInvoices(folder, progress));
            Vorbereiten();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Sollstellung", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _btnLaden.Enabled = true;
        }
    }

    private void Vorbereiten()
    {
        if (_index is null) return;
        Tp.Reload();
        _posten = Tp.SollstellungVorbereiten(_index.All, Year, Month);
        Fill();
        var ohneDatum = Sollstellung.OhneDatum(_index.All).Count;
        _status($"Sollstellung {Month:00}/{Year}: {_posten.Count} Rechnung(en) im Monat" +
                (ohneDatum > 0 ? $", {ohneDatum} Rechnung(en) im Ordner ohne lesbares Datum (siehe \"rechnung.datumRegex\" in regeln.json)" : ""));
    }

    private void Fill()
    {
        _filling = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var p in _posten)
            {
                int i = _grid.Rows.Add();
                _grid.Rows[i].Tag = p;
                FillRow(_grid.Rows[i], p);
            }
        }
        finally
        {
            _filling = false;
        }
        UpdateInfo();
        UpdateButtons();
    }

    private void FillRow(DataGridViewRow row, SollstellungsPosten p)
    {
        var k = p.Kontierung;
        var z = k.Zeilen.FirstOrDefault();
        bool gestellt = p.BereitsGestellt.Length > 0;
        row.Cells["Auswahl"].Value = p.Auswahl;
        row.Cells["Auswahl"].ReadOnly = gestellt;
        row.Cells["Nr"].Value = p.Rechnung.Number;
        row.Cells["Datum"].Value = p.Rechnung.Date?.ToString("dd.MM.yyyy", De) ?? "";
        row.Cells["Empfaenger"].Value = p.Rechnung.RecipientFirstLine;
        row.Cells["Brutto"].Value = p.Rechnung.GrossAmount?.ToString("N2", De) ?? "?";
        row.Cells["Debitor"].Value = k.Personenkonto?.ToString(CultureInfo.InvariantCulture) ?? "";
        row.Cells["DebitorName"].Value = k.Personenkonto is int pk ? Tp.Personen.Get(pk)?.Name ?? "(nicht im Verzeichnis)" : "";
        row.Cells["Erloes"].Value = z is { Sachkonto: > 0 } ? z.Sachkonto.ToString(CultureInfo.InvariantCulture) : "";
        row.Cells["Bu"].Value = z?.BuSchluessel ?? "";
        row.Cells["Erloes"].ReadOnly = row.Cells["Bu"].ReadOnly = row.Cells["Debitor"].ReadOnly = gestellt;

        var checks = gestellt ? new List<Pruefung>() : Tp.Pruefen(p);
        row.Cells["Status"].Value = gestellt ? $"bereits übergeben ({p.BereitsGestellt})"
            : checks.Count == 0 ? "OK" : string.Join("; ", checks.Select(c => c.Text));
        row.DefaultCellStyle.BackColor = gestellt ? Color.Gainsboro
            : checks.Any(c => c.Stufe == PruefStufe.Fehler) ? Color.FromArgb(0xF8, 0xCB, 0xAD)
            : checks.Count > 0 ? Color.FromArgb(0xFF, 0xF2, 0xCC)
            : Color.Empty;
    }

    private void OnCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_filling || e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not SollstellungsPosten p) return;
        var row = _grid.Rows[e.RowIndex];
        var text = Convert.ToString(row.Cells[e.ColumnIndex].Value)?.Trim() ?? "";
        var z = p.Kontierung.Zeilen[0];
        switch (_grid.Columns[e.ColumnIndex].Name)
        {
            case "Auswahl":
                p.Auswahl = row.Cells["Auswahl"].Value is true;
                break;
            case "Debitor":
                p.Kontierung.Personenkonto = int.TryParse(text, out var d) && d > 0 ? d : null;
                break;
            case "Erloes":
                z.Sachkonto = int.TryParse(text, out var s) && s > 0 ? s : 0;
                break;
            case "Bu":
                z.BuSchluessel = text;
                break;
            default:
                return;
        }
        _filling = true;
        try { FillRow(row, p); }
        finally { _filling = false; }
        UpdateInfo();
        UpdateButtons();
    }

    private void UpdateInfo()
    {
        var sel = _posten.Where(p => p.Auswahl).ToList();
        _lblInfo.Text = _posten.Count == 0 ? "" :
            $"{sel.Count} von {_posten.Count} ausgewählt, Summe {sel.Sum(p => p.Rechnung.GrossAmount ?? 0).ToString("N2", De)} EUR";
    }

    private void UpdateButtons()
    {
        _btnExport.Enabled = _posten.Any(p => p.Auswahl);
        _btnDebitor.Enabled = _posten.Count > 0;
        _btnFreigeben.Enabled = _index is not null;
    }

    private SollstellungsPosten? Current => _grid.CurrentRow?.Tag as SollstellungsPosten;

    private void OnDebitorNeu()
    {
        if (Current is not { } p)
        {
            MessageBox.Show(this, "Bitte eine Rechnung auswählen.", "Sollstellung", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var name = Prompt.Ask(this, "Neuer Debitor", "Name des Kunden (bekommt die nächste freie Debitorennummer):", p.Rechnung.RecipientFirstLine);
        if (string.IsNullOrWhiteSpace(name)) return;
        var acc = Tp.PersonenkontoAnlegen(name, PersonenArt.Debitor);
        // auf alle offenen Rechnungen desselben Empfängers ohne Debitor anwenden
        foreach (var x in _posten.Where(x => x.BereitsGestellt.Length == 0 && x.Kontierung.Personenkonto is null &&
                                             x.Rechnung.RecipientFirstLine == p.Rechnung.RecipientFirstLine))
            x.Kontierung.Personenkonto = acc.Konto;
        p.Kontierung.Personenkonto = acc.Konto;
        Fill();
        _status($"Debitor {acc} angelegt.");
    }

    private void OnPersonen()
    {
        Tp.Reload();
        using var dlg = new PersonAccountsForm(Tp.Personen, Tp.Settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Tp.Personen.Konten = dlg.Result;
        Tp.SavePersonen();
        // Debitoren neu zuordnen, wo noch keiner gesetzt ist
        foreach (var p in _posten.Where(p => p.BereitsGestellt.Length == 0 && p.Kontierung.Personenkonto is null))
            p.Kontierung = Sollstellung.KontierungFuer(p.Rechnung, Tp.Settings, Tp.Personen);
        Fill();
    }

    private void OnFreigeben()
    {
        var q = $"Die Sollstellung für {Month:00}/{Year} freigeben?\n\nDas ist nur nötig, wenn der Import in Taxpool gelöscht wurde und die " +
                "Rechnungen noch einmal übergeben werden sollen. Sonst entstehen die Forderungen doppelt.";
        if (MessageBox.Show(this, q, "Monat freigeben", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var n = Tp.SollstellungFreigeben(Year, Month);
        Vorbereiten();
        _status($"{n} Rechnung(en) für {Month:00}/{Year} freigegeben.");
    }

    private void OnExport()
    {
        _grid.EndEdit();
        var output = _outputFolder();
        if (output.Length == 0)
        {
            MessageBox.Show(this, "Bitte in Schritt 1 einen Ausgabeordner angeben.", "Sollstellung", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var sel = _posten.Where(p => p.Auswahl).ToList();
        var q = $"{sel.Count} Ausgangsrechnung(en) über {sel.Sum(p => p.Rechnung.GrossAmount ?? 0).ToString("N2", De)} EUR für {Month:00}/{Year} " +
                "als Importdatei für Taxpool erzeugen?\n\nDie Rechnungen gelten danach als übergeben.";
        if (MessageBox.Show(this, q, "Sollstellung", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var r = Tp.ExportSollstellung(_posten, Year, Month, output);
            Fill();
            var hinweise = r.Hinweise.Where(h => h.Stufe == PruefStufe.Hinweis).ToList();
            var msg = $"Fertig: {r.Buchungssaetze} Buchungssätze.\n\nImportdatei: {Path.GetFileName(r.Datei)}\n" +
                      (r.PersonenkontenDatei is null ? "" : $"Verwendete Debitoren: {Path.GetFileName(r.PersonenkontenDatei)}\n") +
                      (hinweise.Count > 0 ? "\nHinweise:\n" + string.Join("\n", hinweise.Take(10).Select(h => "• " + h.Nummer + ": " + h.Text)) + "\n" : "") +
                      "\nAusgabeordner öffnen?";
            _status($"Sollstellung erzeugt: {Path.GetFileName(r.Datei)}");
            if (MessageBox.Show(this, msg, "Sollstellung", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Sollstellung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
