"""
Renders app/Relic.App/assets/relic.ico — Relic's brand icon: the gold gem from the UI
(the `I.diamond` mark in ui/app.js) sitting on the dark navy tile of the app's own title bar.

Run from anywhere:  python build\\make_icon.py  [--preview <path.png>]

The .ico this writes is COMMITTED to the repo, so nothing in the normal pipeline needs Pillow:
<ApplicationIcon> wants the file to already exist at compile time, and Inno Setup reads it straight
from the source tree. Re-run this only when the artwork itself changes.
"""

from __future__ import annotations

import argparse
import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "app" / "Relic.App" / "assets" / "relic.ico"

# Palette copied verbatim from ui/relic.css — :root (--gold/--gold2/--goldD) plus the two gradients
# the chrome already uses (.titlebar and .btn), so the icon and the window read as one object.
TILE_TOP = (0x17, 0x1D, 0x2E)      # .titlebar gradient start
TILE_BOTTOM = (0x10, 0x13, 0x1F)   # .titlebar gradient end
GOLD = (0xC8, 0xA0, 0x4F)          # --gold
GOLD_LIGHT = (0xE6, 0xCF, 0x93)    # --gold2
GOLD_DARK = (0x9B, 0x7D, 0x3A)     # --goldD
GOLD_MID = (0xD8, 0xB8, 0x78)      # between --gold and --gold2: the top-right facet
GOLD_WARM = (0xB5, 0x8F, 0x45)     # between --gold and --goldD: the bottom-left facet
EDGE_LIGHT = (0xF0, 0xDC, 0xA2)    # .btn gradient highlight
EDGE_DARK = (0x7A, 0x61, 0x29)

# 20 and 40 are the 125%-display scalings of 16 and 32 — Windows asks for exactly those on the
# scaled laptops this app targets, and without them the shell downsamples 24 or 48 into a smear.
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

SS = 8  # supersample factor; all drawing happens at size*SS and is LANCZOS'd down


def _diamond(cx: float, cy: float, r: float) -> list[tuple[float, float]]:
    """The `M12 2 22 12 12 22 2 12z` silhouette: N, E, S, W of a square rotated 45°."""
    return [(cx, cy - r), (cx + r, cy), (cx, cy + r), (cx - r, cy)]


def render(size: int) -> Image.Image:
    """One frame, drawn for the size it will actually be shown at.

    Three legibility tiers rather than one master image downsampled: a 1-px-wide facet edge or a
    glow is pure grey mush at 16 px, and every grey pixel there costs contrast the small sizes
    cannot spare. The gem also grows as the canvas shrinks, so the mark stays recognisable.
    """
    full = size >= 32
    mid = 20 <= size < 32
    s = size * SS
    cx = cy = s / 2.0
    r = (0.36 if full else 0.40 if mid else 0.42) * s
    corner = round((0.20 if size < 20 else 0.22) * s)
    gem = _diamond(cx, cy, r)

    # The tile colour is painted across the WHOLE canvas and the rounded corners live only in the
    # alpha mask below. Resampling straight RGBA, where the corners are transparent black, drags
    # dark fringes into the corner alpha; keeping colour and coverage apart keeps them clean.
    grad = Image.linear_gradient("L").resize((s, s), Image.Resampling.BILINEAR)
    tile = Image.composite(Image.new("RGB", (s, s), TILE_BOTTOM),
                           Image.new("RGB", (s, s), TILE_TOP), grad)

    if size >= 48:
        glow = Image.new("L", (s, s), 0)
        ImageDraw.Draw(glow).polygon(_diamond(cx, cy, r * 1.35), fill=255)
        glow = glow.filter(ImageFilter.GaussianBlur(s * 0.05)).point(lambda v: v * 70 // 255)
        tile.paste(Image.new("RGB", (s, s), GOLD), (0, 0), glow)

        rim = Image.new("L", (s, s), 0)
        rw = max(1, round(s * 0.010))
        ImageDraw.Draw(rim).rounded_rectangle(
            [rw / 2, rw / 2, s - 1 - rw / 2, s - 1 - rw / 2], radius=corner, outline=255, width=rw)
        tile.paste(Image.new("RGB", (s, s), GOLD), (0, 0), rim.point(lambda v: v * 77 // 255))

    d = ImageDraw.Draw(tile)
    n, e, so, w = gem
    d.polygon(gem, fill=GOLD)  # base coat first, so the facets below cannot leave seams between them
    if full:
        d.polygon([(cx, cy), n, w], fill=GOLD_LIGHT)
        d.polygon([(cx, cy), n, e], fill=GOLD_MID)
        d.polygon([(cx, cy), e, so], fill=GOLD_DARK)
        d.polygon([(cx, cy), so, w], fill=GOLD_WARM)
        ew = max(1, round(s * 0.016))
        d.line([w, n], fill=EDGE_LIGHT, width=ew)
        d.line([e, so], fill=EDGE_DARK, width=ew)
    else:
        d.polygon([w, n, e], fill=GOLD_LIGHT)

    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=corner, fill=255)

    out = tile.resize((size, size), Image.Resampling.LANCZOS).convert("RGBA")
    out.putalpha(mask.resize((size, size), Image.Resampling.LANCZOS))
    return out


def _dib_frame(im: Image.Image) -> bytes:
    """32-bit BGRA DIB, bottom-up, followed by the 1-bpp AND mask.

    Hand-rolled because Pillow's ICO writer PNG-compresses every frame and stamps wPlanes=0.
    PNG frames are only universally understood at 256 px — Inno Setup, the classic shell icon
    paths and System.Drawing's HICON conversion all expect a plain DIB below that.
    """
    w, h = im.size
    stride = w * 4
    raw = im.tobytes("raw", "BGRA")
    xor = b"".join(raw[y * stride:(y + 1) * stride] for y in range(h - 1, -1, -1))

    # Windows still consults the AND mask for sub-256 frames even when the DIB carries real alpha;
    # omitting it is what makes hand-built .ico files render as black boxes on some shells.
    alpha = im.getchannel("A").tobytes()
    row_bytes = ((w + 31) // 32) * 4
    and_mask = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(row_bytes)
        for x in range(w):
            if alpha[y * w + x] == 0:
                row[x >> 3] |= 0x80 >> (x & 7)
        and_mask += row

    # BITMAPINFOHEADER; biHeight is doubled because it spans the XOR image plus the AND mask.
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(and_mask), 0, 0, 0, 0)
    return header + xor + bytes(and_mask)


def _png_frame(im: Image.Image) -> bytes:
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path: Path, frames: list[Image.Image]) -> None:
    frames = sorted(frames, key=lambda f: f.size[0])
    blobs = [_png_frame(f) if f.size[0] >= 256 else _dib_frame(f) for f in frames]

    out = bytearray(struct.pack("<HHH", 0, 1, len(frames)))  # ICONDIR: reserved, type=icon, count
    offset = 6 + 16 * len(frames)
    for f, blob in zip(frames, blobs):
        w, h = f.size
        # bWidth/bHeight are single bytes, so 256 is encoded as 0.
        out += struct.pack("<BBBBHHII", w & 0xFF, h & 0xFF, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    for blob in blobs:
        out += blob
    path.write_bytes(out)


def write_preview(path: Path, frames: list[Image.Image]) -> None:
    """Native-size row + an 8x nearest-neighbour blow-up of the small tiers, for eyeballing."""
    pad, bg = 10, (0x80, 0x80, 0x80, 0xFF)
    small = [f for f in frames if f.size[0] <= 32]
    top_h = max(f.size[1] for f in frames)
    top_w = sum(f.size[0] + pad for f in frames) + pad
    zoom_w = sum(f.size[0] * 8 + pad for f in small) + pad
    zoom_h = max(f.size[1] for f in small) * 8

    canvas = Image.new("RGBA", (max(top_w, zoom_w), top_h + zoom_h + 3 * pad), bg)
    x = pad
    for f in frames:
        canvas.alpha_composite(f, (x, pad + top_h - f.size[1]))
        x += f.size[0] + pad
    x, y = pad, top_h + 2 * pad
    for f in small:
        canvas.alpha_composite(f.resize((f.size[0] * 8, f.size[1] * 8), Image.Resampling.NEAREST), (x, y))
        x += f.size[0] * 8 + pad
    canvas.convert("RGB").save(path)


def main() -> int:
    ap = argparse.ArgumentParser(description="Render Relic's brand icon into app/Relic.App/assets/relic.ico.")
    ap.add_argument("--preview", metavar="PATH",
                    help="also render the frames side by side to PATH (visual check only; never written into assets/)")
    args = ap.parse_args()

    frames = [render(n) for n in SIZES]
    OUT.parent.mkdir(parents=True, exist_ok=True)
    write_ico(OUT, frames)

    # Read the artifact back rather than trusting the writer: a malformed .ico fails at csc time
    # (<ApplicationIcon>) or, worse, silently at ISCC time.
    with Image.open(OUT) as ico:
        found = sorted(w for w, _ in ico.info["sizes"])
        for n in found:
            ico.size = (n, n)
            frame = ico.convert("RGBA")
            if frame.size != (n, n):
                raise SystemExit(f"frame {n} decoded as {frame.size}")
    if found != sorted(SIZES):
        raise SystemExit(f"expected sizes {sorted(SIZES)}, got {found}")

    print(f"==> wrote {OUT} ({len(found)} sizes: {', '.join(map(str, found))}; "
          f"{OUT.stat().st_size / 1024:.1f} KB)")

    if args.preview:
        write_preview(Path(args.preview), frames)
        print(f"==> preview at {args.preview}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
