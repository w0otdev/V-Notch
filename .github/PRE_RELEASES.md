# Automatic beta releases

Each push to `main` runs the full CI gates. After CI succeeds for that exact
commit, `Release Installer` builds, signs and publishes both installer variants
as a GitHub pre-release. PRs do not publish releases. A push containing several
commits builds its final commit; push commits separately to publish each one.

With `<Version>2.0.0</Version>` in `V-Notch.csproj` and `2.0.0` already published
as stable, release workflow run 184
produces `v2.0.1-beta.184.1`. A new run produces `v2.0.1-beta.185.1`; rerunning
all jobs in run 184 produces `v2.0.1-beta.184.2`. Failed-job retries reuse the
identity already allocated by the successful preparation job, so both installer
variants and their signed manifests still agree.

The suffix uses GitHub's workflow run number and attempt. Build metadata also
records the globally unique run ID, attempt and complete source commit SHA.
Versions are calculated during CI; the workflow does not commit version bumps.
It creates a separate release/tag for each build and keeps earlier betas.
Beta releases use `--latest=false`, so the stable update channel is unaffected.

The application's displayed version, updater and integrity checks use the same
release version from `AssemblyInformationalVersion`. Windows file/installer/package
versions use `major.minor.run_number.run_attempt` for beta builds, for example
`2.0.184.1`. The assembly identity stays at the project version. Counters outside
Windows' supported component range are rejected rather than wrapped or reused.

To publish the stable version after these betas, change the project version to
`2.0.1` and push tag `v2.0.1`. Stable tags must exactly match the project version.
The workflow reads [GitHub's latest published stable release](https://docs.github.com/en/rest/releases/releases#get-the-latest-release): while `2.0.1` is
unreleased, main keeps producing `2.0.1-beta.*` even after the version bump.
Once stable `2.0.1` is published, further pushes target `2.0.2-beta.*` automatically.
If the project version is ahead of the latest stable (or no stable exists),
beta targets the project version itself. A project behind stable or an API error
other than 404 stops the release rather than guessing a target or downgrading.

Installed beta clients can upgrade to the final release of the same core
(`2.0.1-beta.*` -> `2.0.1`) or any higher stable version, with beta opt-in either
enabled or disabled. The opt-in includes stable releases as well as betas.
With beta updates disabled, an older stable release is not offered as a
downgrade (`2.0.2-beta.*` does not upgrade to `2.0.1`). Windows file/build
numbers do not determine update precedence; the semantic release version does.

The existing `code-signing` and `update-signing` environments are still used.
`VNOTCH_UPDATE_SIGNING_KEY_PEM` in `update-signing` must match the embedded public
key. Optional Authenticode certificate settings stay in `code-signing`.
For unattended publishing, those environments must allow main and must not
require a reviewer. Keep signing secrets in the protected environments.

Stable assets are checked by the pinned original 1.9.3 updater. Beta assets are
checked by the current updater, including signature/hash failures and opt-in
discovery. Older clients receive a stable release first; they cannot discover
or install the new beta version format until upgraded.

Every stable release also verifies its actual signed installers using the current
updater compiled with a beta informational version of the same core, a high
numeric Windows file version, and both opt-in settings. The probe checks discovery,
valid downloads, tampering, invalid signatures and missing signatures without
executing an installer.

The Windows CI validation runs this promotion probe with synthetic signed payloads
on PRs and main as well, before release jobs can start.
