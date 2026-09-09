# Signed updates (1.9.2 and later)

Updates require an ECDSA P-256 / SHA-256 signature checked with the public key
embedded in the application. No paid certificate or signing service is required.
Authenticode remains an optional additional policy for users who configure its
publisher and thumbprint allowlists. This does not remove Windows SmartScreen
warnings or authenticate a first manual download.

## One-time release setup

The initial public key is `Security/update-public-key.pem`. The matching private
key was generated outside the repository at
`%LOCALAPPDATA%\VNotchReleaseKeys\update-2026-09-private.pem`, in a directory
restricted to the current Windows user. Back up this file securely. Do not paste
it into issues, chat, logs, or source control.

In the GitHub repository, create an environment named `update-signing`, restrict
deployment to trusted release workflows/branches, and require a maintainer review
where available. Add an environment secret named `VNOTCH_UPDATE_SIGNING_KEY_PEM`
containing the complete private PEM file. Keep signing credentials out of the
build job. The separate signing job downloads the built installers and never
executes them. Anyone able to change an approved signing workflow or obtain the
private key can authorize updates: review changes before approving this job.

The workflow intentionally fails instead of publishing an unsigned update when
the secret is absent or does not match the embedded public key. This local change
does not configure GitHub secrets or publish a release.

## Release assets and compatibility

For each original installer name (`V-Notch-Setup.exe` and
`V-Notch-Setup-SelfContained.exe`), publish:

- The installer.
- `.sha256`: retained for older clients.
- `.manifest.json`: exact UTF-8 bytes, schema 1, with `SchemaVersion`, `Version`,
  `InstallerName`, `Size`, and `Sha256` properties.
- `.manifest.sig`: raw 64-byte IEEE P1363 ECDSA signature over the JSON bytes.

Do not reformat a manifest after signing. A stable release branch/tag must be
`v` plus the exact project version. Bump the project version for each update.
The updater checks signed version, installer name, size, and hash and rejects
same-version installs and downgrades. Missing manifest assets are not offered as
updates. Nightly prereleases remain outside the automatic latest-release feed.

Older clients can install the first signed-manifest release through their
existing checksum flow. That first transition retains the old client's security
properties; mandatory manifest verification begins after it is installed.
Older clients with an explicit Authenticode policy still enforce that policy.

For a manual release using PowerShell 7:

```powershell
./scripts/Sign-UpdateManifest.ps1 -InstallerPath installers/V-Notch-Setup.exe -Version 1.9.2 -PrivateKeyPath "$env:LOCALAPPDATA\VNotchReleaseKeys\update-2026-09-private.pem"
```

Sign both installer variants after all binary modifications, including any
optional Authenticode signing. The script verifies that the private key matches
the pinned public key and writes the three sidecars.

## Key rotation

Do not regenerate or replace the public key casually: existing installations
trust the old key. First ship a transition version, signed by the old key, that
trusts the new key. Keep an overlap plan for clients that skip versions. Losing
the old private key without a transition requires a trusted manual reinstall.
Generating a replacement key is deliberately refused when key files exist.
