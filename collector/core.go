package main

import (
	"encoding/binary"
	"net/netip"
	"time"
)

type flowKey [40]byte

type direction uint8

const (
	upload direction = iota
	download
)

type trafficClass uint8

const (
	unknown trafficClass = iota
	direct
	proxy
)

type packet struct {
	key           flowKey
	direction     direction
	bytes         uint64
	keyValid      bool
	newConnection bool
	sequence      uint32
	capturedAt    time.Time
}

// parsePacket retains only the normalized 5-tuple and IP byte count. It never
// retains packet payloads, DNS names, or individual destination statistics.
func parsePacket(frame []byte, client [4]byte) (packet, bool) {
	var result packet
	if len(frame) < 14 {
		return result, false
	}
	etherType := binary.BigEndian.Uint16(frame[12:14])
	ipOffset := 14
	for tags := 0; etherType == 0x8100 || etherType == 0x88a8 || etherType == 0x9100; tags++ {
		if tags >= 2 || len(frame) < ipOffset+4 {
			return result, false
		}
		etherType = binary.BigEndian.Uint16(frame[ipOffset+2 : ipOffset+4])
		ipOffset += 4
	}
	if etherType != 0x0800 || len(frame) < ipOffset+20 {
		return result, false
	}
	ip := frame[ipOffset:]
	headerLength := int(ip[0]&15) * 4
	if ip[0]>>4 != 4 || headerLength < 20 || len(ip) < headerLength {
		return result, false
	}
	totalLength := int(binary.BigEndian.Uint16(ip[2:4]))
	if totalLength < headerLength || (ip[9] != 6 && ip[9] != 17) {
		return result, false
	}
	source := [4]byte{ip[12], ip[13], ip[14], ip[15]}
	destination := [4]byte{ip[16], ip[17], ip[18], ip[19]}
	var remote [4]byte
	if source == client && destination != client {
		result.direction, remote = upload, destination
	} else if destination == client && source != client {
		result.direction, remote = download, source
	} else {
		return result, false
	}
	if !publicRemote(remote) {
		return result, false
	}
	result.bytes = uint64(totalLength)
	// Noninitial IP fragments have no transport header. Count them as unknown
	// instead of interpreting arbitrary fragment bytes as port numbers.
	if binary.BigEndian.Uint16(ip[6:8])&0x1fff != 0 || totalLength < headerLength+4 || len(ip) < headerLength+4 {
		return result, true
	}
	copy(result.key[0:16], ipv4Mapped(client))
	copy(result.key[16:32], ipv4Mapped(remote))
	ports := ip[headerLength : headerLength+4]
	if result.direction == upload {
		copy(result.key[32:36], ports)
	} else {
		copy(result.key[32:34], ports[2:4])
		copy(result.key[34:36], ports[0:2])
	}
	result.key[36] = ip[9]
	result.keyValid = true
	if ip[9] == 6 && result.direction == upload && totalLength >= headerLength+14 && len(ip) >= headerLength+14 {
		result.newConnection = ip[headerLength+13]&0x12 == 0x02
		result.sequence = binary.BigEndian.Uint32(ip[headerLength+4 : headerLength+8])
	}
	return result, true
}

func ipv4Mapped(ip [4]byte) []byte {
	result := make([]byte, 16)
	result[10], result[11] = 0xff, 0xff
	copy(result[12:], ip[:])
	return result
}

func publicRemote(ip [4]byte) bool {
	address := netip.AddrFrom4(ip)
	if !address.IsGlobalUnicast() || address.IsPrivate() || address.IsLoopback() || address.IsLinkLocalUnicast() {
		return false
	}
	// Also exclude this-network, shared-address space, reserved Class E,
	// protocol-assignment networks, and the limited broadcast address.
	return ip[0] != 0 && ip[0] < 240 && !(ip[0] == 100 && ip[1]&0xc0 == 64) &&
		!(ip[0] == 192 && ip[1] == 0 && ip[2] == 0) && !(ip[0] == 198 && ip[1]&0xfe == 18)
}

func classifyOutbound(outbound byte, proxyOutbounds map[byte]bool) trafficClass {
	if outbound == 0 {
		return direct
	}
	// These reserved dae outbound IDs must never become proxy by configuration.
	if outbound == 1 || outbound >= 0xfb {
		return unknown
	}
	if proxyOutbounds[outbound] {
		return proxy
	}
	return unknown
}

type counters struct {
	InstanceID     string `json:"instanceId"`
	Timestamp      int64  `json:"timestamp"`
	DirectDown     uint64 `json:"directDown"`
	DirectUp       uint64 `json:"directUp"`
	ProxyDown      uint64 `json:"proxyDown"`
	ProxyUp        uint64 `json:"proxyUp"`
	UnknownDown    uint64 `json:"unknownDown"`
	UnknownUp      uint64 `json:"unknownUp"`
	DroppedPackets uint64 `json:"droppedPackets"`
	Client         string `json:"client"`
	Interface      string `json:"interface"`
	MapID          uint32 `json:"mapId"`
}

func (c *counters) add(class trafficClass, up, down uint64) {
	switch class {
	case direct:
		c.DirectUp += up
		c.DirectDown += down
	case proxy:
		c.ProxyUp += up
		c.ProxyDown += down
	default:
		c.UnknownUp += up
		c.UnknownDown += down
	}
}

type cacheEntry struct {
	class   trafficClass
	expires time.Time
}

type pendingEntry struct {
	up, down  uint64
	deadline  time.Time
	notBefore time.Time
}

type flowLookup func(flowKey) (trafficClass, bool)

type synEntry struct {
	sequence uint32
	expires  time.Time
}

type accumulator struct {
	totals          counters
	cache           map[flowKey]cacheEntry
	pending         map[flowKey]pendingEntry
	proxyOutbounds  map[byte]bool
	lookup          flowLookup
	maxFlows        int
	syns            map[flowKey]synEntry
	onNewConnection func(flowKey, time.Time)
}

func newAccumulator(lookup flowLookup, proxyOutbounds map[byte]bool) *accumulator {
	return &accumulator{cache: make(map[flowKey]cacheEntry), pending: make(map[flowKey]pendingEntry),
		lookup: lookup, proxyOutbounds: proxyOutbounds, maxFlows: 8192, syns: make(map[flowKey]synEntry)}
}

func (a *accumulator) add(p packet, now time.Time) {
	var up, down uint64
	if p.direction == upload {
		up = p.bytes
	} else {
		down = p.bytes
	}
	if !p.keyValid {
		a.totals.add(unknown, up, down)
		return
	}
	if p.newConnection {
		previous, seen := a.syns[p.key]
		if !seen || previous.sequence != p.sequence || !now.Before(previous.expires) {
			delete(a.cache, p.key)
			if pending, ok := a.pending[p.key]; ok {
				a.totals.add(unknown, pending.up, pending.down)
				delete(a.pending, p.key)
			}
			if a.onNewConnection != nil {
				a.onNewConnection(p.key, now)
			}
			// AF_PACKET may observe this SYN before tc replaces a reused tuple.
			// Defer classification to the next retry instead of trusting an old
			// map entry or a log from the previous connection immediately.
			if len(a.pending) < a.maxFlows {
				a.pending[p.key] = pendingEntry{deadline: now.Add(2 * time.Second), notBefore: now.Add(50 * time.Millisecond)}
			}
		}
		if len(a.syns) >= a.maxFlows {
			for old := range a.syns {
				delete(a.syns, old)
				break
			}
		}
		a.syns[p.key] = synEntry{sequence: p.sequence, expires: now.Add(5 * time.Minute)}
	}
	if cached, ok := a.cache[p.key]; ok && now.Before(cached.expires) {
		a.totals.add(cached.class, up, down)
		return
	}
	if pending, ok := a.pending[p.key]; ok {
		pending.up += up
		pending.down += down
		a.pending[p.key] = pending
		return
	}
	if class, found := a.lookup(p.key); found {
		a.remember(p.key, class, now)
		a.totals.add(class, up, down)
		return
	}
	if len(a.pending) >= a.maxFlows {
		a.totals.add(unknown, up, down)
		return
	}
	a.pending[p.key] = pendingEntry{up: up, down: down, deadline: now.Add(2 * time.Second)}
}

func (a *accumulator) retry(now time.Time) {
	for key, pending := range a.pending {
		if now.Before(pending.notBefore) {
			continue
		}
		if class, found := a.lookup(key); found {
			a.totals.add(class, pending.up, pending.down)
			a.remember(key, class, now)
			delete(a.pending, key)
		} else if !now.Before(pending.deadline) {
			a.totals.add(unknown, pending.up, pending.down)
			delete(a.pending, key)
		}
	}
	for key, syn := range a.syns {
		if !now.Before(syn.expires) {
			delete(a.syns, key)
		}
	}
	for key, cached := range a.cache {
		if !now.Before(cached.expires) {
			delete(a.cache, key)
		}
	}
}

func (a *accumulator) remember(key flowKey, class trafficClass, now time.Time) {
	if len(a.cache) >= a.maxFlows {
		// Arbitrary eviction keeps memory bounded without retaining an extra
		// address-bearing eviction queue. A miss only costs a future map lookup.
		for old := range a.cache {
			delete(a.cache, old)
			break
		}
	}
	a.cache[key] = cacheEntry{class: class, expires: now.Add(2 * time.Second)}
}

func (a *accumulator) invalidate() {
	for _, pending := range a.pending {
		a.totals.add(unknown, pending.up, pending.down)
	}
	clear(a.cache)
	clear(a.pending)
	clear(a.syns)
}
