"""Regression checks for the runner's coverage reporting; no Godot runtime needed."""

import contextlib
import dataclasses
import io
import os
import shutil
import subprocess
import tempfile
import threading
import time
import unittest
import xml.etree.ElementTree as ElementTree
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch

import build

QUIT = "GdUnit4 Godot Runtime Test Runner ends with exit code: 0"


class DevDependenciesTest(unittest.TestCase):
    def test_installed_xvfb_is_verified_without_package_download(self) -> None:
        with patch("build.shutil.which", side_effect=lambda command: f"/usr/bin/{command}"), patch.object(build, "run") as run:
            build.setup_xvfb()
        run.assert_called_once()
        self.assertEqual(run.call_args.args[0][:2], ["xvfb-run", "-a"])
        self.assertIn("XOpenDisplay", run.call_args.args[0][-1])

    def test_fedora_dependency_is_extracted_and_verified_without_sudo(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            local = Path(temporary) / "xvfb"

            def run(command: list[str | Path], **kwargs: object) -> None:
                if command[:2] == ["dnf", "download"]:
                    packages = Path(command[command.index("--destdir") + 1])
                    (packages / "xvfb.rpm").touch()
                elif command[0] == "cpio":
                    for directory in ("usr/bin", "usr/lib64", "usr/share/X11/xkb"):
                        (local / directory).mkdir(parents=True, exist_ok=True)
                    (local / "usr/bin/xvfb-run").touch()

            with (
                patch.object(build, "XVFB_LOCAL", local),
                patch.dict(os.environ),
                patch("build.shutil.which", side_effect=lambda command: None if command in ("xvfb-run", "Xvfb") else command),
                patch("build.Path.is_file", return_value=True),
                patch.object(build, "run", side_effect=run) as commands,
                patch("build.subprocess.run", return_value=subprocess.CompletedProcess([], 0, stdout=b"rpm payload")),
            ):
                build.setup_xvfb()
                self.assertEqual(os.environ["PATH"].split(os.pathsep)[0], str(local / "bin"))
                self.assertIn(str(local / "usr/lib64"), os.environ["LD_LIBRARY_PATH"].split(os.pathsep))
                self.assertEqual(os.environ["XKB_CONFIG_ROOT"], str(local / "usr/share/X11/xkb"))
                self.assertIn("-xkbdir", (local / "bin/Xvfb").read_text())
            self.assertIn("xkeyboard-config", commands.call_args_list[0].args[0])
            self.assertEqual(commands.call_args_list[1].args[0], ["cpio", "-idmu", "--quiet"])
            self.assertEqual(commands.call_args_list[1].kwargs["input"], b"rpm payload")
            self.assertEqual(commands.call_args_list[-1].args[0][:2], ["xvfb-run", "-a"])
            self.assertTrue(all("sudo" not in call.args[0] for call in commands.call_args_list))

    def test_missing_extraction_tools_do_not_attempt_sudo(self) -> None:
        with (
            patch("build.shutil.which", side_effect=lambda command: command if command == "dnf" else None),
            patch.object(build, "run") as run,
            self.assertRaisesRegex(SystemExit, "rpm2cpio, cpio"),
        ):
            build.setup_xvfb()
        run.assert_not_called()

    def test_failed_x11_verification_rejects_setup(self) -> None:
        with (
            patch("build.subprocess.run", return_value=subprocess.CompletedProcess([], 1)),
            contextlib.redirect_stderr(io.StringIO()),
            self.assertRaises(SystemExit),
        ):
            build.verify_xvfb()


class CoverageReportingTest(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.addCleanup(patch.stopall)
        patch.object(build, "BUILD", self.root).start()
        patch.object(build, "ROOT", self.root).start()
        for folder in ("test-logs", "test-results"):
            (self.root / folder).mkdir()
        self.options = build.TestOptions([], [], "", 1, False, True, False, False)
        self.suite = build.Suite(Path("ExampleTest.cs"), "Scalpel.Tests.Example", [], [], False)
        # What the processes of a run (see alongside) record.
        self.passed = self.result("<testsuite><testcase /></testsuite>")
        self.started: list[str] = []
        self.others_running: list[int] = []
        self.running = 0
        self.lock = threading.Lock()

    def result(self, xml: str, output: str = QUIT, status: int = 0) -> build.ProcessResult:
        log, report = self.root / "run.log", self.root / "run.xml"
        log.write_text(output)
        report.write_text(xml)
        return build.problems(log, report, status, 60)

    def test_broken_only_suite_writes_skipped_report_without_launching(self) -> None:
        self.suite.cases = [build.Case("Broken", ["broken"], "BROKEN: example", [])]
        with patch.object(build, "run_process") as process:
            outcome = build.run_suite(self.suite, self.options)
        process.assert_not_called()
        self.assertTrue(build.report(outcome).startswith("skip "))
        xml = ElementTree.parse(self.root / "test-results" / "Example.xml")
        self.assertIsNotNone(xml.find("testcase/skipped"))

    def test_errors_keep_their_stack_trace(self) -> None:
        output = "\n".join(
            [
                "ERROR: System.InvalidOperationException: The underlying collection was modified.",
                "   at System.Collections.Concurrent.BlockingCollection`1.TryTake()",
                "   at: void Godot.GodotSynchronizationContext.ExecutePendingContinuations()",
                "next line",
            ]
        )
        self.assertEqual(
            build.godot_errors(output),
            [
                "ERROR: System.InvalidOperationException: The underlying collection was modified.\n"
                "       at System.Collections.Concurrent.BlockingCollection`1.TryTake()\n"
                "       at: void Godot.GodotSynchronizationContext.ExecutePendingContinuations()"
            ],
        )

    def test_empty_suite_is_skipped(self) -> None:
        self.assertTrue(build.report(build.run_suite(self.suite, self.options)).startswith("skip "))

    def test_all_runtime_skips_fail_when_runnable_cases_were_selected(self) -> None:
        result = self.result("<testsuite><testcase><skipped /></testcase></testsuite>")
        self.assertEqual(result.executed, 0)
        self.assertIn("Runnable test cases were selected", result.failures[0])

    def test_empty_process_report_fails(self) -> None:
        result = self.result("<testsuite />")
        self.assertEqual(result.executed, 0)
        self.assertIn("no test cases ran", result.failures[0])

    def test_mixed_report_counts_only_executed_cases(self) -> None:
        result = self.result("<testsuite><testcase /><testcase><skipped /></testcase></testsuite>")
        self.assertEqual(result, build.ProcessResult([], 1))

    def test_errors_still_fail_when_all_cases_are_skipped(self) -> None:
        result = self.result("<testsuite><testcase><skipped /></testcase></testsuite>", "ERROR: startup failed")
        self.assertEqual(result.executed, 0)
        self.assertTrue(result.failures)

    def test_assertion_failures_are_preserved(self) -> None:
        result = self.result('<testsuite><testcase name="Cut"><failure message="gap missing" /></testcase></testsuite>')
        self.assertEqual(result.executed, 1)
        self.assertIn("gap missing", result.failures[0])

    def test_passing_cases_fail_when_godot_never_quits(self) -> None:
        result = self.result("<testsuite><testcase /></testsuite>", "Rebuild Godot Project ends with exit code: 0", 1)
        self.assertEqual(result.failures, ["[ERROR] Godot didn't quit cleanly after its cases (runner exit never reported)."])

    def test_passing_cases_fail_when_godot_quits_with_an_error(self) -> None:
        result = self.result("<testsuite><testcase /></testsuite>", QUIT.replace(": 0", ": 134"))
        self.assertIn("runner exit 134", result.failures[0])

    def test_empty_isolated_process_fails_even_if_another_case_passes(self) -> None:
        self.suite.isolate_cases = True
        self.suite.cases = [build.Case("Ignored", [], "", ['"a"', '"b"'])]
        results = [self.result("<testsuite><testcase /></testsuite>"), self.result("<testsuite />")]
        with patch.object(build, "run_process", side_effect=results) as process:
            outcome = build.run_suite(self.suite, self.options)
        self.assertEqual(process.call_count, 2)
        self.assertEqual(outcome.executed, 1)
        self.assertTrue(build.report(outcome).startswith("FAIL "))

    def test_passing_suite_does_not_hide_unexpectedly_empty_suite(self) -> None:
        self.suite.cases = [build.Case("Runs", [], "", [])]
        empty = build.Suite(Path("EmptyTest.cs"), "Scalpel.Tests.Empty", [], [], False, [build.Case("Expected", [], "", [])])
        results = [self.result("<testsuite><testcase /></testsuite>"), self.result("<testsuite />")]
        with patch.object(build, "setup"), patch.object(build, "discover", return_value=[self.suite, empty]):
            with patch.object(build, "run_process", side_effect=results):
                with contextlib.redirect_stdout(io.StringIO()) as output:
                    status = build.test(["--all"])
        self.assertEqual(status, 1)
        self.assertIn("FAIL Scalpel.Tests.Empty", output.getvalue())
        self.assertIn("1 passed, 1 failed, 0 skipped", output.getvalue())

    def test_passing_suite_with_no_selected_cases_in_another_suite_succeeds(self) -> None:
        self.suite.cases = [build.Case("Runs", [], "", [])]
        skipped = build.Suite(Path("SkippedTest.cs"), "Scalpel.Tests.Skipped", [], [], False, [build.Case("Broken", ["broken"], "", [])])
        with patch.object(build, "setup"), patch.object(build, "discover", return_value=[self.suite, skipped]):
            with patch.object(build, "run_process", return_value=self.result("<testsuite><testcase /></testsuite>")):
                with contextlib.redirect_stdout(io.StringIO()) as output:
                    status = build.test(["--all"])
        self.assertEqual(status, 0)
        self.assertIn("1 passed, 0 failed, 1 skipped", output.getvalue())

    def test_first_suite_runs_alone_until_the_adapter_installed_its_runner(self) -> None:
        self.suite.cases = [build.Case("Runs", [], "", [])]
        other = build.Suite(Path("OtherTest.cs"), "Scalpel.Tests.Other", [], [], False, [build.Case("Runs", [], "", [])])
        with patch.object(build, "setup"), patch.object(build, "discover", return_value=[self.suite, other]):
            with patch.object(build, "RUNNER_CLASS", self.root / "missing"), patch.object(build, "run_process", side_effect=self.alongside):
                with contextlib.redirect_stdout(io.StringIO()):
                    status = build.test(["--all", "--jobs", "2"])
        self.assertEqual(status, 0)
        self.assertEqual(self.started, ["Scalpel.Tests.Example", "Scalpel.Tests.Other"])
        # The second process started only once the first had ended.
        self.assertEqual(self.others_running, [0, 0])

    def alongside(self, suite: build.Suite, *arguments: object) -> build.ProcessResult:
        """A run_process that notes how many other processes were running when it started, waiting a moment for any
        the scheduler may be about to start beside it."""
        with self.lock:
            self.started.append(suite.full_name)
            self.others_running.append(self.running)
            self.running += 1
        time.sleep(0.1)
        with self.lock:
            self.running -= 1
        return self.passed

    def test_frame_budget_suites_run_alone_and_first_then_the_rest_together(self) -> None:
        self.suite.cases = [build.Case("Runs", [], "", [])]
        timed = build.Suite(Path("TimedTest.cs"), "Scalpel.Tests.Timed", ["frame_budget"], [], False, [build.Case("Runs", [], "", [])])
        other = build.Suite(Path("OtherTest.cs"), "Scalpel.Tests.Other", [], [], False, [build.Case("Runs", [], "", [])])
        (self.root / "runner.cs").touch()
        with patch.object(build, "setup"), patch.object(build, "discover", return_value=[self.suite, timed, other]):
            with patch.object(build, "RUNNER_CLASS", self.root / "runner.cs"), patch.object(build, "run_process", side_effect=self.alongside):
                with contextlib.redirect_stdout(io.StringIO()) as output:
                    status = build.test(["--all", "--jobs", "2"])
        self.assertEqual(status, 0)
        self.assertEqual(self.started[0], "Scalpel.Tests.Timed")
        # The timed suite had the machine to itself; the two others shared it.
        self.assertEqual(self.others_running, [0, 0, 1])
        self.assertIn("3 test suites in 3 test processes: 1 alone, then 2 at a time.", output.getvalue())

    def test_frame_budget_suites_share_the_machine_on_ci_where_frames_are_not_timed(self) -> None:
        timed = build.Suite(Path("TimedTest.cs"), "Scalpel.Tests.Timed", ["frame_budget"], [], False)
        local = dataclasses.replace(self.options, ci_run=False)
        self.assertEqual(build.frame_budget(timed, local), "cpu")
        self.assertTrue(build.runs_alone(timed, local))
        self.assertEqual(build.frame_budget(timed, self.options), "off")
        self.assertFalse(build.runs_alone(timed, self.options))
        self.assertEqual(build.frame_budget(self.suite, local), "off")
        self.assertFalse(build.runs_alone(self.suite, local))

    def test_key_frame_takers_run_alone(self) -> None:
        self.suite.categories = ["visual_confirmation"]
        self.assertFalse(build.runs_alone(self.suite, self.options))
        self.assertTrue(build.runs_alone(self.suite, dataclasses.replace(self.options, key_frames=True)))

    def test_capacity_is_one_process_per_core_within_memory(self) -> None:
        with patch.object(os, "cpu_count", return_value=8), patch.object(os, "sysconf", side_effect=[3 << 30, 1]):
            self.assertEqual(build.capacity(), 1)
        with patch.object(os, "cpu_count", return_value=8), patch.object(os, "sysconf", side_effect=[64 << 30, 1]):
            self.assertEqual(build.capacity(), 8)

    def test_every_suite_that_checks_frame_times_is_categorized(self) -> None:
        """Only frame_budget suites run alone, so one that checks the budget without the category would fail its
        frames on contention. The helpers that check it: tests/Support/ToolSession.cs, ScenarioFlow.cs and
        tests/Scenarios/GameplaySweep.cs."""
        helpers = ("FrameBudget", "Budget.Check", "ToolSession", "ScenarioFlow", "GameplaySweep")
        with patch.object(build, "ROOT", Path(build.__file__).parent):
            suites = build.discover()
        for suite in suites:
            with self.subTest(suite=suite.full_name):
                checks = any(helper in suite.path.read_text() for helper in helpers)
                self.assertEqual(checks, "frame_budget" in suite.categories)

    def test_isolated_rows_sharing_a_filter_run_in_one_process(self) -> None:
        self.suite.isolate_cases = True
        # Two rows of the same scenario, told apart only by a later argument.
        self.suite.cases = [build.Case("Plays", [], "", ['"a"', '"b"', '"a"'])]
        self.assertEqual([process.name for process in build.processes(self.suite, self.options)], ["Example__Plays_a", "Example__Plays_b"])

    def test_slow_suites_processes_are_submitted_first(self) -> None:
        self.suite.cases = [build.Case("Runs", [], "", [])]
        slow = build.Suite(Path("SlowTest.cs"), "Scalpel.Tests.Slow", ["slow"], [], True, [build.Case("Plays", [], "", ['"a"', '"b"'])])
        (self.root / "runner.cs").touch()
        with patch.object(build, "setup"), patch.object(build, "discover", return_value=[self.suite, slow]):
            with patch.object(ThreadPoolExecutor, "submit", autospec=True, side_effect=ThreadPoolExecutor.submit) as submit:
                with (
                    patch.object(build, "RUNNER_CLASS", self.root / "runner.cs"),
                    patch.object(build, "run_process", return_value=self.result("<testsuite><testcase /></testsuite>")),
                ):
                    with contextlib.redirect_stdout(io.StringIO()):
                        status = build.test(["--all", "--jobs", "2"])
        self.assertEqual(status, 0)
        submitted = [call.args[1].__self__.name for call in submit.call_args_list]
        self.assertEqual(submitted, ["Slow__Plays_a", "Slow__Plays_b", "Example"])

    def test_isolated_cases_are_spread_over_the_pool(self) -> None:
        self.suite.isolate_cases = True
        self.suite.cases = [build.Case("Plays", [], "", ['"a"', '"b"', '"c"'])]
        other = build.Suite(Path("OtherTest.cs"), "Scalpel.Tests.Other", [], [], False, [build.Case("Runs", [], "", [])])
        started: list[str] = []
        # Built once: the pool's threads would otherwise write and parse the same report files at the same time.
        passed = self.result("<testsuite><testcase /></testsuite>")

        def run_process(suite: build.Suite, options: build.TestOptions, name: str, *arguments: object) -> build.ProcessResult:
            started.append(name)
            return passed

        (self.root / "runner.cs").touch()
        with patch.object(build, "setup"), patch.object(build, "discover", return_value=[self.suite, other]):
            with patch.object(ThreadPoolExecutor, "submit", autospec=True, side_effect=ThreadPoolExecutor.submit) as submit:
                with patch.object(build, "RUNNER_CLASS", self.root / "runner.cs"), patch.object(build, "run_process", side_effect=run_process):
                    with contextlib.redirect_stdout(io.StringIO()) as output:
                        status = build.test(["--all", "--jobs", "4"])
        self.assertEqual(status, 0)
        # Each isolated case is a pool task of its own, beside the other suite's single process.
        self.assertEqual(sorted(started), ["Example__Plays_a", "Example__Plays_b", "Example__Plays_c", "Other"])
        self.assertEqual(submit.call_count, 4)
        self.assertIn("2 test suites in 4 test processes: 0 alone, then 4 at a time.", output.getvalue())
        self.assertIn("2 passed, 0 failed", output.getvalue())

    def test_entirely_skipped_run_cannot_pass(self) -> None:
        with patch.object(build, "setup"), patch.object(build, "discover", return_value=[self.suite]):
            with contextlib.redirect_stdout(io.StringIO()) as output, contextlib.redirect_stderr(io.StringIO()):
                status = build.test(["--all"])
        self.assertEqual(status, 1)
        self.assertIn("0 passed, 0 failed, 1 skipped", output.getvalue())

    def test_functional_visual_suite_is_headless_even_with_inherited_capture_flag(self) -> None:
        self.suite.categories = ["visual_confirmation"]
        with patch.dict(os.environ, {"WITH_KEY_FRAMES": "1"}), patch.object(build, "dotnet", return_value="dotnet"):
            with patch("build.subprocess.run", return_value=subprocess.CompletedProcess([], 0)) as process:
                with patch.object(build, "problems", return_value=build.ProcessResult([], 1)):
                    build.run_process(self.suite, self.options, "Example", "filter", 1)
        self.assertEqual(process.call_args.args[0][0], "dotnet")
        self.assertNotIn("WITH_KEY_FRAMES", process.call_args.kwargs["env"])
        settings = (self.root / "test-settings" / "Example.runsettings").read_text()
        self.assertIn("--headless", settings)

    def test_key_frames_use_the_virtual_display_even_with_a_real_one(self) -> None:
        self.suite.categories = ["visual_confirmation"]
        self.options.key_frames = True
        with patch.dict(os.environ, {"DISPLAY": ":0"}), patch.object(build, "dotnet", return_value="dotnet"):
            with patch("build.subprocess.run", return_value=subprocess.CompletedProcess([], 0)) as process:
                with patch.object(build, "problems", return_value=build.ProcessResult([], 1)), patch("build.shutil.which", return_value="xvfb-run"):
                    build.run_process(self.suite, self.options, "Example", "filter", 1)
        self.assertEqual(process.call_args.args[0][0], "xvfb-run")
        self.assertEqual(process.call_args.kwargs["env"]["LIBGL_ALWAYS_SOFTWARE"], "1")

    def key_frames(self, passed: bool) -> tuple[Path, Path]:
        taken, baseline = self.root / "screenshots", self.root / "tests" / "KeyFrames"
        patch.object(build, "KEY_FRAMES", taken).start()
        patch.object(build, "BASELINE", baseline).start()
        patch.object(build, "REVIEW", self.root / "review").start()
        (taken / "scalpel").mkdir(parents=True)
        (taken / "scalpel" / "00_cut_top.png").write_text("new")
        (baseline / "gone").mkdir(parents=True)
        (baseline / "gone" / "00_old_top.jpg").write_text("old")
        if passed:
            (taken / build.PASSED).touch()
        return taken, baseline

    def test_baseline_is_left_alone_without_a_passed_run(self) -> None:
        self.key_frames(passed=False)
        with patch.object(build, "keyframes_tool") as tool, contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(build.generate_baseline(["--use-latest-set"]), 1)
        tool.assert_not_called()

    def test_baseline_is_updated_from_a_passed_run(self) -> None:
        self.key_frames(passed=True)
        with patch.object(build, "keyframes_tool", return_value=0) as tool:
            self.assertEqual(build.generate_baseline(["--use-latest-set"]), 0)
        self.assertEqual(tool.call_args.args[0], "update")

    def test_focused_baseline_needs_a_focused_run_that_passed(self) -> None:
        taken, _ = self.key_frames(passed=True)
        with patch.object(build, "keyframes_tool", return_value=0) as tool, contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(build.generate_baseline(["--focused"]), 1)
            tool.assert_not_called()
            (taken / build.FOCUSED).touch()
            self.assertEqual(build.generate_baseline(["--focused"]), 0)
        self.assertIn("--focused", tool.call_args.args)

    def test_baseline_run_that_fails_leaves_the_baseline_alone(self) -> None:
        taken, _ = self.key_frames(passed=True)

        def fails(_: list[str]) -> int:
            shutil.rmtree(taken)
            return 1

        with patch.object(build, "test", side_effect=fails), patch.object(build, "keyframes_tool") as tool, contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(build.generate_baseline([]), 1)
        tool.assert_not_called()

    def test_review_fails_without_a_baseline(self) -> None:
        _, baseline = self.key_frames(passed=True)
        shutil.rmtree(baseline)
        with patch.object(build, "keyframes_tool", return_value=0), contextlib.redirect_stderr(io.StringIO()) as errors:
            self.assertEqual(build.screenshot_review(["--use-latest-set"]), 1)
        self.assertIn("generate_baseline", errors.getvalue())

    def test_review_fails_when_key_frames_differ(self) -> None:
        self.key_frames(passed=True)
        with patch.object(build, "keyframes_tool", return_value=1):
            self.assertEqual(build.screenshot_review(["--use-latest-set"]), 1)

    def test_review_passes_when_nothing_differs(self) -> None:
        self.key_frames(passed=True)
        with patch.object(build, "keyframes_tool", return_value=0) as tool:
            self.assertEqual(build.screenshot_review(["--use-latest-set"]), 0)
        self.assertEqual(tool.call_args.args[0], "report")

    def key_frame_test(self, *arguments: str) -> Path:
        """Runs test() whose one visual suite takes a key frame and passes; returns the screenshots folder."""
        taken, _ = self.key_frames(passed=True)
        self.suite.categories = ["visual_confirmation"]
        self.suite.cases = [build.Case("Runs", [], "", [])]

        def takes_a_key_frame(suite: build.Suite, *arguments: object) -> build.ProcessResult:
            (taken / "scalpel").mkdir(parents=True, exist_ok=True)
            (taken / "scalpel" / "00_cut_top.png").write_text("new")
            return self.passed

        with patch.object(build, "setup"), patch.object(build, "discover", return_value=[self.suite]):
            with patch.object(build, "run_process", side_effect=takes_a_key_frame), contextlib.redirect_stdout(io.StringIO()):
                build.test(["--with-key-frames", *arguments])
        return taken

    def test_full_regression_that_passed_is_marked(self) -> None:
        taken = self.key_frame_test("--all")
        self.assertTrue((taken / build.PASSED).exists())

    def test_focused_key_frame_run_clears_the_last_and_is_not_marked(self) -> None:
        taken = self.key_frame_test("--all", "--tag", "visual_confirmation")
        self.assertFalse((taken / build.PASSED).exists())

    def test_key_frame_run_with_broken_cases_is_not_marked(self) -> None:
        with patch.dict(os.environ, {"RUN_BROKEN": "1"}):
            taken = self.key_frame_test("--all")
        self.assertFalse((taken / build.PASSED).exists())


if __name__ == "__main__":
    unittest.main()
