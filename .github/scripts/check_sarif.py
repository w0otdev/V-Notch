"""Fail closed if the security-only CodeQL suite reports findings or errors."""

import argparse
import json
from pathlib import Path


def check(directory):
    reports = sorted(directory.glob("*.sarif"))
    if not reports:
        raise ValueError("CodeQL did not produce a SARIF report.")
    findings = 0
    for report in reports:
        data = json.loads(report.read_text(encoding="utf-8"))
        if data.get("version") != "2.1.0" or not isinstance(data.get("runs"), list) or not data["runs"]:
            raise ValueError("Invalid or empty SARIF report.")
        for run in data["runs"]:
            if run.get("tool", {}).get("driver", {}).get("name") != "CodeQL":
                raise ValueError("Expected a CodeQL report.")
            for invocation in run.get("invocations", []):
                if invocation.get("executionSuccessful") is False:
                    raise ValueError("CodeQL analysis did not complete successfully.")
                for notification in invocation.get("toolExecutionNotifications", []):
                    if notification.get("level") == "error":
                        descriptor_id = notification.get("descriptor", {}).get("id", "diagnostic")
                        message = notification.get("message", {}).get("text", "")
                        print(f"CodeQL diagnostic notification [{descriptor_id}]: {message}")
            results = run.get("results")
            if not isinstance(results, list):
                raise ValueError("CodeQL results are missing.")
            findings += len(results)
    if findings:
        raise ValueError(f"CodeQL reported {findings} security finding(s); inspect code scanning alerts.")
    return len(reports)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    try:
        count = check(parser.parse_args().directory)
    except (ValueError, OSError) as error:
        parser.exit(1, f"SAST gate failed: {error}\n")
    print(f"SAST gate passed: {count} CodeQL report(s), no findings.")


if __name__ == "__main__":
    main()
