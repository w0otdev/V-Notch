"""Run the translation build's cache check with inherited PowerShell module paths."""

import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
WINDOWS_POWERSHELL = shutil.which("powershell.exe")
POWERSHELL_CORE = shutil.which("pwsh")
INPUTS = (
    "scripts/Build-TranslationRuntime.ps1",
    "Tools/TranslationRuntime/CMakeLists.txt",
    "Tools/TranslationRuntime/sentencepiece.json",
    "Native/Services/Translation/LiteTranslationRuntime.cpp",
    "Native/Services/Translation/LiteTranslationAbi.h",
    "Native/Services/Translation/LiteTranslationChunks.h",
)
RUNTIME_FILES = (
    "VNotch.Translation.dll", "ctranslate2.dll", "libiomp5md.dll",
    "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll",
)


@unittest.skipUnless(sys.platform == "win32" and WINDOWS_POWERSHELL and POWERSHELL_CORE,
                     "Windows PowerShell and PowerShell Core are required")
class TranslationRuntimeBuildTests(unittest.TestCase):
    def test_cached_build_prefers_windows_powershell_modules_over_inherited_core_modules(self):
        core = subprocess.run(
            [POWERSHELL_CORE, "-NoProfile", "-Command", "$PSHOME"],
            capture_output=True, text=True, check=True, timeout=15, creationflags=0x08000000,
        )
        core_modules = Path(core.stdout.strip()) / "Modules"
        with tempfile.TemporaryDirectory(prefix="vnotch translation build ") as temporary:
            root = Path(temporary)
            for name in INPUTS:
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / name, target)
            output = root / "Native/out/translation"
            output.mkdir(parents=True)
            signature = ":".join(hashlib.sha256((root / name).read_bytes()).hexdigest().upper()
                                 for name in INPUTS)
            (output / "build.signature").write_text(signature, encoding="ascii")
            for name in RUNTIME_FILES:
                (output / name).write_bytes(b"cached runtime fixture")
            harness = root / "invoke.ps1"
            # MSBuild preserves the parent's PSModulePath, putting PowerShell 7
            # modules before Windows PowerShell's. Set that order explicitly.
            harness.write_text(r'''
param([string]$CoreModules)
$ErrorActionPreference = 'Stop'
$env:PSModulePath = $CoreModules + [IO.Path]::PathSeparator + (Join-Path $PSHOME 'Modules')
& "$PSScriptRoot/scripts/Build-TranslationRuntime.ps1" -Root $PSScriptRoot
''', encoding="utf-8")
            result = subprocess.run(
                [WINDOWS_POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(harness),
                 "-CoreModules", str(core_modules)],
                cwd=root, capture_output=True, text=True, timeout=30,
                creationflags=0x08000000,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual(signature, (output / "build.signature").read_text(encoding="ascii"))
            self.assertFalse((root / ".cache").exists(), "Cached builds must not download or compile")
            for name in RUNTIME_FILES:
                self.assertEqual(b"cached runtime fixture", (output / name).read_bytes())


if __name__ == "__main__":
    unittest.main()
