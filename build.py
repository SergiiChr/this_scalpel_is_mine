#!/usr/bin/env python3
"""One script for players and developers (Fedora first, works on any x86_64 Linux with the listed packages).

  ./build.py play      Play the game from source. Downloads Godot (.NET build) on first use.
  ./build.py dev       Set up everything for development: system packages, Godot, export templates, the Python
                       virtualenv (.venv) for the asset generators, then builds and imports the project.
  ./build.py setup     Download Godot and the .NET SDK if missing, build and import the project. No sudo.
  ./build.py test      Run the smoke tests. --all or --tag TAG for other suites, see ./build.py test --help.
  ./build.py shots     Render screenshots of a scenario in a virtual display (needs xvfb-run):
                       ./build.py shots [scenario] [out dir], default appendectomy into build/shots.
                       RENDERER=forward_plus for the default renderer, SHOTS_ARGS=--materials for the material board.
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
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import xml.etree.ElementTree as ElementTree
import zipfile
from collections.abc import Sequence
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from pathlib import Path

GODOT_VERSION = "4.7.2"
# The SDK installed when the system has none, and the runtime the project targets (TargetFramework in the .csproj).
DOTNET_CHANNEL = "10.0"
PYTHON_VERSION = "3.11"
ROOT = Path(__file__).resolve().parent
TOOLS = ROOT / ".tools"
VENV = ROOT / ".venv"
BUILD = ROOT / "build"
GODOT_NAME = f"Godot_v{GODOT_VERSION}-stable_mono_linux_x86_64"
GODOT = Path(os.environ.get("GODOT_BIN") or TOOLS / GODOT_NAME / f"Godot_v{GODOT_VERSION}-stable_mono_linux.x86_64")
DOTNET_LOCAL = TOOLS / "dotnet"
TEMPLATES = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / f"godot/export_templates/{GODOT_VERSION}.stable.mono"
BASE_URL = f"https://github.com/godotengine/godot/releases/download/{GODOT_VERSION}-stable"
OUTPUT = BUILD / "ThisScalpelIsMine.x86_64"
ASSEMBLY = ROOT / ".godot/mono/temp/bin/Debug/ThisScalpelIsMine.dll"


def run(command: Sequence[str | Path], **kwargs: object) -> None:
    """Runs a command, stopping the script with its exit code when it fails."""
    result = subprocess.run([str(part) for part in command], check=False, **kwargs)  # type: ignore[call-overload]
    if result.returncode != 0:
        sys.exit(result.returncode)


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


@functools.cache
def dotnet() -> str:
    """The dotnet command: the system's if it has an SDK this new (the analyzers need it) and the runtime the project
    targets (the test host runs on it), else one installed into .tools without sudo."""
    if shutil.which("dotnet"):
        listed = [subprocess.run(["dotnet", option], capture_output=True, text=True, check=False).stdout for option in ("--list-sdks", "--list-runtimes")]
        major = int(DOTNET_CHANNEL.split(".")[0])
        sdk = any(int(line.split(".")[0]) >= major for line in listed[0].splitlines() if line[:1].isdigit())
        if sdk and f"Microsoft.NETCore.App {major}." in listed[1]:
            return "dotnet"
    local = DOTNET_LOCAL / "dotnet"
    if not local.exists():
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
    subprocess.run([GODOT, "--headless", "--path", ROOT, "--import"], capture_output=True, check=False)


def setup() -> None:
    fetch_godot()
    build_project()


def setup_python() -> None:
    python = f"python{PYTHON_VERSION}"
    require(f"{python}:{python}")
    if not (VENV / "bin/python").exists():
        print("Creating the Python virtualenv in .venv...")
        run([python, "-m", "venv", VENV])
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
# A test fails on any of these in its Godot output, as well as on its own assertions. Engine leak reports printed while
# quitting are noise.
ERRORS = re.compile(r"SCRIPT ERROR|Parse Error|ERROR:|Unhandled exception")
NOISE = re.compile(r"at exit|leaked", re.IGNORECASE)
# The view key frames are rendered at, under a virtual display when there's no real one.
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
    jobs: int
    key_frames: bool
    ci_run: bool
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


def escape(text: str) -> str:
    """Text inside a test filter: its operators taken literally."""
    return re.sub(r"([\\()&|=!~])", r"\\\1", text)


def runsettings(suite: Suite, options: TestOptions, cases: int) -> Path:
    """The test run settings for one suite's process: its Godot options and how long the whole run may take."""
    args = [*suite.godot_args, *(KEY_FRAME_ARGS if options.key_frames and suite.visual else ["--headless"])]
    session = max(cases, 1) * suite.timeout * 1000 + 120_000
    path = BUILD / "test-settings" / f"{suite.name}.runsettings"
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


def run_process(suite: Suite, options: TestOptions, name: str, test_filter: str, cases: int) -> list[str]:
    """Runs one dotnet test process. Returns what failed, empty when everything passed."""
    log = BUILD / "test-logs" / f"{name}.log"
    report = BUILD / "test-results" / f"{name}.xml"
    report.unlink(missing_ok=True)
    command: list[str] = [
        dotnet(), "test", str(ASSEMBLY), "--settings", str(runsettings(suite, options, cases)), "--filter", test_filter,
        "--logger", f"junit;LogFilePath={report}", "--logger", "console;verbosity=normal",
    ]  # fmt: skip
    environment = {**os.environ, "GODOT_BIN": str(GODOT), "CI_RUN": "1" if options.ci_run else ""}
    # .NET sizes its young generation by the CPU's L3 cache. Frame times are judged as on the reference machine (an
    # i9-14900HX, 36 MB of L3): a server CPU's far bigger cache would let garbage pile up into one long pause.
    environment.setdefault("DOTNET_GCgen0size", hex(36 << 20))
    if options.key_frames and suite.visual:
        environment["WITH_KEY_FRAMES"] = "1"
        if not os.environ.get("DISPLAY"):
            if not shutil.which("xvfb-run"):
                return ["Visual tests need xvfb-run or a DISPLAY."]
            command = ["xvfb-run", "-a", "-s", "-screen 0 1280x720x24", *command]
    # GdUnit4 talks to its Godot process over a named pipe with a fixed name, which .NET puts in the temp folder:
    # runs at the same time (--jobs, other checkouts) each need their own.
    with log.open("w") as out, tempfile.TemporaryDirectory(prefix="gdunit-") as temp:
        environment["TMPDIR"] = temp
        status = subprocess.run(command, stdout=out, stderr=subprocess.STDOUT, env=environment, check=False).returncode
    return problems(log, report, status, suite.timeout)


def problems(log: Path, report: Path, status: int, timeout: int) -> list[str]:
    """What went wrong in a test process: failed and slow cases, errors in its output, or no report at all."""
    found = []
    if not report.exists():
        return [f"[ERROR] No test report was written (exit {status}); see {log}"]
    tree = ElementTree.parse(report)
    cases = tree.findall(".//testcase")
    if not cases:
        found.append("[ERROR] No test cases ran.")
    for case in cases:
        name = f"{case.get('classname', '')} {case.get('name', '')}"
        failure = case.find("failure")
        if failure is not None:
            found.append(f"[Failed] {name}: {failure.get('message', '')}")
        if float(case.get("time", "0")) > timeout:
            found.append(f"[ERROR] {name} took {float(case.get('time', '0')):.0f} s, longer than {timeout} s.")
    # The process log too: the engine reports some errors (startup, import, shutdown) outside any test's output.
    output = "\n".join([*(element.text or "" for element in tree.iter("system-out")), log.read_text(errors="replace")])
    errors = [line.strip() for line in output.splitlines() if ERRORS.search(line) and not NOISE.search(line)]
    found += dict.fromkeys(errors)
    if status != 0 and not found:
        found.append(f"[ERROR] Test process exited with status {status}.")
    return found


def run_suite(suite: Suite, options: TestOptions) -> Outcome:
    """Runs a suite in its own Godot process, or each of its cases in one of its own when it isolates them."""
    base = f"FullyQualifiedName~{suite.full_name}."
    if not options.run_broken:
        base += "&TestCategory!=broken"
    cases = [case for case in suite.cases if options.run_broken or not case.broken]
    names = [name for case in cases for name in case.names() if options.case in name]
    log = BUILD / "test-logs" / f"{suite.name}.log"
    if not names:
        return Outcome(suite, log, [])
    if not suite.isolate_cases:
        test_filter = base + (f"&Name~{escape(options.case)}" if options.case else "")
        return Outcome(suite, log, run_process(suite, options, suite.name, test_filter, len(names)))
    failures = []
    for case in cases:
        for value in case.values or [""]:
            name = f"{case.method}({value})" if value else case.method
            if options.case not in name:
                continue
            test_filter = f"{base}&FullyQualifiedName~.{case.method}" + (f"&Name~{escape(value)}" if value else "")
            case_name = f"{suite.name}__{case.method}_{re.sub(r'[^a-z0-9]+', '_', value.lower()).strip('_')}".rstrip("_")
            failures += run_process(suite, options, case_name, test_filter, 1)
    return Outcome(suite, log, failures)


def report(outcome: Outcome) -> str:
    if not outcome.failures:
        return f"ok   {outcome.suite.full_name}"
    lines = "\n".join(f"     {line}" for line in outcome.failures[:20])
    return f"FAIL {outcome.suite.full_name}\n{lines}\n     log: {outcome.log.relative_to(ROOT)}"


def test(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="./build.py test", description="Runs GdUnit4 test suites under tests/.")
    parser.add_argument("--all", action="store_true", help="every suite; without it or --tag, the smoke tag")
    parser.add_argument("--tag", action="append", default=[], help="suites with all of these categories")
    parser.add_argument("--skip", action="append", default=[], help="leave out suites with this category")
    parser.add_argument("--case", default="", help="only the test cases whose name contains this")
    parser.add_argument("--jobs", type=int, default=1, help="suites run at once")
    parser.add_argument(
        "--with-key-frames",
        action="store_true",
        help="visual_confirmation suites save their key frames (under a display, the compatibility renderer) and check "
        "the frame budget; they run alone, after the others, so their frame times aren't slowed by their neighbours",
    )
    parser.add_argument("--ci-run", action="store_true", help="report frame times without checking them against the budget")
    parser.add_argument("--list", action="store_true", help="show what would run")
    parsed = parser.parse_args(arguments)
    options = TestOptions(
        tags=parsed.tag or ([] if parsed.all else ["smoke"]),
        skips=parsed.skip,
        case=parsed.case,
        jobs=parsed.jobs,
        key_frames=parsed.with_key_frames,
        ci_run=parsed.ci_run,
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
    setup()
    for folder in ("test-logs", "test-results"):
        (BUILD / folder).mkdir(parents=True, exist_ok=True)
    # With key frames, visual suites check the frame-time budget against the wall clock: they run alone, after the
    # others, so suites running beside them don't slow their frames down.
    alone = [suite for suite in suites if options.key_frames and suite.visual] if options.jobs > 1 else suites
    together = [suite for suite in suites if suite not in alone]
    outcomes: list[Outcome] = []
    if together:
        print(f"Running {len(together)} test suites, {options.jobs} at a time.")
        with ThreadPoolExecutor(options.jobs) as pool:
            for outcome in pool.map(lambda suite: run_suite(suite, options), together):
                print(report(outcome), flush=True)
                outcomes.append(outcome)
    for suite in alone:
        outcome = run_suite(suite, options)
        print(report(outcome), flush=True)
        outcomes.append(outcome)
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
    print(f"{len(outcomes) - failed} passed, {failed} failed, {len(pending)} pending.")
    return 1 if failed else 0


# --- Commands ---------------------------------------------------------------------------------------------------------


def lint() -> None:
    need_venv()
    run([dotnet(), "build", "--nologo", "-v", "quiet", ROOT / "ThisScalpelIsMine.csproj"])
    # Exported games build without the tests: their code must not lean on anything only the tests bring.
    run([dotnet(), "build", "--nologo", "-v", "quiet", "-c", "ExportRelease", ROOT / "ThisScalpelIsMine.csproj"])
    run([dotnet(), "format", ROOT / "ThisScalpelIsMine.csproj", "--verify-no-changes"])
    for tool in (["ruff", "check"], ["ruff", "format", "--check"], ["mypy"]):
        run([VENV / "bin" / tool[0], *tool[1:], "tools", "build.py"], cwd=ROOT)


def export() -> int:
    """Exports the game, then starts it once. Godot reports a broken export (no solution file, a failed .NET publish)
    only as errors in its output and still exits with 0, so both steps are judged by their output too."""
    fetch_templates()
    OUTPUT.unlink(missing_ok=True)
    steps = {
        "export": [GODOT, "--headless", "--path", ROOT, "--export-release", "Linux", OUTPUT],
        "export-launch": [OUTPUT, "--headless", "--quit-after", "120"],
    }
    for name, command in steps.items():
        log = BUILD / f"{name}.log"
        result = subprocess.run([str(part) for part in command], capture_output=True, text=True, check=False)
        log.write_text(result.stdout + result.stderr)
        errors = [line for line in log.read_text().splitlines() if ERRORS.search(line) and not NOISE.search(line)]
        if result.returncode != 0 or errors:
            print("\n".join(errors[:20]))
            print(f"{name} failed (exit {result.returncode}), see {log}")
            return 1
    print(f"Done: {OUTPUT}")
    return 0


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
        require("git:git", "xvfb-run:xorg-x11-server-Xvfb")
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
    elif command == "shots":
        setup()
        scenario = arguments[0] if arguments else "appendectomy"
        out = Path(arguments[1]) if len(arguments) > 1 else BUILD / "shots"
        out.mkdir(parents=True, exist_ok=True)
        # The compatibility renderer works on software OpenGL, so this runs without a GPU. RENDERER=forward_plus checks
        # the default renderer instead (needs Vulkan: lavapipe works, mesa-vulkan-drivers).
        renderer = os.environ.get("RENDERER", "gl_compatibility")
        extra = os.environ.get("SHOTS_ARGS", "").split()
        run(["xvfb-run", "-a", GODOT, "--path", ROOT, "--rendering-method", renderer, "res://tests/Support/Screenshot.tscn",
             "--", f"--scenario={scenario}", f"--out={out}", *extra])  # fmt: skip
        print(f"Screenshots in {out}")
    elif command == "build":
        setup()
        if os.environ.get("SKIP_TESTS") != "1" and test(["--all"]) != 0:
            return 1
        return export()
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
