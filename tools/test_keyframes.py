"""Checks of the key frame comparison and baseline update on small generated images; no Godot runtime needed."""

import json
import sys
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageDraw

# Imported by its file's name, as mypy (run on the tools folder, which isn't a package) knows it.
sys.path.insert(0, str(Path(__file__).parent))
import keyframes  # noqa: E402


def scene(path: Path, line: int = 0, shift: int = 0, speck: bool = False) -> None:
    """A gradient view, with a line `line` pixels wide, every pixel `shift` brighter, or one odd pixel."""
    image = Image.new("RGB", (96, 64))
    image.putdata([(40 + x + shift, 60 + y + shift, 90 + shift) for y in range(64) for x in range(96)])
    draw = ImageDraw.Draw(image)
    if line:
        draw.line([(10, 5), (10, 58)], fill=(200, 30, 30), width=line)
    if speck:
        image.putpixel((50, 30), (255, 255, 255))
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path)


class KeyFramesTest(unittest.TestCase):
    def setUp(self) -> None:
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.run_frames, self.baseline = self.root / "run", self.root / "KeyFrames"

    def take(self, name: str, line: int = 0, shift: int = 0) -> None:
        for view in ("top", "oblique"):
            scene(self.run_frames / "scalpel" / f"{name}_{view}.png", line, shift)
        index = self.run_frames / "scalpel" / keyframes.DESCRIPTIONS
        descriptions = json.loads(index.read_text()) if index.exists() else {}
        index.write_text(json.dumps({**descriptions, f"{name}_top.png": f"{name} as it should be"}))

    def differ(self) -> int:
        return keyframes.report(self.run_frames, self.baseline, self.root / "review", False, "passed")[1]

    def test_first_update_writes_every_key_frame_as_jpeg(self) -> None:
        self.take("00_cut")
        self.assertEqual(keyframes.update(self.run_frames, self.baseline), 1)
        self.assertEqual(sorted(path.name for path in (self.baseline / "scalpel").iterdir()), ["00_cut_oblique.jpg", "00_cut_top.jpg", "keyframes.json"])
        self.assertTrue((self.baseline / ".gdignore").exists())
        self.assertEqual(json.loads((self.baseline / "scalpel" / keyframes.DESCRIPTIONS).read_text())["00_cut_top.jpg"], "00_cut as it should be")
        self.assertEqual(self.differ(), 0)

    def test_thin_line_is_a_change(self) -> None:
        self.take("00_cut")
        keyframes.update(self.run_frames, self.baseline)
        self.take("00_cut", line=2)
        self.assertEqual(self.differ(), 1)

    def test_faint_shift_over_the_whole_view_is_a_change(self) -> None:
        self.take("00_cut")
        keyframes.update(self.run_frames, self.baseline)
        self.take("00_cut", shift=3)
        self.assertEqual(self.differ(), 1)

    def test_lone_pixel_is_not_a_change(self) -> None:
        self.take("00_cut")
        keyframes.update(self.run_frames, self.baseline)
        for view in ("top", "oblique"):
            scene(self.run_frames / "scalpel" / f"00_cut_{view}.png", speck=True)
        self.assertEqual(self.differ(), 0)

    def test_missing_and_new_key_frames_differ(self) -> None:
        self.take("00_cut")
        keyframes.update(self.run_frames, self.baseline)
        for path in (self.run_frames / "scalpel").glob("00_cut_*.png"):
            path.unlink()
        self.take("01_stitch")
        self.assertEqual(self.differ(), 2)

    def test_update_writes_only_what_differs(self) -> None:
        self.take("00_cut")
        self.take("01_close")
        self.take("02_gone")
        keyframes.update(self.run_frames, self.baseline)
        unchanged = (self.baseline / "scalpel" / "00_cut_top.jpg").stat().st_mtime_ns
        self.take("01_close", line=3)
        for path in (self.run_frames / "scalpel").glob("02_gone_*.png"):
            path.unlink()
        self.assertEqual(keyframes.update(self.run_frames, self.baseline), 2)
        self.assertEqual((self.baseline / "scalpel" / "00_cut_top.jpg").stat().st_mtime_ns, unchanged)
        self.assertFalse((self.baseline / "scalpel" / "02_gone_top.jpg").exists())
        self.assertEqual(self.differ(), 0)

    def test_folder_of_a_test_without_key_frames_goes(self) -> None:
        self.take("00_cut")
        keyframes.update(self.run_frames, self.baseline)
        for path in (self.run_frames / "scalpel").iterdir():
            path.unlink()
        scene(self.run_frames / "suture" / "00_knot_top.png")
        keyframes.update(self.run_frames, self.baseline)
        self.assertFalse((self.baseline / "scalpel").exists())


if __name__ == "__main__":
    unittest.main()
