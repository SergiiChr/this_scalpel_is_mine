"""Key frame review report: compares a run's key frames against a baseline run and writes one sheet per changed or new
key frame, with a Markdown index that says what to open.

  python -m tools.keyframes CURRENT BASELINE OUT [--all]

CURRENT and BASELINE are screenshot folders (build/test-artifacts/screenshots of two runs); BASELINE may be missing.
A sheet puts a key frame's views (top | oblique) side by side, sized to what a model sees anyway, so one key frame is
one image to read.
"""

from __future__ import annotations

import argparse
import re
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFont

# Views of one key frame, in the order a sheet shows them. Files without one of these suffixes are a view of their own.
VIEWS = ("top", "oblique", "view")
# A pixel counts as changed when its brightness differs by more than this (0-255): software rendering under xvfb is
# deterministic, but leaves room for dithering and float noise.
PIXEL_TOLERANCE = 8
# A view counts as changed when more than this share of its pixels did.
CHANGED_SHARE = 0.001
# The widest image a model reads without scaling it down: a sheet is sized to it so what's saved is what's seen.
SHEET_WIDTH = 1568
CAPTION_HEIGHT = 28
CHECKLIST = "continuity between frames, clipping, mesh intersections, material consistency, tool contact, animation transitions, UI legibility"


@dataclass
class KeyFrame:
    folder: str
    name: str
    # Place in its folder: the order the test took it in.
    order: int
    # View name to file, in VIEWS order.
    views: dict[str, Path] = field(default_factory=dict)


@dataclass
class Change:
    view: str
    share: float
    box: tuple[int, int, int, int] | None


def key_frames(root: Path) -> dict[tuple[str, str], KeyFrame]:
    """Key frames under root by (folder, name). The name leaves out the NN_ order prefix, so adding a key frame early in
    a test doesn't make every later one look new; a name taken twice in a folder gets #2, #3."""
    found: dict[tuple[str, str], KeyFrame] = {}
    if not root.is_dir():
        return found
    for folder in sorted({path.parent for path in root.rglob("*.png")}):
        relative = folder.relative_to(root).as_posix()
        seen: dict[str, int] = {}
        by_stem: dict[str, KeyFrame] = {}
        for path in sorted(folder.glob("*.png")):
            stem, view = path.stem, "view"
            for suffix in VIEWS:
                if stem.endswith(f"_{suffix}"):
                    stem, view = stem.removesuffix(f"_{suffix}"), suffix
                    break
            if stem not in by_stem:
                name = re.sub(r"^\d+_", "", stem)
                seen[name] = seen.get(name, 0) + 1
                by_stem[stem] = KeyFrame(relative, name if seen[name] == 1 else f"{name}#{seen[name]}", len(by_stem))
            by_stem[stem].views[view] = path
        for frame in by_stem.values():
            frame.views = {view: frame.views[view] for view in VIEWS if view in frame.views}
            found[(frame.folder, frame.name)] = frame
    return found


def compare(current: Path, before: Path, view: str) -> Change | None:
    """How a view changed, None when it didn't."""
    with Image.open(current) as now, Image.open(before) as then:
        if now.size != then.size:
            return Change(view, 1.0, None)
        mask = ImageChops.difference(now.convert("RGB"), then.convert("RGB")).convert("L").point(lambda v: 255 if v > PIXEL_TOLERANCE else 0)
    share = mask.histogram()[255] / (mask.width * mask.height)
    return Change(view, share, mask.getbbox()) if share > CHANGED_SHARE else None


def sheet(frame: KeyFrame, label: str, out: Path) -> None:
    """The frame's views side by side under a caption, scaled down to SHEET_WIDTH."""
    images = [Image.open(path).convert("RGB") for path in frame.views.values()]
    height = max(image.height for image in images)
    width = sum(image.width for image in images)
    scale = min(1.0, SHEET_WIDTH / width)
    canvas = Image.new("RGB", (width, height), "black")
    x = 0
    for image in images:
        canvas.paste(image, (x, 0))
        x += image.width
        image.close()
    canvas = canvas.resize((round(width * scale), round(height * scale)), Image.Resampling.LANCZOS)
    result = Image.new("RGB", (canvas.width, canvas.height + CAPTION_HEIGHT), "black")
    result.paste(canvas, (0, CAPTION_HEIGHT))
    caption = f"{frame.folder}/{frame.name} ({label}): {' | '.join(frame.views)}"
    ImageDraw.Draw(result).text((8, 4), caption, fill="white", font=ImageFont.load_default(size=18))
    out.parent.mkdir(parents=True, exist_ok=True)
    result.save(out)


def describe(changes: list[Change]) -> str:
    parts = []
    for change in changes:
        box = f", pixels {change.box[0]}-{change.box[2]} x {change.box[1]}-{change.box[3]}" if change.box else ", new size"
        parts.append(f"{change.view} {change.share:.1%}{box}")
    return "; ".join(parts)


def report(current_root: Path, baseline_root: Path, out: Path, everything: bool) -> str:
    """Writes the sheets and returns the Markdown index."""
    current, baseline = key_frames(current_root), key_frames(baseline_root)
    has_baseline = bool(baseline)
    sections: dict[str, list[str]] = {}
    unchanged: dict[str, int] = {}
    counts = {"changed": 0, "new": 0, "removed": 0, "unchanged": 0}

    for key in sorted(current.keys() | baseline.keys(), key=lambda key: (key[0], (current.get(key) or baseline[key]).order)):
        frame, before = current.get(key), baseline.get(key)
        folder, name = key
        target = out / "sheets" / folder / f"{name.replace('#', '_')}.png"
        lines = sections.setdefault(folder, [])
        if frame is None:
            counts["removed"] += 1
            lines.append(f"- removed: {name}")
            continue
        if before is None:
            counts["new"] += 1
            sheet(frame, "new", target)
            lines.append(f"- {'new' if has_baseline else 'frame'}: {name} -> `{target.as_posix()}`")
            continue
        changes = [
            change
            for view, path in frame.views.items()
            if (change := compare(path, before.views[view], view) if view in before.views else Change(view, 1.0, None))
        ]
        if not changes and not everything:
            counts["unchanged"] += 1
            unchanged[folder] = unchanged.get(folder, 0) + 1
            continue
        counts["changed" if changes else "unchanged"] += 1
        sheet(frame, "changed" if changes else "unchanged", target)
        before_sheet = target.with_suffix(".before.png")
        if changes:
            sheet(before, "before", before_sheet)
            lines.append(f"- changed: {name} ({describe(changes)}) -> `{target.as_posix()}`, before: `{before_sheet.as_posix()}`")
        else:
            lines.append(f"- unchanged: {name} -> `{target.as_posix()}`")

    summary = ", ".join(f"{count} {state}" for state, count in counts.items())
    text = [
        "# Key frame review",
        "",
        f"Current: `{current_root.as_posix()}`  ",
        f"Baseline: `{baseline_root.as_posix()}`" if has_baseline else "Baseline: none, every key frame is listed.",
        "",
        f"{summary}.",
        "",
        "Open every sheet listed below: one sheet is one key frame, its views side by side in the order named in its "
        "caption (top | oblique, or the surgeon's view). Open a `before` sheet only when a change needs explaining. "
        "Read the key frames of a folder in order, they tell the story of the test.",
        "",
        f"Look for: {CHECKLIST}.",
        "Each line gives where a change is (share of pixels and their box in the full-size view) to look at first.",
    ]
    for folder in sorted(sections.keys() | unchanged.keys()):
        lines = sections.get(folder, [])
        if unchanged.get(folder):
            lines = [*lines, f"- {unchanged[folder]} unchanged"]
        if lines:
            text += ["", f"## {folder}", "", *lines]
    return "\n".join(text) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(prog="python -m tools.keyframes", description=__doc__.split("\n\n")[0])
    parser.add_argument("current", type=Path, help="screenshot folder of the run to review")
    parser.add_argument("baseline", type=Path, help="screenshot folder to compare against, may be missing")
    parser.add_argument("out", type=Path, help="report folder, emptied first")
    parser.add_argument("--all", action="store_true", help="sheets for unchanged key frames too")
    parsed = parser.parse_args()
    if not key_frames(parsed.current):
        print(f"No key frames in {parsed.current}.", file=sys.stderr)
        return 1
    shutil.rmtree(parsed.out, ignore_errors=True)
    parsed.out.mkdir(parents=True)
    index = parsed.out / "report.md"
    index.write_text(report(parsed.current, parsed.baseline, parsed.out, parsed.all))
    print(f"Key frame review: {index}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
