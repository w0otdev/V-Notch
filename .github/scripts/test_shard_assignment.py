import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from xml.etree import ElementTree


RUNNER = Path(__file__).resolve().parents[2] / "Tools/Invoke-TestShard.ps1"
PWSH = shutil.which("pwsh")


@unittest.skipUnless(PWSH, "PowerShell is required to exercise the shard runner")
class ShardAssignmentTests(unittest.TestCase):
    def test_each_class_runs_once_when_discovered_case_counts_differ(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            assembly = root / "bin/Release/Fixture.dll"
            assembly.parent.mkdir(parents=True)
            assembly.touch()
            classes = [f"Fixture.Class{i:02}" for i in range(16)]
            (root / "tests.txt").write_text("\n".join(f"{name}.Test" for name in classes))
            harness = root / "run.ps1"
            harness.write_text(r"""
param($runner)
$ErrorActionPreference = 'Stop'
function dotnet {
    if ($args[0] -eq 'vstest') {
        $rows = @(Get-Content "$PSScriptRoot/tests.txt")
        if ($index % 2 -eq 1) { $rows += 'Fixture.Class00.ExtraTest' }
        $path = $args[-1].Substring('/ListTestsTargetPath:'.Length)
        Set-Content -LiteralPath $path -Value $rows
    }
    $global:LASTEXITCODE = 0
}
foreach ($index in 0..3) {
    & $runner -ShardIndex $index -TotalShards 4 -ProjectPath "$PSScriptRoot/Fixture.csproj" -ResultsDirectory $PSScriptRoot
}
""", encoding="utf-8")
            result = subprocess.run([PWSH, "-NoProfile", "-File", str(harness), str(RUNNER)],
                                    capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            assigned = []
            for settings in root.glob("shard-*.runsettings"):
                test_filter = ElementTree.parse(settings).findtext("./RunConfiguration/TestCaseFilter")
                assigned.extend(re.findall(r"FullyQualifiedName~(Fixture\.Class\d+)\.", test_filter))
            self.assertCountEqual(assigned, classes)
