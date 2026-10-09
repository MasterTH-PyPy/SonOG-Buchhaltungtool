using SonOG.Buchhaltung.Infrastructure;

namespace SonOG.Buchhaltung.App;

/// <summary>Verwaltung der IMAP-Postfächer. Passwörter werden nur verschlüsselt (Windows-DPAPI) gespeichert.</summary>
public sealed class MailAccountsForm : Form
{
    private readonly List<MailAccount> _accounts;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 993, Width = 80 };
    private readonly TextBox _user = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _folders = new() { Dock = DockStyle.Fill, Text = "INBOX" };
    private readonly CheckBox _enabled = new() { Text = "aktiv", Checked = true, AutoSize = true };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private bool _loading;

    public MailAccountsForm(List<MailAccount> accounts)
    {
        _accounts = accounts.Select(a => new MailAccount
        {
            Name = a.Name, Host = a.Host, Port = a.Port, User = a.User, PasswordProtected = a.PasswordProtected,
            Folders = a.Folders, Enabled = a.Enabled,
        }).ToList();

        Text = "Mail-Postfächer";
        ClientSize = new Size(860, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var left = new Panel { Dock = DockStyle.Left, Width = 210, Padding = new Padding(8) };
        var btnAdd = new Button { Text = "Hinzufügen", Dock = DockStyle.Bottom, Height = 32 };
        var btnDel = new Button { Text = "Entfernen", Dock = DockStyle.Bottom, Height = 32 };
        left.Controls.Add(_list);
        left.Controls.Add(btnDel);
        left.Controls.Add(btnAdd);

        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c)
        {
            form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) }, 0, form.RowCount++);
            form.Controls.Add(c, 1, form.RowCount - 1);
        }
        form.RowCount = 0;
        Row("Bezeichnung:", _name);
        Row("IMAP-Server:", _host);
        Row("Port:", _port);
        Row("Benutzer:", _user);
        Row("Passwort:", _password);
        Row("Ordner:", _folders);
        Row("", _enabled);
        _hint.Text = "web.de: imap.web.de, Port 993, in den web.de-Einstellungen IMAP-Zugriff erlauben.\n" +
                     "Gmail: imap.gmail.com, Port 993, 2-Faktor aktivieren und ein App-Passwort verwenden.\n" +
                     "Roundcube: IMAP-Server des eigenen Hosters.\n" +
                     "Mehrere Ordner mit Semikolon trennen. Es wird nur gelesen, nichts gelöscht oder verschoben. " +
                     "Leeres Passwortfeld = Passwort unverändert lassen.";
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(_hint, 1, form.RowCount++);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
        var test = new Button { Text = "Verbindung testen", AutoSize = true, MinimumSize = new Size(140, 30) };
        test.Click += async (_, _) =>
        {
            Commit();
            if (_shown < 0 || _shown >= _accounts.Count) return;
            var a = _accounts[_shown];
            test.Enabled = false;
            test.Text = "Teste ...";
            var msg = await Task.Run(() => SonOG.Buchhaltung.Infrastructure.Mail.MailReceiptFinder.TestConnection(a));
            test.Text = "Verbindung testen";
            test.Enabled = true;
            MessageBox.Show(this, msg, "Verbindungstest");
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 30) };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 30) };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(ok);
        bottom.Controls.Add(test);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(form);
        Controls.Add(left);
        Controls.Add(bottom);

        btnAdd.Click += (_, _) =>
        {
            Commit();
            _accounts.Add(new MailAccount { Name = "Neues Postfach", Port = 993 });
            Rebind(_accounts.Count - 1);
        };
        btnDel.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            _accounts.RemoveAt(_list.SelectedIndex);
            Rebind(Math.Min(_list.SelectedIndex, _accounts.Count - 1));
        };
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        ok.Click += (_, _) => Commit();
        if (_accounts.Count == 0) _accounts.Add(new MailAccount { Name = "Postfach 1", Port = 993 });
        Rebind(0);
    }

    public List<MailAccount> Result => _accounts;

    private int _shown = -1;

    private void Rebind(int select)
    {
        _loading = true;
        _list.Items.Clear();
        foreach (var a in _accounts) _list.Items.Add(a.Name.Length > 0 ? a.Name : a.Host);
        _shown = -1;
        _loading = false;
        if (select >= 0 && select < _list.Items.Count) _list.SelectedIndex = select; else ShowSelected();
    }

    private void ShowSelected()
    {
        if (_loading) return;
        Commit();
        _shown = _list.SelectedIndex;
        var has = _shown >= 0 && _shown < _accounts.Count;
        foreach (Control c in new Control[] { _name, _host, _port, _user, _password, _folders, _enabled }) c.Enabled = has;
        if (!has) return;
        var a = _accounts[_shown];
        _name.Text = a.Name; _host.Text = a.Host; _port.Value = a.Port; _user.Text = a.User;
        _password.Text = ""; _folders.Text = a.Folders; _enabled.Checked = a.Enabled;
    }

    private void Commit()
    {
        if (_shown < 0 || _shown >= _accounts.Count) return;
        var a = _accounts[_shown];
        a.Name = _name.Text.Trim(); a.Host = _host.Text.Trim(); a.Port = (int)_port.Value; a.User = _user.Text.Trim();
        a.Folders = _folders.Text.Trim().Length > 0 ? _folders.Text.Trim() : "INBOX"; a.Enabled = _enabled.Checked;
        if (_password.Text.Length > 0) { a.SetPassword(_password.Text); _password.Text = ""; }
        if (_list.Items.Count > _shown) { _loading = true; _list.Items[_shown] = a.Name.Length > 0 ? a.Name : a.Host; _loading = false; }
    }
}
