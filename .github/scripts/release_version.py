"""One release identity shared by both installer variants, manifests and tag."""

import json
import os
from pathlib import Path
import re
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET


def numeric_version(value):
    if not isinstance(value, str) or not re.fullmatch(r"(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2,3}", value):
        raise ValueError("Release versions must have three or four canonical numeric components.")
    parts = [int(part) for part in value.split(".")]
    if any(part > 65534 for part in parts):
        raise ValueError("Release version exceeds Windows version limits.")
    return parts


def latest_stable_version(repository, token):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("A GitHub owner/repository is required.")
    request = Request(f"https://api.github.com/repos/{repository}/releases/latest", headers={
        "Accept": "application/vnd.github+json", "User-Agent": "V-Notch-Release",
        "Authorization": "Bearer " + token,
    })
    try:
        with urlopen(request, timeout=30) as response:
            release = json.load(response)
    except HTTPError as error:
        if error.code == 404:
            return None  # No stable release yet; beta targets the project's version.
        raise  # Never guess a beta core when GitHub is unavailable or rate-limiting.
    if release.get("draft") is not False or release.get("prerelease") is not False:
        raise ValueError("GitHub latest release must be a published stable release.")
    version = release.get("tag_name", "").removeprefix("v").removeprefix("V")
    numeric_version(version)
    return version


def release_identity(project, branch, run_number, run_attempt, run_id, sha, latest_stable):
    version = next((node.text.strip() for node in ET.parse(project).findall("./PropertyGroup/Version") if node.text), "")
    parts = numeric_version(version)
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("A full release commit SHA is required.")
    if any(not re.fullmatch(r"[1-9][0-9]*", str(value)) for value in (run_number, run_attempt, run_id)):
        raise ValueError("Positive workflow run identifiers are required.")

    prerelease = branch == "main"
    if prerelease:
        if int(run_number) > 65534 or int(run_attempt) > 65534:
            raise ValueError("Build identity exceeds Windows version limits; refusing to wrap or reuse a build number.")
        # A version bump preparing a stable release must keep its existing beta core.
        # Advance to the next patch only once this project version is actually published.
        core = version
        if latest_stable is not None:
            stable_parts = numeric_version(latest_stable)
            project_order = tuple((parts + [0])[:4])
            stable_order = tuple((stable_parts + [0])[:4])
            if stable_order > project_order:
                raise ValueError("Project version is behind the latest stable release; refusing a beta downgrade.")
            if stable_order == project_order:
                if parts[2] >= 65534:
                    raise ValueError("Next beta patch exceeds Windows version limits.")
                core = f"{parts[0]}.{parts[1]}.{parts[2] + 1}"
        version = f"{core}-beta.{run_number}.{run_attempt}"
        windows_version = f"{parts[0]}.{parts[1]}.{run_number}.{run_attempt}"
    elif branch == "v" + version:
        windows_version = ".".join(str(part) for part in (parts + [0])[:4])
    else:
        raise ValueError("Stable tag must exactly match the project version; only main publishes automatic betas.")

    return {
        "version": version,
        "tag": "v" + version,
        "windows_version": windows_version,
        "informational_version": f"{version}+build.{run_id}.{run_attempt}.sha.{sha}",
        "prerelease": str(prerelease).lower(),
    }


def main():
    branch = os.environ["RELEASE_BRANCH"]
    stable = latest_stable_version(os.environ["GH_REPO"], os.environ["GH_TOKEN"]) if branch == "main" else None
    identity = release_identity(
        Path("V-Notch.csproj"), branch, os.environ["RELEASE_RUN_NUMBER"],
        os.environ["RELEASE_RUN_ATTEMPT"], os.environ["RELEASE_RUN_ID"], os.environ["HEAD_SHA"], stable,
    )
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        for key, value in identity.items():
            stream.write(f"{key}={value}\n")
    print(json.dumps(identity, indent=2))


if __name__ == "__main__":
    main()
