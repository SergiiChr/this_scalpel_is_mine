"""Manual art in the style of a Victorian surgical guide: parchment texture, page frame and pen-and-ink diagrams.

Strokes are resampled with small random offsets and drawn twice, so they read as drawn by hand.
Every drawing seeds its own random generator, so output only changes when the drawing does.
Labels are converted to outlines because Godot's SVG importer does not draw <text> elements.
"""

from __future__ import annotations

import math
import random
from collections.abc import Callable, Iterable, Sequence
from pathlib import Path

import numpy as np
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from numpy.typing import NDArray
from PIL import Image, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
MANUAL_DIR = ROOT / "assets" / "manual"
FONTS_DIR = ROOT / "assets" / "fonts"
INK = "#231c16"
RED = "#b3241c"
DARK_RED = "#5c0f0a"
PALE = "#efe6d2"
FONT_FILES = {"script": "LaBelleAurore-Regular.ttf", "sc": "IMFellEnglishSC-Regular.ttf", "body": "IMFellEnglish-Regular.ttf"}
FRAME_MARGIN = 44

Point = tuple[float, float]
_fonts: dict[str, TTFont] = {}


def _font(name: str) -> TTFont:
    if name not in _fonts:
        _fonts[name] = TTFont(FONTS_DIR / FONT_FILES[name])
    return _fonts[name]


class Sketch:
    """An SVG drawing made of jittered ink strokes."""

    def __init__(self, width: int, height: int, seed: int) -> None:
        self.width, self.height = width, height
        self.rng = random.Random(seed)
        self.defs: list[str] = []
        self.body: list[str] = []

    def _jitter(self, points: Sequence[Point], wobble: float) -> list[Point]:
        out: list[Point] = []
        for (x0, y0), (x1, y1) in zip(points, points[1:], strict=False):
            n = max(1, int(math.dist((x0, y0), (x1, y1)) / 6))
            for i in range(n):
                t = i / n
                out.append((x0 + (x1 - x0) * t + self.rng.uniform(-wobble, wobble), y0 + (y1 - y0) * t + self.rng.uniform(-wobble, wobble)))
        out.append(points[-1])
        return out

    def line(
        self, points: Sequence[Point], width: float = 1.6, color: str = INK, passes: int = 2, wobble: float = 0.7, closed: bool = False, dash: str = ""
    ) -> None:
        """A hand drawn polyline. Two passes with slightly different widths give the doubled pen line."""
        extra = f' stroke-dasharray="{dash}"' if dash else ""
        for _ in range(passes):
            pts = self._jitter([*points, *points[:1]] if closed else points, wobble)
            d = "M" + " L".join(f"{x:.1f} {y:.1f}" for x, y in pts)
            w = width * self.rng.uniform(0.7, 1.1)
            self.body.append(f'<path d="{d}" stroke="{color}" stroke-width="{w:.2f}" fill="none" stroke-linecap="round" stroke-linejoin="round"{extra}/>')

    def fill(self, points: Sequence[Point], color: str, opacity: float = 1.0) -> None:
        pts = " ".join(f"{x:.1f},{y:.1f}" for x, y in points)
        self.body.append(f'<polygon points="{pts}" fill="{color}" fill-opacity="{opacity}"/>')

    def shape(self, points: Sequence[Point], width: float = 1.6, fill: str = PALE) -> None:
        """A filled outline, so it hides what is drawn behind it."""
        self.fill(points, fill)
        self.line(points, width, closed=True)

    def hatch(self, points: Sequence[Point], angle: float, gap: float, width: float = 0.7, color: str = INK) -> None:
        """Parallel strokes clipped to a polygon, for shading."""
        clip = f"c{len(self.defs)}"
        pts = " ".join(f"{x:.1f},{y:.1f}" for x, y in points)
        self.defs.append(f'<clipPath id="{clip}"><polygon points="{pts}"/></clipPath>')
        xs, ys = [p[0] for p in points], [p[1] for p in points]
        cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
        reach = math.dist((min(xs), min(ys)), (max(xs), max(ys))) / 2 + 2
        ca, sa = math.cos(math.radians(angle)), math.sin(math.radians(angle))
        start = len(self.body)
        k = -reach
        while k < reach:
            a = (cx - sa * k - ca * reach, cy + ca * k - sa * reach)
            b = (cx - sa * k + ca * reach, cy + ca * k + sa * reach)
            self.line([a, b], width, color, passes=1, wobble=0.4)
            k += gap * self.rng.uniform(0.8, 1.2)
        self.body[start:] = [f'<g clip-path="url(#{clip})">', *self.body[start:], "</g>"]

    def ellipse(self, cx: float, cy: float, rx: float, ry: float, width: float = 1.4, color: str = INK, fill: str = "", passes: int = 2) -> None:
        pts = [(cx + rx * math.cos(a / 24 * math.tau), cy + ry * math.sin(a / 24 * math.tau)) for a in range(24)]
        if fill:
            self.fill(pts, fill)
        self.line(pts, width, color, passes=passes, wobble=0.5, closed=True)

    def dots(self, points: Sequence[Point], count: int, color: str = INK) -> None:
        """Stipple inside a polygon's bounding box, kept to points inside the polygon."""
        xs, ys = [p[0] for p in points], [p[1] for p in points]
        placed = 0
        while placed < count:
            p = (self.rng.uniform(min(xs), max(xs)), self.rng.uniform(min(ys), max(ys)))
            if _inside(p, points):
                self.body.append(f'<circle cx="{p[0]:.1f}" cy="{p[1]:.1f}" r="0.7" fill="{color}"/>')
                placed += 1

    def arrow(self, start: Point, end: Point, color: str = RED, width: float = 1.1, dash: str = "") -> None:
        self.line([start, end], width, color, passes=1, dash=dash)
        ang = math.atan2(end[1] - start[1], end[0] - start[0])
        left = (end[0] - 9 * math.cos(ang - 0.4), end[1] - 9 * math.sin(ang - 0.4))
        right = (end[0] - 9 * math.cos(ang + 0.4), end[1] - 9 * math.sin(ang + 0.4))
        self.fill([end, left, right], color)

    def curved_arrow(self, center: Point, radius: float, start: float, end: float, color: str = RED) -> None:
        """An arc from one angle to another (degrees, clockwise on screen) with a head at the end."""
        angles = [math.radians(start + (end - start) * i / 10) for i in range(11)]
        pts = [(center[0] + radius * math.cos(a), center[1] + radius * math.sin(a)) for a in angles]
        self.line(pts[:-1], 1.1, color, passes=1)
        self.arrow(pts[-2], pts[-1], color)

    def text(self, x: float, y: float, s: str, size: float = 21, font: str = "script", color: str = RED, rotate: float = -2, anchor: str = "start") -> None:
        """Text as outlines. anchor is start, middle or end, like SVG's text-anchor."""
        tt = _font(font)
        glyphs = tt.getGlyphSet()
        cmap = tt.getBestCmap()
        hmtx = tt["hmtx"]
        scale = size / tt["head"].unitsPerEm
        names = [cmap.get(ord(ch), ".notdef") for ch in s]
        width = sum(hmtx[n][0] for n in names) * scale
        pen = SVGPathPen(glyphs)
        cursor = x - {"start": 0.0, "middle": width / 2, "end": width}[anchor]
        for name in names:
            glyphs[name].draw(TransformPen(pen, (scale, 0, 0, -scale, cursor, y)))
            cursor += hmtx[name][0] * scale
        self.body.append(f'<path d="{pen.getCommands()}" fill="{color}" transform="rotate({rotate} {x:.1f} {y:.1f})"/>')

    def svg(self) -> str:
        return (
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{self.width}" height="{self.height}" viewBox="0 0 {self.width} {self.height}">'
            f"<defs>{''.join(self.defs)}</defs>{''.join(self.body)}</svg>\n"
        )


def _inside(p: Point, poly: Sequence[Point]) -> bool:
    inside = False
    for (x0, y0), (x1, y1) in zip(poly, [*poly[1:], *poly[:1]], strict=False):
        if (y0 > p[1]) != (y1 > p[1]) and p[0] < x0 + (p[1] - y0) * (x1 - x0) / (y1 - y0):
            inside = not inside
    return inside


def _along(origin: Point, angle: float) -> Callable[[float, float], Point]:
    """Maps (distance along, distance across) to page coordinates for a drawing lying at an angle."""
    ux, uy = math.cos(math.radians(angle)), math.sin(math.radians(angle))

    def at(s: float, n: float) -> Point:
        return (origin[0] + ux * s - uy * n, origin[1] + uy * s + ux * n)

    return at


def _scalpel(sk: Sketch, tip: Point, angle: float, length: float = 170) -> None:
    """A #10 blade on a flat handle. angle points from the tip toward the handle."""
    at = _along(tip, angle)
    blade = [at(0, 0), at(6, 7), at(18, 11), at(34, 11), at(62, 6), at(62, -5), at(40, -5)]
    sk.shape(blade, 1.5)
    sk.line([at(22, 2), at(58, 1)], 0.8, passes=1)
    handle = [at(62, -5), at(length - 12, -6), at(length, -3), at(length, 3), at(length - 12, 6), at(62, 5)]
    sk.shape(handle, 1.7)
    sk.hatch(handle, angle + 60, 3)
    for s in range(80, int(length) - 20, 9):
        sk.line([at(s, -5), at(s + 3, 5)], 0.8, passes=1)


# --- Diagrams ---------------------------------------------------------------------------------------


def incision() -> Sketch:
    sk = Sketch(640, 300, 3)
    left, right, skin, fat, muscle, bottom = 140, 470, 110, 128, 188, 250
    dx, dy, cut = 60, -42, 250
    front = [(left, skin), (right, skin), (right, bottom), (left, bottom)]
    side = [(right, skin), (right + dx, skin + dy), (right + dx, bottom + dy), (right, bottom)]
    sk.line(front, 2.2, closed=True)
    sk.line([(left, skin), (left + dx, skin + dy), (right + dx, skin + dy), (right, skin)], 2.0)
    sk.line(side[1:], 2.0)
    sk.hatch(side, 60, 5, 0.8)
    for boundary in (fat, muscle):
        sk.line([(left + i * 10, boundary + math.sin(i * 0.9) * 1.5) for i in range((right - left) // 10 + 1)], 1.3, passes=1)
    sk.dots([(left, skin + 3), (right, skin + 3), (right, fat - 2), (left, fat - 2)], 220)
    lobules = Sketch(0, 0, 4)
    y, row = fat + 8.0, 0
    while y < muscle + 8:
        x = left - (row % 2) * 11.0
        while x < right + 10:
            r = sk.rng.uniform(8, 12)
            pts = [(x + r * math.cos(a / 8 * math.tau) * sk.rng.uniform(0.9, 1.1), y + r * 0.8 * math.sin(a / 8 * math.tau)) for a in range(8)]
            lobules.line(pts, 0.9, passes=1, wobble=0.6, closed=True)
            x += 22
        y += 15
        row += 1
    _clipped(sk, lobules, [(left, fat), (right, fat), (right, muscle), (left, muscle)])
    fibres = Sketch(0, 0, 5)
    for i in range(14):
        yy = muscle + 5 + i * 4.5
        fibres.line([(left + j * 12, yy + math.sin(j * 0.7 + i) * 1.8) for j in range((right - left) // 12 + 2)], 0.8, passes=1)
    muscle_box = [(left, muscle), (right, muscle), (right, bottom), (left, bottom)]
    _clipped(sk, fibres, muscle_box)
    sk.hatch(muscle_box, 35, 7, 0.6)
    wound = [(cut - 16, skin), (cut, muscle - 4), (cut + 16, skin)]
    sk.fill(wound, RED, 0.85)
    sk.hatch(wound, -30, 3.5, 0.7, DARK_RED)
    sk.line(wound, 1.8)
    sk.fill([(cut - 15, skin), (cut - 20, skin + 4), (cut - 21, skin + 22), (cut - 17, skin + 21), (cut - 18, skin + 12), (cut - 16, skin + 2)], RED)
    sk.line([(cut, skin - 1), (cut + dx * 0.55, skin + dy * 0.55)], 2.2, RED)
    sk.line([(cut + dx * 0.55, skin + dy * 0.55), (cut + dx * 0.95, skin + dy * 0.95)], 1.2, passes=1, dash="4 4")
    _scalpel(sk, (cut + 1, muscle - 12), -58)
    for i, (y0, y1, name) in enumerate([(skin, fat, "skin"), (fat, muscle, "fat"), (muscle, bottom, "muscle")]):
        mid = (y0 + y1) / 2
        sk.text(12, mid + 6, f"{i + 1}  {name}", rotate=-2)
        sk.line([(left - 44 if name == "muscle" else left - 62, mid + 2), (left - 4, mid)], 0.9, RED, passes=1)
    sk.arrow((cut - 90, skin - 58), (cut - 22, skin - 6))
    sk.text(cut - 200, skin - 66, "Deepen in layers", 22)
    sk.arrow((right - 60, bottom + 18), (cut + 10, muscle + 2), dash="5 4")
    sk.text(right - 190, bottom + 40, "Never full depth at once.", 21, rotate=-1)
    return sk


def _clipped(sk: Sketch, inner: Sketch, clip: Sequence[Point]) -> None:
    """Draws another sketch's strokes inside a polygon."""
    cid = f"c{len(sk.defs)}"
    pts = " ".join(f"{x:.1f},{y:.1f}" for x, y in clip)
    sk.defs.append(f'<clipPath id="{cid}"><polygon points="{pts}"/></clipPath>')
    sk.body += [f'<g clip-path="url(#{cid})">', *inner.body, "</g>"]


def _skin_patch(sk: Sketch, x: float, y: float, w: float, h: float) -> None:
    patch = [(x, y), (x + w, y + 4), (x + w - 3, y + h), (x + 2, y + h - 3)]
    sk.shape(patch, 1.6, "#ead9bd")
    sk.dots(patch, 90)
    sk.line([(x + 12, y + h / 2), (x + w - 12, y + h / 2 + 1)], 2.2, RED)


def closures() -> Sketch:
    sk = Sketch(640, 210, 11)
    w, h, top = 170, 70, 30
    for i in range(3):
        _skin_patch(sk, 20 + i * 210, top, w, h)
    mid = top + h / 2
    for k in range(6):
        x = 44 + k * 24
        sk.line([(x, mid - 13), (x + 2, mid + 13)], 1.5, passes=1)
        sk.ellipse(x - 1, mid - 14, 2.2, 2.2, 1.2)
        sk.line([(x - 2, mid - 15), (x - 8, mid - 20)], 0.9, passes=1)
    for k in range(5):
        x = 252 + k * 28
        sk.line([(x - 4, mid - 12), (x, mid - 14), (x, mid + 14), (x - 4, mid + 12)], 1.9, passes=1)
    for k in range(4):
        x = 452 + k * 36
        strip = [(x, mid - 26), (x + 16, mid - 25), (x + 17, mid + 26), (x + 1, mid + 25)]
        sk.shape(strip, 1.1, "#f4ecda")
        sk.hatch(strip, 90, 4, 0.5)
    for i, (name, note) in enumerate([("Sutures", "slow, strongest"), ("Staples", "fast, secure"), ("Adhesive strips", "superficial only")]):
        cx = 20 + i * 210 + w / 2
        sk.text(cx, top + h + 40, name, 22, "sc", INK, 0, "middle")
        sk.text(cx, top + h + 70, note, 21, anchor="middle", rotate=-1)
    return sk


def _frame_box(sk: Sketch, x: float, y: float, w: float, h: float) -> None:
    sk.line([(x, y), (x + w, y), (x + w, y + h), (x, y + h)], 1.6, closed=True)
    sk.line([(x + 4, y + 4), (x + w - 4, y + 4), (x + w - 4, y + h - 4), (x + 4, y + h - 4)], 0.8, RED, passes=1, closed=True)


def setup_order() -> Sketch:
    sk = Sketch(640, 190, 17)
    boxes = [(10 + i * 160, 14) for i in range(4)]
    for i, (x, y) in enumerate(boxes):
        _frame_box(sk, x, y, 130, 120)
        if i < 3:
            sk.arrow((x + 136, y + 60), (x + 156, y + 60), INK, 1.4)
    # 1: drip bag with its line.
    x, y = boxes[0]
    bag = [(x + 45, y + 18), (x + 85, y + 18), (x + 88, y + 70), (x + 75, y + 82), (x + 55, y + 82), (x + 42, y + 70)]
    sk.shape(bag, 1.6)
    sk.hatch([(x + 46, y + 52), (x + 86, y + 52), (x + 87, y + 70), (x + 75, y + 80), (x + 55, y + 80), (x + 43, y + 70)], 45, 4)
    sk.line([(x + 65, y + 82), (x + 65, y + 92), (x + 60, y + 104), (x + 80, y + 108), (x + 102, y + 100)], 1.2)
    sk.ellipse(x + 65, y + 14, 6, 4, 1.2)
    # 2: iodine bottle, its dish and a soaked pad held in forceps.
    x, y = boxes[1]
    bottle = [(x + 14, y + 44), (x + 42, y + 44), (x + 42, y + 102), (x + 14, y + 102)]
    sk.shape(bottle, 1.6)
    sk.shape([(x + 21, y + 30), (x + 35, y + 30), (x + 35, y + 44), (x + 21, y + 44)], 1.4)
    sk.fill([(x + 16, y + 66), (x + 40, y + 66), (x + 40, y + 100), (x + 16, y + 100)], "#9a4a1c", 0.8)
    sk.hatch(bottle, 70, 5)
    sk.ellipse(x + 88, y + 98, 30, 8, 1.5, fill=PALE)
    sk.ellipse(x + 88, y + 97, 22, 4, 0.8, fill="#9a4a1c", passes=1)
    sk.line([(x + 120, y + 22), (x + 88, y + 74)], 1.6)
    sk.line([(x + 124, y + 26), (x + 92, y + 76)], 1.6)
    sk.ellipse(x + 88, y + 80, 10, 6, 1.2, fill="#c87a3c")
    # 3: anaesthetic mask.
    x, y = boxes[2]
    mask = [(x + 30, y + 88), (x + 50, y + 44), (x + 80, y + 44), (x + 100, y + 88)]
    sk.shape(mask, 1.6)
    sk.hatch(mask, 20, 5)
    sk.ellipse(x + 65, y + 90, 36, 8, 1.5, fill=PALE)
    sk.line([(x + 65, y + 44), (x + 65, y + 30), (x + 80, y + 20), (x + 110, y + 22)], 2.4)
    # 4: skin marker and a dashed line.
    x, y = boxes[3]
    sk.line([(x + 18, y + 96), (x + 110, y + 70)], 1.8, RED, dash="7 5")
    _marker(sk, (x + 58, y + 84), -62)
    for i, name in enumerate(["IV access", "Skin prep", "Anaesthesia", "Marking"]):
        bx, by = boxes[i]
        sk.text(bx + 65, by + 160, f"{i + 1}. {name}", 21, "sc", INK, 0, "middle")
    return sk


def _marker(sk: Sketch, tip: Point, angle: float) -> None:
    at = _along(tip, angle)
    sk.shape([at(0, 0), at(10, -4), at(10, 4)], 1.2, RED)
    body = [at(10, -6), at(78, -6), at(80, 0), at(78, 6), at(10, 6)]
    sk.shape(body, 1.6)
    sk.hatch(body, angle + 70, 3.5)


def rhythms() -> Sketch:
    sk = Sketch(640, 330, 23)
    strips = [(20, "Sinus rhythm: no action."), (125, "Ventricular fibrillation: defibrillate."), (230, "Asystole: adrenaline, then defibrillate.")]
    for y, label in strips:
        box = [(20, y), (620, y), (620, y + 70), (20, y + 70)]
        for gx in range(20, 621, 15):
            sk.line([(gx, y), (gx, y + 70)], 0.4, RED, passes=1, wobble=0.2)
        for gy in range(y, y + 71, 14):
            sk.line([(20, gy), (620, gy)], 0.4, RED, passes=1, wobble=0.2)
        sk.line(box, 1.6, closed=True)
        sk.text(30, y + 96, label, 21, rotate=-1)
    base = strips[0][0] + 45
    beat: list[Point] = []
    for k in range(5):
        x0 = 30 + k * 120
        beat += [(x0, base), (x0 + 20, base), (x0 + 28, base - 6), (x0 + 36, base), (x0 + 44, base), (x0 + 50, base + 6), (x0 + 56, base - 36)]
        beat += [(x0 + 62, base + 12), (x0 + 68, base), (x0 + 84, base), (x0 + 96, base - 9), (x0 + 108, base)]
    sk.line(beat, 1.7, wobble=0.4)
    base = strips[1][0] + 35
    sk.line([(30 + i * 7, base + math.sin(i * 1.3) * 16 * sk.rng.uniform(0.4, 1.1)) for i in range(84)], 1.6, wobble=0.6)
    base = strips[2][0] + 40
    sk.line([(30 + i * 30, base + sk.rng.uniform(-1, 1)) for i in range(20)], 1.7, wobble=0.5)
    return sk


def blood_types() -> Sketch:
    sk = Sketch(560, 290, 29)
    cols, rows = ["O+", "A+", "B+", "AB+"], ["O-", "A+", "B+", "Bombay"]
    grid = [[True] * 4, [False, True, False, True], [False, False, True, True]]
    x0, y0, cw, rh = 170, 70, 90, 48
    sk.text(20, 42, "donor", 22, rotate=-4)
    sk.text(x0 + cw * 2, 30, "recipient", 22, anchor="middle", rotate=-2)
    for c, name in enumerate(cols):
        sk.text(x0 + c * cw + cw / 2, y0 - 10, name, 22, "sc", INK, 0, "middle")
    for r, name in enumerate(rows):
        sk.text(40, y0 + r * rh + 32, name, 22, "sc", INK, 0)
    sk.line([(20, y0), (x0 + 4 * cw, y0)], 1.8)
    sk.line([(x0 - 6, 20), (x0 - 6, y0 + 4 * rh)], 1.8)
    for r in range(1, 4):
        sk.line([(20, y0 + r * rh), (x0 + 4 * cw, y0 + r * rh)], 0.6, passes=1)
    for r, row in enumerate(grid):
        for c, ok in enumerate(row):
            cx, cy = x0 + c * cw + cw / 2, y0 + r * rh + rh / 2
            if ok:
                sk.line([(cx - 10, cy), (cx - 3, cy + 9), (cx + 12, cy - 12)], 2.0)
            else:
                sk.line([(cx - 9, cy - 9), (cx + 9, cy + 9)], 2.0, RED)
                sk.line([(cx + 9, cy - 9), (cx - 9, cy + 9)], 2.0, RED)
    sk.text(x0 + cw * 2, y0 + 3 * rh + 32, "Bombay blood only", 22, anchor="middle", rotate=-1)
    return sk


def _bone(sk: Sketch, start: Point, end: Point, broken_end: bool) -> list[Point]:
    """A long bone fragment from a knobbly end to a jagged fracture. Returns the outline."""
    ang = math.degrees(math.atan2(end[1] - start[1], end[0] - start[0]))
    length = math.dist(start, end)
    at = _along(start, ang)
    head = [at(-8, -16), at(4, -20), at(14, -12), at(20, -9)]
    tail = [at(length, 9), at(length - 5, 3), at(length + 3, -2), at(length - 2, -9)] if broken_end else [at(length, 9), at(length, -9)]
    outline = [at(20, 9), at(14, 12), at(4, 20), at(-8, 16), at(-12, 0), *head, *[at(length * 0.5, -8)], *reversed(tail), at(length * 0.5, 8)]
    sk.shape(outline, 1.7, "#f1e7cf")
    sk.hatch(outline, ang + 80, 5, 0.6)
    return outline


def alignment() -> Sketch:
    sk = Sketch(560, 230, 31)
    _bone(sk, (70, 120), (250, 100), True)
    _bone(sk, (490, 90), (300, 130), True)
    sk.line([(30, 175), (530, 175)], 1.0, passes=1, dash="8 7")
    sk.text(280, 205, "anatomical axis: hold until confirmed", 21, anchor="middle", rotate=-1)
    sk.arrow((170, 66), (80, 82))
    sk.text(20, 42, "A: traction", 22, rotate=-3)
    sk.curved_arrow((410, 100), 62, 75, 25)
    sk.text(380, 42, "B: alignment", 22, rotate=-3)
    return sk


def _patient(sk: Sketch, x: float, y: float, pose: str) -> None:
    """A patient lying on a table, seen from the side: supine, lateral or prone."""
    top = [(x - 75, y), (x + 75, y), (x + 75, y + 9), (x - 75, y + 9)]
    sk.shape(top, 1.6)
    sk.hatch(top, 45, 3.5)
    for leg in (-60, 60):
        sk.line([(x + leg, y + 9), (x + leg, y + 50)], 1.8)
    height = {"supine": 18, "lateral": 30, "prone": 18}[pose]
    body = [(x - 52, y), (x - 50, y - height + 4), (x - 30, y - height), (x + 20, y - height + 2), (x + 62, y - 10), (x + 70, y)]
    if pose == "lateral":
        body = [(x - 52, y), (x - 50, y - 26), (x - 20, y - 30), (x + 18, y - 24), (x + 38, y - 30), (x + 60, y - 12), (x + 68, y)]
    sk.shape(body, 1.7)
    sk.hatch(body, {"supine": 70, "lateral": 50, "prone": -70}[pose], 5)
    sk.ellipse(x - 64, y - 11, 12, 11, 1.6, fill=PALE)
    if pose == "supine":
        sk.shape([(x - 66, y - 22), (x - 63, y - 28), (x - 60, y - 21)], 1.2)
        sk.shape([(x + 64, y - 10), (x + 66, y - 24), (x + 71, y - 23), (x + 70, y - 8)], 1.3)
    elif pose == "prone":
        sk.hatch([(x - 76, y - 22), (x - 52, y - 22), (x - 52, y - 11), (x - 76, y - 11)], 30, 3)
        sk.shape([(x + 66, y - 4), (x + 82, y + 4), (x + 80, y + 9), (x + 66, y + 4)], 1.3)
    else:
        sk.shape([(x - 76, y - 12), (x - 83, y - 9), (x - 76, y - 6)], 1.2)


def turning() -> Sketch:
    sk = Sketch(680, 215, 37)
    for i, pose in enumerate(["supine", "lateral", "prone"]):
        cx = 110 + i * 230
        _patient(sk, cx, 110, pose)
        sk.text(cx, 200, pose, 24, "sc", INK, 0, "middle")
        if i < 2:
            sk.arrow((cx + 90, 75), (cx + 130, 75), INK, 1.4)
    sk.text(340, 30, "One step at a time. All staff on one side.", 21, anchor="middle", rotate=-1)
    return sk


def _hemostat(sk: Sketch, x: float, y: float) -> None:
    """Jaws point left from the box joint at (x, y); finger rings to the right."""
    for side in (-1, 1):
        shank = [(x, y + side * 2), (x + 70, y + side * 10), (x + 96, y + side * 16), (x + 96, y + side * 20), (x + 70, y + side * 14), (x, y + side * 6)]
        sk.shape(shank, 1.6)
        sk.ellipse(x + 108, y + side * 22, 13, 10, 2.0, fill=PALE)
        sk.ellipse(x + 108, y + side * 22, 8, 5, 1.0)
    jaws = [(x, y - 5), (x - 58, y - 2), (x - 66, y + 1), (x - 58, y + 4), (x, y + 6)]
    sk.shape(jaws, 1.8)
    for k in range(-56, -6, 5):
        sk.line([(x + k, y - 3), (x + k + 2, y + 4)], 0.7, passes=1)
    sk.ellipse(x, y, 6, 6, 1.6, fill=PALE)


def instruments() -> Sketch:
    sk = Sketch(660, 360, 41)
    sk.line([(8, 8), (652, 8), (652, 352), (8, 352)], 1.8, closed=True)
    sk.line([(14, 14), (646, 14), (646, 346), (14, 346)], 0.8, RED, passes=1, closed=True)
    _scalpel(sk, (40, 62), 0, 190)
    sk.text(135, 105, "scalpel", 22, anchor="middle")
    # Forceps: two tapering arms joined at the back, ribbed where the fingers press.
    for side in (-1, 1):
        arm = [(262, 60), (385, 60 + side * 12), (389, 60 + side * 16), (385, 60 + side * 19), (262, 60 + side * 5)]
        sk.shape(arm, 1.5)
        for k in range(300, 360, 5):
            sk.line([(k, 60 + side * 7.5), (k + 1, 60 + side * 13)], 0.6, passes=1)
    sk.shape([(250, 56), (264, 55), (264, 65), (250, 64)], 1.5)
    sk.text(325, 105, "forceps", 22, anchor="middle")
    _hemostat(sk, 500, 62)
    sk.text(530, 115, "hemostat", 22, anchor="middle")
    # Retractor: hatched handle with a hooked blade.
    handle = [(40, 165), (190, 163), (190, 177), (40, 179)]
    sk.shape(handle, 1.7)
    sk.hatch(handle, 60, 3.5)
    blade = [(190, 163), (214, 163), (230, 206), (222, 214), (210, 212), (200, 177), (190, 177)]
    sk.shape(blade, 1.7)
    sk.hatch([(214, 163), (230, 206), (222, 214), (206, 177)], 20, 3.5)
    sk.text(130, 225, "retractor", 22, anchor="middle")
    # Curved needle trailing its thread.
    arc = [(310 + 34 * math.cos(math.radians(d)), 185 - 34 * math.sin(math.radians(d))) for d in range(200, 10, -10)]
    sk.line(arc, 2.0)
    thread = [arc[-1]] + [(arc[-1][0] + 8 + k * 8, arc[-1][1] - 4 + 12 * math.sin(k * 0.6)) for k in range(12)]
    sk.line(thread, 0.9, RED, wobble=0.3)
    sk.text(340, 240, "suture needle", 22, anchor="middle")
    # Gauze roll.
    roll = [(490, 160), (570, 160), (570, 204), (490, 204)]
    sk.shape(roll, 1.5, "#f6efe0")
    for k in range(500, 570, 9):
        sk.line([(k, 161), (k + 2, 203)], 0.5, passes=1)
    sk.hatch([(490, 194), (570, 194), (570, 204), (490, 204)], 30, 3)
    sk.ellipse(570, 182, 11, 22, 1.5, fill="#f6efe0")
    sk.ellipse(570, 182, 4, 8, 1.0)
    sk.shape([(572, 204), (610, 214), (632, 208), (626, 220), (592, 222), (570, 206)], 1.2, "#f6efe0")
    sk.text(550, 245, "gauze", 22, anchor="middle")
    # Bone saw with a heavy grip, as in the old surgical plates.
    grip = [(40, 282), (84, 268), (106, 274), (110, 314), (84, 330), (54, 324), (62, 306), (40, 296)]
    sk.shape(grip, 1.8, "#3a2a1e")
    sk.ellipse(82, 296, 7, 10, 1.4, fill=PALE)
    top, bottom = 276, 314
    blade = [(110, top), (360, top), (360, bottom), (110, bottom)]
    sk.shape(blade, 1.6)
    spine = [(110, top), (360, top), (360, top + 9), (110, top + 9)]
    sk.hatch(spine, 0, 2.5)
    sk.line([(110, top + 9), (360, top + 9)], 1.0, passes=1)
    teeth = [(110 + k * 6, bottom + (5 if k % 2 else 0)) for k in range(42)]
    sk.line(teeth, 1.0, passes=1, wobble=0.2)
    sk.text(425, 305, "bone saw", 22, anchor="middle")
    # Gelpi retractor standing up: ring handles with the ratchet between them, arms bowing apart from the box joint,
    # each ending in a point turned outward.
    for side in (-1, 1):
        sk.shape(
            [(592 + side * 2, 300), (592 + side * 24, 276), (592 + side * 20, 256), (592 + side * 16, 256), (592 + side * 19, 276), (592 + side * 1, 296)], 1.4
        )
        sk.line([(592 + side * 18, 257), (592 + side * 25, 255), (592 + side * 27, 262)], 1.3, passes=1)
        sk.shape([(592 + side * 2, 300), (592 + side * 9, 320), (592 + side * 12, 322), (592 + side * 6, 300)], 1.3)
        sk.ellipse(592 + side * 13, 331, 8, 7, 1.8, fill=PALE)
        sk.ellipse(592 + side * 13, 331, 4, 3, 0.9)
    sk.line([(583, 318), (592, 313), (602, 315)], 1.2, passes=1)
    sk.ellipse(592, 300, 4, 4, 1.4, fill=PALE)
    sk.text(530, 300, "gelpi", 22, anchor="middle")
    return sk


DIAGRAMS: dict[str, Callable[[], Sketch]] = {
    "incision_depth": incision,
    "closures": closures,
    "setup_order": setup_order,
    "rhythms": rhythms,
    "blood_types": blood_types,
    "alignment": alignment,
    "turning": turning,
    "instruments": instruments,
}


# --- Paper and frame --------------------------------------------------------------------------------


def paper(width: int = 1400, height: int = 1800) -> Image.Image:
    """Pale parchment: faint blotches, fibres and slightly darker edges."""
    rng = np.random.default_rng(7)

    def smooth(scale: int) -> NDArray[np.float32]:
        small = (rng.random((height // scale + 2, width // scale + 2)) * 255).astype(np.uint8)
        return np.asarray(Image.fromarray(small).resize((width, height), Image.Resampling.BICUBIC), dtype=np.float32) / np.float32(255)

    blotch = 0.5 * smooth(300) + 0.3 * smooth(90) + 0.2 * smooth(25)
    yy, xx = np.mgrid[0:height, 0:width].astype(np.float32)
    edge = np.clip(np.minimum.reduce([xx, width - xx, yy, height - yy]) / 160, 0, 1) ** 0.6
    t = np.clip((blotch - 0.4) * 0.6 + (1 - edge) * 0.45, 0, 1)[..., None]
    base, dark = np.array([240, 233, 216], np.float32), np.array([212, 196, 164], np.float32)
    rgb = base * (1 - t) + dark * t + rng.normal(0, 3.5, (height, width, 1)).astype(np.float32)
    fibres = np.zeros((height, width), np.uint8)
    for _ in range(900):
        x, y, ang, length = rng.integers(0, width), rng.integers(0, height), rng.random() * math.pi, rng.integers(8, 40)
        s = np.arange(length)
        px, py = (x + np.cos(ang) * s).astype(int), (y + np.sin(ang) * s).astype(int)
        ok = (px >= 0) & (px < width) & (py >= 0) & (py < height)
        fibres[py[ok], px[ok]] = 36
    mask = Image.fromarray(fibres).filter(ImageFilter.GaussianBlur(0.6))
    img = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8))
    return Image.composite(Image.new("RGB", (width, height), (170, 150, 118)), img, mask)


def frame() -> str:
    """Nine-patch page border: red outer rule, black double rule, red inner rule and square corner blocks.
    Every line sits inside FRAME_MARGIN, so the middle of the texture is empty and stretches cleanly."""
    s = 4 * FRAME_MARGIN
    rules = [(3, RED, 1.6), (12, INK, 2.2), (17, INK, 0.9), (24, RED, 0.9)]
    parts = [f'<rect x="{i}" y="{i}" width="{s - 2 * i}" height="{s - 2 * i}" stroke="{c}" stroke-width="{w}" fill="none"/>' for i, c, w in rules]
    for cx, cy in ((0, 0), (s, 0), (0, s), (s, s)):
        x, y = min(cx, s - 21) if cx else 1, min(cy, s - 21) if cy else 1
        parts.append(f'<rect x="{x}" y="{y}" width="20" height="20" stroke="{INK}" stroke-width="2.2" fill="#e8dcc0"/>')
        parts.append(f'<rect x="{x + 5}" y="{y + 5}" width="10" height="10" stroke="{RED}" stroke-width="1" fill="none"/>')
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="{s}" height="{s}" viewBox="0 0 {s} {s}">{"".join(parts)}</svg>\n'


def build() -> Iterable[Path]:
    diagrams = MANUAL_DIR / "diagrams"
    diagrams.mkdir(parents=True, exist_ok=True)
    for name, draw in DIAGRAMS.items():
        path = diagrams / f"{name}.svg"
        path.write_text(draw().svg())
        yield path
    path = MANUAL_DIR / "paper.jpg"
    paper().save(path, quality=88)
    yield path
    path = MANUAL_DIR / "frame.svg"
    path.write_text(frame())
    yield path
