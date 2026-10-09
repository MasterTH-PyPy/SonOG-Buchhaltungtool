using System.ComponentModel;
using System.Diagnostics;
using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Infrastructure;

namespace SonOG.Buchhaltung.App;

public sealed class MainForm : Form
{
    private readonly BuchhaltungService _service = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private BuchhaltungSession? _session;
    private readonly BindingList<BookingRow> _rows = new();

    private readonly TextBox _txtStatement = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtInvoices = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtEingang = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtOutput = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _numFrom = new() { Minimum = 1, Maximum = 999999, Value = 1, Width = 80 };
    private readonly NumericUpDown _numTo = new() { Minimum = 1, Maximum = 999999, Value = 9999, Width = 80 };
    private readonly CheckBox _chkOpenFrom = new() { Text = "nur ab", AutoSize = true, Margin = new Padding(3, 5, 6, 3) };
    private readonly DateTimePicker _dtOpenFrom = new() { Format = DateTimePickerFormat.Short, Width = 130, Enabled = false };
    private readonly CheckBox _chkOnlyReview = new() { Text = "Nur Prüffälle anzeigen", AutoSize = true };
    private readonly CheckBox _chkPrint = new() { Text = "Druckpaket erstellen", AutoSize = true, Checked = true };

    private readonly Button _btnLoad = new() { Text = "Einlesen / Vorschau", AutoSize = true };
    private readonly Button _btnAccept = new() { Text = "Vorschlag übernehmen", AutoSize = true };
    private readonly Button _btnAttach = new() { Text = "Beleg zuordnen ...", AutoSize = true };
    private readonly Button _btnExport = new() { Text = "Exportieren ...", AutoSize = true };
    private readonly Button _btnPrint = new() { Text = "Drucken (rückwärts) – Vorschau ...", AutoSize = true };
    private readonly CheckBox _chkMail = new() { Text = "Mails abrufen", AutoSize = true };
    private readonly Button _btnMail = new() { Text = "Mail-Postfächer ...", AutoSize = true };
    private readonly Button _btnRules = new() { Text = "Regeln öffnen", AutoSize = true };
    private readonly Button _btnRelease = new() { Text = "Nummern freigeben", AutoSize = true };

    private readonly DataGridView _grid = new();
    private readonly TextBox _txtWarnings = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Height = 60, BackColor = SystemColors.Info };
    private readonly Panel _warnPanel = new() { Dock = DockStyle.Top, Height = 64, Padding = new Padding(8, 0, 8, 4), Visible = false };
    private readonly TextBox _txtDetails = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly ToolStripStatusLabel _lblStatus = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    public MainForm()
    {
        Text = "SonOG Buchhaltung - Kontoauszug nummerieren und abgleichen";
        Width = 1250;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        LoadSettings();
        WireEvents();
        UpdateButtons();
    }

    // ---------------------------------------------------------------------------------------------
    // Aufbau
    // ---------------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        var inputs = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8, 8, 8, 0) };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddPathRow(inputs, "Kontoauszug (PDF):", _txtStatement, () => BrowseFile(_txtStatement, "PDF-Dateien (*.pdf)|*.pdf"));
        AddPathRow(inputs, "Ausgangsrechnungen:", _txtInvoices, () => BrowseFolder(_txtInvoices, "Ordner mit den Ausgangsrechnungen (CAO)"));
        AddPathRow(inputs, "Eingangsrechnungen:", _txtEingang, () => BrowseFolder(_txtEingang, "Ordner mit den Eingangsrechnungen (PDF, Amazon-Wochenexporte auch als ZIP)"));
        AddPathRow(inputs, "Ausgabeordner:", _txtOutput, () => BrowseFolder(_txtOutput, "Ausgabeordner"));

        inputs.RowCount++;
        inputs.Controls.Add(new Label { Text = "Offene Rechnungen:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) }, 0, inputs.RowCount - 1);
        var openFrom = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
        openFrom.Controls.Add(_chkOpenFrom);
        openFrom.Controls.Add(_dtOpenFrom);
        inputs.Controls.Add(openFrom, 1, inputs.RowCount - 1);
        _chkOpenFrom.CheckedChanged += (_, _) => _dtOpenFrom.Enabled = _chkOpenFrom.Checked;

        inputs.RowCount++;
        inputs.Controls.Add(new Label { Text = "Nummernkreis:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) }, 0, inputs.RowCount - 1);
        var range = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
        range.Controls.Add(new Label { Text = "von", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        range.Controls.Add(_numFrom);
        range.Controls.Add(new Label { Text = "bis", AutoSize = true, Margin = new Padding(6, 6, 3, 0) });
        range.Controls.Add(_numTo);
        range.Controls.Add(new Label { Text = "(kleinste/größte laufende Nummer; ist der Zähler darunter, wird er hochgesetzt)", AutoSize = true, Margin = new Padding(8, 6, 0, 0) });
        inputs.Controls.Add(range, 1, inputs.RowCount - 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 4, 8, 4), WrapContents = true };
        buttons.Controls.AddRange(new Control[] { _btnLoad, _btnAccept, _btnAttach, _btnExport, _btnPrint, _chkPrint, _btnMail, _chkMail, _btnRules, _btnRelease, _chkOnlyReview });
        _chkPrint.Margin = new Padding(3, 8, 12, 3);
        _chkOnlyReview.Margin = new Padding(12, 8, 3, 3);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoGenerateColumns = true;
        _grid.DataSource = _rows;
        _grid.DataBindingComplete += (_, _) => FormatColumns();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_txtDetails);
        // SplitterDistance erst setzen, wenn das Fenster seine Größe hat (sonst ArgumentException)
        Shown += (_, _) => split.SplitterDistance = Math.Max(120, split.Height * 7 / 10);

        var status = new StatusStrip();
        status.Items.Add(_lblStatus);

        _warnPanel.Controls.Add(_txtWarnings);

        // Reihenfolge beim Hinzufügen: zuerst Fill, dann Top-Elemente von unten nach oben
        Controls.Add(split);
        Controls.Add(_warnPanel);
        Controls.Add(buttons);
        Controls.Add(inputs);
        Controls.Add(status);
    }

    private static void AddPathRow(TableLayoutPanel table, string label, TextBox box, Action browse)
    {
        table.RowCount++;
        int row = table.RowCount - 1;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) }, 0, row);
        table.Controls.Add(box, 1, row);
        var btn = new Button { Text = "...", Width = 32, AutoSize = false };
        btn.Click += (_, _) => browse();
        table.Controls.Add(btn, 2, row);
    }

    private void WireEvents()
    {
        _btnLoad.Click += async (_, _) => await OnLoadAsync();
        _btnAccept.Click += (_, _) => OnAccept();
        _btnAttach.Click += (_, _) => OnAttach();
        _btnExport.Click += async (_, _) => await OnExportAsync(false);
        _btnPrint.Click += async (_, _) => await OnExportAsync(true);
        _btnMail.Click += (_, _) =>
        {
            using var dlg = new MailAccountsForm(_settings.MailAccounts, _settings.MailDaysBefore, _settings.MailDaysAfter);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _settings.MailAccounts = dlg.Result;
            _settings.MailDaysBefore = dlg.DaysBefore;
            _settings.MailDaysAfter = dlg.DaysAfter;
            try { _settings.Save(); } catch (IOException) { }
        };
        _btnRules.Click += (_, _) => OnOpenRules();
        _btnRelease.Click += (_, _) => OnRelease();
        _chkOnlyReview.CheckedChanged += (_, _) => Rebind();
        _grid.SelectionChanged += (_, _) => { ShowDetails(); UpdateButtons(); };
        _grid.RowPrePaint += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            if (_grid.Rows[e.RowIndex].DataBoundItem is BookingRow row)
                _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = row.BackColor;
        };
        FormClosing += (_, _) => SaveSettings();
    }

    private void FormatColumns()
    {
        if (_grid.Columns.Count == 0) return;
        void Col(string name, int width, bool fill = false, string? format = null, bool right = false)
        {
            var c = _grid.Columns[name];
            if (c is null) return;
            c.Width = width;
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            if (format is not null) c.DefaultCellStyle.Format = format;
            if (right) c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        Col("Nr", 85);
        Col("Datum", 80);
        Col("Betrag", 90, format: "N2", right: true);
        Col("Kategorie", 140);
        Col("Status", 170);
        Col("Rechnungen", 170);
        Col("Hinweis", 200, fill: true);
        Col("Buchungstext", 300, fill: true);
        // Beide Spalten teilen sich den Rest: Hinweis 35 %, Buchungstext 65 %; lange Texte als Tooltip statt breiter Spalte.
        foreach (var (name, weight) in new[] { ("Hinweis", 35f), ("Buchungstext", 65f) })
        {
            var c = _grid.Columns[name];
            if (c is null) continue;
            c.FillWeight = weight;
            c.MinimumWidth = 120;
            c.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        }
        _grid.ShowCellToolTips = true;
        _grid.CellToolTipTextNeeded -= GridToolTip;
        _grid.CellToolTipTextNeeded += GridToolTip;
    }

    private void GridToolTip(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        var name = _grid.Columns[e.ColumnIndex].Name;
        if (name is "Hinweis" or "Buchungstext")
            _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText = Convert.ToString(_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? "";
    }

    // ---------------------------------------------------------------------------------------------
    // Einstellungen
    // ---------------------------------------------------------------------------------------------

    private void LoadSettings()
    {
        _txtStatement.Text = _settings.StatementPath;
        _txtInvoices.Text = _settings.InvoiceFolder;
        _txtEingang.Text = _settings.EingangFolder;
        _txtOutput.Text = _settings.OutputFolder;
        _chkMail.Checked = _settings.FetchMail;
        _numFrom.Value = Math.Clamp(_settings.NumberFrom, 1, 999999);
        _numTo.Value = Math.Clamp(_settings.NumberTo, 1, 999999);
        if (DateOnly.TryParse(_settings.OpenFrom, out var d))
        {
            _dtOpenFrom.Value = d.ToDateTime(TimeOnly.MinValue);
            _chkOpenFrom.Checked = true;
        }
        else
        {
            _dtOpenFrom.Value = new DateTime(DateTime.Today.Year, 1, 1);
            _chkOpenFrom.Checked = false;
        }
    }

    private void SaveSettings()
    {
        _settings.StatementPath = _txtStatement.Text.Trim();
        _settings.InvoiceFolder = _txtInvoices.Text.Trim();
        _settings.EingangFolder = _txtEingang.Text.Trim();
        _settings.OutputFolder = _txtOutput.Text.Trim();
        _settings.FetchMail = _chkMail.Checked;
        _settings.NumberFrom = (int)_numFrom.Value;
        _settings.NumberTo = (int)_numTo.Value;
        _settings.OpenFrom = _chkOpenFrom.Checked ? DateOnly.FromDateTime(_dtOpenFrom.Value).ToString("yyyy-MM-dd") : "";
        try { _settings.Save(); } catch (IOException) { }
    }

    private ServiceOptions BuildOptions() => new()
    {
        StatementPath = _txtStatement.Text.Trim(),
        InvoiceFolder = string.IsNullOrWhiteSpace(_txtInvoices.Text) ? null : _txtInvoices.Text.Trim(),
        EingangFolder = string.IsNullOrWhiteSpace(_txtEingang.Text) ? null : _txtEingang.Text.Trim(),
        OutputFolder = _txtOutput.Text.Trim(),
        MailAccounts = _settings.MailAccounts,
        FetchMail = _chkMail.Checked && _settings.MailAccounts.Count > 0,
        NumberFrom = (int)_numFrom.Value,
        NumberTo = (int)_numTo.Value,
        MailDaysBefore = _settings.MailDaysBefore,
        MailDaysAfter = _settings.MailDaysAfter,
        OpenFrom = _chkOpenFrom.Checked ? DateOnly.FromDateTime(_dtOpenFrom.Value) : null,
    };

    private void BrowseFile(TextBox target, string filter)
    {
        using var dlg = new OpenFileDialog { Filter = filter, FileName = target.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
    }

    private void BrowseFolder(TextBox target, string description)
    {
        using var dlg = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, SelectedPath = target.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    // ---------------------------------------------------------------------------------------------
    // Aktionen
    // ---------------------------------------------------------------------------------------------

    private async Task OnLoadAsync()
    {
        if (!File.Exists(_txtStatement.Text.Trim()))
        {
            MessageBox.Show(this, "Bitte zuerst einen Kontoauszug (PDF) auswählen.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SaveSettings();
        var options = BuildOptions();
        var progress = new Progress<string>(m => _lblStatus.Text = m);

        SetBusy(true);
        try
        {
            _session = await Task.Run(() => _service.Load(options, progress));
            Rebind();
            ShowWarnings();
            _lblStatus.Text = Summary();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Fehler beim Einlesen.";
            MessageBox.Show(this, ex.Message, "Fehler beim Einlesen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnAccept()
    {
        if (SelectedBooking() is not { Match: { Accepted: false } } b) return;
        _service.AcceptSuggestion(b);
        RefreshKeepSelection();
    }

    private void OnAttach()
    {
        if (SelectedBooking() is not { } b) return;
        using var dlg = new OpenFileDialog { Filter = "PDF-Dateien (*.pdf)|*.pdf", Title = "Beleg für Buchung " + b.Number };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _service.AttachReceipt(b, dlg.FileName);
        RefreshKeepSelection();
    }

    private async Task OnExportAsync(bool printNow)
    {
        if (_session is null) return;
        if (string.IsNullOrWhiteSpace(_txtOutput.Text))
        {
            MessageBox.Show(this, "Bitte einen Ausgabeordner angeben.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var bookings = _session.Statement.Bookings;

        if (printNow)
        {
            using var sel = new ReceiptSelectionForm(bookings);
            if (sel.HasItems)
            {
                if (sel.ShowDialog(this) != DialogResult.OK) return;
                sel.ApplyDeselection();
                Rebind();
            }
        }

        int open = _rows.Count(r => r.NeedsReview);
        var question =
            $"Es werden {bookings.Count} Buchungen nummeriert ({bookings[0].Number} bis {bookings[^1].Number}) " +
            "und die Nummern dauerhaft vergeben. Derselbe Auszug bekommt bei einem erneuten Export dieselben Nummern." +
            (open > 0 ? $"\n\nBei {open} Buchung(en) ist noch etwas zu prüfen (gelb/rot markiert)." : "") +
            "\n\nDer Export legt gestempelte Kopien an; das Original wird nicht verändert.\n\nFortfahren?";
        if (MessageBox.Show(this, question, "Exportieren", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        SaveSettings();
        var options = BuildOptions();
        var progress = new Progress<string>(m => _lblStatus.Text = m);
        bool print = _chkPrint.Checked || printNow;

        SetBusy(true);
        try
        {
            var session = _session;
            var result = await Task.Run(() => _service.Export(session, options, print, progress));
            Rebind();
            _lblStatus.Text = $"Export fertig: {result.FirstNumber} bis {result.LastNumber}, {result.ReceiptCount} Beleg(e).";

            if (printNow && result.PrintPackage is not null)
            {
                _lblStatus.Text = "Druckpaket wird im PDF-Programm geöffnet ...";
                PrintPdf(result.PrintPackage);
                return;
            }

            var msg = $"Fertig.\n\nKontoauszug: {Path.GetFileName(result.StatementPdf)}\nListe: {Path.GetFileName(result.Excel)}\n" +
                      $"Belege: {result.ReceiptCount} gestempelt" + (result.PrintPackage is null ? "" : $"\nDruckpaket: {Path.GetFileName(result.PrintPackage)}") +
                      "\n\nAusgabeordner öffnen?";
            if (MessageBox.Show(this, msg, "Export", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(options.OutputFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Fehler beim Export.";
            MessageBox.Show(this, ex.Message, "Fehler beim Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Öffnet das Druckpaket im Standard-PDF-Programm; gedruckt wird von dort aus (Strg+P).</summary>
    private void PrintPdf(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnOpenRules()
    {
        try
        {
            // Legt beim ersten Aufruf eine Standarddatei an
            SonOG.Buchhaltung.Core.Rules.AppRules.Load(_service.RulesPath);
            Process.Start(new ProcessStartInfo(_service.RulesPath) { UseShellExecute = true });
            MessageBox.Show(this, "Nach dem Speichern der Regeln den Auszug erneut einlesen.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnRelease()
    {
        if (_session is null) return;
        var q = $"Die Nummern des Auszugs '{_session.StatementKey}' freigeben?\n\n" +
                "Das ist nur nötig, wenn dieser Auszug neu nummeriert werden soll (z. B. weil sich die Buchungsanzahl geändert hat). " +
                "Bereits ausgegebene Unterlagen mit den alten Nummern müssen dann ersetzt werden.";
        if (MessageBox.Show(this, q, "Nummern freigeben", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var done = _service.ReleaseNumbers(_session);
        MessageBox.Show(this, done ? "Nummern freigegeben. Bitte den Auszug neu einlesen." : "Für diesen Auszug waren keine Nummern vergeben.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------------------------------------------------------------------------------------------
    // Anzeige
    // ---------------------------------------------------------------------------------------------

    private Booking? SelectedBooking() =>
        _grid.CurrentRow?.DataBoundItem is BookingRow row ? row.Booking : null;

    private void Rebind()
    {
        _rows.RaiseListChangedEvents = false;
        _rows.Clear();
        if (_session is not null)
        {
            foreach (var b in _session.Statement.Bookings)
            {
                var row = new BookingRow(b);
                if (_chkOnlyReview.Checked && !row.NeedsReview) continue;
                _rows.Add(row);
            }
        }
        _rows.RaiseListChangedEvents = true;
        _rows.ResetBindings();
        ShowDetails();
        UpdateButtons();
    }

    private void RefreshKeepSelection()
    {
        int index = _grid.CurrentRow?.Index ?? -1;
        _rows.ResetBindings();
        if (index >= 0 && index < _grid.Rows.Count)
            _grid.CurrentCell = _grid.Rows[index].Cells[0];
        _lblStatus.Text = Summary();
        ShowDetails();
        UpdateButtons();
    }

    private string Summary()
    {
        if (_session is null) return "";
        var st = _session.Statement;
        var all = st.Bookings.Select(b => new BookingRow(b)).ToList();
        var balance = st.BalanceOk switch { true => "Saldo OK", false => "SALDO ABWEICHUNG", _ => "Saldo nicht prüfbar" };
        return $"Auszug {st.Title}: {st.Bookings.Count} Buchungen, {balance}, Nummern {st.Bookings[0].Number} bis {st.Bookings[^1].Number}, " +
               $"beleglos {st.Bookings.Count(b => b.Beleglos)}, zu prüfen {all.Count(r => r.NeedsReview)}, offene Rechnungen {_session.Reconcile.OpenInvoices.Count}";
    }

    private void ShowWarnings()
    {
        if (_session is { Warnings.Count: > 0 })
        {
            _txtWarnings.Text = string.Join(Environment.NewLine, _session.Warnings);
            _warnPanel.Visible = true;
        }
        else
        {
            _warnPanel.Visible = false;
        }
    }

    private void ShowDetails()
    {
        if (SelectedBooking() is not { } b)
        {
            _txtDetails.Text = "";
            return;
        }
        var row = new BookingRow(b);
        var lines = new List<string>
        {
            $"Nr.:        {b.Number}",
            $"Datum:      {row.Datum}",
            $"Betrag:     {b.Amount:N2} EUR",
            $"Kategorie:  {row.Kategorie}",
            $"Status:     {row.Status}",
        };
        if (b.PayerName.Length > 0) lines.Add($"Zahler:     {b.PayerName}");
        if (row.Hinweis.Length > 0) lines.Add($"Hinweis:    {row.Hinweis}");
        if (b.Match is { Invoices.Count: > 0 } m)
            foreach (var i in m.Invoices)
                lines.Add($"Rechnung:   {i.Number}  {(i.GrossAmount is null ? "Betrag ?" : i.GrossAmount.Value.ToString("N2") + " EUR")}  {i.RecipientFirstLine}  ({i.FilePath})");
        foreach (var f in b.ReceiptFiles) lines.Add($"Beleg:      {f}");
        lines.Add("");
        lines.Add($"{b.Type}: {b.Text}");
        _txtDetails.Text = string.Join(Environment.NewLine, lines);
    }

    private void UpdateButtons()
    {
        bool has = _session is not null;
        var b = SelectedBooking();
        _btnExport.Enabled = has;
        _btnPrint.Enabled = has;
        _btnRelease.Enabled = has;
        _btnAttach.Enabled = b is not null;
        _btnAccept.Enabled = b?.Match is { Accepted: false, Status: not MatchStatus.Ok };
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _btnLoad.Enabled = !busy;
        _btnExport.Enabled = !busy && _session is not null;
        _btnPrint.Enabled = !busy && _session is not null;
        _btnAccept.Enabled = !busy && _btnAccept.Enabled;
        _btnAttach.Enabled = !busy && _btnAttach.Enabled;
        _btnRelease.Enabled = !busy && _session is not null;
        if (!busy) UpdateButtons();
    }
}
