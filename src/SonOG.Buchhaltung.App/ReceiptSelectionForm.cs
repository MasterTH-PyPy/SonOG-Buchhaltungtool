using System.Diagnostics;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.App;

/// <summary>Zeigt vor dem Druck alle Belege, die ins Druckpaket kommen. Falsche Zuordnungen (z. B. aus Mails) lassen sich abwählen.</summary>
public sealed class ReceiptSelectionForm : Form
{
    private sealed record Item(Booking Booking, string File);

    private readonly List<Item> _items = new();
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false, BackgroundColor = SystemColors.Window,
    };

    public ReceiptSelectionForm(IReadOnlyList<Booking> bookings)
    {
        Text = "Belege für den Druck";
        Size = new Size(1000, 560);
        StartPosition = FormStartPosition.CenterParent;

        foreach (var b in bookings.Where(b => b.ReceiptFiles.Count > 0))
            foreach (var f in b.ReceiptFiles.Distinct()) _items.Add(new Item(b, f));

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Druck", HeaderText = "Drucken", Width = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Nr", HeaderText = "Nr", Width = 80, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Betrag", HeaderText = "Betrag", Width = 80, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Text", HeaderText = "Buchungstext", Width = 260, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Datei", HeaderText = "Beleg (Doppelklick öffnet)", Width = 260, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Hinweis", HeaderText = "Hinweis", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        _grid.Columns["Betrag"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        foreach (var it in _items)
        {
            var fromMail = it.Booking.ReceiptNote.Contains("Mail", StringComparison.OrdinalIgnoreCase);
            var i = _grid.Rows.Add(true, it.Booking.Number, it.Booking.Amount.ToString("N2"),
                System.Text.RegularExpressions.Regex.Replace(it.Booking.Text, @"\s+", " ").Trim(), Path.GetFileName(it.File), it.Booking.ReceiptNote);
            if (fromMail) _grid.Rows[i].DefaultCellStyle.BackColor = Color.FromArgb(0xFF, 0xF2, 0xCC);
        }
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            try { Process.Start(new ProcessStartInfo(_items[e.RowIndex].File) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

        var info = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(8, 8, 8, 0),
            Text = "Gelb markiert = per Mail-Suche gefunden, bitte prüfen. Abgewählte Belege werden nicht gestempelt und nicht gedruckt.",
        };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        var ok = new Button { Text = "Weiter zum Druck", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(_grid);
        Controls.Add(info);
        Controls.Add(bottom);
    }

    public bool HasItems => _items.Count > 0;

    /// <summary>Entfernt abgewählte Belege aus den Buchungen.</summary>
    public List<Booking> ApplyDeselection()
    {
        var changed = new List<Booking>();
        for (int i = 0; i < _items.Count; i++)
        {
            if (_grid.Rows[i].Cells["Druck"].Value is true) continue;
            var b = _items[i].Booking;
            b.ReceiptFiles.Remove(_items[i].File);
            b.ReceiptNote = "Beleg abgewählt: " + Path.GetFileName(_items[i].File);
            if (!changed.Contains(b)) changed.Add(b);
        }
        return changed;
    }
}
