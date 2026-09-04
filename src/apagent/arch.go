package main

import (
	"context"
	"fmt"
	"runtime"
	"strings"
	"time"
)

// machineToGOARCH maps a `uname -m` machine string to the Go architecture that can run on it.
// Empty means the machine string is unrecognized, which is reported rather than assumed safe.
func machineToGOARCH(machine string) string {
	switch strings.ToLower(strings.TrimSpace(machine)) {
	case "armv6l", "armv7l", "armv7", "armv8l", "arm":
		return "arm"
	case "aarch64", "arm64", "aarch64_be":
		return "arm64"
	case "x86_64", "amd64":
		return "amd64"
	case "i386", "i486", "i586", "i686", "x86":
		return "386"
	default:
		return ""
	}
}

// archGate decides whether a binary built for goarch may run on a host reporting machine.
// A recognized mismatch is refused; an unrecognized machine string is allowed through with a
// reason, because the sh wrapper is the primary gate and a new SKU must not be locked out here.
func archGate(machine, goarch string) (ok bool, reason string) {
	m := strings.TrimSpace(machine)
	if m == "" {
		return true, "host architecture unknown (uname unavailable), continuing on the wrapper's gate"
	}
	want := machineToGOARCH(m)
	if want == "" {
		return true, fmt.Sprintf("unrecognized machine %q, continuing: binary is %s", m, goarch)
	}
	// armv8l reports as 32-bit ARM userspace on a 64-bit core, so an arm binary is correct there.
	if want == goarch {
		return true, ""
	}
	return false, fmt.Sprintf("this build is %s but the host reports %s (needs a %s build)", goarch, m, want)
}

// hostMachine returns `uname -m`, or an empty string where uname is unavailable.
func hostMachine(ctx context.Context) string {
	out, err := runCommand(ctx, 3*time.Second, "uname", "-m")
	if err != nil {
		return ""
	}
	return strings.TrimSpace(out)
}

// hostKernel returns `uname -r`, or an empty string where uname is unavailable.
func hostKernel(ctx context.Context) string {
	out, err := runCommand(ctx, 3*time.Second, "uname", "-r")
	if err != nil {
		return ""
	}
	return strings.TrimSpace(out)
}

func buildGOARCH() string { return runtime.GOARCH }
