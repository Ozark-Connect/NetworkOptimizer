"""Run the real installer with synthetic binaries and isolated gateway paths.

Run with: python3 -m unittest discover -s tests/scripts -p 'test_lan_ntp_server.py' -v
No network, real chronyd, gateway files or services are used.
"""

import hashlib
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "src/NetworkOptimizer.Web/Resources/PerfTweaks/35-lan-ntp-server.sh"
BASH = shutil.which("bash")  # Current Bash, as on the Debian target (macOS /bin/bash is 3.2).


def digest(data):
    return hashlib.sha256(data).hexdigest()


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="ntp-install-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for directory in ("data/lan-ntp-server/bin", "etc/systemd/system", "run", "stubs", "payload"):
            (self.root / directory).mkdir(parents=True)
        self.dir = self.root / "data/lan-ntp-server"
        self.old = {
            "bin/chronyd": b"#!/bin/sh\necho old-chronyd\n",
            "bin/chronyc": b"#!/bin/sh\necho old-chronyc\n",
            "chrony-copyright": b"old synthetic copyright\n",
        }
        self.old["chrony.sha256"] = self.manifest(self.old)
        for name, data in self.old.items():
            path = self.dir / name
            path.write_bytes(data)
            path.chmod(0o755 if name.startswith("bin/") else 0o644)
        self.new = {
            "chronyd": b"#!/bin/sh\necho synthetic-chronyd\n",
            "chronyc": b"#!/bin/sh\necho synthetic-chronyc\n",
        }
        self.archive = b"synthetic archive, not a Debian package\n"
        (self.root / "archive").write_bytes(self.archive)
        for name, data in self.new.items():
            (self.root / "payload" / name).write_bytes(data)
        self.env = {
            "PATH": str(self.root / "stubs") + ":/usr/bin:/bin:/usr/sbin:/sbin",
            "FIXTURE": str(self.root), "FAULT": "", "LC_ALL": "C",
        }
        self.stub("dpkg", 'echo arm64')
        self.stub("getent", 'exit 1')
        self.stub("id", 'echo synthetic-group')
        self.stub("ip", 'exit 0')
        self.stub("ss", 'exit 0')
        # macOS's sha256sum lacks GNU's --strict/--status. Check real fixture bytes
        # with hashlib instead; never bypass either archive or binary validation.
        checker = self.root / "stubs/sha256sum"
        checker.write_text("#!" + shutil.which("python3") + "\n" + '''
import hashlib
from pathlib import Path
import sys
try:
    files = [arg for arg in sys.argv[1:] if not arg.startswith('-')]
    text = Path(files[0]).read_text() if files else sys.stdin.read()
    for line in text.splitlines():
        expected, name = line.split('  ', 1)
        if hashlib.sha256(Path(name).read_bytes()).hexdigest() != expected:
            sys.exit(1)
except (OSError, ValueError):
    sys.exit(1)
''')
        checker.chmod(0o755)
        self.stub("systemctl", 'echo "$*" >> "$FIXTURE/systemctl.log"; exit 0')
        self.stub("curl", '''
echo download >> "$FIXTURE/downloads"
[ "$FAULT" != download ] || exit 22
if [ "$FAULT" = first-mirror ] && [ ! -e "$FIXTURE/first-mirror-failed" ]; then
    touch "$FIXTURE/first-mirror-failed"; exit 22
fi
while [ "$#" -gt 0 ]; do
    if [ "$1" = -o ]; then cp "$FIXTURE/archive" "$2"; exit; fi
    shift
done
exit 2
''')
        self.stub("dpkg-deb", '''
[ "$FAULT" != extract ] || exit 1
mkdir -p "$3/usr/sbin" "$3/usr/bin" "$3/usr/share/doc/chrony"
cp "$FIXTURE/payload/chronyd" "$3/usr/sbin/chronyd"
cp "$FIXTURE/payload/chronyc" "$3/usr/bin/chronyc"
echo 'new synthetic copyright' > "$3/usr/share/doc/chrony/copyright"
''')
        for name in ("install", "mv", "cp", "mktemp"):
            real = shutil.which(name, path="/usr/bin:/bin:/usr/sbin:/sbin")
            self.stub(name, f'''
fail=no
case "$FAULT:{name}:$*" in
    install:install:*'/new/bin/'*|backup:cp:*'/old/'*|hash-write:install:*'/new/chrony.sha256.new'*|stage-rename:mv:*'/new/chrony.sha256.new'*|mktemp:mktemp:*) fail=yes ;;
esac
if [ "$fail" = yes ] && [ ! -e "$FIXTURE/fault-fired" ]; then
    touch "$FIXTURE/fault-fired"
    echo 'injected {name} error' >&2
    exit 1
fi
if [ "{name}" = mv ]; then
    case "$1 $2 $3" in
        *'/old/'*) ;; # rollback must remain available
        *)
            for arg do
                if [ "$arg" = "$FIXTURE/data/lan-ntp-server/$FAIL_COMMIT" ] && [ -n "$FAIL_COMMIT" ] && [ ! -e "$FIXTURE/fault-fired" ]; then
                    touch "$FIXTURE/fault-fired"; exit 1
                fi
            done ;;
    esac
fi
exec {shlex.quote(real)} "$@"
''')

    def stub(self, name, body):
        path = self.root / "stubs" / name
        path.write_text("#!/bin/sh\n" + body + "\n")
        path.chmod(0o755)

    def manifest(self, binaries):
        return "".join(f"{digest(binaries['bin/' + name])}  {self.dir}/bin/{name}\n"
                       for name in ("chronyd", "chronyc")).encode()

    def run_installer(self, fault="", fail_commit=""):
        source = SCRIPT.read_text()
        # Rewrite every device path, including paths embedded in generated helpers/units.
        for prefix in ("/data/", "/etc/", "/run/"):
            source = source.replace(prefix, str(self.root) + prefix)
        pins = {
            "efae2e04c60f675126b0251c391f7df23e87e8c776445516a282f5b7bba4769a": digest(self.archive),
            "8b49e06c6a13954ac437ca3859a1c923206dfd6b8dc4279d5a207111c3ee71e2": digest(self.new["chronyd"]),
            "943a851395b3f2012bbecff0fd7606c6681b175a6e70aa95eac7c4ef2807eb83": digest(self.new["chronyc"]),
        }
        for old, new in pins.items():
            self.assertIn(old, source)
            source = source.replace(old, new)
        path = self.root / "installer.sh"
        path.write_text(source)
        return subprocess.run([BASH, str(path)], env=self.env | {"FAULT": fault, "FAIL_COMMIT": fail_commit},
                              cwd=self.root, capture_output=True, text=True, timeout=15)

    def assert_old_install(self):
        for name, data in self.old.items():
            self.assertEqual(data, (self.dir / name).read_bytes(), name)
            self.assertEqual(0o755 if name.startswith("bin/") else 0o644,
                             (self.dir / name).stat().st_mode & 0o777, name)
        checked = subprocess.run([str(self.root / "stubs/sha256sum"),
                                  "-c", "--strict", str(self.dir / "chrony.sha256")],
                                 cwd=self.root, env=self.env, capture_output=True, timeout=5)
        self.assertEqual(0, checked.returncode, checked.stderr)
        self.assertFalse((self.root / "systemctl.log").exists())

    def test_download_failure_preserves_working_install_and_start_hashes(self):
        result = self.run_installer("download")
        self.assertNotEqual(0, result.returncode)
        self.assert_old_install()

    def test_compatibility_failure_preserves_working_install(self):
        self.new["chronyd"] = b"#!/bin/sh\nexit 1\n"
        (self.root / "payload/chronyd").write_bytes(self.new["chronyd"])
        self.assertNotEqual(0, self.run_installer().returncode)
        self.assert_old_install()

    def test_bad_archive_preserves_working_install(self):
        (self.root / "archive").write_bytes(b"corrupt download")
        self.assertNotEqual(0, self.run_installer().returncode)
        self.assert_old_install()

    def test_wrong_extracted_binary_preserves_working_install(self):
        (self.root / "payload/chronyc").write_bytes(b"#!/bin/sh\necho wrong-binary\n")
        self.assertNotEqual(0, self.run_installer().returncode)
        self.assert_old_install()

    def test_staging_and_backup_errors_preserve_working_install(self):
        for fault in ("extract", "install", "backup", "hash-write", "stage-rename", "mktemp"):
            with self.subTest(fault=fault):
                (self.root / "fault-fired").unlink(missing_ok=True)
                self.assertNotEqual(0, self.run_installer(fault).returncode)
                self.assert_old_install()
                if fault != "extract":
                    self.assertTrue((self.root / "fault-fired").exists(), fault)

    def test_each_commit_write_failure_rolls_back_and_can_retry(self):
        for name in ("bin/chronyd", "bin/chronyc", "chrony.sha256", "chrony-copyright"):
            with self.subTest(name=name):
                (self.root / "fault-fired").unlink(missing_ok=True)
                self.assertNotEqual(0, self.run_installer(fail_commit=name).returncode)
                self.assert_old_install()
        result = self.run_installer()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assert_new_install()

    def assert_new_install(self):
        for name, data in self.new.items():
            self.assertEqual(data, (self.dir / "bin" / name).read_bytes())
            self.assertEqual(0o755, (self.dir / "bin" / name).stat().st_mode & 0o777)
        expected = self.manifest({"bin/" + name: data for name, data in self.new.items()})
        self.assertEqual(expected, (self.dir / "chrony.sha256").read_bytes())

    def test_success_and_unchanged_retry_do_not_download_again(self):
        self.assertEqual(0, self.run_installer().returncode)
        self.assert_new_install()
        before = {name: (self.dir / name).stat().st_mtime_ns for name in self.old}
        self.assertEqual(0, self.run_installer("download").returncode)
        self.assertEqual("download\n", (self.root / "downloads").read_text())
        self.assertEqual(before, {name: (self.dir / name).stat().st_mtime_ns for name in self.old})

    def test_first_install_and_mirror_fallback(self):
        for name in self.old:
            (self.dir / name).unlink()
        result = self.run_installer("first-mirror")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assert_new_install()
        self.assertEqual("download\ndownload\n", (self.root / "downloads").read_text())

    def test_first_install_commit_failure_leaves_no_partial_files_and_can_retry(self):
        for name in self.old:
            (self.dir / name).unlink()
        result = self.run_installer(fail_commit="chrony.sha256")
        self.assertNotEqual(0, result.returncode)
        for name in self.old:
            self.assertFalse((self.dir / name).exists(), name)
        result = self.run_installer()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assert_new_install()

    def test_missing_start_hashes_are_repaired_without_downloading(self):
        self.assertEqual(0, self.run_installer().returncode)
        before = (self.dir / "bin/chronyd").stat().st_mtime_ns
        (self.dir / "chrony.sha256").unlink()
        result = self.run_installer("download")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assert_new_install()
        self.assertEqual(before, (self.dir / "bin/chronyd").stat().st_mtime_ns)
        self.assertEqual("download\n", (self.root / "downloads").read_text())


if __name__ == "__main__":
    unittest.main()
