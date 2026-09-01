#!/usr/bin/env python3
"""Collapse the .trx files dotnet test writes into one compact JSON summary.

Two consumers:
  * the admin panel's tests page (#291, A7), which reads the JSON artifact through
    a backend proxy and renders per-suite and per-test results;
  * a reviewer, via the markdown table this writes to $GITHUB_STEP_SUMMARY.

Exits non-zero when any test failed, so the CI job goes red. The Test step runs
with continue-on-error so that a failing suite still produces this report — which
is precisely when the per-test detail is worth having.

Usage: trx_to_json.py <trx-search-root> <output-json> [expected-suite-count]

Pass expected-suite-count to catch a test project that crashed before writing any
.trx at all. Without it, a project that never ran is silently absent from the
report and the run goes green on a subset of the suite.
"""
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# A failure message can be a multi-page stack trace. Keep enough to identify the
# defect without bloating an artifact the panel fetches on every page load.
MAX_MESSAGE = 2000


def parse(path):
    """Extract (suite name, results) from one .trx file."""
    root = ET.parse(path).getroot()

    # unitTestResult carries the outcome; unitTest carries the class name. They are
    # joined on the executionId, which only exists on the definition side.
    class_by_execution = {}
    for unit_test in root.findall(".//t:TestDefinitions/t:UnitTest", NS):
        execution = unit_test.find("t:Execution", NS)
        method = unit_test.find("t:TestMethod", NS)
        if execution is not None and method is not None:
            class_by_execution[execution.get("id")] = method.get("className", "")

    tests = []
    for result in root.findall(".//t:Results/t:UnitTestResult", NS):
        message = ""
        output = result.find("t:Output", NS)
        if output is not None:
            error = output.find("t:ErrorInfo", NS)
            if error is not None:
                parts = [
                    (error.findtext("t:Message", default="", namespaces=NS) or "").strip(),
                    (error.findtext("t:StackTrace", default="", namespaces=NS) or "").strip(),
                ]
                message = "\n".join(p for p in parts if p)[:MAX_MESSAGE]

        full_class = class_by_execution.get(result.get("executionId"), "")
        tests.append({
            "name": result.get("testName", ""),
            "class": full_class.rsplit(".", 1)[-1] if full_class else "",
            "outcome": result.get("outcome", "Unknown"),
            "durationMs": duration_ms(result.get("duration")),
            "message": message,
        })

    # CI names each file after its project, so the stem is the suite label. The
    # storage attribute is a lowercased path, which reads badly in the summary.
    return os.path.basename(path).removesuffix(".trx"), tests


def duration_ms(value):
    """'00:00:01.2340000' -> 1234. Absent on skipped tests."""
    if not value:
        return 0
    try:
        hours, minutes, seconds = value.split(":")
        return round((int(hours) * 3600 + int(minutes) * 60 + float(seconds)) * 1000)
    except (ValueError, AttributeError):
        return 0


def main():
    if len(sys.argv) not in (3, 4):
        sys.exit(f"usage: {sys.argv[0]} <trx-search-root> <output-json> [expected-suite-count]")
    search_root, out_path = sys.argv[1], sys.argv[2]
    expected = int(sys.argv[3]) if len(sys.argv) == 4 else None

    trx_files = sorted(glob.glob(os.path.join(search_root, "**", "*.trx"), recursive=True))
    if not trx_files:
        # No .trx at all means the test step never got far enough to write one —
        # a build break, a crashed runner. That is a failure, not an empty report.
        sys.exit(f"no .trx files found under {search_root!r}; the test run produced no results")

    if expected is not None and len(trx_files) != expected:
        found = ", ".join(os.path.basename(p) for p in trx_files) or "none"
        sys.exit(
            f"expected {expected} result file(s), found {len(trx_files)} ({found}). "
            "A test project crashed before writing results, or two wrote to the same path."
        )

    suites = []
    for path in trx_files:
        try:
            name, tests = parse(path)
        except ET.ParseError as exc:
            sys.exit(f"could not parse {path}: {exc}")
        suites.append({
            "suite": name,
            "total": len(tests),
            "passed": sum(1 for t in tests if t["outcome"] == "Passed"),
            "failed": sum(1 for t in tests if t["outcome"] == "Failed"),
            "skipped": sum(1 for t in tests if t["outcome"] not in ("Passed", "Failed")),
            "durationMs": sum(t["durationMs"] for t in tests),
            "tests": tests,
        })

    report = {
        "commit": os.environ.get("GITHUB_SHA", ""),
        "ref": os.environ.get("GITHUB_REF_NAME", ""),
        "runId": os.environ.get("GITHUB_RUN_ID", ""),
        "total": sum(s["total"] for s in suites),
        "passed": sum(s["passed"] for s in suites),
        "failed": sum(s["failed"] for s in suites),
        "skipped": sum(s["skipped"] for s in suites),
        "durationMs": sum(s["durationMs"] for s in suites),
        "suites": suites,
    }

    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2)

    write_summary(report)

    print(f"{report['passed']}/{report['total']} passed, {report['failed']} failed")
    sys.exit(1 if report["failed"] else 0)


def write_summary(report):
    """Render a markdown table into the GitHub Actions job summary, if we're in one."""
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not summary_path:
        return

    icon = "❌" if report["failed"] else "✅"
    lines = [
        f"## {icon} {report['passed']}/{report['total']} tests passed",
        "",
        "| Suite | Passed | Failed | Skipped | Duration |",
        "| --- | ---: | ---: | ---: | ---: |",
    ]
    for suite in report["suites"]:
        lines.append(
            f"| {suite['suite']} | {suite['passed']} | {suite['failed']} "
            f"| {suite['skipped']} | {suite['durationMs'] / 1000:.1f}s |"
        )

    failures = [
        (s["suite"], t) for s in report["suites"] for t in s["tests"] if t["outcome"] == "Failed"
    ]
    if failures:
        lines += ["", "### Failures", ""]
        for suite_name, test in failures:
            lines += [
                f"**{suite_name} · {test['class']}.{test['name']}**",
                "",
                "```",
                test["message"] or "(no message)",
                "```",
                "",
            ]

    with open(summary_path, "a", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
