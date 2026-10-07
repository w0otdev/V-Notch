"""Exercise the actual scanner with disposable, synthetic Git commits."""

import argparse
from pathlib import Path
import random
import string
import subprocess
import sys
import tempfile

CREATE_NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("binary", type=Path)
    args = parser.parse_args()
    binary = args.binary.resolve()
    repository = Path(__file__).resolve().parents[2]
    config = repository / ".gitleaks.toml"
    ignore = repository / ".gitleaksignore"
    # Deliberately synthetic; do not embed a complete token literal in source.
    generator = random.Random(1729)
    pat = "ghp_" + "".join(generator.choice(string.ascii_letters + string.digits) for _ in range(36))
    google = "AIza" + "SyD_abc1234567890XYZ_abcdef12345678"
    cases = [
        ("Tests/SecurityAuditTests.cs", f'var apiKey = "{google}";', 0),
        ("Services/FutureClient.cs", f'var apiKey = "{google}";', 1),
        ("Services/YouTubeSubtitleService.cs", f'var apiKey = "{google}";', 1),
        ("Services/YouTubeSubtitleService.cs", f'var token = "{pat}";', 1),
        ("Tests/SecurityAuditTests.cs", f'var token = "{pat}";', 1),
        ("README.md", f'Token: {pat}', 1),
    ]
    for path, content, expected in cases:
        with tempfile.TemporaryDirectory(prefix="vnotch-secret-gate-") as temporary:
            root = Path(temporary)
            if not root.resolve().is_relative_to(Path(tempfile.gettempdir()).resolve()):
                raise RuntimeError("Scanner fixture must remain in the temporary directory.")
            target = root / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content, encoding="utf-8")
            for command in (
                ["git", "init", "--quiet"],
                ["git", "add", "."],
                ["git", "-c", "user.name=Scanner Test", "-c", "user.email=scanner@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Synthetic scanner test"],
            ):
                subprocess.run(command, cwd=root, check=True, capture_output=True, creationflags=CREATE_NO_WINDOW)
            result = subprocess.run(
                [str(binary), "git", str(root), "--log-opts=--all", "--config", str(config),
                 "--gitleaks-ignore-path", str(ignore), "--redact=100", "--no-banner", "--log-level", "error"],
                cwd=root, capture_output=True, creationflags=CREATE_NO_WINDOW,
            )
            if result.returncode != expected:
                raise RuntimeError(f"Scanner did not enforce the expected gate for {path}: exit {result.returncode}, expected {expected}.")
    print("Secret scanner checks passed: exact fixture exception; production, test and documentation leaks blocked.")


if __name__ == "__main__":
    main()
