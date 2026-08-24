package main

import (
	"context"
	_ "embed"
	"flag"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"runtime/debug"
	"strconv"
	"strings"
	"syscall"
	"time"
)

var version = "dev"

// binaryVersionRaw holds the AP agent's CONTRACT version - an integer that is completely
// independent of the release version above.
//
// BUMP src/apagent/binary-version (by one) WHENEVER YOU CHANGE THE AGENT'S RUNTIME BEHAVIOR
// (anything under src/apagent/*.go that affects what the deployed binary actually does).
// Do NOT bump it for release-only rebuilds.
//
// Network Optimizer embeds the SAME file and compares the two to decide whether to prompt the user
// to redeploy. Keeping the value in one file means the Go binary and the .NET app can never
// disagree about the wire shape.
//
//go:embed binary-version
var binaryVersionRaw string

// binaryVersion returns the embedded agent contract version as an integer.
func binaryVersion() int {
	v, _ := strconv.Atoi(strings.TrimSpace(binaryVersionRaw))
	return v
}

// exitCannotRunHere is EX_CONFIG. It separates "this agent cannot run on this host" (wrong
// architecture, no hostapd) from a crash in the deploy tooling, which is what makes a support
// ticket answerable. Every other failure exits 1.
const exitCannotRunHere = 78

func main() {
	os.Exit(run())
}

func run() int {
	configPath := flag.String("config", defaultConfigPath, "Path to config file")
	listenIface := flag.String("listen-interface", "", "Interface to bind (default br0)")
	listenAddr := flag.String("listen-address", "", "Explicit address to bind, overrides -listen-interface")
	port := flag.Int("port", 0, "Listener port (default 8899)")
	tokenFile := flag.String("token-file", "", "File holding the bearer token")
	hostapdDir := flag.String("hostapd-dir", "", "hostapd control socket directory (default /var/run/hostapd)")
	syslogPath := flag.String("syslog", "", "Syslog file to probe for stahtd (default /var/log/messages)")
	fastInterval := flag.Int("fast-interval-ms", 0, "Fast RF poll interval in milliseconds (default 1000)")
	slowInterval := flag.Int("slow-interval", 0, "Slow identity poll interval in seconds (default 30)")
	ignoreFatal := flag.Bool("ignore-fatal-probes", false, "Start even when a fatal probe fails (off-device testing only)")
	showVersion := flag.Bool("version", false, "Print version and exit")
	showBinaryVersion := flag.Bool("binary-version", false, "Print the agent contract version (integer) and exit")
	flag.Parse()

	if *showVersion {
		fmt.Println(version)
		return 0
	}
	if *showBinaryVersion {
		fmt.Println(binaryVersion())
		return 0
	}

	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})))

	ctx := context.Background()

	// Architecture is gated before any other work, because everything after it assumes the binary
	// belongs on this host.
	machine := hostMachine(ctx)
	if ok, reason := archGate(machine, buildGOARCH()); !ok {
		slog.Error("unsupported architecture", "reason", reason, "machine", machine, "goarch", buildGOARCH())
		fmt.Fprintf(os.Stderr, "apagent: %s\n", reason)
		return exitCannotRunHere
	} else if reason != "" {
		slog.Warn("architecture gate inconclusive", "reason", reason)
	}

	cfg, err := loadConfig(*configPath, Overrides{
		ListenInterface: *listenIface,
		ListenAddress:   *listenAddr,
		Port:            *port,
		TokenFile:       *tokenFile,
		HostapdDir:      *hostapdDir,
		SyslogPath:      *syslogPath,
		FastIntervalMs:  *fastInterval,
		SlowInterval:    *slowInterval,
	})
	if err != nil {
		slog.Error("configuration failed", "error", err)
		fmt.Fprintf(os.Stderr, "apagent: %v\n", err)
		return 1
	}

	// Go's default GC grows the heap freely, which looks alarming in top on an AP that is also
	// serving clients, so the ceiling is explicit and configurable.
	debug.SetMemoryLimit(int64(cfg.MemoryLimitMB) << 20)

	board := readBoardInfo(cfg.BoardInfoPath)
	platform := PlatformInfo{
		Machine:       machine,
		KernelRelease: hostKernel(ctx),
		Model:         board["board.name"],
		ModelShort:    board["board.shortname"],
		Firmware:      readFirmwareVersion(cfg.FirmwarePath),
		GOARCH:        buildGOARCH(),
	}

	state := NewState(time.Now().UTC(), platform)

	table := NewTable(cfg.MaxTrackedClients, time.Duration(cfg.ClientTTLSeconds)*time.Second)
	ring := NewEventRing(cfg.EventBufferSize)
	collector := NewCollector(cfg, table, ring)
	state.AttachTelemetry(table, ring, collector)

	probes := runProbes(ctx, cfg)
	state.SetProbes(probes)
	logProbeSummary(probes)

	if failed, isFatal := probes.FatalFailure(); isFatal {
		if !*ignoreFatal {
			slog.Error("fatal probe did not resolve", "probe", failed.Name, "detail", failed.Detail)
			fmt.Fprintf(os.Stderr, "apagent: %s unavailable: %s\napagent: this host cannot run the AP agent\n",
				failed.Name, failed.Detail)
			return exitCannotRunHere
		}
		slog.Warn("starting with a failed fatal probe, -ignore-fatal-probes is set",
			"probe", failed.Name, "detail", failed.Detail)
	}

	ifaces := collectInterfaces()
	bindAddr, err := resolveBindAddress(cfg, ifaces)
	if err != nil {
		slog.Error("cannot resolve bind address", "error", err)
		fmt.Fprintf(os.Stderr, "apagent: %v\n", err)
		return 1
	}
	if isWildcardAddress(bindAddr) {
		slog.Warn("binding a wildcard address exposes client telemetry on every VLAN the AP holds",
			"address", bindAddr)
	}

	ln, err := net.Listen("tcp", net.JoinHostPort(bindAddr, strconv.Itoa(cfg.Port)))
	if err != nil {
		slog.Error("failed to bind listener", "address", bindAddr, "port", cfg.Port, "error", err)
		fmt.Fprintf(os.Stderr, "apagent: bind %s:%d: %v\n", bindAddr, cfg.Port, err)
		return 1
	}

	// Advertise what was actually bound; the configured port is not proof of the bound port.
	boundHost, boundPortStr, _ := net.SplitHostPort(ln.Addr().String())
	table.SetApMAC(managementMAC(ifaces, cfg.ListenInterface))
	boundPort, _ := strconv.Atoi(boundPortStr)
	state.SetListener(ListenerInfo{
		Interface: cfg.ListenInterface,
		Address:   boundHost,
		Port:      boundPort,
		TLS:       false,
		Auth:      "bearer",
	})

	collectCtx, stopCollectors := context.WithCancel(ctx)
	defer stopCollectors()
	collector.Apply(collectCtx, probes)
	collector.Start(collectCtx)

	srv := newServer(state, cfg.Token)
	serveErr := make(chan error, 1)
	go func() {
		if err := srv.Serve(ln); err != nil && err != http.ErrServerClosed {
			serveErr <- err
		}
	}()

	slog.Info("apagent running",
		"version", version,
		"binary_version", binaryVersion(),
		"address", boundHost,
		"port", boundPort,
		"interface", cfg.ListenInterface,
		"vaps", len(probes.Vaps),
		"radios", len(probes.Radios),
		"memory_limit_mb", cfg.MemoryLimitMB,
		"fast_interval_ms", cfg.FastIntervalMs,
		"slow_interval_seconds", cfg.SlowIntervalSeconds,
		"event_buffer", cfg.EventBufferSize,
	)

	probeTicker := time.NewTicker(time.Duration(cfg.ProbeInterval) * time.Second)
	defer probeTicker.Stop()

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGTERM, syscall.SIGINT)

	for {
		select {
		case sig := <-sigCh:
			// An AP is doing a job that matters more than our telemetry, so a stop is immediate.
			slog.Info("shutdown signal received", "signal", sig.String())
			stopCollectors()
			shutdownCtx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			srv.Shutdown(shutdownCtx)
			cancel()
			collector.Wait()
			return 0

		case err := <-serveErr:
			slog.Error("listener stopped", "error", err)
			return 1

		case <-probeTicker.C:
			// A firmware upgrade or a provision cycle can change what resolves under a running
			// agent, so capabilities are re-probed rather than fixed at startup. VAP names change
			// with it, which is why the collector is handed the new set rather than its own.
			refreshed := runProbes(ctx, cfg)
			state.SetProbes(refreshed)
			collector.Apply(collectCtx, refreshed)
		}
	}
}

func logProbeSummary(p ProbeSet) {
	for _, r := range p.Results {
		level := slog.LevelInfo
		if !r.Available {
			level = slog.LevelWarn
		}
		slog.Log(context.Background(), level, "capability probe",
			"probe", r.Name,
			"available", r.Available,
			"fatal", r.Fatal,
			"detail", r.Detail,
			"degrades", r.Degrades,
		)
	}
	slog.Info("probe summary",
		"vaps", strings.Join(p.Vaps, ","),
		"radios", strings.Join(p.Radios, ","),
		"unavailable", strings.Join(p.Unavailable(), ","),
		"control_surface_vaps", len(p.ControlSurface),
	)
}
