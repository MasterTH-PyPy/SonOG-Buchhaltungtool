"""Erzeugt das App-Icon (app.ico) in allen Windows-Größen.

Motiv: Beleg mit SonOG-Logo und T-Konto (Soll | Haben) auf blauem Grund, grüner Haken = abgeglichen.
Das Logo ist aus dem 16-px-Favicon von sonog.de nachgezeichnet (gestapeltes "S").
Aufruf: python tools/make_icon.py   (benötigt Pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "src" / "SonOG.Buchhaltung.App" / "Resources"
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SS = 4  # Supersampling

BLUE_TOP = (30, 86, 224)
BLUE_BOTTOM = (13, 51, 179)
PAPER = (255, 255, 255)
PAPER_SHADE = (220, 228, 245)
INK = (13, 51, 179)
LINE = (168, 182, 214)
GREEN = (22, 163, 74)
GREEN_DARK = (21, 128, 61)


def draw_logo(d: ImageDraw.ImageDraw, x0: float, y0: float, w: float) -> None:
    """SonOG-Logo: oberer Block nach links, unterer nach rechts versetzt, helle Fugen dazwischen."""
    h = w
    dark, mid, light = (14, 16, 18), (52, 56, 58), (160, 166, 170)
    r = w * 0.20
    d.rounded_rectangle([x0 + 0.06 * w, y0, x0 + 0.80 * w, y0 + 0.54 * h], radius=r, fill=mid)
    d.rounded_rectangle([x0 + 0.20 * w, y0 + 0.46 * h, x0 + 0.94 * w, y0 + h], radius=r, fill=dark)
    d.rectangle([x0 + 0.20 * w, y0 + 0.46 * h, x0 + 0.80 * w, y0 + 0.54 * h], fill=dark)
    lw = max(1.0, 0.06 * h)
    d.rounded_rectangle([x0 + 0.24 * w, y0 + 0.28 * h, x0 + 0.72 * w, y0 + 0.28 * h + lw], radius=lw / 2, fill=light)
    d.rounded_rectangle([x0 + 0.28 * w, y0 + 0.70 * h, x0 + 0.76 * w, y0 + 0.70 * h + lw], radius=lw / 2, fill=light)


def draw(size: int) -> Image.Image:
    S = size * SS
    u = S / 256.0  # Entwurfsraster 256 x 256
    small = size <= 24

    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Hintergrund: abgerundetes Quadrat mit vertikalem Verlauf
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / max(1, S - 1)
        c = tuple(round(BLUE_TOP[i] + (BLUE_BOTTOM[i] - BLUE_TOP[i]) * t) for i in range(3))
        gd.line([(0, y), (S, y)], fill=c + (255,))
    mask = Image.new("L", (S, S), 0)
    pad = 0 if small else 8 * u
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, S - pad, S - pad], radius=(40 if not small else 48) * u, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)

    # Beleg (Papier) mit umgeknickter Ecke
    px0, py0, px1, py1 = (60 * u, 40 * u, 180 * u, 206 * u) if not small else (52 * u, 34 * u, 190 * u, 214 * u)
    fold = 34 * u
    if not small:
        # zweiter Beleg dahinter (Belegstapel)
        o = 14 * u
        d.polygon([(px0 + o, py0 + o), (px1 + o - fold, py0 + o), (px1 + o, py0 + o + fold), (px1 + o, py1 + o), (px0 + o, py1 + o)],
                  fill=(196, 210, 242))
        # weicher Schatten des vorderen Belegs
        d.rectangle([px0 + 3 * u, py1, px1 + 3 * u, py1 + 4 * u], fill=(8, 30, 110, 70))
    d.polygon([(px0, py0), (px1 - fold, py0), (px1, py0 + fold), (px1, py1), (px0, py1)], fill=PAPER)
    d.polygon([(px1 - fold, py0), (px1 - fold, py0 + fold), (px1, py0 + fold)], fill=PAPER_SHADE)

    if not small:
        # Kopfzeile: SonOG-Logo und Belegnummer
        draw_logo(d, px0 + 14 * u, py0 + 12 * u, 40 * u)
        d.rounded_rectangle([px0 + 62 * u, py0 + 22 * u, px0 + 96 * u, py0 + 31 * u], radius=4 * u, fill=LINE)
        d.rounded_rectangle([px0 + 62 * u, py0 + 38 * u, px0 + 84 * u, py0 + 46 * u], radius=4 * u, fill=LINE)
        # T-Konto: Soll | Haben
        tx0, tx1 = px0 + 16 * u, px1 - 16 * u
        ty = py0 + 66 * u
        mid = (tx0 + tx1) / 2
        w = 7 * u
        d.rectangle([tx0, ty, tx1, ty + w], fill=INK)
        d.rectangle([mid - w / 2, ty, mid + w / 2, py1 - 22 * u], fill=INK)
        # Buchungszeilen links und rechts
        for i in range(3):
            y = ty + (24 + i * 24) * u
            d.rounded_rectangle([tx0 + 2 * u, y, mid - 12 * u, y + 7 * u], radius=3 * u, fill=LINE)
            if i < 2:
                d.rounded_rectangle([mid + 12 * u, y, tx1 - 2 * u - (14 * u if i == 1 else 0), y + 7 * u], radius=3 * u, fill=LINE)
    else:
        # kleine Größen: nur das Logo auf dem Beleg
        draw_logo(d, px0 + 14 * u, py0 + 20 * u, 112 * u)

    # Grüner Haken-Button unten rechts
    r = (46 if not small else 44) * u
    cx, cy = (190 * u, 186 * u) if not small else (200 * u, 200 * u)
    d.ellipse([cx - r - 6 * u, cy - r - 6 * u, cx + r + 6 * u, cy + r + 6 * u], fill=(255, 255, 255, 255))
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=GREEN)
    lw = int((13 if not small else 20) * u)
    pts = [(cx - r * 0.48, cy + r * 0.02), (cx - r * 0.12, cy + r * 0.36), (cx + r * 0.50, cy - r * 0.34)]
    d.line(pts, fill=(255, 255, 255), width=lw, joint="curve")
    for x, y in (pts[0], pts[2]):
        d.ellipse([x - lw / 2, y - lw / 2, x + lw / 2, y + lw / 2], fill=(255, 255, 255))

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    images = [draw(s) for s in SIZES]
    images[-1].save(OUT / "app.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    draw(512).save(OUT / "app.png")
    print("geschrieben:", OUT / "app.ico")


if __name__ == "__main__":
    main()
