"""Exercise pre-push command routing without compiling or touching real outputs."""

import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell.exe")


@unittest.skipUnless(POWERSHELL, "PowerShell is required to exercise the hook")
class PrePushPathsTests(unittest.TestCase):
    def run_hook(self, *options):
        with tempfile.TemporaryDirectory(prefix="vnotch-pre-push-") as temporary:
            root = Path(temporary)
            (root / "Tools").mkdir()
            (root / "Tests").mkdir()
            shutil.copyfile(ROOT / "Tools/Test-PrePush.ps1", root / "Tools/Test-PrePush.ps1")
            (root / "Tools/Assert-ModelAssets.ps1").write_text("$global:LASTEXITCODE = 0", encoding="utf-8")
            (root / "Tests/WidgetServiceTests.cs").touch()
            harness = root / "invoke.ps1"
            harness.write_text(r"""
param([switch]$Full, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$global:commands = [Collections.Generic.List[object]]::new()
function global:git {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'diff') { 'Directory.Build.props'; 'Services/WidgetService.cs' }
}
function global:python { $global:LASTEXITCODE = 0 }
function global:dotnet {
    $global:commands.Add(@($args))
    $global:LASTEXITCODE = 0
}
& "$PSScriptRoot/Tools/Test-PrePush.ps1" -BaseRef fixture -Full:$Full -SkipBuild:$SkipBuild
ConvertTo-Json -InputObject @($global:commands) -Depth 5 |
    Set-Content -LiteralPath "$PSScriptRoot/commands.json" -Encoding utf8
""", encoding="utf-8")
            result = subprocess.run(
                [POWERSHELL, "-NoProfile", "-File", str(harness), *options],
                cwd=root, capture_output=True, text=True, timeout=30,
                creationflags=0x08000000 if sys.platform == "win32" else 0,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            captured = json.loads((root / "commands.json").read_text(encoding="utf-8-sig"))
            commands = []
            for command in captured:
                # PowerShell splits literal -p:Name=value for a function mock,
                # whereas native dotnet receives one argument. Splat values
                # already arrive intact, including paths containing spaces.
                normalized = []
                arguments = iter(command)
                for argument in arguments:
                    value = str(argument)
                    if value.endswith(":"):
                        value += str(next(arguments))
                    normalized.append(value)
                commands.append(normalized)
            expected = {
                "ArtifactsPath": root / "artifacts/pre-push",
                "VNotchNativeArtifactPath": root / "artifacts/pre-push/native/VNotch.Native.dll",
                "_VNotchNativeObjDir": root / "artifacts/pre-push/native/obj",
            }
            for command in commands:
                properties = dict(arg[3:].split("=", 1) for arg in command if arg.startswith("-p:"))
                for name, path in expected.items():
                    self.assertIn(name, properties, command)
                    self.assertEqual(path, Path(properties[name]), command)
            return commands

    def test_affected_tests_use_the_same_isolated_managed_and_native_build(self):
        commands = self.run_hook()
        self.assertEqual(["restore", "build", "test"], [command[0] for command in commands])
        self.assertIn("--locked-mode", commands[0])
        self.assertIn("-p:SkipNativeCompile=false", commands[1])
        self.assertIn("-warnaserror", commands[1])
        self.assertIn("--no-build", commands[2])
        self.assertIn("--no-restore", commands[2])
        self.assertIn("WidgetServiceTests", commands[2][commands[2].index("--filter") + 1])

    def test_full_suite_uses_the_same_isolated_build(self):
        commands = self.run_hook("-Full")
        self.assertEqual(["restore", "build", "test"], [command[0] for command in commands])
        self.assertIn("Category!=DesktopIntegration", commands[-1])

    def test_skip_build_reuses_pre_push_outputs(self):
        commands = self.run_hook("-SkipBuild")
        self.assertEqual(["restore", "test"], [command[0] for command in commands])
        self.assertIn("--no-build", commands[-1])


if __name__ == "__main__":
    unittest.main()
