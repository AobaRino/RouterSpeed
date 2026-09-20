//go:build linux && amd64

package main

import (
	"bytes"
	"errors"
	"fmt"
	"runtime"
	"syscall"
	"unsafe"
)

const (
	sysBPF            = 321
	bpfMapLookupElem  = 1
	bpfMapGetNextKey  = 4
	bpfObjGet         = 7
	bpfObjGetInfoByFD = 15
	bpfReadOnly       = 8
)

type mapInfo struct {
	Type       uint32   `json:"type"`
	ID         uint32   `json:"mapId"`
	KeySize    uint32   `json:"keySize"`
	ValueSize  uint32   `json:"valueSize"`
	MaxEntries uint32   `json:"maxEntries"`
	Flags      uint32   `json:"flags"`
	Name       [16]byte `json:"-"`
}

type routeMap struct {
	fd   int
	info mapInfo
}

func bpfCall(command uintptr, attr unsafe.Pointer, size uintptr) (uintptr, error) {
	value, _, errno := syscall.Syscall(sysBPF, command, uintptr(attr), size)
	if errno != 0 {
		return 0, errno
	}
	return value, nil
}

func openRouteMap(path string) (*routeMap, error) {
	name, err := syscall.BytePtrFromString(path)
	if err != nil {
		return nil, err
	}
	attr := struct {
		Path      uint64
		FD        uint32
		FileFlags uint32
	}{Path: uint64(uintptr(unsafe.Pointer(name))), FileFlags: bpfReadOnly}
	fd, err := bpfCall(bpfObjGet, unsafe.Pointer(&attr), unsafe.Sizeof(attr))
	runtime.KeepAlive(name)
	if err != nil {
		return nil, fmt.Errorf("open read-only routing map: %w", err)
	}
	result := &routeMap{fd: int(fd)}
	infoAttr := struct {
		FD     uint32
		Length uint32
		Info   uint64
	}{FD: uint32(fd), Length: uint32(unsafe.Sizeof(result.info)), Info: uint64(uintptr(unsafe.Pointer(&result.info)))}
	_, err = bpfCall(bpfObjGetInfoByFD, unsafe.Pointer(&infoAttr), unsafe.Sizeof(infoAttr))
	runtime.KeepAlive(result)
	if err != nil {
		result.close()
		return nil, fmt.Errorf("read map metadata: %w", err)
	}
	if result.info.KeySize != 40 || result.info.ValueSize != 36 {
		result.close()
		return nil, fmt.Errorf("unsupported routing map layout: key=%d value=%d (expected 40/36)", result.info.KeySize, result.info.ValueSize)
	}
	return result, nil
}

func (m *routeMap) close() {
	if m != nil && m.fd >= 0 {
		syscall.Close(m.fd)
		m.fd = -1
	}
}

func (m *routeMap) lookup(key flowKey) (byte, bool) {
	if m == nil || m.fd < 0 {
		return 0, false
	}
	var value [36]byte
	attr := struct {
		FD    uint32
		Pad   uint32
		Key   uint64
		Value uint64
		Flags uint64
	}{
		FD: uint32(m.fd), Key: uint64(uintptr(unsafe.Pointer(&key[0]))), Value: uint64(uintptr(unsafe.Pointer(&value[0])))}
	_, err := bpfCall(bpfMapLookupElem, unsafe.Pointer(&attr), unsafe.Sizeof(attr))
	runtime.KeepAlive(key)
	runtime.KeepAlive(value)
	if err != nil {
		return 0, false
	}
	return value[11], true
}

type inspection struct {
	mapInfo
	Client         string          `json:"client"`
	Entries        uint64          `json:"entries"`
	ClientEntries  uint64          `json:"clientEntries"`
	OutboundCounts map[byte]uint64 `json:"outboundCounts"`
	Truncated      bool            `json:"truncated"`
}

func (m *routeMap) inspect(client [4]byte, clientText string) (inspection, error) {
	result := inspection{mapInfo: m.info, Client: clientText, OutboundCounts: make(map[byte]uint64)}
	var key, next flowKey
	var keyPointer uint64
	clientMapped := ipv4Mapped(client)
	// Concurrent changes can restart GET_NEXT_KEY iteration. Bound the scan.
	limit := uint64(m.info.MaxEntries)*2 + 1
	if limit > 2_000_000 {
		limit = 2_000_000
	}
	seen := make(map[flowKey]bool)
	for iteration := uint64(0); iteration < limit; iteration++ {
		attr := struct {
			FD   uint32
			Pad  uint32
			Key  uint64
			Next uint64
		}{
			FD: uint32(m.fd), Key: keyPointer, Next: uint64(uintptr(unsafe.Pointer(&next[0])))}
		_, err := bpfCall(bpfMapGetNextKey, unsafe.Pointer(&attr), unsafe.Sizeof(attr))
		runtime.KeepAlive(key)
		runtime.KeepAlive(next)
		if errors.Is(err, syscall.ENOENT) {
			return result, nil
		}
		if err != nil {
			return result, fmt.Errorf("iterate map: %w", err)
		}
		if !seen[next] {
			seen[next] = true
			result.Entries++
			if bytes.Equal(next[0:16], clientMapped) {
				if outbound, ok := m.lookup(next); ok {
					result.ClientEntries++
					result.OutboundCounts[outbound]++
				}
			}
		}
		key = next
		keyPointer = uint64(uintptr(unsafe.Pointer(&key[0])))
	}
	result.Truncated = true
	return result, nil
}
