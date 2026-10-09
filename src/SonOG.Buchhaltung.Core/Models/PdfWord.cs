namespace SonOG.Buchhaltung.Core.Models;

/// <summary>
/// Ein Wort aus einem PDF. Koordinaten in Punkt, Ursprung links oben (Y wächst nach unten).
/// Y0/Y1 sind die Ober-/Unterkante der Textzeile (nicht der einzelnen Glyphen), damit alle Wörter
/// einer Zeile denselben Y0 haben.
/// </summary>
public sealed record PdfWord(double X0, double Y0, double X1, double Y1, string Text);

public sealed record PdfPageWords(int PageIndex, double Width, double Height, IReadOnlyList<PdfWord> Words);
