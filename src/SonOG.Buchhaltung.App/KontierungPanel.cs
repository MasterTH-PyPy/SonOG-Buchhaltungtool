using System.Diagnostics;
using System.Globalization;
using SonOG.Buchhaltung.Core.Accounting;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Infrastructure;

namespace SonOG.Buchhaltung.App;

/// <summary>
/// Schritt 2 (Kontieren: Personenkonto, Sachkonten, Splitbuchungen) und Schritt 3 (Importdatei für Taxpool).
/// Jede Änderung wird sofort gespeichert und beim nächsten Einlesen desselben Auszugs wiederhergestellt.
/// </summary>
internal sealed class KontierungPanel : UserControl
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private const string KeinPersonenkonto = "(kein Personenkonto - direkt gegen Bank)";

    private readonly BuchhaltungService _service;
    private readonly Func<string> _outputFolder;
    private readonly Action<string> _status;
    private BuchhaltungSession? _session;
    private bool _filling;

    private TaxpoolService Tp => _service.Taxpool;

    // Liste
    private readonly DataGridView _list = new();
    private readonly CheckBox _chkOffen = new() { Text = "Nur offene / fehlerhafte", AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private readonly Button _btnNeu = new() { Text = "Vorschläge neu", AutoSize = true };
    private readonly Button _btnPersonen = new() { Text = "Personenkonten ...", AutoSize = true };
    private readonly Button _btnImport = new() { Text = "Aus Taxpool lesen ...", AutoSize = true };
    private readonly Button _btnEinstellungen = new() { Text = "Einstellungen / Regeln öffnen", AutoSize = true };
    private readonly Button _btnExport = new() { Text = "3 · Taxpool-Importdatei erzeugen ...", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
    private readonly Label _lblInfo = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3), ForeColor = Color.DimGray };

    // Editor
    private readonly Label _lblBooking = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) };
    private readonly ComboBox _cmbPerson = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly Button _btnPersonNeu = new() { Text = "Neu ...", AutoSize = true };
    private readonly CheckBox _chkEinbuchen = new() { Text = "Beleg auf dem Personenkonto einbuchen, dann Zahlung ausbuchen", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private readonly ComboBox _cmbVorlage = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _btnVorlage = new() { Text = "Anwenden", AutoSize = true };
    private readonly Button _btnStichwort = new() { Text = "Automatisch ...", AutoSize = true };
    private readonly DataGridView _lines = new();
    private readonly Button _btnAdd = new() { Text = "+ Zeile (Rest)", AutoSize = true };
    private readonly Button _btnDel = new() { Text = "Zeile entfernen", AutoSize = true };
    private readonly Button _btnReset = new() { Text = "Vorschlag wiederherstellen", AutoSize = true };
    private readonly Button _btnRegel = new() { Text = "Als Regel speichern ...", AutoSize = true };
    private readonly Button _btnOk = new() { Text = "Bestätigen ✓", AutoSize = true };
    private readonly Label _lblSum = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private readonly Label _lblChecks = new() { Dock = DockStyle.Fill, ForeColor = Color.Firebrick, AutoEllipsis = true };

    public KontierungPanel(BuchhaltungService service, Func<string> outputFolder, Action<string> status)
    {
        _service = service;
        _outputFolder = outputFolder;
        _status = status;
        Dock = DockStyle.Fill;
        BuildLayout();
        WireEvents();
        SetSession(null);
    }

    // ---------------------------------------------------------------------------------------------
    // Aufbau
    // ---------------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        top.Controls.AddRange(new Control[] { _btnNeu, _btnPersonen, _btnImport, _btnEinstellungen, _chkOffen, _btnExport, _lblInfo });
        _btnExport.Margin = new Padding(24, 3, 3, 3);

        _list.Dock = DockStyle.Fill;
        _list.ReadOnly = true;
        _list.AllowUserToAddRows = false;
        _list.AllowUserToDeleteRows = false;
        _list.AllowUserToResizeRows = false;
        _list.RowHeadersVisible = false;
        _list.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _list.MultiSelect = false;
        _list.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        void L(string name, string header, float weight, bool right = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (right) c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _list.Columns.Add(c);
        }
        L("Nr", "Nr.", 8);
        L("Datum", "Datum", 8);
        L("Betrag", "Betrag", 9, right: true);
        L("Kategorie", "Kategorie", 11);
        L("Person", "Personenkonto", 16);
        L("Konten", "Sachkonten", 12);
        L("Stand", "Stand", 10);
        L("Hinweis", "Hinweis / Prüfung", 24);
        L("Text", "Buchungstext", 26);

        // Editor
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8, 4, 8, 4) };
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        editor.Controls.Add(_lblBooking, 0, 0);

        var personRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
        personRow.Controls.Add(new Label { Text = "Personenkonto:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        personRow.Controls.Add(_cmbPerson);
        personRow.Controls.Add(_btnPersonNeu);
        personRow.Controls.Add(_chkEinbuchen);
        personRow.Controls.Add(new Label { Text = "Vorlage:", AutoSize = true, Margin = new Padding(18, 7, 3, 3) });
        personRow.Controls.Add(_cmbVorlage);
        personRow.Controls.Add(_btnVorlage);
        personRow.Controls.Add(_btnStichwort);
        new ToolTip().SetToolTip(_btnStichwort, "Stichwort für die gewählte Vorlage hinterlegen: Buchungen mit diesem Text bekommen sie künftig automatisch vorgeschlagen.");
        editor.Controls.Add(personRow, 0, 1);

        _lines.Dock = DockStyle.Fill;
        _lines.AllowUserToAddRows = false;
        _lines.AllowUserToDeleteRows = false;
        _lines.AllowUserToResizeRows = false;
        _lines.RowHeadersWidth = 24;
        _lines.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _lines.MultiSelect = false;
        _lines.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _lines.EditMode = DataGridViewEditMode.EditOnEnter;
        void E(string name, string header, float weight, bool ro = false, bool right = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, ReadOnly = ro, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (right) c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            if (ro) c.DefaultCellStyle.ForeColor = Color.DimGray;
            _lines.Columns.Add(c);
        }
        E("Betrag", "Betrag (brutto)", 10, right: true);
        E("Sachkonto", "Sachkonto", 9);
        E("Kontoname", "Bezeichnung", 18, ro: true);
        E("Bu", "BU", 5);
        E("Rechnung", "Rechnungsnr.", 12);
        E("Datum", "Rechnungsdatum", 10);
        E("Text", "Buchungstext", 30);
        editor.Controls.Add(_lines, 0, 2);

        var lineButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        lineButtons.Controls.AddRange(new Control[] { _btnAdd, _btnDel, _btnReset, _btnRegel, _btnOk, _lblSum });
        _btnOk.Margin = new Padding(18, 3, 3, 3);
        editor.Controls.Add(lineButtons, 0, 3);
        editor.Controls.Add(_lblChecks, 0, 4);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(editor);
        // SplitterDistance erst setzen, wenn der Reiter sichtbar ist und seine Größe hat
        bool splitSet = false;
        split.SizeChanged += (_, _) =>
        {
            if (splitSet || split.Height < 300) return;
            splitSet = true;
            try { split.SplitterDistance = split.Height * 55 / 100; } catch (InvalidOperationException) { } catch (ArgumentException) { }
        };

        Controls.Add(split);
        Controls.Add(top);
    }

    private void WireEvents()
    {
        _list.SelectionChanged += (_, _) => ShowEditor();
        _chkOffen.CheckedChanged += (_, _) => RefreshList();
        _btnNeu.Click += (_, _) => OnNeuVorschlagen();
        _btnPersonen.Click += (_, _) => OnPersonen();
        _btnImport.Click += (_, _) => OnImport();
        _btnEinstellungen.Click += (_, _) => OnEinstellungen();
        _btnExport.Click += (_, _) => OnExport();

        _cmbPerson.SelectionChangeCommitted += (_, _) => BeginInvoke(new Action(OnPersonChanged));
        _cmbPerson.Validated += (_, _) => OnPersonChanged();
        _cmbPerson.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OnPersonChanged(); e.SuppressKeyPress = true; } };
        _btnPersonNeu.Click += (_, _) => OnPersonNeu();
        _chkEinbuchen.CheckedChanged += (_, _) =>
        {
            if (_filling || Current is not { Kontierung: { } k }) return;
            k.RechnungEinbuchen = _chkEinbuchen.Checked;
            Changed();
        };
        _btnVorlage.Click += (_, _) => OnVorlage();
        _btnStichwort.Click += (_, _) => OnStichwort();

        _lines.CellEndEdit += OnLineEdited;
        _lines.DataError += (_, e) => e.ThrowException = false;
        _btnAdd.Click += (_, _) => OnAddLine();
        _btnDel.Click += (_, _) => OnDeleteLine();
        _btnReset.Click += (_, _) =>
        {
            if (Current is not { } b) return;
            b.Kontierung = Tp.Vorschlag(b);
            Changed(manual: false);
        };
        _btnRegel.Click += (_, _) => OnRegel();
        _btnOk.Click += (_, _) => OnBestaetigen();
    }

    // ---------------------------------------------------------------------------------------------
    // Daten
    // ---------------------------------------------------------------------------------------------

    /// <summary>Neuer oder neu eingelesener Auszug (null = keiner).</summary>
    public void SetSession(BuchhaltungSession? session)
    {
        _session = session;
        if (session is not null)
        {
            try
            {
                Tp.Reload();
                // Taxpool-Vorlagen neu lesen, wenn sich die gemerkte Taxpool-Datei geändert hat
                var tp = Tp.TaxpoolAktualisieren();
                var restored = Tp.Vorbereiten(session.Statement, session.StatementKey);
                _status((restored > 0 ? $"Kontierung: {restored} gespeicherte Kontierung(en) übernommen." : "Kontierung vorgeschlagen.") +
                        (tp is null ? "" : "  " + tp));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Kontierung", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        FillCombos();
        RefreshList();
    }

    private Booking? Current => _list.CurrentRow?.Tag as Booking;

    private void FillCombos()
    {
        _cmbPerson.BeginUpdate();
        _cmbPerson.Items.Clear();
        _cmbPerson.Items.Add(KeinPersonenkonto);
        foreach (var k in Tp.Personen.Konten.OrderBy(k => k.Konto)) _cmbPerson.Items.Add(k.ToString());
        _cmbPerson.EndUpdate();

        var keep = _cmbVorlage.SelectedItem as KontierungsRegel;
        _cmbVorlage.BeginUpdate();
        _cmbVorlage.Items.Clear();
        // eigene Regeln zuerst, danach die Taxpool-Vorlagen alphabetisch
        foreach (var r in Tp.Settings.Regeln.Where(r => r.TaxpoolId is null)) _cmbVorlage.Items.Add(r);
        foreach (var r in Tp.Settings.Regeln.Where(r => r.TaxpoolId is not null).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)) _cmbVorlage.Items.Add(r);
        _cmbVorlage.EndUpdate();
        _cmbVorlage.DropDownWidth = 420;
        var again = keep is null ? null : _cmbVorlage.Items.Cast<KontierungsRegel>().FirstOrDefault(r => r.Name == keep.Name);
        if (again is not null) _cmbVorlage.SelectedItem = again;
        else if (_cmbVorlage.Items.Count > 0) _cmbVorlage.SelectedIndex = 0;
    }

    private void RefreshList()
    {
        var keep = Current;
        _list.Rows.Clear();
        if (_session is null)
        {
            _lblInfo.Text = "Erst in Schritt 1 einen Kontoauszug einlesen.";
            ShowEditor();
            UpdateButtons();
            return;
        }

        var bookings = _session.Statement.Bookings;
        int fehler = 0, offen = 0, ok = 0;
        foreach (var b in bookings)
        {
            var checks = Tp.Pruefen(b);
            bool hasError = checks.Any(c => c.Stufe == PruefStufe.Fehler);
            var k = b.Kontierung;
            if (hasError) fehler++;
            else if (k is { Bestaetigt: true }) ok++;
            else offen++;
            if (_chkOffen.Checked && !hasError && k is { Bestaetigt: true }) continue;

            int idx = _list.Rows.Add(
                b.Number,
                b.Date.ToString("dd.MM.yyyy", De),
                b.Amount.ToString("N2", De),
                b.Category.Text() + (b.Beleglos ? " (BL)" : ""),
                PersonText(k?.Personenkonto),
                KontenText(k),
                StandText(k, hasError),
                HinweisText(k, checks),
                System.Text.RegularExpressions.Regex.Replace(b.Text, @"\s+", " ").Trim());
            var row = _list.Rows[idx];
            row.Tag = b;
            row.DefaultCellStyle.BackColor = hasError ? Color.FromArgb(0xF8, 0xCB, 0xAD)
                : k is { Bestaetigt: true } ? Color.FromArgb(0xE2, 0xEF, 0xDA)
                : Color.FromArgb(0xFF, 0xF2, 0xCC);
        }
        _lblInfo.Text = $"{bookings.Count} Buchungen: {ok} bestätigt, {offen} Vorschlag, {fehler} mit Fehler";

        if (keep is not null)
            foreach (DataGridViewRow r in _list.Rows)
                if (r.Tag == keep) { _list.CurrentCell = r.Cells[0]; break; }
        ShowEditor();
        UpdateButtons();
    }

    private void RefreshCurrentRow()
    {
        if (_list.CurrentRow is not { Tag: Booking b } row) return;
        var checks = Tp.Pruefen(b);
        bool hasError = checks.Any(c => c.Stufe == PruefStufe.Fehler);
        var k = b.Kontierung;
        row.Cells["Person"].Value = PersonText(k?.Personenkonto);
        row.Cells["Konten"].Value = KontenText(k);
        row.Cells["Stand"].Value = StandText(k, hasError);
        row.Cells["Hinweis"].Value = HinweisText(k, checks);
        row.DefaultCellStyle.BackColor = hasError ? Color.FromArgb(0xF8, 0xCB, 0xAD)
            : k is { Bestaetigt: true } ? Color.FromArgb(0xE2, 0xEF, 0xDA)
            : Color.FromArgb(0xFF, 0xF2, 0xCC);
        UpdateSumAndChecks(b);

        var bookings = _session!.Statement.Bookings;
        int fehler = bookings.Count(x => Tp.Pruefen(x).Any(c => c.Stufe == PruefStufe.Fehler));
        int ok = bookings.Count(x => x.Kontierung is { Bestaetigt: true } && !Tp.Pruefen(x).Any(c => c.Stufe == PruefStufe.Fehler));
        _lblInfo.Text = $"{bookings.Count} Buchungen: {ok} bestätigt, {bookings.Count - ok - fehler} Vorschlag, {fehler} mit Fehler";
    }

    private string PersonText(int? konto)
    {
        if (konto is not int k) return "";
        return Tp.Personen.Get(k)?.ToString() ?? k.ToString(CultureInfo.InvariantCulture) + " (unbekannt)";
    }

    private static string KontenText(Kontierung? k)
    {
        if (k is null) return "";
        if (!k.BrauchtSachkonto) return k.Zeilen.Count > 1 ? $"Ausgleich ({k.Zeilen.Count} Re.)" : "Ausgleich";
        var konten = k.Zeilen.Select(z => z.Sachkonto > 0 ? z.Sachkonto.ToString(CultureInfo.InvariantCulture) : "?").ToList();
        return k.Zeilen.Count > 1 ? "Split: " + string.Join(", ", konten) : konten.FirstOrDefault() ?? "";
    }

    private static string StandText(Kontierung? k, bool hasError) =>
        hasError ? "Fehler" : k is null ? "" : k.Bestaetigt ? "bestätigt" : k.Quelle switch
        {
            KontierungsQuelle.Regel => "Vorschlag (Regel)",
            KontierungsQuelle.Abgleich => "Vorschlag (Abgleich)",
            KontierungsQuelle.Manuell => "bearbeitet",
            _ => "offen",
        };

    private static string HinweisText(Kontierung? k, List<Pruefung> checks)
    {
        var parts = checks.Select(c => c.Text).ToList();
        if (k is not null && k.Hinweis.Length > 0 && !k.Bestaetigt) parts.Insert(0, k.Hinweis);
        return string.Join("; ", parts);
    }

    // ---------------------------------------------------------------------------------------------
    // Editor
    // ---------------------------------------------------------------------------------------------

    private void ShowEditor()
    {
        _filling = true;
        try
        {
            _lines.Rows.Clear();
            var b = Current;
            if (b?.Kontierung is not { } k)
            {
                _lblBooking.Text = _session is null ? "" : "Buchung in der Liste auswählen.";
                _cmbPerson.Text = "";
                _chkEinbuchen.Checked = false;
                _lblSum.Text = "";
                _lblChecks.Text = "";
                return;
            }

            _lblBooking.Text = $"{b.Number}   {b.Date:dd.MM.yyyy}   {b.Amount.ToString("N2", De)} EUR   {b.Category.Text()}   " +
                               KontierungsVorschlag.Kurztext(b);
            _cmbPerson.Text = k.Personenkonto is int pk ? (Tp.Personen.Get(pk)?.ToString() ?? pk.ToString(CultureInfo.InvariantCulture)) : KeinPersonenkonto;
            _chkEinbuchen.Checked = k.RechnungEinbuchen;
            _chkEinbuchen.Enabled = k.Personenkonto is not null;

            foreach (var z in k.Zeilen)
            {
                _lines.Rows.Add(
                    z.Betrag.ToString("N2", De),
                    z.Sachkonto > 0 ? z.Sachkonto.ToString(CultureInfo.InvariantCulture) : "",
                    Tp.Settings.KontoName(z.Sachkonto),
                    z.BuSchluessel,
                    z.Rechnungsnummer,
                    z.Belegdatum?.ToString("dd.MM.yyyy", De) ?? "",
                    z.Text);
            }
            _lines.Columns["Sachkonto"]!.HeaderText = k.BrauchtSachkonto ? "Sachkonto" : "Sachkonto (nicht nötig)";
            UpdateSumAndChecks(b);
        }
        finally
        {
            _filling = false;
            UpdateButtons();
        }
    }

    private void UpdateSumAndChecks(Booking b)
    {
        var k = b.Kontierung!;
        var diff = Math.Abs(b.Amount) - k.Summe;
        _lblSum.Text = $"Summe {k.Summe.ToString("N2", De)} von {Math.Abs(b.Amount).ToString("N2", De)}" +
                       (diff == 0 ? "  ✓" : $"  -  Differenz {diff.ToString("N2", De)}");
        _lblSum.ForeColor = diff == 0 ? Color.DarkGreen : Color.Firebrick;
        var checks = Tp.Pruefen(b);
        _lblChecks.ForeColor = checks.Any(c => c.Stufe == PruefStufe.Fehler) ? Color.Firebrick : Color.DarkGoldenrod;
        _lblChecks.Text = checks.Count == 0 ? "" : string.Join("   •   ", checks.Select(c => c.Text));
        _btnOk.Text = k.Bestaetigt ? "Bestätigung zurücknehmen" : "Bestätigen ✓";
    }

    private void UpdateButtons()
    {
        bool has = Current?.Kontierung is not null;
        foreach (var c in new Control[] { _cmbPerson, _btnPersonNeu, _cmbVorlage, _btnVorlage, _btnStichwort, _lines, _btnAdd, _btnDel, _btnReset, _btnRegel, _btnOk })
            c.Enabled = has;
        _chkEinbuchen.Enabled = has && Current!.Kontierung!.Personenkonto is not null;
        _btnNeu.Enabled = _session is not null;
        _btnExport.Enabled = _session is not null;
    }

    /// <summary>Nach einer Änderung: speichern und Anzeige aktualisieren. Von Hand geändert = geprüft.</summary>
    private void Changed(bool manual = true, bool reloadEditor = true)
    {
        if (_session is null || Current?.Kontierung is not { } k) return;
        if (manual)
        {
            k.Quelle = KontierungsQuelle.Manuell;
            k.Bestaetigt = true;
        }
        Save();
        if (reloadEditor) ShowEditor();
        RefreshCurrentRow();
    }

    private void Save()
    {
        if (_session is null) return;
        try { Tp.Speichern(_session.Statement, _session.StatementKey); }
        catch (IOException ex) { _status("Kontierung konnte nicht gespeichert werden: " + ex.Message); }
    }

    private void OnPersonChanged()
    {
        if (_filling || Current is not { Kontierung: { } k } b) return;
        var text = _cmbPerson.Text.Trim();
        int? konto = null;
        if (text.Length > 0 && text != KeinPersonenkonto)
        {
            var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var n))
            {
                MessageBox.Show(this, "Bitte ein Personenkonto aus der Liste wählen oder die Kontonummer eingeben.", "Kontierung", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ShowEditor();
                return;
            }
            konto = n;
        }
        if (konto == k.Personenkonto) return;
        bool vorher = k.Personenkonto is not null;
        k.Personenkonto = konto;
        if (konto is null) k.RechnungEinbuchen = false;
        else if (!vorher) k.RechnungEinbuchen = !b.Category.IsCustomerPayment();
        Changed();
    }

    private void OnPersonNeu()
    {
        if (Current is not { Kontierung: { } k } b) return;
        var art = b.Amount > 0 && !b.Category.IsAmazon() ? PersonenArt.Debitor : PersonenArt.Kreditor;
        var defName = b.Match?.Invoices.FirstOrDefault()?.RecipientFirstLine is { Length: > 0 } r ? r
            : b.Category.IsAmazon() ? "Amazon" : b.PayerName;
        var name = Prompt.Ask(this, $"Neuer {art}", $"Name des {(art == PersonenArt.Debitor ? "Kunden" : "Lieferanten")} " +
                                                  $"(bekommt die nächste freie Nummer; Art ändern über \"Personenkonten ...\"):", defName);
        if (string.IsNullOrWhiteSpace(name)) return;
        var defTerm = b.Category.IsAmazon() ? "AMAZON" : (art == PersonenArt.Kreditor ? b.PayerName : "");
        var term = Prompt.Ask(this, $"Neuer {art}", "Suchbegriff im Kontoauszug, an dem künftige Buchungen erkannt werden (leer = nur über den Namen):", defTerm);
        if (term is null) return;

        var acc = Tp.PersonenkontoAnlegen(name, art, term);
        FillCombos();
        k.Personenkonto = acc.Konto;
        k.RechnungEinbuchen = !b.Category.IsCustomerPayment();
        Changed();
        _status($"Personenkonto {acc} angelegt.");
    }

    private void OnVorlage()
    {
        if (Current is not { } b || _cmbVorlage.SelectedItem is not KontierungsRegel regel) return;
        b.Kontierung = Tp.VorlageAnwenden(b, regel);
        b.Kontierung.Bestaetigt = true;
        Changed(manual: false);
    }

    private void OnLineEdited(object? sender, DataGridViewCellEventArgs e)
    {
        if (_filling || e.RowIndex < 0 || Current is not { Kontierung: { } k } b || e.RowIndex >= k.Zeilen.Count) return;
        var z = k.Zeilen[e.RowIndex];
        var cell = _lines.Rows[e.RowIndex].Cells[e.ColumnIndex];
        var text = Convert.ToString(cell.Value)?.Trim() ?? "";
        var col = _lines.Columns[e.ColumnIndex].Name;
        string? error = null;

        switch (col)
        {
            case "Betrag":
                if (decimal.TryParse(text.Replace("€", ""), NumberStyles.Number | NumberStyles.AllowLeadingSign, De, out var v)) z.Betrag = Math.Round(v, 2);
                else error = "Betrag nicht lesbar";
                cell.Value = z.Betrag.ToString("N2", De);
                break;
            case "Sachkonto":
                if (text.Length == 0) z.Sachkonto = 0;
                else if (int.TryParse(text, out var konto) && konto > 0)
                {
                    bool geaendert = z.Sachkonto != konto;
                    z.Sachkonto = konto;
                    // BU-Schlüssel aus Taxpool vorschlagen (Automatikkonto → keiner)
                    if (geaendert && k.BrauchtSachkonto)
                    {
                        if (Tp.Settings.IstAutomatikkonto(konto)) z.BuSchluessel = "";
                        else if (z.BuSchluessel.Length == 0) z.BuSchluessel = Tp.Settings.BuVorschlag(konto, b.Amount < 0);
                        _lines.Rows[e.RowIndex].Cells["Bu"].Value = z.BuSchluessel;
                    }
                }
                else error = "Sachkonto muss eine Zahl sein";
                cell.Value = z.Sachkonto > 0 ? z.Sachkonto.ToString(CultureInfo.InvariantCulture) : "";
                _lines.Rows[e.RowIndex].Cells["Kontoname"].Value = Tp.Settings.KontoName(z.Sachkonto);
                break;
            case "Bu":
                z.BuSchluessel = text;
                break;
            case "Rechnung":
                z.Rechnungsnummer = text;
                break;
            case "Datum":
                if (text.Length == 0) z.Belegdatum = null;
                else if (DateOnly.TryParse(text, De, DateTimeStyles.None, out var d)) z.Belegdatum = d;
                else error = "Datum bitte als TT.MM.JJJJ";
                cell.Value = z.Belegdatum?.ToString("dd.MM.yyyy", De) ?? "";
                break;
            case "Text":
                z.Text = text.Length > 60 ? text[..60] : text;
                cell.Value = z.Text;
                break;
        }
        if (error is not null) _status(error);
        Changed(reloadEditor: false);
    }

    private void OnAddLine()
    {
        if (Current is not { Kontierung: { } k } b) return;
        var rest = Math.Abs(b.Amount) - k.Summe;
        var first = k.Zeilen.FirstOrDefault();
        k.Zeilen.Add(new KontierungsZeile
        {
            Betrag = rest > 0 ? rest : 0,
            BuSchluessel = "",
            Belegdatum = first?.Belegdatum,
            Text = first?.Text ?? KontierungsVorschlag.Kurztext(b),
        });
        Changed();
        if (_lines.Rows.Count > 0) _lines.CurrentCell = _lines.Rows[^1].Cells["Sachkonto"];
    }

    private void OnDeleteLine()
    {
        if (Current is not { Kontierung: { } k } || _lines.CurrentCell is null) return;
        int i = _lines.CurrentCell.RowIndex;
        if (i < 0 || i >= k.Zeilen.Count) return;
        if (k.Zeilen.Count == 1)
        {
            MessageBox.Show(this, "Mindestens eine Zeile muss bleiben.", "Kontierung", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _lines.EndEdit();
        k.Zeilen.RemoveAt(i);
        Changed();
    }

    private void OnRegel()
    {
        if (Current is not { Kontierung: { } k } b) return;
        var z = k.Zeilen[0];
        if (k.BrauchtSachkonto && z.Sachkonto <= 0)
        {
            MessageBox.Show(this, "Bitte zuerst ein Sachkonto eintragen.", "Kontierung", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var defTerm = b.Category.IsAmazon() ? "AMAZON" : b.PayerName.Length > 0 ? b.PayerName : KontierungsVorschlag.Kurztext(b);
        var term = Prompt.Ask(this, "Als Regel speichern",
            "Stichwort im Buchungstext, an dem die Regel greift (Alternativen mit | trennen). Die Regel bekommt Vorrang vor älteren Regeln." +
            (k.Zeilen.Count > 1 ? " Hinweis: gespeichert wird die erste Zeile, Splits teilst du je Buchung." : ""), defTerm);
        if (string.IsNullOrWhiteSpace(term)) return;
        var name = Prompt.Ask(this, "Als Regel speichern", "Name der Regel:", term);
        if (string.IsNullOrWhiteSpace(name)) return;

        Tp.RegelSpeichern(new KontierungsRegel
        {
            Name = name,
            Stichwort = term,
            Richtung = b.Amount < 0 ? Richtung.Ausgang : Richtung.Eingang,
            Sachkonto = z.Sachkonto,
            BuSchluessel = z.BuSchluessel,
            Personenkonto = k.Personenkonto ?? 0,
            RechnungEinbuchen = k.RechnungEinbuchen,
        });
        FillCombos();
        _status($"Regel \"{name}\" gespeichert. \"Vorschläge neu\" wendet sie auf offene Buchungen an.");
    }

    private void OnBestaetigen()
    {
        if (Current is not { Kontierung: { } k } b) return;
        if (!k.Bestaetigt && Tp.Pruefen(b).Any(c => c.Stufe == PruefStufe.Fehler))
        {
            MessageBox.Show(this, "Die Kontierung hat noch Fehler (siehe unten).", "Kontierung", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        k.Bestaetigt = !k.Bestaetigt;
        Save();
        RefreshCurrentRow();
        if (!k.Bestaetigt) return;

        // zur nächsten nicht bestätigten Buchung springen
        int start = _list.CurrentRow?.Index ?? -1;
        for (int i = start + 1; i < _list.Rows.Count; i++)
        {
            if (_list.Rows[i].Tag is Booking { Kontierung.Bestaetigt: false })
            {
                _list.CurrentCell = _list.Rows[i].Cells[0];
                return;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Werkzeuge
    // ---------------------------------------------------------------------------------------------

    private void OnNeuVorschlagen()
    {
        if (_session is null) return;
        try
        {
            Tp.Reload();
            var n = Tp.NeuVorschlagen(_session.Statement);
            Save();
            FillCombos();
            RefreshList();
            _status($"{n} Buchung(en) neu vorgeschlagen (bestätigte und von Hand bearbeitete bleiben unverändert).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Vorschläge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnPersonen()
    {
        Tp.Reload();
        using var dlg = new PersonAccountsForm(Tp.Personen, Tp.Settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Tp.Personen.Konten = dlg.Result;
        Tp.SavePersonen();
        FillCombos();
        if (_session is not null)
        {
            Tp.NeuVorschlagen(_session.Statement);
            Save();
        }
        RefreshList();
    }

    private void OnStichwort()
    {
        if (_cmbVorlage.SelectedItem is not KontierungsRegel regel) return;
        var def = regel.Stichwort.Length > 0 ? regel.Stichwort
            : Current is { } b ? (b.Category.IsAmazon() ? "AMAZON" : b.PayerName.Length > 0 ? b.PayerName : "") : "";
        var term = Prompt.Ask(this, "Vorlage automatisch vorschlagen",
            $"Stichwort im Buchungstext für \"{regel.Name}\" (Alternativen mit | trennen, z. B. TELEKOM|T-MOBILE). " +
            "Leer lassen = nur von Hand anwenden.", def);
        if (term is null) return;
        regel.Stichwort = term;
        if (term.Length > 0 && regel.Richtung == Richtung.Alle && Current is { } cur)
            regel.Richtung = cur.Amount < 0 ? Richtung.Ausgang : Richtung.Eingang;
        Tp.SaveSettings();
        FillCombos();
        if (_session is not null)
        {
            var n = Tp.NeuVorschlagen(_session.Statement);
            Save();
            RefreshList();
            _status(term.Length > 0 ? $"\"{regel.Name}\" greift jetzt bei \"{term}\" ({n} offene Buchung(en) neu vorgeschlagen)." : $"\"{regel.Name}\" ist wieder eine reine Vorlage.");
        }
    }

    private void OnImport()
    {
        var bisher = Tp.Settings.TaxpoolDatei;
        using var dlg = new OpenFileDialog
        {
            Title = "Taxpool-Datensicherung (*.dbb) wählen - oder einen CSV-Export (Kontenplan, Personenkonten, Vorlagen)",
            Filter = "Taxpool-Datensicherung (*.dbb)|*.dbb|Taxpool-Tabelle (*.dbd)|*.dbd|CSV-Export (*.csv;*.txt)|*.csv;*.txt|Alle Dateien (*.*)|*.*",
            Multiselect = true,
        };
        if (bisher.Length > 0 && File.Exists(bisher))
        {
            dlg.InitialDirectory = Path.GetDirectoryName(bisher);
            dlg.FileName = Path.GetFileName(bisher);
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var results = Tp.ImportTaxpool(dlg.FileNames);
            FillCombos();
            if (_session is not null)
            {
                Tp.NeuVorschlagen(_session.Statement);
                Save();
            }
            RefreshList();
            var msg = string.Join(Environment.NewLine + Environment.NewLine, results.Select(r => string.Join(Environment.NewLine, r.Hinweise)));
            MessageBox.Show(this, msg + Environment.NewLine + Environment.NewLine +
                                  "Die Vorlagen stehen unter \"Vorlage\" und lassen sich je Buchung anwenden. Mit \"Automatisch ...\" " +
                                  "hinterlegst du ein Stichwort, dann wird die Vorlage passenden Buchungen selbst vorgeschlagen." +
                                  (Tp.Settings.TaxpoolDatei.Length > 0
                                      ? Environment.NewLine + Environment.NewLine + "Die Taxpool-Datei ist gemerkt: Ändert sie sich (neue Datensicherung), " +
                                        "liest das Tool die Vorlagen beim nächsten Einlesen eines Auszugs automatisch neu."
                                      : ""),
                "Aus Taxpool lesen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Aus Taxpool lesen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnEinstellungen()
    {
        try
        {
            Tp.SaveSettings(); // legt die Datei beim ersten Mal an
            Process.Start(new ProcessStartInfo(Tp.SettingsPath) { UseShellExecute = true });
            MessageBox.Show(this,
                "In buchhaltung.json stehen Kontenrahmen, Bankkonto, Berater-/Mandantennummer, Nummernkreise der Personenkonten, " +
                "das Erlöskonto für die Sollstellung und die Regeln/Vorlagen.\n\nNach dem Speichern \"Vorschläge neu\" klicken.",
                "Kontierung", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Kontierung", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnExport()
    {
        if (_session is null) return;
        _lines.EndEdit();
        var output = _outputFolder();
        if (output.Length == 0)
        {
            MessageBox.Show(this, "Bitte in Schritt 1 einen Ausgabeordner angeben.", "Kontierung", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var exporte = Tp.Exporte(_session.StatementKey);
        if (exporte.Count > 0)
        {
            var last = exporte[^1];
            var q = $"Dieser Auszug wurde schon am {last.Am:dd.MM.yyyy HH:mm} exportiert ({Path.GetFileName(last.Datei)}).\n\n" +
                    "Wurde die Datei bereits in Taxpool importiert, würden die Buchungen doppelt gebucht. Trotzdem neu erzeugen?";
            if (MessageBox.Show(this, q, "Taxpool-Importdatei", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }

        int unbestaetigt = _session.Statement.Bookings.Count(b => b.Kontierung is { Bestaetigt: false });
        if (unbestaetigt > 0)
        {
            var q = $"{unbestaetigt} Buchung(en) sind nur Vorschläge und noch nicht bestätigt (gelb). Trotzdem exportieren?";
            if (MessageBox.Show(this, q, "Taxpool-Importdatei", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }

        try
        {
            var r = Tp.ExportKontoauszug(_session.Statement, _session.StatementKey, output, _service.NummernEndgueltig(_session));
            var hinweise = r.Hinweise.Where(h => h.Stufe == PruefStufe.Hinweis).ToList();
            var msg = $"Fertig: {r.Buchungssaetze} Buchungssätze aus {r.Belege} Kontoauszugsbuchungen.\n\n" +
                      $"Importdatei: {Path.GetFileName(r.Datei)}\n" +
                      (r.PersonenkontenDatei is null ? "" : $"Verwendete Personenkonten: {Path.GetFileName(r.PersonenkontenDatei)}\n") +
                      "\nIn Taxpool über den Import im DATEV-Format (Buchungsstapel) einlesen. Die Buchungen sind nicht festgeschrieben " +
                      "und lassen sich dort noch prüfen." +
                      (hinweise.Count > 0 ? "\n\nHinweise:\n" + string.Join("\n", hinweise.Take(10).Select(h => "• " + h.Nummer + ": " + h.Text)) : "") +
                      "\n\nAusgabeordner öffnen?";
            _status($"Taxpool-Importdatei erzeugt: {Path.GetFileName(r.Datei)}");
            RefreshList();
            if (MessageBox.Show(this, msg, "Taxpool-Importdatei", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Taxpool-Importdatei", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _chkOffen.Checked = true;
        }
    }
}
