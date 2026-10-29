#!/usr/bin/env python3
"""One script for players and developers (Fedora first, works on any x86_64 Linux with the listed packages).

  ./build.py play      Play the game from source. Downloads Godot (.NET build) on first use.
  ./build.py dev       Set up everything for development: packages, project-local Xvfb, Godot, export templates, the Python
                       virtualenv (.venv) for the asset generators, then builds and imports the project.
  ./build.py setup     Download Godot and the .NET SDK if missing, build and import the project. No sudo.
  ./build.py test      Run the smoke tests. --all or --tag TAG for other suites, see ./build.py test --help.
  ./build.py screenshot_review
                       Run the full regression with key frames and write build/review/report.md: one sheet per key
                       frame that is missing, changed or new against the committed baseline in tests/KeyFrames. Fails
                       when any differs.
  ./build.py generate_baseline
                       Run the full regression with key frames and write the ones that differ into the baseline, all of
                       them the first time. --use-latest-set takes the key frames of the last run instead. See --help.
  ./build.py performance_test
                       Run every frame_budget suite and check its frame times; full regressions don't. --render-budget
                       checks whole frames on this machine's display and GPU instead (only on a machine with a GPU).
                       Takes ./build.py test options.
  ./build.py shots     Render screenshots of a scenario in a virtual display (needs xvfb-run):
                       ./build.py shots [scenario] [out dir], default appendectomy into build/shots.
                       RENDERER=forward_plus for the default renderer, SHOTS_ARGS=--materials for the material board.
                       Fails on any error in Godot's output, such as a shader that doesn't compile.
  ./build.py build     Run the tests, export a standalone executable to build/ThisScalpelIsMine.x86_64 and start it
                       once headless; errors in either fail the build.
                       SKIP_TESTS=1 exports without testing.
  ./build.py editor    Open the project in the Godot editor.
  ./build.py assets    Regenerate models and sounds (needs ./build.py dev first).
  ./build.py lint      Lint and type-check the C# code (analyzers, dotnet format) and the Python tools.

Godot lives in ./.tools, export templates where Godot expects them (~/.local/share/godot). Set GODOT_BIN to use your
own Godot .NET binary of the same version instead.
"""

from __future__ import annotations

import argparse
import functools
import itertools
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import xml.etree.ElementTree as ElementTree
import zipfile
from collections.abc import Iterator, Sequence
from concurrent.futures import FIRST_COMPLETED, Future, ThreadPoolExecutor, wait
from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import Literal

GODOT_VERSION = "4.7.2"
# The SDK installed when the system has none, and the runtime the project targets (TargetFramework in the .csproj).
DOTNET_CHANNEL = "10.0"
PYTHON_VERSION = "3.13"
ROOT = Path(__file__).resolve().parent
TOOLS = ROOT / ".tools"
XVFB_LOCAL = TOOLS / "xvfb"
VENV = ROOT / ".venv"
BUILD = ROOT / "build"
GODOT_NAME = f"Godot_v{GODOT_VERSION}-stable_mono_linux_x86_64"
GODOT = Path(os.environ.get("GODOT_BIN") or TOOLS / GODOT_NAME / f"Godot_v{GODOT_VERSION}-stable_mono_linux.x86_64")
DOTNET_LOCAL = TOOLS / "dotnet"
TEMPLATES = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / f"godot/export_templates/{GODOT_VERSION}.stable.mono"
BASE_URL = f"https://github.com/godotengine/godot/releases/download/{GODOT_VERSION}-stable"
OUTPUT = BUILD / "ThisScalpelIsMine.x86_64"
ASSEMBLY = ROOT / ".godot/mono/temp/bin/Debug/ThisScalpelIsMine.dll"
# The runner class the GdUnit4 adapter writes before its first run and builds into the assembly.
RUNNER_CLASS = ROOT / "gdunit4_testadapter_v5/GdUnit4TestRunnerScene.cs"
# Godot as test processes run it: without the adapter's editor pass (see the script), as setup() built and imported.
TEST_GODOT = ROOT / "tools/godot_for_tests.sh"
# A Godot step or test fails on any of these in its output, as well as on its own exit code or assertions.
ERRORS = re.compile(r"SCRIPT ERROR|Parse Error|ERROR:|Unhandled exception")
NOISE = re.compile(r"at exit|leaked", re.IGNORECASE)
# GdUnit4's line once the Godot process running the cases has quit; missing when it hung and was killed.
RUNNER_EXIT = re.compile(r"Godot Runtime Test Runner ends with exit code: (-?\d+)")
# KeyFrames.SaveViewport's line for each key frame it saved, in its case's output.
KEY_FRAME_SAVED = re.compile(r"^\s*Key frame saved: (.+?)\s*$", re.MULTILINE)
# A case that finished but ran over its time limit, as problems() reports it. It isn't retried: it would run as slowly
# the next time. A case GdUnit4 stops at its Timeout may have hung, and is.
OVERRUN = re.compile(r"took \d+ s, longer than \d+ s\.$")
# Stack trace lines kept under an error: enough to show where it came from.
STACK_LINES = 12
# Unexplained errors outside the game that now and then hit a test process, with no game code on their stack; the
# cases still pass. A process failing on nothing but one of them runs once more.
# Each hit is appended to INFRASTRUCTURE_LOG, for whoever pins the cause down.
INFRASTRUCTURE_ERRORS = {
    # In Godot's own C# glue as GdUnit4 starts.
    "The underlying collection was modified from outside of the BlockingCollection": "the startup race in Godot's C# glue",
    # In the test host as it shuts down after its cases, its connection to `dotnet test` already gone.
    "Unable to write data to the transport connection": "the test host's connection closing at its exit",
}
# Logs and JUnit reports of every test process or suite before it ran again, which would overwrite them.
RETRIES = BUILD / "retries"
INFRASTRUCTURE_LOG = RETRIES / "infrastructure-errors.log"


def run(command: Sequence[str | Path], **kwargs: object) -> None:
    """Runs a command, stopping the script with its exit code when it fails."""
    result = subprocess.run([str(part) for part in command], check=False, **kwargs)  # type: ignore[call-overload]
    if result.returncode != 0:
        print(f"Failed (exit {result.returncode}): {' '.join(str(part) for part in command)}", file=sys.stderr)
        sys.exit(result.returncode)


def godot_errors(output: str) -> list[str]:
    """The error lines in Godot's output, each once, with the first STACK_LINES lines of the stack trace that follows an
    exception: without them a report can't say where an error came from. Engine leak reports printed while quitting are
    noise."""
    lines = output.splitlines()
    found = []
    for index, line in enumerate(lines):
        if ERRORS.search(line) and not NOISE.search(line):
            stack = itertools.takewhile(lambda frame: frame.lstrip().startswith("at"), lines[index + 1 :])
            found.append("\n       ".join([line.strip(), *(frame.strip() for frame in itertools.islice(stack, STACK_LINES))]))
    return list(dict.fromkeys(found))


def run_godot(name: str, command: Sequence[str | Path]) -> None:
    """Runs a Godot step with its output in build/<name>.log, stopping the script when it fails.

    Godot exits with 0 after script errors, shaders that don't compile, unhandled exceptions and failed exports, so the
    step fails on any error line in its output too.
    """
    log = BUILD / f"{name}.log"
    with log.open("w") as out:
        status = subprocess.run([str(part) for part in command], stdout=out, stderr=subprocess.STDOUT, check=False).returncode
    errors = godot_errors(log.read_text(errors="replace"))
    if status != 0 or errors:
        print("\n".join(errors[:20]), file=sys.stderr)
        sys.exit(f"{name} failed (exit {status}, {len(errors)} errors), see {log.relative_to(ROOT)}")


def download(url: str, target: Path) -> None:
    """Downloads a release file. GitHub release downloads fail now and then, so it retries before giving up."""
    for attempt in range(5):
        try:
            with urllib.request.urlopen(url, timeout=60) as response, target.open("wb") as out:
                shutil.copyfileobj(response, out)
            return
        except OSError as error:
            print(f"Download failed ({error}), retrying...")
            time.sleep(3 * (attempt + 1))
    sys.exit(f"Could not download {url}")


def require(*pairs: str) -> None:
    """Installs missing system packages with dnf (asks for sudo), or says what to install elsewhere.

    Arguments are "command:fedora-package" pairs.
    """
    missing = [package for command, package in (pair.split(":", 1) for pair in pairs) if shutil.which(command) is None]
    if not missing:
        return
    if shutil.which("dnf"):
        print(f"Installing system packages: {' '.join(missing)}")
        run(["sudo", "dnf", "install", "-y", *missing])
    else:
        sys.exit(f"Please install these packages with your package manager, then run this again: {' '.join(missing)}")


def activate_xvfb() -> None:
    """Use the unpacked executables, libraries and keyboard definitions together."""
    if not (XVFB_LOCAL / "usr/bin/xvfb-run").exists():
        return
    wrappers = XVFB_LOCAL / "bin"
    wrappers.mkdir(exist_ok=True)
    # Xorg reads -xkbdir, unlike libxkbcommon's XKB_CONFIG_ROOT. A wrapper also handles project paths with spaces.
    # https://github.com/mirror/xserver/blob/master/xkb/xkbInit.c
    keyboard = XVFB_LOCAL / "usr/share/X11/xkb"
    arguments = [str(XVFB_LOCAL / "usr/bin/Xvfb"), *(["-xkbdir", str(keyboard)] if keyboard.is_dir() else [])]
    wrapper = wrappers / "Xvfb"
    wrapper.write_text(f'#!/bin/sh\nexec {shlex.join(arguments)} "$@"\n')
    wrapper.chmod(0o755)
    os.environ["PATH"] = f"{wrappers}{os.pathsep}{XVFB_LOCAL / 'usr/bin'}{os.pathsep}{os.environ['PATH']}"
    libraries = [str(path) for path in (XVFB_LOCAL / "usr/lib64", XVFB_LOCAL / "usr/lib") if path.is_dir()]
    os.environ["LD_LIBRARY_PATH"] = os.pathsep.join([*libraries, *filter(None, [os.environ.get("LD_LIBRARY_PATH")])])
    if keyboard.is_dir():
        os.environ["XKB_CONFIG_ROOT"] = str(keyboard)


activate_xvfb()


def verify_xvfb() -> None:
    """Open an authenticated X11 connection; merely having DISPLAY set doesn't prove the server works."""
    run(
        [
            "xvfb-run",
            "-a",
            sys.executable,
            "-c",
            "import ctypes; x = ctypes.CDLL('libX11.so.6'); x.XOpenDisplay.restype = ctypes.c_void_p; "
            "x.XCloseDisplay.argtypes = [ctypes.c_void_p]; display = x.XOpenDisplay(None); "
            "assert display, 'Xvfb did not accept an X11 connection'; "
            "x.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]; "
            "x.XKeysymToKeycode.restype = ctypes.c_ubyte; "
            "assert x.XKeysymToKeycode(display, 0x61), 'Xvfb has no usable keyboard map'; x.XCloseDisplay(display)",
        ]
    )


def setup_xvfb() -> None:
    """Provision the visual-test dependency locally on Fedora, without needing sudo."""
    local_selected = shutil.which("xvfb-run") == str(XVFB_LOCAL / "usr/bin/xvfb-run")
    if shutil.which("xvfb-run") and shutil.which("Xvfb") and shutil.which("xauth") and (not local_selected or (XVFB_LOCAL / "usr/share/X11/xkb").is_dir()):
        verify_xvfb()
        return
    if not shutil.which("dnf"):
        sys.exit("Visual tests require Xvfb and xauth. Install xvfb and xauth with your package manager, then run ./build.py dev again.")
    # Fedora's X server uses its compiled-in /usr/bin/xkbcomp, so that prerequisite must be installed there.
    missing = [command for command in ("rpm2cpio", "cpio") if not shutil.which(command)]
    if not Path("/usr/bin/xkbcomp").is_file():
        missing.append("/usr/bin/xkbcomp")
    if missing:
        sys.exit(f"Local Xvfb setup needs these prerequisite tools installed first: {', '.join(missing)}. No sudo installation was attempted.")
    XVFB_LOCAL.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="xvfb-packages-") as temporary:
        packages = Path(temporary)
        run(["dnf", "download", "--resolve", "--destdir", packages, "xorg-x11-server-Xvfb", "xorg-x11-xauth", "xkeyboard-config"])
        for package in sorted(packages.glob("*.rpm")):
            payload = subprocess.run(["rpm2cpio", str(package)], capture_output=True, check=True).stdout
            run(["cpio", "-idmu", "--quiet"], input=payload, cwd=XVFB_LOCAL)
    activate_xvfb()
    verify_xvfb()


def supported_dotnet(command: str | Path) -> bool:
    """Both an SDK new enough for the analyzers and the runtime the project's test host targets."""
    listed = [subprocess.run([str(command), option], capture_output=True, text=True, check=False).stdout for option in ("--list-sdks", "--list-runtimes")]
    major = int(DOTNET_CHANNEL.split(".")[0])
    sdk = any(int(line.split(".")[0]) >= major for line in listed[0].splitlines() if line[:1].isdigit())
    return sdk and f"Microsoft.NETCore.App {major}." in listed[1]


@functools.cache
def dotnet() -> str:
    """The system command when supported, otherwise one installed into .tools without sudo."""
    if shutil.which("dotnet") and supported_dotnet("dotnet"):
        return "dotnet"
    local = DOTNET_LOCAL / "dotnet"
    if not local.exists() or not supported_dotnet(local):
        print(f"Installing the .NET {DOTNET_CHANNEL} SDK into .tools/dotnet...")
        TOOLS.mkdir(exist_ok=True)
        script = TOOLS / "dotnet-install.sh"
        download("https://dot.net/v1/dotnet-install.sh", script)
        run(["bash", script, "--channel", DOTNET_CHANNEL, "--install-dir", DOTNET_LOCAL])
    os.environ["DOTNET_ROOT"] = str(DOTNET_LOCAL)
    os.environ["PATH"] = f"{DOTNET_LOCAL}{os.pathsep}{os.environ['PATH']}"
    return str(local)


def fetch_godot() -> None:
    if GODOT.exists():
        return
    print(f"Downloading Godot {GODOT_VERSION} (.NET)...")
    TOOLS.mkdir(exist_ok=True)
    archive = TOOLS / "godot.zip"
    download(f"{BASE_URL}/{GODOT_NAME}.zip", archive)
    with zipfile.ZipFile(archive) as zipped:
        zipped.extractall(TOOLS)
    archive.unlink()
    GODOT.chmod(0o755)


def fetch_templates() -> None:
    if (TEMPLATES / "linux_release.x86_64").exists():
        return
    print("Downloading export templates (large, one time only)...")
    TEMPLATES.mkdir(parents=True, exist_ok=True)
    archive = TOOLS / "templates.tpz"
    download(f"{BASE_URL}/Godot_v{GODOT_VERSION}-stable_mono_export_templates.tpz", archive)
    with zipfile.ZipFile(archive) as zipped:
        for name in ("linux_release.x86_64", "linux_debug.x86_64", "version.txt"):
            (TEMPLATES / name).write_bytes(zipped.read(f"templates/{name}"))
    archive.unlink()


def build_project() -> None:
    """Compiles the C# (the analyzers run as part of it), then lets Godot import the assets."""
    run([dotnet(), "build", "--nologo", "-v", "quiet", ROOT / "ThisScalpelIsMine.csproj"])
    command: list[str | Path] = [GODOT, "--headless", "--path", ROOT, "--import"]
    if not (ROOT / ".godot" / "imported").is_dir():
        # A fresh checkout: Godot loads the project's own startup resources (the UI theme's font) before this first
        # import has made them, and says so. The import that counts comes after, when it finds them.
        subprocess.run([str(part) for part in command], stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, check=False)
    run_godot("import", command)


def setup() -> None:
    fetch_godot()
    build_project()


def setup_python() -> None:
    python = f"python{PYTHON_VERSION}"
    require(f"{python}:{python}")
    # A virtualenv of another Python version, left by an older checkout, is replaced.
    if not (VENV / f"bin/{python}").exists():
        print("Creating the Python virtualenv in .venv...")
        run([python, "-m", "venv", "--clear", VENV])
    run([VENV / "bin/pip", "install", "--quiet", "--upgrade", "pip"])
    run([VENV / "bin/pip", "install", "--quiet", "-r", ROOT / "requirements-dev.txt"])


def need_venv() -> None:
    if not (VENV / "bin/python").exists():
        sys.exit("Run ./build.py dev first.")


# --- Tests ------------------------------------------------------------------------------------------------------------

# A test that runs longer than this (seconds) fails, named in its log. A suite tagged slow, where one test takes longer
# than half of it, gets SLOW_TEST_TIMEOUT. A test that hangs is stopped when its suite's session runs out.
TEST_TIMEOUT = 60
SLOW_TEST_TIMEOUT = 360
# The view key frames are rendered at, always under a virtual display.
KEY_FRAME_ARGS = ["--rendering-method", "gl_compatibility", "--audio-driver", "Dummy", "--resolution", "1280x720"]


@dataclass
class Case:
    method: str
    categories: list[str]
    description: str
    # Each value of a parametrized case, as its quoted name part ('"hand_stitch"'), empty for a written one.
    values: list[str]

    @property
    def broken(self) -> bool:
        return "broken" in self.categories

    def names(self) -> list[str]:
        return [f"{self.method}({value})" for value in self.values] or [self.method]

    def descriptions(self) -> list[str]:
        return self.description.replace('\\"', '"').split(" / ") if self.description else []


@dataclass
class Suite:
    path: Path
    full_name: str
    categories: list[str]
    godot_args: list[str]
    isolate_cases: bool
    cases: list[Case] = field(default_factory=list)

    @property
    def name(self) -> str:
        return self.full_name.removeprefix("Scalpel.Tests.").replace(".", "_")

    @property
    def visual(self) -> bool:
        return "visual_confirmation" in self.categories

    @property
    def checks_frames(self) -> bool:
        """Whether its cases check frame times (FrameBudget.Check): such a process needs the machine to itself."""
        return "frame_budget" in self.categories

    @property
    def timeout(self) -> int:
        return SLOW_TEST_TIMEOUT if "slow" in self.categories else TEST_TIMEOUT


CLASS = re.compile(r"^public (?:sealed )?(?:partial )?class (\w+)", re.MULTILINE)
METHOD = re.compile(r"^\s*public (?:async )?(?:Task|void) (\w+)\(", re.MULTILINE)


def attribute_arguments(text: str, attribute: str) -> list[list[str]]:
    """The arguments of every [attribute(...)] in text, each split at its top-level commas (strings kept whole)."""
    found = []
    for match in re.finditer(rf"\b{attribute}\(", text):
        arguments, current, depth, quoted = [], "", 1, False
        for char in text[match.end() :]:
            if char == '"' and not current.endswith("\\"):
                quoted = not quoted
            elif not quoted and char == "(":
                depth += 1
            elif not quoted and char == ")":
                depth -= 1
                if depth == 0:
                    break
            if not quoted and depth == 1 and char == ",":
                arguments.append(current.strip())
                current = ""
            else:
                current += char
        found.append([*arguments, current.strip()] if current.strip() else arguments)
    return found


def strings(text: str, attribute: str) -> list[str]:
    """The string arguments of every [attribute(...)] in text."""
    return [argument.strip('"') for arguments in attribute_arguments(text, attribute) for argument in arguments]


def parse_suite(path: Path) -> Suite | None:
    """A test suite as its C# source declares it: the class's categories and options, its cases with their own.

    The source is read rather than the compiled tests, so selecting what to run needs neither a build nor Godot.
    """
    source = path.read_text()
    namespace = re.search(r"^namespace ([\w.]+);", source, re.MULTILINE)
    declared = CLASS.search(source)
    if not namespace or not declared or "[TestSuite" not in source[: declared.start()]:
        return None
    header = source[source.rfind("\n\n", 0, declared.start()) : declared.start()]
    suite = Suite(
        path=path,
        full_name=f"{namespace.group(1)}.{declared.group(1)}",
        categories=strings(header, "TestCategory"),
        godot_args=strings(header, "GodotArgs"),
        isolate_cases="IsolateCases" in header,
    )
    for match in METHOD.finditer(source):
        before = source[: match.start()]
        block = before[max(before.rfind("}\n"), before.rfind(";\n"), before.rfind("{\n")) :]
        rows = attribute_arguments(block, "TestCase")
        if "[TestCase" not in block:
            continue
        values = [positional[0] for positional in ([a for a in row if "=" not in a.split('"')[0]] for row in rows) if positional]
        descriptions = re.findall(r'Description = "((?:[^"\\]|\\.)*)"', block)
        suite.cases.append(Case(match.group(1), strings(block, "TestCategory"), " / ".join(descriptions), values))
    return suite


def discover() -> list[Suite]:
    suites = [parse_suite(path) for path in sorted((ROOT / "tests").rglob("*Test.cs")) if "Support" not in path.parts]
    return [suite for suite in suites if suite is not None]


@dataclass
class TestOptions:
    tags: list[str]
    skips: list[str]
    case: str
    # Test processes that may run at once; one needing the machine to itself waits for the others and runs alone.
    capacity: int
    key_frames: bool
    ci_run: bool
    # How frame_budget suites check frame times (FRAME_BUDGET).
    budget: Literal["off", "cpu", "render"]
    run_broken: bool


def selected(suites: list[Suite], options: TestOptions) -> list[Suite]:
    picked = []
    for suite in suites:
        if not all(tag in suite.categories for tag in options.tags) or any(tag in suite.categories for tag in options.skips):
            continue
        if options.case and not any(options.case in name for case in suite.cases for name in case.names()):
            continue
        picked.append(suite)
    return picked


def takes_key_frames(suite: Suite, options: TestOptions) -> bool:
    return options.key_frames and suite.visual


def frame_budget(suite: Suite, options: TestOptions) -> str:
    """How the suite checks frame times (FRAME_BUDGET, see tests/Support/FrameBudget.cs): only in suites that check
    them, which run with no other process beside them (others would slow their frames down)."""
    return options.budget if suite.checks_frames else "off"


def runs_alone(suite: Suite, options: TestOptions) -> bool:
    """Whether the suite's processes need the machine to themselves: they time frames, take key frames (told apart
    by when they're written), or draw on the one display of a render budget run."""
    return frame_budget(suite, options) != "off" or takes_key_frames(suite, options) or options.budget == "render"


def godot_view(suite: Suite, options: TestOptions) -> list[str]:
    """Where the suite draws: the key frame view, this machine's display without VSync (frames would wait for the
    screen) and without sound (a missing audio device would be an error), or nowhere."""
    if takes_key_frames(suite, options):
        return KEY_FRAME_ARGS
    return ["--disable-vsync", "--audio-driver", "Dummy"] if options.budget == "render" else ["--headless"]


# What one test process (dotnet test, its Godot and the game) may take of the machine's memory: a smoke case measured
# about 0.9 GiB, so this leaves room for the heavier scenarios and the system.
PROCESS_MEMORY = 2 << 30


def capacity() -> int:
    """How many test processes this machine has room for: one per core this process may run on, as far as its memory
    goes."""
    memory = os.sysconf("SC_PHYS_PAGES") * os.sysconf("SC_PAGE_SIZE")
    return max(1, min(len(os.sched_getaffinity(0)), memory // PROCESS_MEMORY))


def escape(text: str) -> str:
    """Text inside a test filter: its operators taken literally."""
    return re.sub(r"([\\()&|=!~])", r"\\\1", text)


def runsettings(suite: Suite, options: TestOptions, name: str, cases: int) -> Path:
    """The test run settings for one test process: its Godot options and how long the whole run may take."""
    # Godot writes its own output to a file too: the adapter keeps only what a case prints, so a crash's backtrace
    # would show nowhere else.
    args = [*suite.godot_args, *godot_view(suite, options), "--log-file", str(godot_log(name))]
    session = max(cases, 1) * suite.timeout * 1000 + 120_000
    path = BUILD / "test-settings" / f"{name}.runsettings"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        f"""<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <TestSessionTimeout>{session}</TestSessionTimeout>
  </RunConfiguration>
  <GdUnit4>
    <Parameters>{" ".join(args)}</Parameters>
    <CaptureStdOut>true</CaptureStdOut>
    <CompileProcessTimeout>120000</CompileProcessTimeout>
  </GdUnit4>
</RunSettings>
"""
    )
    return path


@dataclass
class Outcome:
    suite: Suite
    log: Path
    failures: list[str]
    executed: int = 0
    # The key frames (PNGs) it saved.
    key_frames: set[Path] = field(default_factory=set)
    failed_cases: dict[tuple[str, str], set[Path]] = field(default_factory=dict)
    overran: dict[tuple[str, str], list[str]] = field(default_factory=dict)
    attributed: bool = False


@dataclass
class ProcessResult:
    failures: list[str]
    executed: int = 0
    # The cases that failed (JUnit class and name), each with the key frames it saved.
    failed_cases: dict[tuple[str, str], set[Path]] = field(default_factory=dict)
    # Those of them that failed on nothing but running over their time limit (OVERRUN), with those failures.
    overran: dict[tuple[str, str], list[str]] = field(default_factory=dict)
    # Every failure belongs to one of them: running them again is running all that failed.
    attributed: bool = False
    # The key frames (PNGs) it saved: the files new or rewritten while it ran. A process that takes key frames runs
    # alone, so no other's can be among them.
    key_frames: set[Path] = field(default_factory=set)


def run_process(suite: Suite, options: TestOptions, name: str, test_filter: str, cases: int) -> ProcessResult:
    """Runs one dotnet test process and counts the cases actually executed, once more after an infrastructure error
    alone (INFRASTRUCTURE_ERRORS)."""
    result = run_process_once(suite, options, name, test_filter, cases)
    known = [next((what for marker, what in INFRASTRUCTURE_ERRORS.items() if marker in failure), None) for failure in result.failures]
    if not known or None in known:
        return result
    kept = keep_attempt(name)
    with INFRASTRUCTURE_LOG.open("a") as out:
        out.write(
            f"{time.strftime('%Y-%m-%d %H:%M:%S')} {name} (capacity {options.capacity}, key frames {options.key_frames}, "
            f"{result.executed} cases ran), kept in {kept}:\n  " + "\n  ".join(result.failures) + "\n"
        )
    print(f"[WARN] {name} hit {' and '.join(sorted(set(filter(None, known))))} (see {INFRASTRUCTURE_LOG}); running it again.")
    return run_process_once(suite, options, name, test_filter, cases)


def keep_attempt(name: str) -> Path:
    """Copies the logs and JUnit reports of the processes called name (a glob for a suite's isolated cases) into a
    folder of their own under RETRIES, before they run again."""
    stem = RETRIES / f"{time.strftime('%Y%m%d-%H%M%S')}-{name.replace('*', 'all')}"
    kept = next(folder for attempt in itertools.count(1) if not (folder := stem.with_name(f"{stem.name}-{attempt}")).exists())
    kept.mkdir(parents=True)
    for source in [*(BUILD / "test-logs").glob(f"{name}.log"), *(BUILD / "test-results").glob(f"{name}.xml")]:
        shutil.copyfile(source, kept / source.name)
    return kept


def godot_log(name: str) -> Path:
    """Where the Godot of the test process called name writes its output."""
    return BUILD / "test-logs" / f"{name}.godot.log"


def run_process_once(suite: Suite, options: TestOptions, name: str, test_filter: str, cases: int) -> ProcessResult:
    """Runs one dotnet test process and counts the cases actually executed."""
    log = BUILD / "test-logs" / f"{name}.log"
    report = BUILD / "test-results" / f"{name}.xml"
    report.unlink(missing_ok=True)
    godot_log(name).unlink(missing_ok=True)
    command: list[str] = [
        dotnet(), "test", str(ASSEMBLY), "--settings", str(runsettings(suite, options, name, cases)), "--filter", test_filter,
        "--logger", f"junit;LogFilePath={report}", "--logger", "console;verbosity=normal",
    ]  # fmt: skip
    # TEST_RUN tells the test assembly it runs tests, not the game (tests/Support/TestRun.cs).
    environment = {**os.environ, "GODOT_BIN": str(TEST_GODOT), "FRAME_BUDGET": frame_budget(suite, options), "TEST_RUN": "1"}
    environment["REAL_GODOT"] = str(GODOT)
    environment.pop("WITH_KEY_FRAMES", None)
    # .NET sizes its young generation by the CPU's L3 cache. Frame times are judged as on the reference machine (an
    # i9-14900HX, 36 MB of L3): a server CPU's far bigger cache would let garbage pile up into one long pause.
    environment.setdefault("DOTNET_GCgen0size", hex(36 << 20))
    # What the exported game's runtime config sets (TieredCompilationQuickJitForLoops in the .csproj): the editor build
    # tests run on reads Godot's own runtime config instead.
    environment.setdefault("DOTNET_TC_QuickJitForLoops", "0")
    if takes_key_frames(suite, options):
        environment["WITH_KEY_FRAMES"] = "1"
        # Always a virtual display and the software renderer, on a machine with a screen and a GPU too.
        # Key frames are compared against the committed baseline, and no two GPUs draw the same pixels.
        if not shutil.which("xvfb-run"):
            return ProcessResult(["Key frames need xvfb-run."])
        environment["LIBGL_ALWAYS_SOFTWARE"] = "1"
        command = ["xvfb-run", "-a", "-s", "-screen 0 1280x720x24", *command]
    # GdUnit4 talks to its Godot process over a named pipe with a fixed name, which .NET puts in the temp folder:
    # runs at the same time (--jobs, other checkouts) each need their own.
    with log.open("w") as out, tempfile.TemporaryDirectory(prefix="gdunit-") as temp:
        environment["TMPDIR"] = temp
        # Godot's user:// (saves, settings, played games) under it too: tests never touch the player's own.
        environment["XDG_DATA_HOME"] = temp
        before = key_frame_files()
        status = subprocess.run(command, stdout=out, stderr=subprocess.STDOUT, env=environment, check=False).returncode
    result = problems(log, report, status, suite.timeout)
    result.key_frames = {path for path, written in key_frame_files().items() if before.get(path) != written}
    return result


def problems(log: Path, report: Path, status: int, timeout: int) -> ProcessResult:
    """What went wrong in a process with selected cases: failures, errors, or no executed cases/report."""
    found = []
    if not report.exists():
        return ProcessResult([f"[ERROR] No test report was written (exit {status}); see {log}"])
    tree = ElementTree.parse(report)
    cases = tree.findall(".//testcase")
    executed = sum(case.find("skipped") is None for case in cases)
    if not executed:
        found.append("[ERROR] Runnable test cases were selected, but no test cases ran.")
    failed_cases: dict[tuple[str, str], set[Path]] = {}
    overran: dict[tuple[str, str], list[str]] = {}
    for case in cases:
        name = f"{case.get('classname', '')} {case.get('name', '')}"
        before = len(found)
        failure = case.find("failure")
        if failure is not None:
            found.append(f"[Failed] {name}: {failure.get('message', '')}")
        error = case.find("error")
        if error is not None:
            found.append(f"[ERROR] {name}: {error.get('message', '')}")
        if float(case.get("time", "0")) > timeout:
            found.append(f"[ERROR] {name} took {float(case.get('time', '0')):.0f} s, longer than {timeout} s.")
        if len(found) > before or len(cases) == 1:
            output = "".join(element.text or "" for element in case.iter("system-out"))
            failed_cases[(case.get("classname", ""), case.get("name", ""))] = {Path(path) for path in KEY_FRAME_SAVED.findall(output)}
            if len(found) > before and all(OVERRUN.search(line) for line in found[before:]):
                overran[(case.get("classname", ""), case.get("name", ""))] = found[before:]
    in_cases = len(found)
    output = log.read_text(errors="replace")
    # The process log too: the engine reports some errors (startup, import, shutdown) outside any test's output.
    found += godot_errors("\n".join([*(element.text or "" for element in tree.iter("system-out")), output]))
    # Passing cases don't make a passing process: Godot must also quit on its own once they're done.
    exits = RUNNER_EXIT.findall(output)
    if executed and not found and exits[-1:] != ["0"]:
        found.append(f"[ERROR] Godot didn't quit cleanly after its cases (runner exit {exits[-1] if exits else 'never reported'}).")
    if status != 0 and not found:
        found.append(f"[ERROR] Test process exited with status {status}.")
    # A process with one case fails only for it, whatever it fails on.
    attributed = bool(found) and (len(cases) == 1 or len(found) == in_cases)
    # A process with one case also fails for it on errors outside it.
    if len(cases) == 1 and len(found) > in_cases:
        overran.clear()
    return ProcessResult(found, executed, failed_cases if found else {}, overran, attributed)


def key_frame_files() -> dict[Path, int]:
    """The run's key frames, each with when it was written."""
    return {path: path.stat().st_mtime_ns for path in KEY_FRAMES.rglob("*.png")}


@dataclass
class Process:
    """One test process of a suite: the whole suite, one of its isolated cases, or a case run again."""

    suite: Suite
    name: str
    test_filter: str
    cases: int
    # Needs the machine to itself (see runs_alone).
    exclusive: bool

    def run(self, options: TestOptions) -> ProcessResult:
        return run_process(self.suite, options, self.name, self.test_filter, self.cases)


def row_filter(case: Case, value: str) -> str:
    """The test filter for one row of a case. GdUnit4 names a method's only row without its values, so that row can't
    be told apart by them."""
    by_value = value and len(case.values) > 1
    return f"FullyQualifiedName~.{case.method}" + (f"&Name~{escape(value)}" if by_value else "")


def case_filter(cases: list[Case], text: str) -> str:
    """The test filter for the rows whose names (method(values)) contain the text."""
    rows = {row_filter(case, value) for case in cases for value in case.values or [""] if text in (f"{case.method}({value})" if value else case.method)}
    return "|".join(f"({row})" for row in sorted(rows))


def processes(suite: Suite, options: TestOptions) -> list[Process]:
    """The processes that run a suite's selected cases: none when no runnable case is selected."""
    base = f"FullyQualifiedName~{suite.full_name}."
    if not options.run_broken:
        base += "&TestCategory!=broken"
    cases = [case for case in suite.cases if options.run_broken or not case.broken]
    selected_names = [name for case in cases for name in case.names() if options.case in name]
    if not selected_names:
        return []
    if not suite.isolate_cases:
        test_filter = base + (f"&({case_filter(cases, options.case)})" if options.case else "")
        return [Process(suite, suite.name, test_filter, len(selected_names), runs_alone(suite, options))]
    found: list[Process] = []
    for case in cases:
        for value in case.values or [""]:
            name = f"{case.method}({value})" if value else case.method
            if options.case not in name:
                continue
            test_filter = f"{base}&{row_filter(case, value)}"
            # Rows that share their first value (a scenario with and without a flag) share the filter, so one process
            # runs them both; a second with the same name would write the same log and report at the same time.
            if any(process.test_filter == test_filter for process in found):
                continue
            process_name = f"{suite.name}__{case.method}_{re.sub(r'[^a-z0-9]+', '_', value.lower()).strip('_')}".rstrip("_")
            found.append(Process(suite, process_name, test_filter, 1, runs_alone(suite, options)))
    return found


def suite_log(suite: Suite) -> Path:
    """The suite's log, a glob when its isolated cases each write their own."""
    return BUILD / "test-logs" / (f"{suite.name}__*.log" if suite.isolate_cases else f"{suite.name}.log")


def skipped_outcome(suite: Suite) -> Outcome:
    """The outcome of a suite with no runnable case selected: a report that says so, nothing launched."""
    reason = "No runnable cases match the selection (broken cases are excluded unless RUN_BROKEN=1)."
    log = BUILD / "test-logs" / f"{suite.name}.log"
    log.write_text(reason + "\n")
    xml = ElementTree.Element("testsuite", name=suite.full_name, tests="1", failures="0", errors="0", skipped="1")
    skipped_case = ElementTree.SubElement(xml, "testcase", classname=suite.full_name, name="No runnable cases")
    ElementTree.SubElement(skipped_case, "skipped", message=reason)
    ElementTree.ElementTree(xml).write(BUILD / "test-results" / f"{suite.name}.xml", encoding="utf-8", xml_declaration=True)
    return Outcome(suite, log, [])


def gather(suite: Suite, results: Sequence[ProcessResult]) -> Outcome:
    """A suite's outcome from the results of all its processes."""
    outcome = Outcome(suite, suite_log(suite), [], attributed=True)
    for result in results:
        add(outcome, result)
    return outcome


def run_all(queue: list[Process], options: TestOptions) -> Iterator[tuple[Process, ProcessResult]]:
    """Runs processes, options.capacity at a time, and yields each with its result as it ends. Exclusive processes go
    first, one at a time with nothing beside them; then the others fill the capacity, slow suites' first, since the
    longest process started last is what makes a run's tail. An isolated suite's cases are processes like any other:
    run one after another they would hold the whole run up long after the others have finished.
    The first test process ever writes the adapter's runner class and builds it into the assembly: it runs alone, as
    processes starting beside that build would load a half-written assembly."""
    queue = sorted(queue, key=lambda process: (not process.exclusive, -process.suite.timeout))
    if queue and not RUNNER_CLASS.exists():
        queue[0] = replace(queue[0], exclusive=True)
    running: dict[Future[ProcessResult], Process] = {}
    with ThreadPoolExecutor(options.capacity) as pool:
        while queue or running:
            while queue and fits(queue[0], list(running.values()), options.capacity):
                process = queue.pop(0)
                running[pool.submit(process.run, options)] = process
            done, _ = wait(running, return_when=FIRST_COMPLETED)
            for future in done:
                yield running.pop(future), future.result()


def fits(process: Process, running: Sequence[Process], capacity: int) -> bool:
    """Whether the process may start beside the running ones."""
    if process.exclusive or any(other.exclusive for other in running):
        return not running
    return len(running) < capacity


def run_suite(suite: Suite, options: TestOptions) -> Outcome:
    """Runs one suite's processes."""
    todo = processes(suite, options)
    return gather(suite, [result for _, result in run_all(todo, options)]) if todo else skipped_outcome(suite)


def run_suites(suites: Sequence[Suite], options: TestOptions) -> Iterator[Outcome]:
    """Runs the suites' processes through run_all and yields each suite's outcome once its last process has ended, so
    suites come out in the order they finish."""
    todo = {suite.full_name: processes(suite, options) for suite in suites}
    results: dict[str, list[ProcessResult]] = {suite.full_name: [] for suite in suites}
    for suite in suites:
        if not todo[suite.full_name]:
            yield skipped_outcome(suite)
    alone = sum(process.exclusive for group in todo.values() for process in group)
    count = sum(len(group) for group in todo.values())
    print(f"Running {len(suites)} test suites in {count} test processes: {alone} alone, then {options.capacity} at a time.", flush=True)
    for process, result in run_all([process for group in todo.values() for process in group], options):
        results[process.suite.full_name].append(result)
        if len(results[process.suite.full_name]) == len(todo[process.suite.full_name]):
            yield gather(process.suite, results[process.suite.full_name])


def add(outcome: Outcome, result: ProcessResult) -> None:
    """Counts one of its processes into a suite's outcome."""
    outcome.failures += result.failures
    outcome.executed += result.executed
    outcome.failed_cases |= result.failed_cases
    outcome.overran |= result.overran
    outcome.attributed &= result.attributed or not result.failures
    outcome.key_frames |= result.key_frames


def run_cases(suite: Suite, options: TestOptions, cases: Sequence[tuple[str, str]]) -> Outcome:
    """Runs the given cases (JUnit class and name) of a suite again, each in a process of its own."""
    outcome = Outcome(suite, BUILD / "test-logs" / f"{suite.name}__retry_*.log", [], attributed=True)
    todo = [
        # A case's name is its method's, with ":index (values)" after it for each row of one.
        # The process is named without the values: a long one would make its report's file name too long.
        Process(
            suite,
            f"{suite.name}__retry_{re.sub(r'[^a-z0-9]+', '_', name.partition(' ')[0].lower()).strip('_')}",
            f"FullyQualifiedName~{suite.full_name}.{name.partition(':')[0]}&Name={escape(name)}",
            1,
            runs_alone(suite, options),
        )
        for _, name in cases
    ]
    for _, result in run_all(todo, options):
        add(outcome, result)
    return outcome


def report(outcome: Outcome) -> str:
    if not outcome.failures:
        if not outcome.executed:
            return f"skip {outcome.suite.full_name} (no cases executed)"
        return f"ok   {outcome.suite.full_name}"
    lines = "\n".join(f"     {line}" for line in outcome.failures[:20])
    return f"FAIL {outcome.suite.full_name}\n{lines}\n     log: {outcome.log.relative_to(ROOT)}"


# Lines of a failed suite's log a CI run prints: the job's own output is often all there is to read of a crash or hang.
CI_LOG_TAIL = 80


def log_tail(outcome: Outcome) -> str:
    """The end of each log of a failed suite, folded in a GitHub Actions group."""
    parts = []
    stem = outcome.log.name.removesuffix(".log")
    for log in sorted({*outcome.log.parent.glob(f"{stem}.log"), *outcome.log.parent.glob(f"{stem}.godot.log")}):
        tail = log.read_text(errors="replace").splitlines()[-CI_LOG_TAIL:]
        parts.append(f"::group::{log.relative_to(ROOT)}, last {len(tail)} lines\n" + "\n".join(tail) + "\n::endgroup::")
    return "\n".join(parts)


def test(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="./build.py test",
        description="Runs GdUnit4 test suites under tests/. A suite fails on a failed case, a case over its time limit, "
        "any SCRIPT ERROR, ERROR: or unhandled exception in Godot's output, or no selected cases executed. "
        "Suites with no runnable cases selected are skipped. "
        "Exits non-zero on any failure or if the entire run executes no cases.",
    )
    parser.add_argument("--all", action="store_true", help="every suite; without it or --tag, the smoke tag")
    parser.add_argument("--tag", action="append", default=[], help="suites with all of these categories")
    parser.add_argument("--skip", action="append", default=[], help="leave out suites with this category")
    parser.add_argument("--case", default="", help="only the test cases whose name contains this")
    parser.add_argument(
        "--jobs", type=int, default=0, help="test processes run at once (an isolated case is one of its own); as many as the machine has room for by default"
    )
    parser.add_argument(
        "--with-key-frames",
        action="store_true",
        help="visual_confirmation suites save their key frames (a virtual display, the compatibility renderer); each of their processes runs alone",
    )
    parser.add_argument("--ci-run", action="store_true", help="never check frame times, print failed suites' logs")
    parser.add_argument(
        "--render-budget",
        action="store_true",
        help="run on this machine's display and GPU, rendering on, one process at a time, and check whole frames against "
        "the 16 ms budget instead of the game's own work against the CPU budget",
    )
    parser.add_argument("--list", action="store_true", help="show what would run")
    parsed = parser.parse_args(arguments)
    if parsed.render_budget and (parsed.with_key_frames or parsed.ci_run):
        parser.error("--render-budget times frames on a real GPU: not with --with-key-frames or --ci-run")
    if parsed.render_budget and not (os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY")):
        parser.error("--render-budget needs a display")
    # Frame times are checked in a focused run, where the change is, and left to performance_test in broad ones: a
    # full regression or a smoke run.
    focused = bool(parsed.tag or parsed.case) and not parsed.ci_run
    options = TestOptions(
        tags=parsed.tag or ([] if parsed.all else ["smoke"]),
        skips=parsed.skip,
        case=parsed.case,
        capacity=parsed.jobs or capacity(),
        key_frames=parsed.with_key_frames,
        ci_run=parsed.ci_run,
        budget="render" if parsed.render_budget else "cpu" if focused else "off",
        run_broken=os.environ.get("RUN_BROKEN") == "1",
    )
    suites = selected(discover(), options)
    if parsed.list:
        for suite in suites:
            print(f"{suite.full_name}  [{', '.join(suite.categories)}]")
        return 0
    if not suites:
        print(f"No tests match tags {options.tags} and case '{options.case}'.", file=sys.stderr)
        return 2
    if options.key_frames:
        # Key frames of an earlier run would pass for this one's.
        shutil.rmtree(KEY_FRAMES, ignore_errors=True)
    setup()
    for folder in ("test-logs", "test-results"):
        (BUILD / folder).mkdir(parents=True, exist_ok=True)
    outcomes: list[Outcome] = []
    for outcome in run_suites(suites, options):
        print(report(outcome), flush=True)
        if options.ci_run and outcome.failures:
            print(log_tail(outcome), flush=True)
        outcomes.append(outcome)
    # Only a full regression may become the baseline: not a focused run, nor one with broken cases.
    full = parsed.all and not (parsed.tag or parsed.skip or parsed.case or options.run_broken)
    if options.key_frames and full:
        # A case failing in a regression for the baseline runs once more on its own, its key frames taken again in
        # place of the failed attempt's: a flaky case shouldn't cost a whole new regression.
        # A failure no case owns (an engine error between cases) runs the whole suite again.
        # Cases that only ran over their time limit (OVERRUN) are not: they would run as slowly again.
        for index, outcome in enumerate(outcomes):
            again = [case for case in outcome.failed_cases if case not in outcome.overran] if outcome.attributed else []
            if not outcome.failures or outcome.attributed and not again:
                continue
            kept = keep_attempt(outcome.log.stem)
            what = f"cases {', '.join(name for _, name in again)}" if again else "suite"
            print(f"Running the failed {what} of {outcome.suite.full_name} again (first attempt kept in {kept}).", flush=True)
            for path in {path for case in again for path in outcome.failed_cases[case]} if again else outcome.key_frames:
                path.unlink(missing_ok=True)
            retry = run_cases(outcome.suite, options, again) if again else run_suite(outcome.suite, options)
            # Its folders are those of both attempts: the retry may save fewer key frames.
            retry.key_frames |= outcome.key_frames
            # Cases left out still failed: on their time limit.
            if again:
                retry.failures += [line for lines in outcome.overran.values() for line in lines]
            outcomes[index] = retry
            print(report(retry), flush=True)
    pending = [
        (f"{suite.full_name}.{name}", description)
        for suite in suites
        for case in suite.cases
        if case.broken and not options.run_broken
        for name, description in zip(case.names(), case.descriptions() or [""], strict=False)
    ]
    for name, description in pending:
        print(f"pending {name}: {description}")
    failed = sum(1 for outcome in outcomes if outcome.failures)
    skipped = sum(1 for outcome in outcomes if not outcome.failures and not outcome.executed)
    print(f"{len(outcomes) - failed - skipped} passed, {failed} failed, {skipped} skipped, {len(pending)} pending.")
    if not any(outcome.executed for outcome in outcomes):
        print("[ERROR] No test cases ran.", file=sys.stderr)
        return 1
    if options.key_frames and full and has_key_frames(KEY_FRAMES):
        # Each failed suite with the folders it saved key frames in: the only ones whose untaken key frames the
        # baseline keeps. One that saved none can't say where its key frames are, so its run can't become the baseline.
        unplaced = [outcome.suite.full_name for outcome in outcomes if outcome.failures and outcome.suite.visual and not outcome.key_frames]
        if unplaced:
            print(f"[ERROR] The key frames can't become the baseline: {', '.join(unplaced)} failed before saving any.", file=sys.stderr)
        elif failed:
            folders = {
                outcome.suite.full_name: sorted({path.parent.relative_to(KEY_FRAMES).as_posix() for path in outcome.key_frames})
                for outcome in outcomes
                if outcome.failures
            }
            (KEY_FRAMES / FAILED).write_text(json.dumps(folders, indent=2) + "\n")
        else:
            (KEY_FRAMES / PASSED).touch()
    elif options.key_frames and not failed and not options.run_broken and has_key_frames(KEY_FRAMES):
        (KEY_FRAMES / FOCUSED).touch()
    return 1 if failed else 0


# --- Key frame review -------------------------------------------------------------------------------------------------

KEY_FRAMES = BUILD / "test-artifacts" / "screenshots"
# Key frames of the last full regression that passed and was reviewed, committed: what a review compares against.
BASELINE = ROOT / "tests" / "KeyFrames"
# Left among a run's key frames by a full regression: only those may become the baseline.
# FAILED maps each suite that failed even when run again to the folders of its key frames (JSON); they need a look
# before they go in.
PASSED = "passed"
FAILED = "failed"
# Left by a focused run with key frames that passed: a feature branch may add or replace its key frames in the baseline.
FOCUSED = "focused"
REVIEW = BUILD / "review"


def has_key_frames(folder: Path) -> bool:
    """Whether a run (PNG) or the baseline (JPEG) has any."""
    return any(path.suffix in (".png", ".jpg") for path in folder.rglob("*"))


def key_frame_run() -> int:
    """Runs the full regression with key frames, which it marks (see PASSED and FAILED)."""
    status = test(["--all", "--with-key-frames"])
    # The suites that take no key frames can pass on their own.
    if status == 0 and not (KEY_FRAMES / PASSED).exists():
        print("[ERROR] The regression passed, but its key frames can't be reviewed: none taken, or RUN_BROKEN ran broken cases.", file=sys.stderr)
        return 1
    return status


def keyframes_tool(*arguments: str | Path) -> int:
    command: list[str | Path] = [pillow_python(), "-m", "tools.keyframes", *arguments]
    return subprocess.run(command, cwd=ROOT, check=False).returncode


def generate_baseline(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="./build.py generate_baseline",
        description="Runs the full regression with key frames and writes the ones that are missing, changed or new into "
        "the committed baseline (tests/KeyFrames), all of them when there is none yet. Key frames that didn't change "
        "keep their files. A regression with failed suites is written too, except that in their folders the baseline "
        "keeps the key frames and views they didn't take: review those suites' sheets first.",
    )
    parser.add_argument("--use-latest-set", action="store_true", help="take the key frames of the last run; only when it was a full regression")
    parser.add_argument("--focused", action="store_true", help="add or replace the key frames of the last focused run that passed")
    parsed = parser.parse_args(arguments)
    if parsed.focused:
        if not (KEY_FRAMES / FOCUSED).exists():
            print("[ERROR] The baseline is left as it was: the last run wasn't a focused run with key frames that passed.", file=sys.stderr)
            return 1
        return keyframes_tool("update", KEY_FRAMES.relative_to(ROOT), BASELINE.relative_to(ROOT), "--focused")
    if not parsed.use_latest_set:
        key_frame_run()
    # Key frames of a partial run never become what later reviews compare against.
    if (KEY_FRAMES / PASSED).exists():
        return keyframes_tool("update", KEY_FRAMES.relative_to(ROOT), BASELINE.relative_to(ROOT))
    if not (KEY_FRAMES / FAILED).exists():
        print("[ERROR] The baseline is left as it was: the key frames aren't those of a full regression.", file=sys.stderr)
        return 1
    failed: dict[str, list[str]] = json.loads((KEY_FRAMES / FAILED).read_text())
    print(f"[WARN] Writing the key frames of a regression in which these suites failed: {', '.join(failed)}.")
    keep = [argument for folders in failed.values() for folder in folders for argument in ("--keep", folder)]
    return keyframes_tool("update", KEY_FRAMES.relative_to(ROOT), BASELINE.relative_to(ROOT), *keep)


def pillow_python() -> Path:
    """A Python with Pillow: the dev virtualenv's, or else a small one in .tools with only the pinned Pillow, made once
    per pinned version."""
    if (VENV / "bin/python").exists():
        return VENV / "bin/python"
    pillow = next(line for line in (ROOT / "requirements.txt").read_text().splitlines() if line.startswith("pillow"))
    venv = TOOLS / f"review-venv-{pillow.split('==')[-1]}"
    if not (venv / "bin/python").exists():
        # Made aside and moved into place once Pillow is in: a failed install leaves nothing that looks ready.
        staging = venv.with_name(venv.name + ".tmp")
        shutil.rmtree(staging, ignore_errors=True)
        run([sys.executable, "-m", "venv", staging])
        run([staging / "bin/pip", "install", "--quiet", pillow])
        staging.rename(venv)
    return venv / "bin/python"


def screenshot_review(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="./build.py screenshot_review",
        description="Runs the full regression with key frames, then writes build/review/report.md with a sheet for each "
        "key frame that is missing, changed or new against the committed baseline (tests/KeyFrames). "
        "Exits non-zero when the regression fails, there is no baseline or any key frame differs from it, after writing "
        "the report.",
    )
    parser.add_argument("--use-latest-set", action="store_true", help="review the key frames of the last run")
    parser.add_argument("--focused", action="store_true", help="review the last focused run with key frames, only in the folders it took")
    parser.add_argument("--all", action="store_true", help="sheets for unchanged key frames too")
    parsed = parser.parse_args(arguments)
    status, regression = 0, "not-run"
    if parsed.focused:
        regression = "focused"
        if not has_key_frames(KEY_FRAMES):
            print("[ERROR] The last run took no key frames: run the suites with test --with-key-frames first.", file=sys.stderr)
            return 1
    elif not parsed.use_latest_set:
        status = key_frame_run()
        regression = "failed" if status else "passed"
    flags = [*(["--all"] if parsed.all else []), "--regression", regression]
    # The report comes first: it helps most when the regression failed.
    report_status = keyframes_tool("report", *(path.relative_to(ROOT) for path in (KEY_FRAMES, BASELINE, REVIEW)), *flags)
    if not has_key_frames(BASELINE):
        print(f"[ERROR] No baseline in {BASELINE.relative_to(ROOT)}, run ./build.py generate_baseline first.", file=sys.stderr)
        return 1
    return status or report_status


# --- Commands ---------------------------------------------------------------------------------------------------------


def lint() -> None:
    need_venv()
    run([dotnet(), "build", "--nologo", "-v", "quiet", ROOT / "ThisScalpelIsMine.csproj"])
    # Exported games build without the tests: their code must not lean on anything only the tests bring.
    run([dotnet(), "build", "--nologo", "-v", "quiet", "-c", "ExportRelease", ROOT / "ThisScalpelIsMine.csproj"])
    run([dotnet(), "format", ROOT / "ThisScalpelIsMine.csproj", "--verify-no-changes"])
    for tool in (["ruff", "check"], ["ruff", "format", "--check"], ["mypy"]):
        run([VENV / "bin" / tool[0], *tool[1:], "tools", "build.py"], cwd=ROOT)


def export() -> None:
    """Exports the game, then starts it once. A broken export (no solution file next to the project, a failed .NET
    publish) shows only as errors in Godot's output, and a game that can't load its code crashes on start."""
    fetch_templates()
    OUTPUT.unlink(missing_ok=True)
    run_godot("export", [GODOT, "--headless", "--path", ROOT, "--export-release", "Linux", OUTPUT])
    run_godot("export-launch", [OUTPUT, "--headless", "--quit-after", "120"])
    print(f"Done: {OUTPUT}")


def main() -> int:
    command, arguments = (sys.argv[1], sys.argv[2:]) if len(sys.argv) > 1 else ("", [])
    # Logs, screenshots and the export land in build/: Godot mustn't import anything there.
    BUILD.mkdir(exist_ok=True)
    (BUILD / ".gdignore").touch()
    if command in ("play", "run"):
        setup()
        os.execv(GODOT, [str(GODOT), "--path", str(ROOT)])
    elif command == "dev":
        # git for version control, Xvfb for the screenshot tool and visual tests.
        require("git:git")
        setup_xvfb()
        fetch_godot()
        fetch_templates()
        setup_python()
        build_project()
        print("\nReady. ./build.py play to play, ./build.py editor to edit, ./build.py test to run the tests.")
    elif command == "setup":
        setup()
        print("Ready. ./build.py test to run the tests, ./build.py shots to render screenshots.")
    elif command == "test":
        return test(arguments)
    elif command == "screenshot_review":
        return screenshot_review(arguments)
    elif command == "generate_baseline":
        return generate_baseline(arguments)
    elif command == "performance_test":
        return test(["--tag", "frame_budget", *arguments])
    elif command == "shots":
        setup()
        scenario = arguments[0] if arguments else "appendectomy"
        out = Path(arguments[1]) if len(arguments) > 1 else BUILD / "shots"
        out.mkdir(parents=True, exist_ok=True)
        # The compatibility renderer works on software OpenGL, so this runs without a GPU. RENDERER=forward_plus checks
        # the default renderer instead (needs Vulkan: lavapipe works, mesa-vulkan-drivers).
        renderer = os.environ.get("RENDERER", "gl_compatibility")
        extra = os.environ.get("SHOTS_ARGS", "").split()
        # Shaders compile only when something is drawn: this is where their errors show up.
        # No sound: a machine without an audio device would report it as an error.
        run_godot("shots", ["xvfb-run", "-a", GODOT, "--path", ROOT, "--rendering-method", renderer, "--audio-driver", "Dummy",
                            "res://tests/Support/Screenshot.tscn", "--", f"--scenario={scenario}", f"--out={out}", *extra])  # fmt: skip
        print(f"Screenshots in {out}")
    elif command == "build":
        setup()
        if os.environ.get("SKIP_TESTS") != "1" and test(["--all"]) != 0:
            return 1
        export()
    elif command == "editor":
        fetch_godot()
        dotnet()
        os.execv(GODOT, [str(GODOT), "--editor", "--path", str(ROOT)])
    elif command == "assets":
        need_venv()
        run([VENV / "bin/python", "-m", "tools.assetgen"], cwd=ROOT)
        run([VENV / "bin/python", "-m", "tools.blender"], cwd=ROOT)
    elif command == "lint":
        lint()
    else:
        print(__doc__)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
