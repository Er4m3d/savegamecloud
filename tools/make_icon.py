"""Génère l'icône de SaveGameCloud : une disquette qui flotte dans les nuages, sur un ciel bleu arrondi.

Usage : python tools/make_icon.py
Produit src/SaveGameCloud/Assets/app.ico (16 à 256 px), app-256.png, et les icônes du site dans docs/.
En 16 px, seule la disquette (agrandie) est dessinée, pour rester lisible.
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
S = 1024  # dessin en haute résolution, puis réduction

SKY_TOP = (120, 185, 255)
SKY_BOTTOM = (52, 116, 232)
NAVY = (27, 40, 74, 255)
METAL = (205, 214, 228, 255)
METAL_SHADE = (170, 182, 202, 255)
LABEL = (255, 255, 255, 255)
LABEL_LINE = (196, 208, 228, 255)
CLOUD = (255, 255, 255, 255)
SHADOW = (12, 40, 110, 70)


def rounded_square_mask(margin: int = 40, radius: int = 220) -> Image.Image:
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((margin, margin, S - margin, S - margin), radius=radius, fill=255)
    return mask


def sky() -> Image.Image:
    gradient = Image.new("RGBA", (S, S))
    draw = ImageDraw.Draw(gradient)
    for y in range(S):
        t = y / (S - 1)
        color = tuple(round(SKY_TOP[i] + (SKY_BOTTOM[i] - SKY_TOP[i]) * t) for i in range(3))
        draw.line([(0, y), (S, y)], fill=color + (255,))
    icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    icon.paste(gradient, (0, 0), rounded_square_mask())
    return icon


def cloud_mask(puffs, base, dy: int = 0) -> Image.Image:
    mask = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(mask)
    for cx, cy, r in puffs:
        d.ellipse((cx - r, cy - r + dy, cx + r, cy + r + dy), fill=255)
    x0, y0, x1, y1, radius = base
    d.rounded_rectangle((x0, y0 + dy, x1, y1 + dy), radius=radius, fill=255)
    return mask


def paste_cloud(icon: Image.Image, puffs, base, clip: Image.Image) -> Image.Image:
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    shadow.paste(SHADOW, (0, 0), _clip(cloud_mask(puffs, base, dy=18), clip))
    icon = Image.alpha_composite(icon, shadow)
    icon.paste(CLOUD, (0, 0), _clip(cloud_mask(puffs, base), clip))
    return icon


def _clip(mask: Image.Image, clip: Image.Image) -> Image.Image:
    return Image.composite(mask, Image.new("L", (S, S), 0), clip)


def draw_floppy(icon: Image.Image, box, with_label_lines: bool = True) -> Image.Image:
    """Disquette 3½" dans la boîte (x0, y0, taille)."""
    x0, y0, size = box
    k = size / 500  # proportions définies pour une disquette de 500 px
    x1, y1 = x0 + size, y0 + size

    def sx(v):
        return x0 + v * k

    def sy(v):
        return y0 + v * k

    # Corps avec le coin supérieur droit coupé
    body = Image.new("L", (S, S), 0)
    bd = ImageDraw.Draw(body)
    bd.rounded_rectangle((x0, y0, x1, y1), radius=round(34 * k), fill=255)
    cut = 74 * k
    bd.polygon([(x1 - cut, y0 - 1), (x1 + 1, y0 - 1), (x1 + 1, y0 + cut)], fill=0)
    # ombre portée
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    shadow.paste(SHADOW, (0, 0), body.transform(body.size, Image.AFFINE, (1, 0, -10 * k, 0, 1, -22 * k)))
    icon = Image.alpha_composite(icon, shadow)
    icon.paste(NAVY, (0, 0), body)

    d = ImageDraw.Draw(icon)
    # Volet métallique et sa fente
    d.rounded_rectangle((sx(118), sy(0), sx(392), sy(168)), radius=round(16 * k), fill=METAL)
    d.rectangle((sx(118), sy(0), sx(392), sy(30)), fill=METAL)
    d.rounded_rectangle((sx(300), sy(30), sx(356), sy(140)), radius=round(10 * k), fill=NAVY)
    d.rectangle((sx(118), sy(150), sx(392), sy(168)), fill=METAL_SHADE)
    # Étiquette
    d.rounded_rectangle((sx(62), sy(226), sx(438), sy(410)), radius=round(22 * k), fill=LABEL)
    if with_label_lines:
        for ly in (272, 316, 360):
            d.rounded_rectangle((sx(104), sy(ly), sx(396), sy(ly + 18)), radius=round(9 * k), fill=LABEL_LINE)
    return icon


def render_full() -> Image.Image:
    clip = rounded_square_mask()
    icon = sky()
    # Disquette
    icon = draw_floppy(icon, (282, 112, 460))
    # Nuages au premier plan : ils ne couvrent que le bas sombre de la disquette, sous l'étiquette
    icon = paste_cloud(
        icon,
        [(215, 770, 110), (335, 650, 112), (480, 640, 128), (640, 660, 120), (760, 690, 105), (850, 790, 95)],
        (120, 740, 915, 900, 70),
        clip,
    )
    return icon


def render_tiny() -> Image.Image:
    """16 px : disquette seule, la plus grande possible, sans les fins détails."""
    icon = sky()
    return draw_floppy(icon, (172, 150, 680), with_label_lines=False)


def main() -> None:
    full = render_full()
    tiny = render_tiny()

    sizes = [256, 128, 64, 48, 32, 24, 16]
    frames = [(tiny if size <= 16 else full).resize((size, size), Image.LANCZOS) for size in sizes]

    assets = ROOT / "src" / "SaveGameCloud" / "Assets"
    docs = ROOT / "docs"
    assets.mkdir(parents=True, exist_ok=True)

    frames[0].save(assets / "app.ico", sizes=[(s, s) for s in sizes], append_images=frames[1:])
    frames[0].save(assets / "app-256.png")
    frames[0].save(docs / "icon-256.png")
    frames[-1].save(docs / "favicon.ico", sizes=[(16, 16), (32, 32)], append_images=[frames[4]])
    full.resize((512, 512), Image.LANCZOS).save(ROOT / "tools" / "icon-preview.png")
    print("Icones generees :", assets / "app.ico", docs / "icon-256.png")


if __name__ == "__main__":
    main()
