# RouterSpeed collector

Read-only Linux amd64 collector for the Windows RouterSpeed bar. It binds an
AF_PACKET socket to the physical LAN interface, filters one IPv4 client in the
kernel, and emits cumulative JSON counters once per second. No routing rules,
daemon settings, firewall rules, or BPF map entries are changed. No packet data,
destinations, domain names, node names, or copied logs are written to disk or
printed. In-memory capture is truncated to 256 bytes per packet.

```
router-speed-collector --interface eth0 --client 192.168.1.10
router-speed-collector --client 192.168.1.10 --inspect
```

The addresses and interface above are examples; use the Windows computer's
actual IPv4 address and the router interface carrying its LAN traffic. The
packet-capture implementation is supported and verified only on Linux amd64.

The default map is `/sys/fs/bpf/daed/routing_tuples_map`; it must have 40-byte
keys and 36-byte values. The default final-route log is
`/var/log/daed/daed.log`. Override these with `--map` and `--log`.

Final dae logs determine direct/proxy routing because `dial_mode: domain` can
reroute connections in userspace. `dialer=direct` or `outbound=direct` means
direct. An exact `outbound=proxy` with a nonempty non-direct dialer means proxy.
Current map outbound 0 establishes direct even if an older proxy log exists;
final logs take precedence over positive map IDs, which alone remain unknown by
default. `--proxy-outbounds=2` is an explicit opt-in for configurations where
that ID is verified to always proxy; it should not be used with userspace
rerouting. Missing routes wait briefly (up to 2 seconds), then count as unknown.
Unknown traffic is never silently folded into direct traffic.

Counter fields are `instanceId` (a fresh random identifier for each process),
`timestamp` (Unix milliseconds), `directDown`, `directUp`,
`proxyDown`, `proxyUp`, `unknownDown`, `unknownUp`, `droppedPackets`, `client`,
`interface`, and `mapId`. Byte counters include IPv4 headers and retransmissions;
they approximate network usage and differ from downloaded file sizes. Packet
offloads, traffic outside this interface, and capture drops can affect totals.
Only public-remote IPv4 TCP/UDP traffic is included; IPv6, LAN/router traffic,
multicast, and router-local DNS forwarding are excluded. Fragments whose ports
are unavailable count as unknown. No IPv6 split is implemented.

Map replacement is detected every five seconds. Log rotation and truncation
are followed automatically, with at most the last 4 MiB read at startup. Flow,
route, SYN, and pending caches are bounded. Active final routes refresh their
expiry; a new TCP SYN sequence invalidates old tuple classifications. Kernel
receive timestamps keep delayed capture processing from deleting a final log
already produced by that same SYN. Route
changes in the log invalidate classification cache entries. Some packets can
remain unknown when logs are absent, expired, or cannot be matched to the
original destination tuple.

Requires root (or equivalent packet-capture and BPF-read privileges), but only
performs map lookup, map metadata inspection, and packet/log reads. `--inspect`
prints only map metadata and aggregate outbound counts for the selected client.
The process exits on SIGINT/SIGTERM or a broken stdout stream; it opens no
listening network port.

For a single service shared by LuCI and the Windows bar, run:

```
router-speed-collector --interface eth0 --client 192.168.1.10 --state-file /tmp/router-speed/status.json --quiet
```

`--state-file` atomically replaces the latest complete JSON snapshot once per
second, using a 0600 temporary file in the same private directory. A missing
directory is created with mode 0700; an existing directory must already have
mode 0700. The `/tmp/router-speed` location keeps snapshots in router RAM.
`--quiet` suppresses periodic stdout output; without it, stdout stays enabled.
No listener is added. The last snapshot remains after shutdown so readers can
detect staleness from `timestamp`. Readers must reset rate calculations when
`instanceId` changes, even if the new byte counters exceed the previous values.

Build with Go 1.22 or newer, without external modules:

```
go test ./...
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o ../router-files/usr/libexec/router-speed-collector .
```

Run these commands from `collector/` in a POSIX shell. The root README includes
PowerShell build commands. The output path stages the binary alongside the
router deployment templates; it does not install or start any service.

Portable parser, classification, race, bounded-cache, and log-tail tests also
run on Windows. Packet and BPF syscalls are isolated in Linux amd64 files.
