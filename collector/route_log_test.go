package main

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

const proxyLog = `time="Sep 19 16:35:43" level=info msg="192.168.233.10:50000 <-> example.test:443" dialer="test node" ip="8.8.8.8:443" network=tcp4 outbound=proxy`

func TestParseFinalRouteLog(t *testing.T) {
	packet, _ := parsePacket(testFrame(true, 6, 0), testClient)
	for _, test := range []struct {
		line string
		want trafficClass
		ok   bool
	}{
		{proxyLog, proxy, true},
		{strings.ReplaceAll(proxyLog, `dialer="test node"`, `dialer=direct`), direct, true},
		{strings.ReplaceAll(proxyLog, "outbound=proxy", "outbound=direct"), direct, true},
		{strings.ReplaceAll(proxyLog, "outbound=proxy", "outbound=somegroup"), unknown, false},
		{strings.ReplaceAll(proxyLog, "tcp4", "udp4(DNS)"), unknown, false},
		{strings.ReplaceAll(proxyLog, "233.10", "233.11"), unknown, false},
		{strings.ReplaceAll(proxyLog, `ip="8.8.8.8:443"`, ""), unknown, false},
		{`msg="192.168.233.10:50000 <-> 8.8.8.8:443" dialer=direct network=tcp4 outbound=direct`, direct, true},
	} {
		key, class, ok := parseRouteLog(test.line, testClient)
		if ok != test.ok || class != test.want || (ok && key != packet.key) {
			t.Fatalf("route parse got class=%d ok=%t", class, ok)
		}
	}
}

func TestFinalRouteTakesPrecedenceAndSYNDoesNotEraseNewRecord(t *testing.T) {
	now := time.Now()
	r := newRouteResolver(testClient, func(flowKey) (byte, bool) { return 2, true }, nil)
	p, _ := parsePacket(testFrame(true, 6, 0), testClient)
	r.consume(proxyLog, now)
	if class, ok := r.lookup(p.key); !ok || class != proxy {
		t.Fatal("final proxy log should take precedence")
	}
	r.forget(p.key, now.Add(-time.Millisecond))
	if _, exists := r.routes[p.key]; !exists {
		t.Fatal("erased a log observed after the SYN")
	}
	r.forget(p.key, now.Add(time.Millisecond))
	if _, ok := r.lookup(p.key); ok {
		t.Fatal("stale log should be invalidated and positive map alone stays unknown")
	}
	positive := newRouteResolver(testClient, func(flowKey) (byte, bool) { return 2, true }, nil)
	if _, ok := positive.lookup(p.key); ok {
		t.Fatal("positive map ID alone must not mean proxy")
	}
}

func TestCurrentKernelDirectOverridesStaleProxyLog(t *testing.T) {
	r := newRouteResolver(testClient, func(flowKey) (byte, bool) { return 0, true }, nil)
	r.consume(proxyLog, time.Now())
	p, _ := parsePacket(testFrame(true, 6, 0), testClient)
	if class, ok := r.lookup(p.key); !ok || class != direct {
		t.Fatal("current kernel direct must override older userspace proxy route")
	}
}

func TestActiveRoutesRefreshAndNewLogsInvalidateCache(t *testing.T) {
	now := time.Now()
	r := newRouteResolver(testClient, func(flowKey) (byte, bool) { return 2, true }, nil)
	p, _ := parsePacket(testFrame(true, 6, 0), testClient)
	r.consume(proxyLog, now)
	old := r.routes[p.key]
	old.expires = now.Add(time.Second)
	r.routes[p.key] = old
	if _, ok := r.lookup(p.key); !ok || r.routes[p.key].expires.Before(now.Add(9*time.Minute)) {
		t.Fatal("active route expiry should refresh")
	}
	called := false
	r.onRoute = func(key flowKey) { called = key == p.key }
	r.consume(strings.ReplaceAll(proxyLog, "outbound=proxy", "outbound=direct"), now)
	if !called {
		t.Fatal("new log must invalidate cached classification")
	}
}

func TestLogTailPartialWritesTruncationAndRotation(t *testing.T) {
	path := filepath.Join(t.TempDir(), "routes.log")
	if err := os.WriteFile(path, []byte("first\npar"), 0600); err != nil {
		t.Fatal(err)
	}
	tail := &logTail{path: path}
	defer tail.close()
	var lines []string
	consume := func(line string) { lines = append(lines, line) }
	if err := tail.poll(consume); err != nil {
		t.Fatal(err)
	}
	file, err := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		t.Fatal(err)
	}
	_, err = file.WriteString("tial\n")
	file.Close()
	if err != nil {
		t.Fatal(err)
	}
	if err := tail.poll(consume); err != nil {
		t.Fatal(err)
	}
	if len(lines) != 2 || lines[1] != "partial" {
		t.Fatal("partial line lost")
	}
	if err := os.WriteFile(path, []byte("new\n"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := tail.poll(consume); err != nil {
		t.Fatal(err)
	}
	// Windows denies renaming an open file; Linux production permits it.
	// Retain the old FileInfo so the test still exercises identity detection.
	if runtime.GOOS == "windows" {
		tail.file.Close()
	}
	if err := os.Rename(path, path+".old"); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("rotated\n"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := tail.poll(consume); err != nil {
		t.Fatal(err)
	}
	if strings.Join(lines, ",") != "first,partial,new,rotated" {
		t.Fatal("rotation replayed or lost complete lines")
	}
}

func TestLogTailKeepsLinesAfterAnOverlongLine(t *testing.T) {
	path := filepath.Join(t.TempDir(), "routes.log")
	if err := os.WriteFile(path, []byte(strings.Repeat("A", 150000)+"\nafter\n"), 0600); err != nil {
		t.Fatal(err)
	}
	tail := &logTail{path: path}
	defer tail.close()
	var last string
	if err := tail.poll(func(line string) { last = line }); err != nil {
		t.Fatal(err)
	}
	if last != "after" {
		t.Fatalf("line after an overlong line was dropped; last = %.20q", last)
	}
}
