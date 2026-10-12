"""Key frame review: compares a run's key frames against the committed baseline and writes one sheet per missing,
changed or new key frame, with a Markdown index that says what to open, or writes the run into the baseline.

  python -m tools.keyframes report CURRENT BASELINE OUT [--all] [--regression passed|failed|not-run]
  python -m tools.keyframes update CURRENT BASELINE [--keep-missing]

CURRENT is a run's screenshot folder (build/test-artifacts/screenshots, PNG) and BASELINE the committed tests/KeyFrames
(JPEG, see stored()); BASELINE may be missing.
report exits non-zero when any key frame is missing, changed or new: a review passes only when nothing differs.
update writes only the key frames that differ, so the ones that didn't keep their files and add nothing to git history.
A sheet puts a key frame's views (top | oblique) side by side, sized to what a model sees anyway, so one key frame is
one image to read.
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import re
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageStat

# Views of one key frame, in the order a sheet shows them. Files without one of these suffixes are a view of their own.
VIEWS = ("top", "front", "oblique", "side", "reverse", "view")
# Each folder's file name to description index, written by the tests' KeyFrames.
DESCRIPTIONS = "keyframes.json"
# The baseline's file type and encoder settings: about a sixth of a PNG's size. A run's key frames are encoded the same
# way before they're compared, so an unchanged scene gives the same bytes and both sides lose the same detail.
# Full chroma (subsampling 0) keeps thin colored edges (a blood line, a tool tip) as sharp as the luma.
BASELINE_SUFFIX = ".jpg"
JPEG_SETTINGS = {"quality": 90, "subsampling": 0, "optimize": True}
IMAGE_SUFFIXES = (".png", BASELINE_SUFFIX)
# A pixel counts as changed when one of its channels differs by more than this (0-255). Key frames are taken without
# film grain and always by the software renderer, but its versions on two machines may still round a pixel differently.
PIXEL_TOLERANCE = 8
# A view counts as changed when it has a block this many pixels across that all changed. Small, concentrated changes
# (a clipping tool tip, a one pixel wide line) count however little of the view they cover; lone pixels don't.
CHANGED_BLOCK = 2
# A view also counts as changed when its pixels differ by this much on average (0-255): a tint or lighting shift too
# faint for PIXEL_TOLERANCE but over the whole view. Rounding noise averages well below it.
MEAN_TOLERANCE = 1.0
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
    # What the test says it should show.
    description: str = ""


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
    images = [path for path in root.rglob("*") if path.suffix in IMAGE_SUFFIXES]
    for folder in sorted({path.parent for path in images}):
        relative = folder.relative_to(root).as_posix()
        seen: dict[str, int] = {}
        by_stem: dict[str, KeyFrame] = {}
        index = folder / DESCRIPTIONS
        descriptions: dict[str, str] = json.loads(index.read_text()) if index.exists() else {}
        for path in sorted(path for path in folder.iterdir() if path.suffix in IMAGE_SUFFIXES):
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
            by_stem[stem].description = by_stem[stem].description or descriptions.get(path.name, "")
        for frame in by_stem.values():
            frame.views = {view: frame.views[view] for view in VIEWS if view in frame.views}
            found[(frame.folder, frame.name)] = frame
    return found


def stored(path: Path) -> bytes:
    """The image as the baseline stores it: a baseline file as it is, a run's PNG encoded to the baseline's JPEG."""
    if path.suffix == BASELINE_SUFFIX:
        return path.read_bytes()
    with Image.open(path) as image:
        out = io.BytesIO()
        image.convert("RGB").save(out, "JPEG", **JPEG_SETTINGS)
    return out.getvalue()


def compare(frame: KeyFrame, before: KeyFrame) -> list[Change]:
    """How each view of a key frame changed against its baseline, views it lost or gained included."""
    changes = []
    for view in VIEWS:
        now, then = frame.views.get(view), before.views.get(view)
        if now is None or then is None:
            if now or then:
                changes.append(Change(view, "missing" if then else "added"))
            continue
        now_bytes, then_bytes = stored(now), stored(then)
        # The same scene encodes to the same bytes: no pixels to compare.
        if now_bytes == then_bytes:
            continue
        with Image.open(io.BytesIO(now_bytes)) as current, Image.open(io.BytesIO(then_bytes)) as baseline:
            if current.size != baseline.size:
                changes.append(Change(view, "resized"))
                continue
            channels = ImageChops.difference(current.convert("RGB"), baseline.convert("RGB")).split()
        biggest = ImageChops.lighter(ImageChops.lighter(channels[0], channels[1]), channels[2])
        mask = biggest.point(lambda v: 255 if v > PIXEL_TOLERANCE else 0)
        # Keeps only pixels whose whole CHANGED_BLOCK square changed. offset() wraps at the edges, which can only add a
        # change, never hide one.
        blocks = mask
        for dx in range(CHANGED_BLOCK):
            for dy in range(CHANGED_BLOCK):
                blocks = ImageChops.darker(blocks, ImageChops.offset(mask, dx, dy))
        mean = ImageStat.Stat(biggest).mean[0]
        if blocks.getbbox():
            box = mask.getbbox() or (0, 0, 0, 0)
            changes.append(Change(view, f"{mask.histogram()[255]} pixels within x {box[0]}-{box[2]}, y {box[1]}-{box[3]}"))
        elif mean > MEAN_TOLERANCE:
            changes.append(Change(view, f"the whole view shifted by {mean:.1f} on average"))
    return changes


def state(frame: KeyFrame | None, before: KeyFrame | None) -> tuple[str, list[Change]]:
    """missing, new, changed or unchanged, with what changed."""
    if frame is None:
        return "missing", []
    if before is None:
        return "new", []
    changes = compare(frame, before)
    return ("changed" if changes else "unchanged"), changes


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


def report(current_root: Path, baseline_root: Path, out: Path, everything: bool, regression: str) -> tuple[str, int]:
    """Writes the sheets and returns the Markdown index and how many key frames differ from the baseline."""
    current, baseline = key_frames(current_root), key_frames(baseline_root)
    states = ("missing", "changed", "new", "unchanged")
    counts = dict.fromkeys(states, 0)
    sections: dict[str, list[str]] = {}
    # The description last printed in each folder: frames that share one get it once.
    described: dict[str, str] = {}
    unchanged: dict[str, int] = {}
    for key in sorted(current.keys() | baseline.keys(), key=lambda key: (key[0], (current.get(key) or baseline[key]).order)):
        frame, before = current.get(key), baseline.get(key)
        folder, name = key
        target = out / "sheets" / folder / f"{name.replace('#', '_')}.png"
        before_sheet = target.with_name(f"{target.stem}.before.png")
        found, changes = state(frame, before)
        counts[found] += 1
        if found == "unchanged" and not everything:
            unchanged[folder] = unchanged.get(folder, 0) + 1
            continue
        line = f"- {found}: {name}"
        if changes:
            line += f" ({'; '.join(f'{change.view} {change.what}' for change in changes)})"
        if frame:
            sheet(frame, found, target)
            line += f" -> `{target.as_posix()}`"
        if before and found != "unchanged":
            sheet(before, "before", before_sheet)
            line += f", before: `{before_sheet.as_posix()}`"
        lines = sections.setdefault(folder, [])
        description = next(known.description for known in (frame, before) if known)
        if description and description != described.get(folder):
            described[folder] = description
            lines.append(f"Should show: {description}")
        lines.append(line)

    text = [
        "# Key frame review",
        "",
        {
            "passed": "Regression: passed.",
            "failed": "Regression: FAILED, even after running the failed suites again (the run's output names them). "
            "Review their sheets: the baseline takes them only if the case isn't visibly broken. "
            "Key frames they didn't take show as missing below and stay in the baseline.",
            "not-run": "Regression: not run, these are the key frames of an earlier run.",
        }[regression],
        f"Current: `{current_root.as_posix()}`  ",
        f"Baseline: `{baseline_root.as_posix()}`"
        if baseline
        else f"Baseline: NONE in `{baseline_root.as_posix()}`. Every key frame shows as new and nothing was compared: "
        "the review fails. Run `./build.py generate_baseline` on main first.",
        "",
        ", ".join(f"{counts[state]} {state}" for state in states) + ".",
        "",
        "- missing: the baseline has this key frame or view and this run doesn't. Always a finding: a test stopped short or no longer captures it.",
        "- changed: a view differs from the baseline. Open the sheet, then `before` when the change isn't what the work "
        "meant to do. Look first where the line says the change is (pixels of the full-size view).",
        "- new: no baseline to compare against. Open the sheet.",
        "",
        "A sheet is one key frame, its views side by side as its caption names them (top | oblique). Read a folder's key "
        'frames in order: they tell the story of the test. A "Should show" line is what the test says the key frames '
        "under it show, up to the next one: check the picture against it.",
        f"Look for: {CHECKLIST}.",
    ]
    for folder in sorted(sections.keys() | unchanged.keys()):
        lines = sections.get(folder, [])
        if unchanged.get(folder):
            lines = [*lines, f"- {unchanged[folder]} unchanged"]
        text += ["", f"## {folder}", "", *lines]
    return "\n".join(text) + "\n", counts["missing"] + counts["changed"] + counts["new"]


def update(current_root: Path, baseline_root: Path, keep_missing: bool = False) -> int:
    """Writes the key frames that differ from the baseline into it and deletes the ones the run no longer takes, unless
    keep_missing (a run with failed cases, which may have stopped before taking them).
    Unchanged key frames keep their bytes, so they cost nothing in git; they only take the run's file name when its
    order prefix moved, which git sees as a rename. Returns how many key frames were written or deleted."""
    current, baseline = key_frames(current_root), key_frames(baseline_root)
    kept = {key: frame for key, frame in baseline.items() if key not in current} if keep_missing else {}
    # Every file's new bytes are read before any is written: under the run's names, a key frame may take a file another
    # one still has.
    files: dict[Path, bytes] = {}
    written = 0
    for key, frame in current.items():
        before = baseline.get(key)
        unchanged = state(frame, before)[0] == "unchanged"
        written += not unchanged
        for view, path in frame.views.items():
            files[baseline_root / frame.folder / path.with_suffix(BASELINE_SUFFIX).name] = (
                before.views[view].read_bytes() if before and unchanged else stored(path)
            )
    for frame in kept.values():
        for path in frame.views.values():
            files.setdefault(path, path.read_bytes())
    written += len(baseline.keys() - current.keys() - kept.keys())
    for frame in baseline.values():
        for path in frame.views.values():
            path.unlink()
    for target, data in files.items():
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    # Descriptions follow the run, whose wording may have changed with the same pictures.
    for folder in {key[0] for key in baseline.keys() | current.keys()}:
        index = baseline_root / folder / DESCRIPTIONS
        written_frames = [frame for frame in [*current.values(), *kept.values()] if frame.folder == folder]
        descriptions = {path.with_suffix(BASELINE_SUFFIX).name: frame.description for frame in written_frames for path in frame.views.values()}
        if descriptions:
            index.write_text(json.dumps(descriptions, indent=2, sort_keys=True) + "\n")
            continue
        # Folders of tests that take no key frames any more go.
        index.unlink(missing_ok=True)
        with contextlib.suppress(OSError):
            index.parent.rmdir()
    # Godot mustn't import them.
    (baseline_root / ".gdignore").touch()
    return written


def main() -> int:
    parser = argparse.ArgumentParser(prog="python -m tools.keyframes", description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True)
    review = commands.add_parser("report", help="write the review report; fails when any key frame differs")
    updating = commands.add_parser("update", help="write the key frames that differ into the baseline")
    for command in (review, updating):
        command.add_argument("current", type=Path, help="screenshot folder of the run")
        command.add_argument("baseline", type=Path, help="baseline folder, may be missing")
    updating.add_argument("--keep-missing", action="store_true", help="keep the baseline's key frames the run didn't take")
    review.add_argument("out", type=Path, help="report folder, emptied first")
    review.add_argument("--all", action="store_true", help="sheets for unchanged key frames too")
    review.add_argument("--regression", choices=("passed", "failed", "not-run"), default="not-run", help="how the run went, for the top of the report")
    parsed = parser.parse_args()
    if parsed.command == "update":
        print(f"Baseline: {update(parsed.current, parsed.baseline, parsed.keep_missing)} key frames written or deleted in {parsed.baseline}.")
        return 0
    # A run that failed before its first key frame still reports the baseline's as missing.
    if not key_frames(parsed.current) and not key_frames(parsed.baseline):
        print(f"No key frames in {parsed.current} or {parsed.baseline}.", file=sys.stderr)
        return 1
    shutil.rmtree(parsed.out, ignore_errors=True)
    parsed.out.mkdir(parents=True)
    index = parsed.out / "report.md"
    text, differ = report(parsed.current, parsed.baseline, parsed.out, parsed.all, parsed.regression)
    index.write_text(text)
    print(f"Key frame review: {index}")
    if differ:
        print(f"[ERROR] {differ} key frames are missing, changed or new against the baseline: review them.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
