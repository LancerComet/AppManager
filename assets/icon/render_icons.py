"""Regenerate all Windows app icon assets from the authored SVGs.

Usage (isolated packages, no global installs — the machine's python may lack venv):

    python -m pip install --target "%LOCALAPPDATA%\\Temp\\icon-pkgs" resvg-py Pillow
    python assets/icon/render_icons.py

If the packages live elsewhere, pass --pkgs <dir>. Outputs land in
src/AppManager/Assets and artifacts/icon-preview.png relative to the repo root
(this file's grandparent directory).

Glyph rule: AppIcon-small.svg is used whenever the rendered glyph is <=32 px,
AppIcon.svg otherwise. The SVG viewBox is 256 and the glyph spans 224/256
(~1/16 padding each side), so "glyph fills canvas" outputs leave a small inset.
"""

from __future__ import annotations

import argparse
import io
import os
import struct
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ICON_DIR = Path(__file__).resolve().parent
ASSETS = REPO / "src" / "AppManager" / "Assets"
PREVIEW = REPO / "artifacts" / "icon-preview.png"

SVG_MAIN = ICON_DIR / "AppIcon.svg"
SVG_SMALL = ICON_DIR / "AppIcon-small.svg"

# viewBox units the authored glyph spans within the 256 box
GLYPH_UNITS = 224.0
VIEWBOX = 256.0


def _import_deps(pkgs: str | None):
    if pkgs:
        sys.path.insert(0, pkgs)
    else:
        default = Path(tempfile.gettempdir()) / "icon-pkgs"
        if default.is_dir():
            sys.path.insert(0, str(default))
    import resvg_py  # noqa: PLC0415
    from PIL import Image  # noqa: PLC0415

    return resvg_py, Image


def render_svg(resvg_py, Image, svg_path: Path, box: int) -> "Image.Image":
    """Rasterize the whole 256 viewBox onto a `box` x `box` RGBA image."""
    png = resvg_py.svg_to_bytes(
        svg_string=svg_path.read_text(encoding="utf-8"),
        width=box,
        height=box,
    )
    return Image.open(io.BytesIO(png)).convert("RGBA")


def canvas_for_glyph(glyph_px: float) -> int:
    """Canvas size that makes the authored glyph render at `glyph_px` pixels."""
    return max(1, round(glyph_px * VIEWBOX / GLYPH_UNITS))


def pick_svg(glyph_px: float) -> Path:
    return SVG_SMALL if glyph_px <= 32 else SVG_MAIN


def render_fill(resvg_py, Image, canvas: int) -> "Image.Image":
    """Glyph sized so it spans the full canvas (authored padding preserved)."""
    glyph = canvas * GLYPH_UNITS / VIEWBOX
    return render_svg(resvg_py, Image, pick_svg(glyph), canvas)


def render_glyph_on(resvg_py, Image, w: int, h: int, glyph_px: float) -> "Image.Image":
    """Transparent canvas w x h with the glyph centered at `glyph_px` px."""
    box = canvas_for_glyph(glyph_px)
    g = render_svg(resvg_py, Image, pick_svg(glyph_px), box)
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    out.paste(g, ((w - box) // 2, (h - box) // 2), g)
    return out


def write_ico(path: Path, frames: dict[int, "Image.Image"]) -> None:
    """Write a .ico with one PNG-compressed frame per size (Vista+ style)."""
    data = [(s, io.BytesIO()) for s in frames]
    for size, buf in data:
        frames[size].save(buf, format="PNG")
    entries = []
    offset = 6 + 16 * len(data)
    body = bytearray()
    for size, buf in data:
        png = buf.getvalue()
        w = size if size < 256 else 0
        entries.append(struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(png), offset))
        body += png
        offset += len(png)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(data)))
        for e in entries:
            f.write(e)
        f.write(bytes(body))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pkgs", default=None, help="dir containing resvg_py + PIL")
    args = ap.parse_args()
    resvg_py, Image = _import_deps(args.pkgs)

    ASSETS.mkdir(parents=True, exist_ok=True)
    produced: list[Path] = []

    def save(img, name: str) -> None:
        p = ASSETS / name
        img.save(p)
        produced.append(p)

    scales = [(100, 1.0), (125, 1.25), (150, 1.5), (200, 2.0), (400, 4.0)]

    # ---- Square44x44Logo: app-tile logo, glyph fills the canvas ----
    for pct, f in scales:
        save(render_fill(resvg_py, Image, round(44 * f)),
             f"Square44x44Logo.scale-{pct}.png")

    # ---- Square44x44Logo targetsize + unplated variants (fill canvas) ----
    for size in (16, 24, 32, 48, 256):
        img = render_fill(resvg_py, Image, size)
        save(img, f"Square44x44Logo.targetsize-{size}.png")
        save(img, f"Square44x44Logo.targetsize-{size}_altform-unplated.png")
        save(img, f"Square44x44Logo.targetsize-{size}_altform-lightunplated.png")

    # ---- Square150x150Logo: glyph at 50% tile width, centered, transparent ----
    for pct, f in scales:
        c = round(150 * f)
        save(render_glyph_on(resvg_py, Image, c, c, c * 0.5),
             f"Square150x150Logo.scale-{pct}.png")

    # ---- Wide310x150Logo: glyph height = 50% tile height, centered ----
    for pct, f in scales:
        w, h = round(310 * f), round(150 * f)
        save(render_glyph_on(resvg_py, Image, w, h, h * 0.5),
             f"Wide310x150Logo.scale-{pct}.png")

    # ---- SplashScreen 620x300: glyph ~50% of height, centered, transparent ----
    for pct, f in scales:
        w, h = round(620 * f), round(300 * f)
        save(render_glyph_on(resvg_py, Image, w, h, h * 0.5),
             f"SplashScreen.scale-{pct}.png")

    # ---- LockScreenLogo (monochrome badge slot; keep full art) ----
    for pct, f in ((100, 1.0), (200, 2.0)):
        save(render_fill(resvg_py, Image, round(24 * f)),
             f"LockScreenLogo.scale-{pct}.png")

    # ---- StoreLogo: glyph fills canvas ----
    for pct, f in scales:
        save(render_fill(resvg_py, Image, round(50 * f)),
             f"StoreLogo.scale-{pct}.png")

    # ---- AppIcon.ico: small SVG for <=32 px frames ----
    frames = {s: render_fill(resvg_py, Image, s)
              for s in (16, 20, 24, 32, 40, 48, 64, 256)}
    ico = ASSETS / "AppIcon.ico"
    write_ico(ico, frames)
    produced.append(ico)

    # ---- preview sheet: light + dark rows, labelled sizes ----
    sizes = (16, 24, 32, 48, 64, 256)
    pad, label_h, cell = 16, 22, 300
    sheet_w = pad * 2 + sum(s + pad for s in sizes)
    sheet_h = pad * 2 + 2 * (256 + label_h + 12) + 16
    from PIL import ImageDraw  # noqa: PLC0415

    sheet = Image.new("RGB", (sheet_w, sheet_h), (128, 128, 128))
    draw = ImageDraw.Draw(sheet)
    for row, bg in enumerate(((0xF3, 0xF3, 0xF3), (0x20, 0x20, 0x20))):
        y0 = pad + row * (256 + label_h + 12 + 16)
        x = pad
        rowbox = (pad, y0, sheet_w - pad, y0 + 256 + label_h)
        draw.rectangle(rowbox, fill=bg)
        for s in sizes:
            g = render_fill(resvg_py, Image, s)
            sheet.paste(g, (x, y0 + (256 - s) // 2), g)
            draw.text((x, y0 + 256 + 4), f"{s}px",
                      fill=(0, 0, 0) if bg[0] > 128 else (255, 255, 255))
            x += s + pad
    PREVIEW.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(PREVIEW)

    print(f"Wrote {len(produced)} asset files to {ASSETS}")
    for p in produced:
        print(" ", p.name)
    print("Preview:", PREVIEW)


if __name__ == "__main__":
    main()
