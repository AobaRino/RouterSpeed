# RouterSpeed router control

Standard-library-only Linux amd64 helper for the native LuCI menu and the
Windows read-only API. It does not change network, firewall, routing, or daed
configuration. All management subprocesses have fixed executable paths and
validated arguments; the root router password is never stored or exported.

Build both this helper and the separate collector, then install the files in
`../router-files/` at the corresponding router paths. The two binaries, rpcd and
CGI wrappers, and the init script need 0755. Adjust the client IPv4 address and
LAN capture interface in `/etc/config/router_speed` for your network. Run
`/usr/libexec/router-speed-control --init` once: it creates a random 256-bit
credential at `/etc/router-speed/token` with 0600 permissions, preserves an
existing valid credential, and prints no credential. The credential directory
is private (0700). UCI package `router_speed` contains only service settings.

The init script starts the separate collector with `--state-file` and `--quiet`.
Snapshots remain in router RAM under `/tmp/router-speed`, never router flash.
The CGI does not start or stop the collector or expose credential management.
The existing uhttpd port remains unchanged. If it uses HTTP, transport is not
encrypted; use a trusted LAN or configure HTTPS separately. Credentials protect
only the specified client's read-only counters. The verified deployment target
is Linux amd64 with compatible LuCI, rpcd, UCI, uhttpd, procd, and dae / daed.

## LuCI RPC

Object `router-speed` implements:

- `status {}`: `{enabled, available, sampleAgeMs, counters, error?}`.
- `config {}`: `{enabled, client, interface, hasToken, interfaces}`.
- `save {enabled:boolean,client:string,interface:string}`:
  `{ok:true,config:{...}}`; affects only the collector service.
- `credentials {}` and `rotate_token {}`:
  `{client,apiPath:'/cgi-bin/router-speed',token}`.

The rpcd ACL grants status/config as read operations. Save, credential display,
and credential rotation require write permission in the existing authenticated
LuCI session. CGI credentials do not grant ubus or LuCI access. Rotation takes
effect on the next request and requires updating the Windows credential.

## Windows API

`GET /cgi-bin/router-speed` requires `Authorization: Bearer <token>` and an
exact IPv4 source match with the configured client. Forwarded headers, cookies,
and query-string tokens are not accepted. Wrong sources return 403; missing or
wrong tokens return 401; disabled, stale (more than five seconds), mismatched,
or unavailable collector data returns 503. Successful responses are the bare
cumulative counters JSON, including `instanceId`. All responses use no-store.

LuCI builds this export using the page's own origin, not a server-supplied Host:

```json
{"RouterUrl":"http://192.168.1.1/cgi-bin/router-speed","Client":"192.168.1.10","Token":"generated credential"}
```

These addresses and the token above are placeholders. Use the export generated
by your own authenticated LuCI session; `--init` creates the random credential.

Build and test using Go 1.22 or later, from `router-control/` in a POSIX shell:

```
go test ./...
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o ../router-files/usr/libexec/router-speed-control .
```

The root README provides PowerShell commands and the manual first-deployment
steps. Building only stages the executable; it does not deploy files or change
router services.
