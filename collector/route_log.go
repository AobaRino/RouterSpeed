package main

import (
	"bufio"
	"encoding/binary"
	"errors"
	"io"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"time"
)

type routeRecord struct {
	class   trafficClass
	seenAt  time.Time
	expires time.Time
}

type routeResolver struct {
	client         [4]byte
	routes         map[flowKey]routeRecord
	mapLookup      func(flowKey) (byte, bool)
	proxyOutbounds map[byte]bool
	maxRoutes      int
	onRoute        func(flowKey)
}

func newRouteResolver(client [4]byte, mapLookup func(flowKey) (byte, bool), proxyIDs map[byte]bool) *routeResolver {
	return &routeResolver{client: client, routes: make(map[flowKey]routeRecord), mapLookup: mapLookup, proxyOutbounds: proxyIDs, maxRoutes: 8192}
}

func (r *routeResolver) lookup(key flowKey) (trafficClass, bool) {
	// Kernel DIRECT bypasses userspace, so an older final route log must not
	// override a current direct map entry after a UDP tuple is reused.
	outbound, mapFound := r.mapLookup(key)
	if mapFound && outbound == 0 {
		return direct, true
	}
	if record, ok := r.routes[key]; ok {
		if time.Now().Before(record.expires) {
			ttl := 10 * time.Minute
			if key[36] == 17 {
				ttl = 30 * time.Second
			}
			record.expires = time.Now().Add(ttl)
			r.routes[key] = record
			return record.class, true
		}
		delete(r.routes, key)
	}
	if mapFound {
		class := classifyOutbound(outbound, r.proxyOutbounds)
		if class != unknown {
			return class, true
		}
	}
	return unknown, false
}

func (r *routeResolver) forget(key flowKey, capturedAt time.Time) {
	// The reader may have already consumed the log produced by this SYN.
	// Keep records observed after the packet arrived at the collector.
	if record, ok := r.routes[key]; ok && record.seenAt.Before(capturedAt) {
		delete(r.routes, key)
	}
}

func (r *routeResolver) consume(line string, now time.Time) {
	key, class, ok := parseRouteLog(line, r.client)
	if !ok {
		return
	}
	if len(r.routes) >= r.maxRoutes {
		for old, record := range r.routes {
			if !now.Before(record.expires) {
				delete(r.routes, old)
			}
		}
		if len(r.routes) >= r.maxRoutes {
			for old := range r.routes {
				delete(r.routes, old)
				break
			}
		}
	}
	ttl := 10 * time.Minute
	if key[36] == 17 {
		ttl = 30 * time.Second
	}
	r.routes[key] = routeRecord{class: class, seenAt: now, expires: now.Add(ttl)}
	if r.onRoute != nil {
		r.onRoute(key)
	}
}

// parseLogFields accepts the logfmt subset emitted by logrus and drops malformed
// quoted fields. Values never leave this process.
func parseLogFields(line string) map[string]string {
	fields := make(map[string]string)
	for len(line) > 0 {
		line = strings.TrimLeft(line, " \t\r\n")
		equals := strings.IndexByte(line, '=')
		if equals <= 0 {
			break
		}
		key := line[:equals]
		if strings.ContainsAny(key, " \t") {
			break
		}
		line = line[equals+1:]
		value := ""
		if strings.HasPrefix(line, "\"") {
			end := 1
			for end < len(line) {
				if line[end] == '\\' {
					end += 2
					continue
				}
				if line[end] == '"' {
					break
				}
				end++
			}
			if end >= len(line) {
				break
			}
			decoded, err := strconv.Unquote(line[:end+1])
			if err != nil {
				break
			}
			value, line = decoded, line[end+1:]
		} else {
			end := strings.IndexAny(line, " \t\r\n")
			if end < 0 {
				value, line = line, ""
			} else {
				value, line = line[:end], line[end:]
			}
		}
		fields[key] = value
	}
	return fields
}

func parseRouteLog(line string, client [4]byte) (flowKey, trafficClass, bool) {
	var key flowKey
	fields := parseLogFields(line)
	var protocol byte
	switch fields["network"] {
	case "tcp4":
		protocol = 6
	case "udp4":
		protocol = 17
	default:
		return key, unknown, false // In particular, skip udp4(DNS).
	}
	endpoints := strings.Split(fields["msg"], " <-> ")
	if len(endpoints) != 2 {
		return key, unknown, false
	}
	source, err := netip.ParseAddrPort(endpoints[0])
	if err != nil || !source.Addr().Is4() || source.Addr().As4() != client {
		return key, unknown, false
	}
	remoteText := fields["ip"]
	if remoteText == "" {
		remoteText = endpoints[1]
	}
	remote, err := netip.ParseAddrPort(remoteText)
	if err != nil || !remote.Addr().Is4() || !publicRemote(remote.Addr().As4()) {
		return key, unknown, false
	}
	class := unknown
	if fields["dialer"] == "direct" || fields["outbound"] == "direct" {
		class = direct
	} else if fields["outbound"] == "proxy" && fields["dialer"] != "" {
		class = proxy
	}
	if class == unknown {
		return key, unknown, false
	}
	copy(key[0:16], ipv4Mapped(client))
	copy(key[16:32], ipv4Mapped(remote.Addr().As4()))
	binary.BigEndian.PutUint16(key[32:34], source.Port())
	binary.BigEndian.PutUint16(key[34:36], remote.Port())
	key[36] = protocol
	return key, class, true
}

// logTail uses bounded reads and never copies logs to disk. os.SameFile detects
// rename/recreate rotation, while a reduced size detects copy-truncate rotation.
type logTail struct {
	path    string
	file    *os.File
	info    os.FileInfo
	offset  int64
	partial string
}

func (t *logTail) close() {
	if t.file != nil {
		t.file.Close()
		t.file = nil
	}
}

func (t *logTail) poll(consume func(string)) error {
	if t.path == "" {
		return nil
	}
	info, err := os.Stat(t.path)
	if err != nil {
		t.close()
		return err
	}
	if t.file == nil || !os.SameFile(t.info, info) || info.Size() < t.offset {
		t.close()
		file, err := os.Open(t.path)
		if err != nil {
			return err
		}
		t.file, t.info, t.offset, t.partial = file, info, 0, ""
		if info.Size() > 4*1024*1024 {
			t.offset = info.Size() - 4*1024*1024
			if _, err := t.file.Seek(t.offset, io.SeekStart); err != nil {
				return err
			}
			// The initial partial line is discarded below.
			t.partial = "\x00"
		}
	}
	buffer := make([]byte, 64*1024)
	// A busy or malicious log cannot starve packet processing indefinitely.
	for read := 0; read < 4*1024*1024; {
		n, err := t.file.Read(buffer)
		if n > 0 {
			t.offset += int64(n)
			read += n
			data := t.partial + string(buffer[:n])
			last := strings.LastIndexByte(data, '\n')
			if last >= 0 {
				scanner := bufio.NewScanner(strings.NewReader(data[:last+1]))
				scanner.Buffer(make([]byte, 4096), 128*1024)
				for scanner.Scan() {
					line := scanner.Text()
					if !strings.HasPrefix(line, "\x00") {
						consume(line)
					}
				}
				t.partial = data[last+1:]
			} else {
				t.partial = data
			}
			if len(t.partial) > 128*1024 {
				t.partial = "\x00"
			}
		}
		if errors.Is(err, io.EOF) {
			return nil
		}
		if err != nil {
			return err
		}
		if n == 0 {
			return nil
		}
	}
	return nil
}
