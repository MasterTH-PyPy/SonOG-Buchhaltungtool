namespace SonOG.Buchhaltung.App;

/// <summary>Kleiner Eingabedialog (WinForms hat kein InputBox).</summary>
internal static class Prompt
{
    public static string? Ask(IWin32Window owner, string title, string label, string value = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(460, 130),
        };
        var lbl = new Label { Text = label, Left = 12, Top = 12, Width = 436, Height = 34 };
        var box = new TextBox { Left = 12, Top = 50, Width = 436, Text = value };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 282, Top = 88, Width = 80 };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, Left = 368, Top = 88, Width = 80 };
        form.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}

/// <summary>App-Icon aus den eingebetteten Ressourcen.</summary>
internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon? Get()
    {
        if (_icon is not null) return _icon;
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("SonOG.Buchhaltung.App.Resources.app.ico");
        _icon = s is null ? null : new Icon(s);
        return _icon;
    }
}
