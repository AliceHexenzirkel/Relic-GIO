"""
Renders app/Relic.App/ui/assets/splash/<avatarId>.webp -- the character tiles behind the "Game
commands" picker (.ptile) and the selected-character banner (.gmhero .art).

Run from anywhere:  python build\\make_splashes.py  [--sheet <path.png>]

Source art is assets_ref/characters_splash/, a read-only reference folder that is NOT part of the
build (it is ~170 MB of full-size PNG). The .webp files this writes are COMMITTED, so nothing in
the normal pipeline needs Pillow or that folder: Relic.App.csproj ships ui/** verbatim. Re-run this
only when a character is added to config/gamedata.json or the source art changes.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path

from PIL import Image

REPO = Path(__file__).resolve().parent.parent
SRC = REPO / "assets_ref" / "characters_splash"
OUT = REPO / "app" / "Relic.App" / "ui" / "assets" / "splash"

# 155x210 is .ptile's aspect-ratio in relic.css. Matching it exactly makes the tile's
# `object-fit:cover` a no-op, so the framing decided here is the framing the user sees.
OUT_W, OUT_H = 480, 650

# Alpha below this is the fringe left by the source PNGs' own antialiasing, not artwork. The trim
# works on row/column alpha SUMS rather than Pillow's getbbox() because several sources carry a
# stray near-transparent pixel in a corner, which alone would defeat a per-pixel bounding box.
ALPHA_FLOOR = 8
TRIM_SHARE = 0.004  # a row/column counts as artwork once it holds this share of the busiest one

# Cover-fit crops the bottom, never the top: gacha splashes put the head at the very top of the
# canvas, so a 1.0 zoom would fit the whole body and leave the face a dozen pixels tall in a 155 px
# tile. 1.15 drops the feet -- which the .nm name gradient covers anyway -- and keeps every head.
ZOOM = 1.15

QUALITY = 82
METHOD = 6  # slowest/densest WebP search; this runs once per art change, never in the build

# Explicit map rather than fuzzy name matching, because " - " in the reference folder does NOT mean
# "alternate outfit": it is the art's own title, and 22 characters have no plain "<Name>.png" at all.
# Each of those was picked by eye as the release-era default look (the skins -- Summertime Sparkle,
# Sea Breeze Dandelion, Red Dead of Night, Ein Immernachtstraum, Twilight Blossom, Cherries
# Snow-Laden, Opulent Splendor, Blossoming Starlight, Pact of Stars and Moon, Orchid's Evening Gown,
# Frostflower Dew, New Year's Cheer, Bamboo Rain, Springbloom Missive, Sailwind Shadow -- are all
# deliberately NOT used). The map also absorbs the folder's spelling drift ("Yunjin.png").
# A second element selects one half of a shared canvas; see split() below.
SOURCES: dict[int, str | tuple[str, str]] = {
    10000002: "Kamisato Ayaka - Flawless Radiance.png",
    10000003: "Jean - Gunnhildr_s Legacy.png",
    10000005: ("Traveler.png", "right"),   # Aether stands on the right of the shared twin art
    10000006: "Lisa - Purple Rose.png",
    10000007: ("Traveler.png", "left"),    # Lumine on the left
    10000014: "Barbara - Innocent Longing.png",
    10000015: "Kaeya - Icy Featherflight.png",
    10000016: "Diluc - Darknight Blaze.png",
    10000020: "Razor.png",
    10000021: "Amber - 5-Star Outrider.png",
    10000022: "Venti.png",
    10000023: "Xiangling - Red Pepper and Turmeric.png",
    10000024: "Beidou.png",
    10000025: "Xingqiu - Azure Silk.png",
    10000026: "Xiao.png",
    10000027: "Ningguang - Gold Leaf and Pearly Jade.png",
    10000029: "Klee - Shooting Spark.png",
    10000030: "Zhongli.png",
    10000031: "Fischl - Dunkelnacht Sakrament.png",
    10000032: "Bennett - Fortune_s Favor.png",
    10000033: "Tartaglia.png",
    10000034: "Noelle.png",
    10000035: "Qiqi.png",
    10000036: "Chongyun.png",
    10000037: "Ganyu - Frostdew Trail.png",
    10000038: "Albedo.png",
    10000039: "Diona.png",
    10000041: "Mona - Flowing Fate.png",
    10000042: "Keqing - Piercing Thunderbolt.png",
    10000043: "Sucrose.png",
    10000044: "Xinyan.png",
    10000045: "Rosaria - Executor_s Thorns.png",
    10000046: "Hu Tao - Plum Blossom Bouquet.png",
    10000047: "Kaedehara Kazuha.png",
    10000048: "Yanfei.png",
    10000049: "Yoimiya.png",
    10000050: "Thoma.png",
    10000051: "Eula.png",
    10000052: "Raiden Shogun.png",
    10000053: "Sayu.png",
    10000054: "Sangonomiya Kokomi.png",
    10000055: "Gorou.png",
    10000056: "Kujou Sara.png",
    10000057: "Arataki Itto.png",
    10000058: "Yae Miko.png",
    10000059: "Shikanoin Heizou.png",
    10000060: "Yelan - The Waning Point.png",
    10000062: "Aloy - Machine Hunter.png",
    10000063: "Shenhe - The World_s Shackles.png",
    10000064: "Yunjin.png",
    10000065: "Kuki Shinobu.png",
    10000066: "Kamisato Ayato.png",
}


def _span(profile: list[int]) -> tuple[int, int]:
    """First and last index whose alpha mass clears TRIM_SHARE of the busiest slice."""
    floor = max(profile) * TRIM_SHARE
    live = [i for i, v in enumerate(profile) if v > floor]
    return (live[0], live[-1] + 1) if live else (0, len(profile))


def _profiles(mask: Image.Image) -> tuple[list[int], list[int]]:
    """Per-column and per-row alpha sums. BOX-resizing to a 1-px strip is the sum, done in C."""
    w, h = mask.size
    cols = mask.resize((w, 1), Image.Resampling.BOX)
    rows = mask.resize((1, h), Image.Resampling.BOX)
    return [cols.getpixel((x, 0)) for x in range(w)], [rows.getpixel((0, y)) for y in range(h)]


def split(im: Image.Image, mask: Image.Image, side: str) -> tuple[Image.Image, Image.Image]:
    """Halve a canvas that holds two characters (the Traveler twins share one splash).

    Cut at the emptiest column of the middle third rather than at the exact centre, so the seam
    falls in the gap between the two figures instead of through a hand or a cape.
    """
    w = mask.size[0]
    cols, _ = _profiles(mask)
    lo, hi = int(w * 0.38), int(w * 0.62)
    cut = min(range(lo, hi), key=lambda x: cols[x])
    box = (cut, 0, w, mask.size[1]) if side == "right" else (0, 0, cut, mask.size[1])
    return im.crop(box), mask.crop(box)


def card(path: Path, side: str | None) -> Image.Image:
    im = Image.open(path).convert("RGBA")  # sources are a mix of palette-PNG, RGBA-PNG and one WebP
    mask = im.getchannel("A").point(lambda v: 255 if v > ALPHA_FLOOR else 0)
    if side:
        im, mask = split(im, mask, side)

    cols, rows = _profiles(mask)
    x0, x1 = _span(cols)
    y0, y1 = _span(rows)
    im, mask = im.crop((x0, y0, x1, y1)), mask.crop((x0, y0, x1, y1))
    w, h = im.size

    # Horizontal centre is the MEDIAN column of alpha mass, not the middle of the bounding box:
    # a trailing cape or a one-sided burst of element effects drags the box centre well off the
    # character, and on a 3:4 crop that is enough to push the face against the edge.
    cols, _ = _profiles(mask)
    half, acc, cx = sum(cols) / 2, 0, w / 2
    for x, v in enumerate(cols):
        acc += v
        if acc >= half:
            cx = x + 0.5
            break

    s = max(OUT_W / w, OUT_H / h) * ZOOM
    sw, sh = max(OUT_W, round(w * s)), max(OUT_H, round(h * s))
    im = im.resize((sw, sh), Image.Resampling.LANCZOS)
    left = max(0, min(round(cx * sw / w - OUT_W / 2), sw - OUT_W))
    return im.crop((left, 0, left + OUT_W, OUT_H))


def contact_sheet(cards: list[tuple[int, Image.Image]], path: Path) -> None:
    """All tiles at their real 155x210 size on the tile's own cream, for eyeballing the framing."""
    tw, th, cols = 155, 210, 9
    rows = (len(cards) + cols - 1) // cols
    sheet = Image.new("RGBA", (cols * tw, rows * th), (0xE8, 0xDC, 0xC4, 0xFF))
    for i, (_, c) in enumerate(cards):
        sheet.alpha_composite(c.resize((tw, th), Image.Resampling.LANCZOS),
                              ((i % cols) * tw, (i // cols) * th))
    sheet.convert("RGB").save(path)


def main() -> int:
    ap = argparse.ArgumentParser(description="Render the character splash tiles into ui/assets/splash.")
    ap.add_argument("--sheet", metavar="PATH",
                    help="also render every tile side by side to PATH (visual check only; never written into assets/)")
    args = ap.parse_args()

    # Fail on a typo instead of silently shipping 51 tiles and one broken image element.
    missing = [f for v in SOURCES.values() for f in [v if isinstance(v, str) else v[0]]
               if not (SRC / f).is_file()]
    if missing:
        raise SystemExit("missing from assets_ref/characters_splash: " + ", ".join(sorted(set(missing))))

    OUT.mkdir(parents=True, exist_ok=True)
    cards, total = [], 0
    for avatar_id, spec in SOURCES.items():
        name, side = (spec, None) if isinstance(spec, str) else spec
        c = card(SRC / name, side)
        dest = OUT / f"{avatar_id}.webp"
        c.save(dest, "WEBP", quality=QUALITY, method=METHOD)
        size = dest.stat().st_size
        total += size
        cards.append((avatar_id, c))
        print(f"  {avatar_id}.webp  {size / 1024:6.1f} KB  <- {name}" + (f" ({side})" if side else ""))

    print(f"\n==> {len(cards)} splashes in {OUT}  ({total / 1_048_576:.2f} MB, "
          f"{OUT_W}x{OUT_H}, quality {QUALITY})")

    if args.sheet:
        contact_sheet(cards, Path(args.sheet))
        print(f"==> contact sheet for checking at {args.sheet}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
