"""Regression checks for the runner's coverage reporting; no Godot runtime needed."""

import contextlib
import io
import os
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ElementTree
from pathlib import Path
from unittest.mock import patch

import build


class CoverageReportingTest(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.addCleanup(patch.stopall)
        patch.object(build, "BUILD", self.root).start()
        for folder in ("test-logs", "test-results"):
            (self.root / folder).mkdir()
        self.options = build.TestOptions([], [], "", 1, False, True, False)
        self.suite = build.Suite(Path("ExampleTest.cs"), "Scalpel.Tests.Example", [], [], False)

    def result(self, xml: str, output: str = "", status: int = 0) -> build.ProcessResult:
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

    def test_empty_suite_is_skipped(self) -> None:
        self.assertTrue(build.report(build.run_suite(self.suite, self.options)).startswith("skip "))

    def test_runtime_skips_do_not_count_as_executed(self) -> None:
        result = self.result("<testsuite><testcase><skipped /></testcase></testsuite>")
        self.assertEqual(result, build.ProcessResult([], 0))

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

    def test_isolated_suite_with_only_runtime_skips_is_skipped(self) -> None:
        self.suite.isolate_cases = True
        self.suite.cases = [build.Case("Ignored", [], "", ['"a"', '"b"'])]
        with patch.object(build, "run_process", return_value=build.ProcessResult([])) as process:
            outcome = build.run_suite(self.suite, self.options)
        self.assertEqual(process.call_count, 2)
        self.assertTrue(build.report(outcome).startswith("skip "))

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


if __name__ == "__main__":
    unittest.main()
