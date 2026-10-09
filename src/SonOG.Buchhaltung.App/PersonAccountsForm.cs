using System.Globalization;
using SonOG.Buchhaltung.Core.Accounting;

namespace SonOG.Buchhaltung.App;

/// <summary>Personenkonten (Debitoren/Kreditoren) bearbeiten.</summary>
internal sealed class PersonAccountsForm : Form
{
    private readonly AccountingSettings _settings;
    private readonly DataGridView _grid = new();

    public List<PersonAccount> Result { get; private set; } = new();

    public PersonAccountsForm(PersonAccountDirectory dir, AccountingSettings settings)
    {
        _settings = settings;
        Text = "Personenkonten (Debitoren und Kreditoren)";
        Icon = AppIcon.Get();
        Width = 1000;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(8, 8, 8, 0),
            Text = $"Debitoren {settings.DebitorenVon}-{settings.DebitorenBis}, Kreditoren {settings.KreditorenVon}-{settings.KreditorenBis}. " +
                   "Suchbegriffe (mit Komma getrennt) erkennen die Buchung im Kontoauszug, z. B. \"AMAZON\". " +
                   "Das Standard-Sachkonto wird vorgeschlagen, wenn keine Regel greift. Konto leer lassen = nächste freie Nummer.",
        };

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = true;
        _grid.AllowUserToDeleteRows = true;
        _grid.RowHeadersWidth = 24;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Konto", HeaderText = "Konto", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", FillWeight = 35 });
        var art = new DataGridViewComboBoxColumn { Name = "Art", HeaderText = "Art", FillWeight = 12, FlatStyle = FlatStyle.Flat };
        art.Items.AddRange("Debitor", "Kreditor");
        _grid.Columns.Add(art);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Such", HeaderText = "Suchbegriffe", FillWeight = 25 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sach", HeaderText = "Standard-Sachkonto", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bu", HeaderText = "BU", FillWeight = 6 });
        _grid.DataError += (_, e) => e.ThrowException = false;

        foreach (var k in dir.Konten.OrderBy(k => k.Konto))
            _grid.Rows.Add(k.Konto.ToString(CultureInfo.InvariantCulture), k.Name, k.Art.ToString(), string.Join(", ", k.Suchbegriffe),
                k.StandardSachkonto > 0 ? k.StandardSachkonto.ToString(CultureInfo.InvariantCulture) : "", k.StandardBuSchluessel);

        var ok = new Button { Text = "Speichern", AutoSize = true };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => OnOk();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(_grid);
        Controls.Add(info);
        Controls.Add(buttons);
        CancelButton = cancel;
    }

    private void OnOk()
    {
        _grid.EndEdit();
        var list = new List<PersonAccount>();
        var errors = new List<string>();
        var tmp = new PersonAccountDirectory();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            string Cell(string c) => Convert.ToString(row.Cells[c].Value)?.Trim() ?? "";
            var name = Cell("Name");
            var kontoText = Cell("Konto");
            if (name.Length == 0 && kontoText.Length == 0) continue;

            var artText = Cell("Art");
            int konto = 0;
            if (kontoText.Length > 0 && !int.TryParse(kontoText, out konto))
            {
                errors.Add($"Zeile {row.Index + 1}: Konto \"{kontoText}\" ist keine Zahl");
                continue;
            }
            PersonenArt art;
            if (artText.Length > 0) art = Enum.Parse<PersonenArt>(artText);
            else if (konto >= _settings.DebitorenVon && konto <= _settings.DebitorenBis) art = PersonenArt.Debitor;
            else if (konto >= _settings.KreditorenVon && konto <= _settings.KreditorenBis) art = PersonenArt.Kreditor;
            else
            {
                errors.Add($"Zeile {row.Index + 1}: bitte Art (Debitor/Kreditor) wählen");
                continue;
            }
            tmp.Konten.AddRange(list);
            if (konto == 0) konto = tmp.NextFree(art, _settings);
            tmp.Konten.Clear();

            bool inRange = art == PersonenArt.Debitor
                ? konto >= _settings.DebitorenVon && konto <= _settings.DebitorenBis
                : konto >= _settings.KreditorenVon && konto <= _settings.KreditorenBis;
            if (!inRange) errors.Add($"{konto} {name}: liegt nicht im Nummernkreis für {art}en");
            if (list.Any(x => x.Konto == konto)) errors.Add($"Konto {konto} ist doppelt");

            int.TryParse(Cell("Sach"), out var sach);
            list.Add(new PersonAccount
            {
                Konto = konto,
                Name = name,
                Art = art,
                Suchbegriffe = Cell("Such").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                StandardSachkonto = sach,
                StandardBuSchluessel = Cell("Bu"),
            });
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors.Take(15)), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result = list.OrderBy(k => k.Konto).ToList();
        DialogResult = DialogResult.OK;
        Close();
    }
}
