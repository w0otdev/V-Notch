"""Keep security scanning unconditional; skip Windows for known docs-only edits."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys


DOCUMENTATION = {
    "README.md", "README_VI.md", "CHANGELOG.md", "CONTRIBUTING.md",
    "GLOBAL_RELEASE_AUDIT.md", "DEVOPS_PIPELINE_AUDIT.md",
}

PACKAGED_DOCS = {
    "TERMS_OF_SERVICE.md",
    "TERMS_OF_SERVICE_VI.md",
    "THIRD_PARTY_NOTICES.md",
    "docs/TERMS_OF_SERVICE.md",
    "docs/TERMS_OF_SERVICE_VI.md",
    "docs/THIRD_PARTY_NOTICES.md",
}


def needs_windows(paths):
    paths = [path.replace("\\", "/") for path in paths]
    # Packaged terms/notices and unknown inputs require the build. Do not
    # classify every Markdown file as documentation: some are embedded assets.
    return not paths or any(
        path in PACKAGED_DOCS or (path not in DOCUMENTATION and not path.startswith("docs/"))
        for path in paths
    )


def scope(base, head, ref, directory=None):
    if ref.startswith("refs/tags/") or not re.fullmatch(r"[0-9a-f]{40}", base or "") or base == "0" * 40:
        return True
    if not re.fullmatch(r"[0-9a-f]{40}", head or ""):
        raise ValueError("A complete commit SHA is required.")
    try:
        result = subprocess.run(
            ["git", "diff", "--no-renames", "--name-only", "-z", base, head, "--"],
            cwd=directory, check=True, capture_output=True,
        )
    except subprocess.CalledProcessError as error:
        # Full history may still omit the old tip after a force-push. Only
        # skip validation when a successful diff proves the change is docs-only.
        detail = error.stderr.decode("utf-8", errors="replace").strip()
        print(f"Cannot determine changed files; requiring full validation (git diff exited {error.returncode}): {detail}", file=sys.stderr)
        return True
    return needs_windows([p.decode("utf-8") for p in result.stdout.split(b"\0") if p])


def release_eligible(document, head):
    jobs = document.get("jobs")
    if not isinstance(jobs, list) or not jobs:
        raise ValueError("The completed CI run must include jobs.")
    matching = [job for job in jobs if job.get("name") == "Windows validation"]
    if len(matching) != 1:
        raise ValueError("Cannot identify the Windows validation job.")
    job = matching[0]
    if job.get("head_sha") != head or job.get("status") != "completed":
        raise ValueError("Validation must belong to the completed release commit.")
    return job.get("conclusion") == "success"


def main():
    parser = argparse.ArgumentParser()
    modes = parser.add_subparsers(dest="mode", required=True)
    modes.add_parser("scope")
    release = modes.add_parser("release")
    release.add_argument("jobs", type=Path)
    args = parser.parse_args()
    if args.mode == "scope":
        name, value = "windows", scope(os.getenv("BASE_SHA", ""), os.getenv("HEAD_SHA", ""), os.getenv("GIT_REF", ""))
    else:
        name, value = "eligible", release_eligible(json.loads(args.jobs.read_text(encoding="utf-8")), os.environ["HEAD_SHA"])
    output = f"{name}={str(value).lower()}\n"
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        stream.write(output)
    print(output.strip())


if __name__ == "__main__":
    main()
