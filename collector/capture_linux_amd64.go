//go:build linux && amd64

package main

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"net"
	"runtime"
	"sync/atomic"
	"syscall"
	"time"
	"unsafe"
)

const (
	ethPAll          = 0x0300 // htons(ETH_P_ALL), on amd64.
	solPacket        = 263
	packetStatistics = 6
	soAttachFilter   = 26
	soTimestampNS    = 35 // SO_TIMESTAMPNS / SCM_TIMESTAMPNS on Linux amd64.
)

type packetCapture struct {
	fd      int
	dropped uint64
	client  [4]byte
}

func openCapture(interfaceName string, client [4]byte) (*packetCapture, error) {
	device, err := net.InterfaceByName(interfaceName)
	if err != nil {
		return nil, fmt.Errorf("find capture interface: %w", err)
	}
	// Protocol zero defers reception until options and the filter are ready.
	fd, err := syscall.Socket(syscall.AF_PACKET, syscall.SOCK_RAW|syscall.SOCK_CLOEXEC, 0)
	if err != nil {
		return nil, fmt.Errorf("open packet socket: %w", err)
	}
	capture := &packetCapture{fd: fd, client: client}
	if err := syscall.SetsockoptInt(fd, syscall.SOL_SOCKET, soTimestampNS, 1); err != nil {
		capture.close()
		return nil, fmt.Errorf("enable kernel packet timestamps: %w", err)
	}
	if err := attachClientFilter(fd, client); err != nil {
		capture.close()
		return nil, fmt.Errorf("attach client capture filter: %w", err)
	}
	_ = syscall.SetsockoptInt(fd, syscall.SOL_SOCKET, syscall.SO_RCVBUF, 4*1024*1024)
	if err := syscall.SetsockoptTimeval(fd, syscall.SOL_SOCKET, syscall.SO_RCVTIMEO, &syscall.Timeval{Sec: 1}); err != nil {
		capture.close()
		return nil, err
	}
	if err := syscall.Bind(fd, &syscall.SockaddrLinklayer{Protocol: ethPAll, Ifindex: device.Index}); err != nil {
		capture.close()
		return nil, fmt.Errorf("bind packet socket: %w", err)
	}
	return capture, nil
}

func (c *packetCapture) close() {
	if c != nil && c.fd >= 0 {
		syscall.Close(c.fd)
		c.fd = -1
	}
}

func (c *packetCapture) run(ctx context.Context, packets chan<- packet, failures chan<- error) {
	buffer := make([]byte, 256)
	control := make([]byte, syscall.CmsgSpace(16))
	for {
		if ctx.Err() != nil {
			return
		}
		n, controlLength, receiveFlags, _, err := syscall.Recvmsg(c.fd, buffer, control, 0)
		if err != nil {
			if errors.Is(err, syscall.EAGAIN) || errors.Is(err, syscall.EWOULDBLOCK) || errors.Is(err, syscall.EINTR) {
				continue
			}
			if ctx.Err() == nil {
				select {
				case failures <- fmt.Errorf("receive packet headers: %w", err):
				default:
				}
			}
			return
		}
		if n > len(buffer) {
			n = len(buffer)
		}
		if parsed, ok := parsePacket(buffer[:n], c.client); ok {
			messages, controlErr := syscall.ParseSocketControlMessage(control[:controlLength])
			if controlErr == nil && receiveFlags&syscall.MSG_CTRUNC == 0 {
				for _, message := range messages {
					if message.Header.Level == syscall.SOL_SOCKET && message.Header.Type == soTimestampNS && len(message.Data) >= 16 {
						seconds := int64(binary.LittleEndian.Uint64(message.Data[:8]))
						nanoseconds := int64(binary.LittleEndian.Uint64(message.Data[8:16]))
						if seconds > 0 && nanoseconds >= 0 && nanoseconds < int64(time.Second) {
							parsed.capturedAt = time.Unix(seconds, nanoseconds)
						}
					}
				}
			}
			if parsed.capturedAt.IsZero() {
				select {
				case failures <- errors.New("kernel receive timestamp missing or truncated"):
				default:
				}
				return
			}
			select {
			case packets <- parsed:
			default:
				atomic.AddUint64(&c.dropped, 1)
			}
		}
	}
}

func (c *packetCapture) droppedPackets() uint64 {
	var stats struct {
		Packets uint32
		Drops   uint32
	}
	length := uint32(unsafe.Sizeof(stats))
	_, _, errno := syscall.Syscall6(syscall.SYS_GETSOCKOPT, uintptr(c.fd), solPacket, packetStatistics, uintptr(unsafe.Pointer(&stats)), uintptr(unsafe.Pointer(&length)), 0)
	if errno == 0 {
		atomic.AddUint64(&c.dropped, uint64(stats.Drops))
	}
	return atomic.LoadUint64(&c.dropped)
}

type filterInstruction struct {
	code                  uint16
	k                     uint32
	trueLabel, falseLabel string
}

func attachClientFilter(fd int, client [4]byte) error {
	// Classic socket BPF rejects all other clients before copying packet data to
	// userspace. Accept at most 256 header bytes, including up to two VLAN tags.
	const (
		loadH = 0x28
		loadW = 0x20
		equal = 0x15
		ret   = 0x06
	)
	var source []filterInstruction
	labels := make(map[string]int)
	emit := func(code uint16, k uint32, yes, no string) {
		source = append(source, filterInstruction{code, k, yes, no})
	}
	label := func(name string) { labels[name] = len(source) }
	for depth := 0; depth <= 2; depth++ {
		name := fmt.Sprintf("ether%d", depth)
		label(name)
		emit(loadH, uint32(12+depth*4), "", "")
		emit(equal, 0x0800, fmt.Sprintf("ip%d", depth), fmt.Sprintf("tag%d", depth))
		label(fmt.Sprintf("tag%d", depth))
		if depth == 2 {
			emit(ret, 0, "", "")
			continue
		}
		emit(equal, 0x8100, fmt.Sprintf("ether%d", depth+1), fmt.Sprintf("tag88_%d", depth))
		label(fmt.Sprintf("tag88_%d", depth))
		emit(equal, 0x88a8, fmt.Sprintf("ether%d", depth+1), fmt.Sprintf("tag91_%d", depth))
		label(fmt.Sprintf("tag91_%d", depth))
		emit(equal, 0x9100, fmt.Sprintf("ether%d", depth+1), "reject")
	}
	ip := binary.BigEndian.Uint32(client[:])
	for depth := 0; depth <= 2; depth++ {
		label(fmt.Sprintf("ip%d", depth))
		emit(loadW, uint32(26+depth*4), "", "")
		emit(equal, ip, "accept", fmt.Sprintf("dst%d", depth))
		label(fmt.Sprintf("dst%d", depth))
		emit(loadW, uint32(30+depth*4), "", "")
		emit(equal, ip, "accept", "reject")
	}
	label("reject")
	emit(ret, 0, "", "")
	label("accept")
	emit(ret, 256, "", "")
	filters := make([]syscall.SockFilter, len(source))
	for i, instruction := range source {
		filters[i] = syscall.SockFilter{Code: instruction.code, K: instruction.k}
		if instruction.code == equal {
			jt, jf := labels[instruction.trueLabel]-i-1, labels[instruction.falseLabel]-i-1
			if jt < 0 || jt > 255 || jf < 0 || jf > 255 {
				return errors.New("invalid socket filter jump")
			}
			filters[i].Jt, filters[i].Jf = uint8(jt), uint8(jf)
		}
	}
	program := syscall.SockFprog{Len: uint16(len(filters)), Filter: &filters[0]}
	_, _, errno := syscall.Syscall6(syscall.SYS_SETSOCKOPT, uintptr(fd), syscall.SOL_SOCKET, soAttachFilter, uintptr(unsafe.Pointer(&program)), unsafe.Sizeof(program), 0)
	runtime.KeepAlive(filters)
	if errno != 0 {
		return errno
	}
	return nil
}
