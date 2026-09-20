//go:build linux && amd64

package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"net/netip"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "router-speed-collector:", err)
		os.Exit(1)
	}
}

func run() error {
	interfaceName := flag.String("interface", "eth0", "physical LAN interface carrying the client's packets")
	clientText := flag.String("client", "192.168.233.10", "IPv4 address of the Windows computer")
	mapPath := flag.String("map", "/sys/fs/bpf/daed/routing_tuples_map", "pinned dae routing map (read only)")
	logPath := flag.String("log", "/var/log/daed/daed.log", "read final routes from this dae log; empty disables log reading")
	proxyIDsText := flag.String("proxy-outbounds", "", "explicitly trust these comma-separated map IDs as proxy without a final route log")
	inspect := flag.Bool("inspect", false, "print map metadata and aggregate client outbound counts, then exit")
	statePath := flag.String("state-file", "", "atomically write counters to this file in a private directory; use /tmp/router-speed/status.json for RAM-only state")
	quiet := flag.Bool("quiet", false, "suppress periodic counters on stdout (state-file output is unaffected)")
	flag.Parse()
	clientAddress, err := netip.ParseAddr(*clientText)
	if err != nil || !clientAddress.Is4() {
		return errors.New("--client must be an IPv4 address")
	}
	client := clientAddress.As4()
	proxyIDs := make(map[byte]bool)
	for _, text := range strings.Split(*proxyIDsText, ",") {
		text = strings.TrimSpace(text)
		if text == "" {
			continue
		}
		id, err := strconv.ParseUint(text, 10, 8)
		if err != nil || id < 2 || id >= 0xfb {
			return errors.New("--proxy-outbounds requires IDs between 2 and 250")
		}
		proxyIDs[byte(id)] = true
	}
	routing, err := openRouteMap(*mapPath)
	if err != nil {
		return err
	}
	defer func() { routing.close() }()
	encoder := json.NewEncoder(os.Stdout)
	signal.Ignore(syscall.SIGPIPE)
	if *inspect {
		result, err := routing.inspect(client, clientAddress.String())
		if err != nil {
			return err
		}
		return encoder.Encode(result)
	}
	state, err := openStateFile(*statePath)
	if err != nil {
		return err
	}
	instanceID, err := newInstanceID()
	if err != nil {
		return err
	}
	resolver := newRouteResolver(client, func(key flowKey) (byte, bool) { return routing.lookup(key) }, proxyIDs)
	tail := &logTail{path: *logPath}
	defer tail.close()
	logUnavailable := false
	pollLog := func() {
		now := time.Now()
		if err := tail.poll(func(line string) { resolver.consume(line, now) }); err != nil {
			if !logUnavailable {
				fmt.Fprintln(os.Stderr, "router-speed-collector: final route log unavailable; unresolved traffic remains unknown")
			}
			logUnavailable = true
		} else {
			logUnavailable = false
		}
	}
	pollLog()
	capture, err := openCapture(*interfaceName, client)
	if err != nil {
		return err
	}
	defer capture.close()
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	packets := make(chan packet, 8192)
	failures := make(chan error, 1)
	go capture.run(ctx, packets, failures)
	counts := newAccumulator(resolver.lookup, proxyIDs)
	counts.onNewConnection = resolver.forget
	resolver.onRoute = func(key flowKey) { delete(counts.cache, key) }
	counts.totals.Client, counts.totals.Interface = clientAddress.String(), *interfaceName
	counts.totals.MapID = routing.info.ID
	counts.totals.InstanceID = instanceID
	tick := time.NewTicker(time.Second)
	retry := time.NewTicker(100 * time.Millisecond)
	refresh := time.NewTicker(5 * time.Second)
	defer tick.Stop()
	defer retry.Stop()
	defer refresh.Stop()
	writeCounters := func(now time.Time) error {
		counts.totals.Timestamp = now.UnixMilli()
		counts.totals.DroppedPackets = capture.droppedPackets()
		if err := state.write(counts.totals); err != nil {
			return err
		}
		if *quiet {
			return nil
		}
		return encoder.Encode(counts.totals)
	}
	if err := writeCounters(time.Now()); err != nil {
		if errors.Is(err, syscall.EPIPE) {
			return nil
		}
		return err
	}
	mapUnavailable := false
	for {
		select {
		case <-ctx.Done():
			counts.invalidate()
			_ = writeCounters(time.Now())
			return nil
		case err := <-failures:
			return err
		case p := <-packets:
			counts.add(p, p.capturedAt)
		case now := <-retry.C:
			pollLog()
			counts.retry(now)
		case now := <-tick.C:
			if err := writeCounters(now); err != nil {
				if errors.Is(err, syscall.EPIPE) {
					return nil
				}
				return err
			}
		case <-refresh.C:
			updated, err := openRouteMap(*mapPath)
			if err != nil {
				if routing != nil {
					routing.close()
					routing = nil
					counts.invalidate()
					clear(resolver.routes)
					counts.totals.MapID = 0
				}
				if !mapUnavailable {
					fmt.Fprintln(os.Stderr, "router-speed-collector: routing map unavailable; unresolved traffic remains unknown")
				}
				mapUnavailable = true
				continue
			}
			mapUnavailable = false
			if routing == nil || updated.info.ID != routing.info.ID {
				routing.close()
				routing = updated
				counts.invalidate()
				clear(resolver.routes)
				counts.totals.MapID = routing.info.ID
			} else {
				updated.close()
			}
		}
	}
}
