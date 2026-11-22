import hashlib
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile


class MirrorChecksumTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.package = self.root / "hongxue-stronghold-companion-v1.9.8-win-x64.zip"
        with zipfile.ZipFile(self.package, "w") as archive:
            archive.writestr("update-layout.json", "{}")
        digest = hashlib.sha256(self.package.read_bytes()).hexdigest()
        self.full_line = f"{digest}  {self.package.name}"
        self.delta_line = f"{'0' * 64}  delta-v1.9.7-to-v1.9.8.zip"

    def verify(self, *lines):
        manifest = self.root / "SHA256SUMS.txt"
        manifest.write_bytes("".join(line + "\r\n" for line in lines).encode("ascii"))
        script = Path(__file__).with_name("verify-mirror-checksum.sh").resolve()
        return subprocess.run(
            ["bash", script.as_posix(), self.package.name], cwd=self.root,
            capture_output=True, text=True, check=False,
        )

    def test_full_package_with_windows_line_ending(self):
        result = self.verify(self.full_line)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_full_package_with_separate_delta_entry(self):
        result = self.verify(self.full_line, self.delta_line)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_package_entry(self):
        self.assertNotEqual(self.verify(self.delta_line).returncode, 0)

    def test_modified_package(self):
        with zipfile.ZipFile(self.package, "a") as archive:
            archive.writestr("changed.txt", "changed after manifest generation")
        self.assertNotEqual(self.verify(self.full_line).returncode, 0)


if __name__ == "__main__":
    unittest.main()
