using System.Globalization;
using SonOG.Buchhaltung.Core.Accounting;

namespace SonOG.Buchhaltung.App;

/// <summary>Sachkonto über Bezeichnung oder Nummer suchen (alle eingegebenen Wörter müssen vorkommen).</summary>
internal sealed class KontoSucheForm : Form
{
    private readonly TextBox _txt = new() { Dock = DockStyle.Top, PlaceholderText = "Bezeichnung oder Nummer, z. B. \"büro\", \"kfz rep\", \"49\"" };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly Label _lbl = new() { Dock = DockStyle.Bottom, Height = 22, ForeColor = Color.DimGray, Padding = new Padding(4, 4, 0, 0) };
    private readonly List<(int Nr, string Name, string Suchtext)> _konten;
    private readonly AccountingSettings _s;

    public int Konto { get; private set; }

    public KontoSucheForm(AccountingSettings s, string suche = "")
    {
        _s = s;
        Text = "Sachkonto suchen";
        Icon = AppIcon.Get();
        Width = 640;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;

        _konten = s.Kontenplan
            .Where(k => !s.IstPersonenkonto(k.Key))
            .Select(k => (k.Key, k.Value, (k.Key.ToString(CultureInfo.InvariantCulture) + " " + k.Value).ToLowerInvariant()))
            .OrderBy(k => k.Key).ToList();

        _list.Columns.Add("Konto", 70);
        _list.Columns.Add("Bezeichnung", 420);
        _list.Columns.Add("Steuer", 110);

        var ok = new Button { Text = "Übernehmen", AutoSize = true };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Uebernehmen();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(_list);
        Controls.Add(_txt);
        Controls.Add(_lbl);
        Controls.Add(buttons);
        CancelButton = cancel;

        _txt.TextChanged += (_, _) => Filtern();
        _txt.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Down or Keys.Up && _list.Items.Count > 0)
            {
                _list.Focus();
                if (_list.SelectedIndices.Count == 0) _list.Items[0].Selected = true;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter) { Uebernehmen(); e.SuppressKeyPress = true; }
        };
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Uebernehmen(); e.SuppressKeyPress = true; } };
        _list.DoubleClick += (_, _) => Uebernehmen();

        _txt.Text = suche;
        Filtern();
        Shown += (_, _) => { _txt.Focus(); _txt.SelectionStart = _txt.Text.Length; };
    }

    /// <summary>Konten, die zu allen Wörtern passen (für die direkte Übernahme bei genau einem Treffer).</summary>
    public static List<int> Treffer(AccountingSettings s, string suche)
    {
        var woerter = Woerter(suche);
        if (woerter.Length == 0) return new List<int>();
        return s.Kontenplan
            .Where(k => !s.IstPersonenkonto(k.Key))
            .Where(k => woerter.All(w => (k.Key.ToString(CultureInfo.InvariantCulture) + " " + k.Value).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(k => k.Key).OrderBy(k => k).ToList();
    }

    private static string[] Woerter(string s) => s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void Filtern()
    {
        var woerter = Woerter(_txt.Text);
        var treffer = _konten.Where(k => woerter.All(w => k.Suchtext.Contains(w))).Take(500).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var (nr, name, _) in treffer)
        {
            var steuer = _s.IstAutomatikkonto(nr) ? "Automatik"
                : _s.BuVorschlag(nr, true) is { Length: > 0 } bu ? "BU " + bu
                : "";
            _list.Items.Add(new ListViewItem(new[] { nr.ToString(CultureInfo.InvariantCulture), name, steuer }) { Tag = nr });
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        _lbl.Text = _konten.Count == 0
            ? "Kein Kontenplan vorhanden - bitte zuerst \"Aus Taxpool lesen ...\""
            : $"{treffer.Count}{(treffer.Count == 500 ? "+" : "")} von {_konten.Count} Konten";
    }

    private void Uebernehmen()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not int nr) return;
        Konto = nr;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Sucht ein Konto: genau ein Treffer → direkt, sonst Dialog. 0 = abgebrochen.</summary>
    public static int Suchen(IWin32Window owner, AccountingSettings s, string suche)
    {
        var t = Treffer(s, suche);
        if (t.Count == 1) return t[0];
        using var dlg = new KontoSucheForm(s, suche);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Konto : 0;
    }
}
