"""Key frame review report: compares a run's key frames against a baseline run and writes one sheet per missing,
changed or new key frame, with a Markdown index that says what to open.

  python -m tools.keyframes CURRENT BASELINE OUT [--all] [--regression-failed]

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

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

# Views of one key frame, in the order a sheet shows them. Files without one of these suffixes are a view of their own.
VIEWS = ("top", "oblique", "view")
# A pixel counts as changed when one of its channels differs by more than this (0-255). Rendering is deterministic, but
# the screen grime shader's film grain moves every pixel by up to 6 between runs.
PIXEL_TOLERANCE = 8
# A view counts as changed when it has a block this many pixels across that all changed. Small, concentrated changes
# (a clipping tool tip, a drop of blood) count however little of the view they cover; lone pixels don't.
CHANGED_BLOCK = 3
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
    # What changed: "missing" or "added" for a whole view, "resized", or where its pixels changed.
    what: str


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


def compare(frame: KeyFrame, before: KeyFrame) -> list[Change]:
    """How each view of a key frame changed against its baseline, views it lost or gained included."""
    changes = []
    for view in VIEWS:
        now, then = frame.views.get(view), before.views.get(view)
        if now is None or then is None:
            if now or then:
                changes.append(Change(view, "missing" if then else "added"))
            continue
        with Image.open(now) as current, Image.open(then) as baseline:
            if current.size != baseline.size:
                changes.append(Change(view, "resized"))
                continue
            channels = ImageChops.difference(current.convert("RGB"), baseline.convert("RGB")).split()
        biggest = ImageChops.lighter(ImageChops.lighter(channels[0], channels[1]), channels[2])
        mask = biggest.point(lambda v: 255 if v > PIXEL_TOLERANCE else 0)
        # Keeps only pixels whose whole block around them changed.
        blocks = mask.filter(ImageFilter.MinFilter(CHANGED_BLOCK))
        if blocks.getbbox():
            box = mask.getbbox() or (0, 0, 0, 0)
            changes.append(Change(view, f"{mask.histogram()[255]} pixels within x {box[0]}-{box[2]}, y {box[1]}-{box[3]}"))
    return changes


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


def report(current_root: Path, baseline_root: Path, out: Path, everything: bool, regression_failed: bool) -> str:
    """Writes the sheets and returns the Markdown index."""
    current, baseline = key_frames(current_root), key_frames(baseline_root)
    states = ("missing", "changed", "new", "unchanged")
    counts = dict.fromkeys(states, 0)
    sections: dict[str, list[str]] = {}
    unchanged: dict[str, int] = {}
    for key in sorted(current.keys() | baseline.keys(), key=lambda key: (key[0], (current.get(key) or baseline[key]).order)):
        frame, before = current.get(key), baseline.get(key)
        folder, name = key
        target = out / "sheets" / folder / f"{name.replace('#', '_')}.png"
        before_sheet = target.with_name(f"{target.stem}.before.png")
        changes = compare(frame, before) if frame and before else []
        state = "missing" if frame is None else "new" if before is None else "changed" if changes else "unchanged"
        counts[state] += 1
        if state == "unchanged" and not everything:
            unchanged[folder] = unchanged.get(folder, 0) + 1
            continue
        line = f"- {state}: {name}"
        if changes:
            line += f" ({'; '.join(f'{change.view} {change.what}' for change in changes)})"
        if frame:
            sheet(frame, state, target)
            line += f" -> `{target.as_posix()}`"
        if before and state != "unchanged":
            sheet(before, "before", before_sheet)
            line += f", before: `{before_sheet.as_posix()}`"
        sections.setdefault(folder, []).append(line)

    text = [
        "# Key frame review",
        "",
        "Regression: FAILED. Fix the failed cases first (the run's output names them): key frames they didn't take show as missing below."
        if regression_failed
        else "Regression: passed.",
        f"Current: `{current_root.as_posix()}`  ",
        f"Baseline: `{baseline_root.as_posix()}`" if baseline else "Baseline: none, every key frame is new.",
        "",
        ", ".join(f"{counts[state]} {state}" for state in states) + ".",
        "",
        "- missing: the baseline has this key frame or view and this run doesn't. Always a finding: a test stopped short or no longer captures it.",
        "- changed: a view differs from the baseline. Open the sheet, then `before` when the change isn't what the work "
        "meant to do. Look first where the line says the change is (pixels of the full-size view).",
        "- new: no baseline to compare against. Open the sheet.",
        "",
        "A sheet is one key frame, its views side by side as its caption names them (top | oblique). Read a folder's key "
        "frames in order: they tell the story of the test.",
        f"Look for: {CHECKLIST}.",
    ]
    for folder in sorted(sections.keys() | unchanged.keys()):
        lines = sections.get(folder, [])
        if unchanged.get(folder):
            lines = [*lines, f"- {unchanged[folder]} unchanged"]
        text += ["", f"## {folder}", "", *lines]
    return "\n".join(text) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(prog="python -m tools.keyframes", description=__doc__.split("\n\n")[0])
    parser.add_argument("current", type=Path, help="screenshot folder of the run to review")
    parser.add_argument("baseline", type=Path, help="screenshot folder to compare against, may be missing")
    parser.add_argument("out", type=Path, help="report folder, emptied first")
    parser.add_argument("--all", action="store_true", help="sheets for unchanged key frames too")
    parser.add_argument("--regression-failed", action="store_true", help="say so at the top of the report")
    parsed = parser.parse_args()
    if not key_frames(parsed.current):
        print(f"No key frames in {parsed.current}.", file=sys.stderr)
        return 1
    shutil.rmtree(parsed.out, ignore_errors=True)
    parsed.out.mkdir(parents=True)
    index = parsed.out / "report.md"
    index.write_text(report(parsed.current, parsed.baseline, parsed.out, parsed.all, parsed.regression_failed))
    print(f"Key frame review: {index}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
