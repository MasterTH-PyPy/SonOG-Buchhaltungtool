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
        // mehrzeilige Texte: Label und Dialog wachsen mit
        int h = Math.Max(34, TextRenderer.MeasureText(label, form.Font, new Size(436, 0), TextFormatFlags.WordBreak).Height + 4);
        form.ClientSize = new Size(460, 96 + h);
        var lbl = new Label { Text = label, Left = 12, Top = 12, Width = 436, Height = h };
        var box = new TextBox { Left = 12, Top = 16 + h, Width = 436, Text = value };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 282, Top = 54 + h, Width = 80 };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, Left = 368, Top = 54 + h, Width = 80 };
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

/// <summary>Versionsstand der Anwendung (beim Bauen aus Git gebildet, siehe Directory.Build.props).</summary>
internal static class AppVersion
{
    public static string Text { get; } =
        typeof(AppVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "?";
}
