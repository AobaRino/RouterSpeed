package main

import (
	"encoding/binary"
	"testing"
	"time"
)

var testClient = [4]byte{192, 168, 233, 10}
var testRemote = [4]byte{8, 8, 8, 8}

func testFrame(up bool, protocol byte, tags int) []byte {
	frame := make([]byte, 14+4*tags+20+20)
	binary.BigEndian.PutUint16(frame[12:14], 0x0800)
	for tag := 0; tag < tags; tag++ {
		binary.BigEndian.PutUint16(frame[12+tag*4:14+tag*4], 0x8100)
		binary.BigEndian.PutUint16(frame[16+tag*4:18+tag*4], 0x0800)
	}
	ip := frame[14+4*tags:]
	ip[0], ip[9] = 0x45, protocol
	binary.BigEndian.PutUint16(ip[2:4], 1400)
	if up {
		copy(ip[12:16], testClient[:])
		copy(ip[16:20], testRemote[:])
		binary.BigEndian.PutUint16(ip[20:22], 50000)
		binary.BigEndian.PutUint16(ip[22:24], 443)
	} else {
		copy(ip[12:16], testRemote[:])
		copy(ip[16:20], testClient[:])
		binary.BigEndian.PutUint16(ip[20:22], 443)
		binary.BigEndian.PutUint16(ip[22:24], 50000)
	}
	return frame
}

func TestParseNormalizesDirectionsAndVLAN(t *testing.T) {
	for _, protocol := range []byte{6, 17} {
		up, ok := parsePacket(testFrame(true, protocol, 0), testClient)
		if !ok || !up.keyValid || up.direction != upload || up.bytes != 1400 {
			t.Fatal("invalid upload", up, ok)
		}
		if binary.BigEndian.Uint16(up.key[32:34]) != 50000 || binary.BigEndian.Uint16(up.key[34:36]) != 443 || up.key[36] != protocol {
			t.Fatal("wrong normalized key")
		}
		if up.key[10] != 255 || up.key[11] != 255 || up.key[12] != 192 || up.key[26] != 255 || up.key[27] != 255 {
			t.Fatal("not IPv4-mapped")
		}
		for tags := 0; tags <= 2; tags++ {
			down, ok := parsePacket(testFrame(false, protocol, tags), testClient)
			if !ok || down.key != up.key || down.direction != download || down.bytes != 1400 {
				t.Fatalf("tags=%d wrong reverse tuple", tags)
			}
		}
	}
}

func TestParserRejectsLocalOtherClientsAndMalformed(t *testing.T) {
	for _, remote := range [][4]byte{{10, 0, 0, 1}, {192, 168, 1, 1}, {172, 16, 0, 2}, {169, 254, 1, 2}, {127, 0, 0, 1}, {224, 0, 0, 1}, {255, 255, 255, 255}, {100, 64, 0, 2}, {0, 1, 2, 3}} {
		frame := testFrame(true, 6, 0)
		copy(frame[30:34], remote[:])
		if _, ok := parsePacket(frame, testClient); ok {
			t.Fatalf("included non-public address: %v", remote)
		}
	}
	frame := testFrame(true, 6, 0)
	frame[29] = 11
	if _, ok := parsePacket(frame, testClient); ok {
		t.Fatal("included another client")
	}
	for length := 0; length < 34; length++ {
		if _, ok := parsePacket(testFrame(true, 6, 0)[:length], testClient); ok {
			t.Fatalf("accepted truncated header length=%d", length)
		}
	}
	frame = testFrame(true, 6, 0)
	frame[14] = 0x44
	if _, ok := parsePacket(frame, testClient); ok {
		t.Fatal("accepted short IPv4 header")
	}
	frame = testFrame(true, 6, 0)
	binary.BigEndian.PutUint16(frame[16:18], 10)
	if _, ok := parsePacket(frame, testClient); ok {
		t.Fatal("accepted IP total length shorter than header")
	}
}

func TestFragmentsAndTruncatedTransportRemainUnknown(t *testing.T) {
	frame := testFrame(true, 6, 0)
	binary.BigEndian.PutUint16(frame[20:22], 1)
	p, ok := parsePacket(frame, testClient)
	if !ok || p.keyValid || p.bytes != 1400 {
		t.Fatal("fragment should retain byte count but no ports")
	}
	p, ok = parsePacket(testFrame(true, 6, 0)[:36], testClient)
	if !ok || p.keyValid || p.bytes != 1400 {
		t.Fatal("short transport header should remain unknown")
	}
}

func TestClassificationIsConservative(t *testing.T) {
	for _, test := range []struct {
		outbound byte
		trusted  map[byte]bool
		want     trafficClass
	}{
		{0, nil, direct}, {1, map[byte]bool{1: true}, unknown}, {2, nil, unknown}, {2, map[byte]bool{2: true}, proxy}, {3, map[byte]bool{2: true}, unknown}, {0xfe, map[byte]bool{0xfe: true}, unknown},
	} {
		if got := classifyOutbound(test.outbound, test.trusted); got != test.want {
			t.Fatalf("outbound %d got %d want %d", test.outbound, got, test.want)
		}
	}
}

func TestPendingRaceResolvesAndExpiresWithoutDirectFallback(t *testing.T) {
	now := time.Now()
	found := false
	a := newAccumulator(func(flowKey) (trafficClass, bool) { return proxy, found }, nil)
	p, _ := parsePacket(testFrame(true, 6, 0), testClient)
	a.add(p, now)
	p.direction = download
	a.add(p, now)
	if a.totals.ProxyUp != 0 || a.totals.UnknownUp != 0 || len(a.pending) != 1 {
		t.Fatal("map miss should wait")
	}
	found = true
	a.retry(now.Add(100 * time.Millisecond))
	if a.totals.ProxyUp != 1400 || a.totals.ProxyDown != 1400 || len(a.pending) != 0 {
		t.Fatal("pending bytes should resolve together")
	}
	found = false
	a.invalidate()
	p.direction = upload
	a.add(p, now)
	a.retry(now.Add(2 * time.Second))
	if a.totals.UnknownUp != 1400 || a.totals.DirectUp != 0 || len(a.pending) != 0 {
		t.Fatal("unresolved bytes should become unknown")
	}
}

func TestBoundsAndMapInvalidationPreserveBytes(t *testing.T) {
	a := newAccumulator(func(flowKey) (trafficClass, bool) { return unknown, false }, nil)
	a.maxFlows = 2
	now := time.Now()
	p, _ := parsePacket(testFrame(true, 17, 0), testClient)
	for i := 0; i < 5; i++ {
		p.key[32] = byte(i)
		a.add(p, now)
	}
	if len(a.pending) != 2 || a.totals.UnknownUp != 4200 {
		t.Fatal("pending cap did not bound memory or retain counts")
	}
	a.invalidate()
	if len(a.pending) != 0 || len(a.cache) != 0 || a.totals.UnknownUp != 7000 {
		t.Fatal("invalidation lost bytes")
	}
}

func TestNewSYNDefersAndInvalidatesButRetransmissionDoesNot(t *testing.T) {
	now := time.Now()
	invalidations := 0
	a := newAccumulator(func(flowKey) (trafficClass, bool) { return direct, true }, nil)
	a.onNewConnection = func(flowKey, time.Time) { invalidations++ }
	frame := testFrame(true, 6, 0)
	frame[47] = 2
	binary.BigEndian.PutUint32(frame[38:42], 123)
	p, ok := parsePacket(frame, testClient)
	if !ok || !p.newConnection || p.sequence != 123 {
		t.Fatal("SYN not detected")
	}
	a.add(p, now)
	if a.totals.DirectUp != 0 || len(a.pending) != 1 {
		t.Fatal("SYN must wait for tc")
	}
	a.retry(now.Add(10 * time.Millisecond))
	if a.totals.DirectUp != 0 {
		t.Fatal("SYN resolved before map grace period")
	}
	a.retry(now.Add(100 * time.Millisecond))
	a.add(p, now.Add(200*time.Millisecond))
	if invalidations != 1 || a.totals.DirectUp != 2800 {
		t.Fatal("retransmission invalidated route")
	}
	p.sequence++
	a.add(p, now.Add(300*time.Millisecond))
	if invalidations != 2 || len(a.pending) != 1 {
		t.Fatal("new connection did not invalidate tuple")
	}
}
