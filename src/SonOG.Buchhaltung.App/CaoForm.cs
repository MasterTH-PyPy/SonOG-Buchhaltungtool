using SonOG.Buchhaltung.Infrastructure;

namespace SonOG.Buchhaltung.App;

/// <summary>Zugang zur CAO-Faktura-Datenbank (Kunde, Betrag, Datum der Ausgangsrechnungen über die Rechnungsnummer).</summary>
internal sealed class CaoForm : Form
{
    private readonly CheckBox _chkAktiv = new() { Text = "Ausgangsrechnungen mit der CAO-Datenbank abgleichen", AutoSize = true };
    private readonly TextBox _txtHost = new() { Width = 220 };
    private readonly NumericUpDown _numPort = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    private readonly TextBox _txtDb = new() { Width = 220 };
    private readonly TextBox _txtUser = new() { Width = 220 };
    private readonly TextBox _txtPass = new() { Width = 220, UseSystemPasswordChar = true, PlaceholderText = "(unverändert)" };
    private readonly Label _lblTest = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(420, 0) };
    private readonly CaoVerbindung _alt;

    public CaoVerbindung Result { get; private set; }

    public CaoForm(CaoVerbindung v)
    {
        _alt = v;
        Result = v;
        Text = "CAO-Faktura-Datenbank";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        _chkAktiv.Checked = v.Aktiv;
        _txtHost.Text = v.Host;
        _numPort.Value = Math.Clamp(v.Port, 1, 65535);
        _txtDb.Text = v.Datenbank;
        _txtUser.Text = v.Benutzer;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) });
            grid.Controls.Add(c);
        }
        grid.Controls.Add(_chkAktiv);
        grid.SetColumnSpan(_chkAktiv, 2);
        Row("Server:", _txtHost);
        Row("Port:", _numPort);
        Row("Datenbank:", _txtDb);
        Row("Benutzer:", _txtUser);
        Row("Passwort:", _txtPass);
        var info = new Label
        {
            AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 8, 3, 3),
            Text = "Über die Rechnungsnummer aus dem Dateinamen werden Kundennummer (= Debitor), Kundenname, Brutto und " +
                   "Rechnungsdatum aus der Tabelle JOURNAL gelesen. Es wird nur gelesen, nichts geschrieben.",
        };
        grid.Controls.Add(info);
        grid.SetColumnSpan(info, 2);
        grid.Controls.Add(_lblTest);
        grid.SetColumnSpan(_lblTest, 2);

        var test = new Button { Text = "Verbindung testen", AutoSize = true };
        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        test.Click += (_, _) => Testen();
        ok.Click += (_, _) => { Result = Lesen(); DialogResult = DialogResult.OK; Close(); };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { cancel, ok, test });

        Controls.Add(grid);
        Controls.Add(buttons);
        CancelButton = cancel;
    }

    private CaoVerbindung Lesen()
    {
        var v = new CaoVerbindung
        {
            Aktiv = _chkAktiv.Checked,
            Host = _txtHost.Text.Trim(),
            Port = (int)_numPort.Value,
            Datenbank = _txtDb.Text.Trim(),
            Benutzer = _txtUser.Text.Trim(),
            PasswortGeschuetzt = _alt.PasswortGeschuetzt,
        };
        if (_txtPass.Text.Length > 0) v.SetPasswort(_txtPass.Text);
        return v;
    }

    private void Testen()
    {
        UseWaitCursor = true;
        try
        {
            var n = CaoDatenbank.Testen(Lesen());
            _lblTest.ForeColor = Color.DarkGreen;
            _lblTest.Text = $"Verbindung OK - {n} Belege mit Rechnungsnummer im Journal.";
        }
        catch (Exception ex)
        {
            _lblTest.ForeColor = Color.Firebrick;
            _lblTest.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }
}
