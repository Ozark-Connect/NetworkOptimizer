"""Exercise the real boot script and C# removal command using isolated shell stubs.

Run: python3 -m unittest discover -s tests/scripts -p 'test_pppoe_reconnect.py' -v
All gateway paths are replaced; no network, diagnostics or real services are used.
"""

import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "src/NetworkOptimizer.Web/Resources/PerfTweaks/40-pppoe-reconnect.sh"
SERVICE = ROOT / "src/NetworkOptimizer.Web/Services/PerfTweaksDeploymentService.cs"


class GatewayTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="pppoe-reconnect-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for directory in ("data/on_boot.d", "data/pppoe-reconnect", "etc/systemd/system", "etc/ppp/peers", "run", "stubs"):
            (self.root / directory).mkdir(parents=True)
        self.time = self.root / "data/pppoe-reconnect/time"
        self.time.write_text("03:30\n")
        self.timer = self.root / "etc/systemd/system/pppoe-reconnect.timer"
        self.unit = self.root / "etc/systemd/system/pppoe-reconnect.service"
        self.boot = self.root / "data/on_boot.d/40-pppoe-reconnect.sh"
        self.log = self.root / "commands"
        self.env = {"PATH": str(self.root / "stubs") + ":/usr/bin:/bin", "FIXTURE": str(self.root), "LC_ALL": "C"}
        self.stub("logger", "exit 0")
        self.stub("rm", '''
for arg do
    if [ -e "$FIXTURE/leave-service-file" ] && [ "$arg" = "$FIXTURE/etc/systemd/system/pppoe-reconnect.service" ]; then
        # Simulate a successful command that left a unit behind; exercise the final guard.
        continue
    fi
    case "$arg" in
        -*) ;;
        "$FIXTURE"/*) /bin/rm -rf "$arg" || exit ;;
        *) echo 'rm outside fixture rejected' >&2; exit 2 ;;
    esac
done
if [ -e "$FIXTURE/reactivate-after-delete" ] && [ ! -e "$FIXTURE/data/pppoe-reconnect" ]; then
    cat "$FIXTURE/reactivate-after-delete" > "$FIXTURE/state-pppoe-reconnect.service"
fi
''')
        self.stub("systemctl", '''
echo "$*" >> "$FIXTURE/commands"
case "$1" in
    daemon-reload)
        [ ! -e "$FIXTURE/fail-reload" ] || { echo 'injected reload error' >&2; exit 1; }
        cp "$FIXTURE/etc/systemd/system/pppoe-reconnect.timer" "$FIXTURE/loaded-timer" 2>/dev/null || :
        ;;
    restart|start)
        [ ! -e "$FIXTURE/fail-start" ] || { echo 'injected start error' >&2; exit 1; }
        touch "$FIXTURE/timer-active"
        cp "$FIXTURE/loaded-timer" "$FIXTURE/active-timer"
        ;;
    stop)
        shift
        for unit do
            if [ -e "$FIXTURE/fail-stop-$unit" ]; then echo "injected stop error: $unit" >&2; exit 1; fi
            [ ! -e "$FIXTURE/absent-$unit" ] || { echo 'unit not loaded' >&2; exit 5; }
            if [ ! -e "$FIXTURE/ignore-stop-$unit" ]; then echo inactive > "$FIXTURE/state-$unit"; fi
        done
        ;;
    show)
        property="$3"
        for unit do :; done
        case "$property" in
            NeedDaemonReload)
                if cmp -s "$FIXTURE/loaded-timer" "$FIXTURE/etc/systemd/system/pppoe-reconnect.timer"; then echo no; else echo yes; fi ;;
            LoadState)
                [ ! -e "$FIXTURE/fail-show" ] || exit 1
                if [ -e "$FIXTURE/absent-$unit" ]; then echo not-found; else echo loaded; fi ;;
            ActiveState)
                [ ! -e "$FIXTURE/fail-show" ] || { echo 'injected state query error' >&2; exit 1; }
                if [ -e "$FIXTURE/state-$unit" ]; then cat "$FIXTURE/state-$unit"; else echo inactive; fi ;;
            *) exit 2 ;;
        esac
        ;;
    is-active)
        for unit do :; done
        if [ -e "$FIXTURE/state-$unit" ]; then
            [ "$(cat "$FIXTURE/state-$unit")" = active ]
        else
            [ -e "$FIXTURE/timer-active" ]
        fi
        ;;
    *) echo 'unexpected systemctl call' >&2; exit 2 ;;
esac
''')

    def stub(self, name, body):
        path = self.root / "stubs" / name
        path.write_text("#!/bin/sh\n" + body + "\n")
        path.chmod(0o755)

    def isolated(self, source):
        for prefix in ("/data/", "/etc/", "/var/run/", "/run/", "/proc/"):
            # Avoid replacing /run inside the already replaced /var/run.
            source = source.replace(prefix, "@@" + prefix.strip("/").replace("/", "_") + "@@/")
        for prefix in ("/data/", "/etc/", "/var/run/", "/run/", "/proc/"):
            source = source.replace("@@" + prefix.strip("/").replace("/", "_") + "@@/", str(self.root) + prefix)
        return source

    def run_boot(self):
        self.boot.write_text(self.isolated(SCRIPT.read_text()))
        return subprocess.run(["/bin/bash", str(self.boot)], env=self.env, cwd=self.root,
                              text=True, capture_output=True, timeout=10)

    def run_removal(self):
        # Evaluate only the actual constant's C# string literals and its two known constants.
        # No copy of the removal logic is maintained in this test.
        declaration = SERVICE.read_text().split("private const string PppoeReconnectRemoveCommand =", 1)[1].split(";\n", 1)[0]
        strings = re.findall(r'\$?("(?:[^"\\]|\\.)*")', declaration)
        command = "".join(json.loads(s) for s in strings)
        command = command.replace("{OnBootDir}", "/data/on_boot.d").replace("{PppoeReconnectDir}", "/data/pppoe-reconnect")
        self.assertNotRegex(command, r"\{[A-Za-z]")
        return subprocess.run(["/bin/sh", "-c", self.isolated(command)], env=self.env, cwd=self.root,
                              text=True, capture_output=True, timeout=10)

    def marker(self, name, value=""):
        (self.root / name).write_text(value)

    def deployed(self):
        for path in (self.boot, self.unit, self.timer):
            path.write_text("synthetic deployed file\n")
        for unit in ("pppoe-reconnect.timer", "pppoe-reconnect.service"):
            self.marker("state-" + unit, "active\n")

    def assert_deployed(self):
        for path in (self.boot, self.unit, self.timer, self.time):
            self.assertTrue(path.exists(), str(path))

    def test_reload_failure_is_an_error_and_identical_retry_applies_schedule(self):
        self.assertEqual(0, self.run_boot().returncode)
        self.time.write_text("05:15\n")
        self.marker("fail-reload")
        result = self.run_boot()
        self.assertNotEqual(0, result.returncode, result.stderr)
        self.assertIn("reload", result.stderr)
        self.assertIn("03:30:00", (self.root / "active-timer").read_text())
        self.assertIn("05:15:00", self.timer.read_text())
        before = self.timer.stat().st_mtime_ns
        (self.root / "fail-reload").unlink()
        self.log.write_text("")
        self.assertEqual(0, self.run_boot().returncode)
        self.assertEqual(before, self.timer.stat().st_mtime_ns)
        self.assertIn("daemon-reload", self.log.read_text())
        self.assertIn("05:15:00", (self.root / "active-timer").read_text())

    def test_first_install_reload_failure_can_retry_unchanged_units(self):
        self.marker("fail-reload")
        self.assertNotEqual(0, self.run_boot().returncode)
        (self.root / "fail-reload").unlink()
        self.assertEqual(0, self.run_boot().returncode)
        self.assertIn("03:30:00", (self.root / "active-timer").read_text())

    def test_invalid_times_keep_default_and_never_become_commands(self):
        sentinel = self.root / "invalid-time-command"
        for time in ("24:00", "4:00", "04:00:00", f"04:00; touch {sentinel}", ""):
            with self.subTest(time=time):
                self.time.write_text(time)
                self.assertEqual(0, self.run_boot().returncode)
                self.assertIn("04:00:00", self.timer.read_text())
                self.assertFalse(sentinel.exists())

    def test_timer_start_failure_is_an_error_even_if_timer_was_active(self):
        self.assertEqual(0, self.run_boot().returncode)
        self.marker("fail-start")
        self.assertNotEqual(0, self.run_boot().returncode)

    def test_removal_stop_failure_preserves_files_and_visible_error(self):
        for unit in ("pppoe-reconnect.timer", "pppoe-reconnect.service"):
            with self.subTest(unit=unit):
                self.deployed()
                self.marker("fail-stop-" + unit)
                result = self.run_removal()
                self.assertNotEqual(0, result.returncode)
                self.assertNotIn("removed", result.stdout)
                self.assertIn("stop error", result.stderr)
                self.assert_deployed()
                (self.root / ("fail-stop-" + unit)).unlink()

    def test_removal_requires_confirmed_stopped_timer_and_service(self):
        for unit in ("pppoe-reconnect.timer", "pppoe-reconnect.service"):
            for state in ("active", "activating", "deactivating", "unknown"):
                with self.subTest(unit=unit, state=state):
                    self.deployed()
                    self.marker("ignore-stop-" + unit)
                    self.marker("state-" + unit, state + "\n")
                    result = self.run_removal()
                    self.assertNotEqual(0, result.returncode)
                    self.assertNotIn("removed", result.stdout)
                    self.assert_deployed()
                    (self.root / ("ignore-stop-" + unit)).unlink()

    def test_removal_state_query_error_preserves_files(self):
        self.deployed()
        self.marker("fail-show")
        result = self.run_removal()
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("removed", result.stdout)
        self.assert_deployed()

    def test_removal_final_guard_rechecks_service_state(self):
        for state in ("active", "activating", "deactivating", "unknown"):
            with self.subTest(state=state):
                self.deployed()
                self.marker("reactivate-after-delete", state + "\n")
                result = self.run_removal()
                self.assertNotEqual(0, result.returncode)
                self.assertNotIn("removed", result.stdout)
                self.assertIn("cleanup incomplete", result.stderr)

    def test_removal_final_guard_checks_service_file(self):
        self.deployed()
        self.marker("leave-service-file")
        result = self.run_removal()
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("removed", result.stdout)
        self.assertTrue(self.unit.exists())

    def test_removal_reload_failure_is_visible_and_keeps_data_for_retry(self):
        self.deployed()
        self.marker("fail-reload")
        result = self.run_removal()
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("removed", result.stdout)
        self.assertIn("injected reload error", result.stderr)
        self.assertTrue(self.time.exists())
        (self.root / "fail-reload").unlink()
        result = self.run_removal()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("removed\n", result.stdout)

    def test_removal_success_and_already_absent_units_are_idempotent(self):
        self.deployed()
        result = self.run_removal()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("removed\n", result.stdout)
        for path in (self.boot, self.unit, self.timer, self.time.parent):
            self.assertFalse(path.exists())
        for unit in ("pppoe-reconnect.timer", "pppoe-reconnect.service"):
            self.marker("absent-" + unit)
        result = self.run_removal()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("removed\n", result.stdout)


if __name__ == "__main__":
    unittest.main()
