package main

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/cgi"
	"net/netip"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

const (
	tokenPath   = "/etc/router-speed/token"
	statePath   = "/tmp/router-speed/status.json"
	apiPath     = "/cgi-bin/router-speed"
	servicePath = "/etc/init.d/router-speed"
)

type config struct {
	Enabled   bool   `json:"enabled"`
	Client    string `json:"client"`
	Interface string `json:"interface"`
}

type configInfo struct {
	config
	HasToken   bool     `json:"hasToken"`
	Interfaces []string `json:"interfaces"`
}

type counters struct {
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
	InstanceID     string `json:"instanceId"`
}

type status struct {
	Enabled     bool      `json:"enabled"`
	Available   bool      `json:"available"`
	SampleAgeMS int64     `json:"sampleAgeMs"`
	Counters    *counters `json:"counters"`
	Error       string    `json:"error,omitempty"`
}

type credentials struct {
	Client  string `json:"client"`
	APIPath string `json:"apiPath"`
	Token   string `json:"token"`
}

var interfacePattern = regexp.MustCompile(`^[A-Za-z0-9_.:-]{1,15}$`)

func validateConfig(c config) error {
	address, err := netip.ParseAddr(c.Client)
	if err != nil || !address.Is4() || !address.IsGlobalUnicast() || address.IsLoopback() {
		return errors.New("client must be a unicast IPv4 address")
	}
	if !interfacePattern.MatchString(c.Interface) {
		return errors.New("invalid LAN interface name")
	}
	return nil
}

func command(path string, args ...string) ([]byte, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, path, args...)
	// Never log command arguments, stderr, or configuration values.
	output, err := cmd.Output()
	if err != nil {
		return nil, errors.New("router configuration operation failed")
	}
	return output, nil
}

func loadConfig() (config, error) {
	var c config
	values := make([]string, 3)
	for i, option := range []string{"enabled", "client", "interface"} {
		b, err := command("/sbin/uci", "-q", "get", "router_speed.main."+option)
		if err != nil {
			return c, err
		}
		values[i] = strings.TrimSpace(string(b))
	}
	if values[0] != "0" && values[0] != "1" {
		return c, errors.New("invalid enabled setting")
	}
	c = config{Enabled: values[0] == "1", Client: values[1], Interface: values[2]}
	return c, validateConfig(c)
}

func describeConfig(c config) configInfo {
	_, err := readToken(tokenPath)
	result := configInfo{config: c, HasToken: err == nil, Interfaces: []string{}}
	if interfaces, err := net.Interfaces(); err == nil {
		for _, iface := range interfaces {
			if iface.Flags&net.FlagLoopback == 0 && interfacePattern.MatchString(iface.Name) {
				result.Interfaces = append(result.Interfaces, iface.Name)
			}
		}
	}
	return result
}

func readToken(path string) (string, error) {
	b, err := readLimited(path, 128)
	if err != nil {
		return "", errors.New("read-only credential is not configured")
	}
	token := strings.TrimSpace(string(b))
	decoded, err := hex.DecodeString(token)
	if err != nil || len(decoded) != 32 {
		return "", errors.New("read-only credential is invalid")
	}
	return token, nil
}

func readLimited(path string, maxBytes int64) ([]byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	b, err := io.ReadAll(io.LimitReader(f, maxBytes+1))
	if err != nil || int64(len(b)) > maxBytes {
		return nil, errors.New("invalid data size")
	}
	return b, nil
}

func replaceToken(path string) (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	token := hex.EncodeToString(b)
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0700); err != nil {
		return "", err
	}
	if err := os.Chmod(dir, 0700); err != nil {
		return "", err
	}
	f, err := os.CreateTemp(dir, ".token-")
	if err != nil {
		return "", err
	}
	defer os.Remove(f.Name())
	if _, err = f.WriteString(token + "\n"); err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err == nil {
		err = closeErr
	}
	if err == nil {
		err = os.Rename(f.Name(), path)
	}
	if err != nil {
		return "", err
	}
	return token, nil
}

func readStatus(c config, path string, now time.Time) status {
	s := status{Enabled: c.Enabled, SampleAgeMS: -1}
	if !c.Enabled {
		s.Error = "collector is disabled"
		return s
	}
	b, err := readLimited(path, 16384)
	if err != nil {
		s.Error = "waiting for collector"
		return s
	}
	var sample counters
	if json.Unmarshal(b, &sample) != nil || sample.Timestamp <= 0 || sample.InstanceID == "" {
		s.Error = "invalid collector sample"
		return s
	}
	s.SampleAgeMS = now.UnixMilli() - sample.Timestamp
	if sample.Client != c.Client || sample.Interface != c.Interface {
		s.Error = "collector configuration is changing"
		return s
	}
	if s.SampleAgeMS < -1000 || s.SampleAgeMS > 5000 {
		s.Error = "collector sample is stale"
		return s
	}
	if s.SampleAgeMS < 0 {
		s.SampleAgeMS = 0
	}
	s.Counters = &sample
	if sample.MapID == 0 {
		s.Error = "dae routing map is unavailable"
		return s
	}
	s.Available = true
	return s
}

func saveConfig(c config) error {
	if err := validateConfig(c); err != nil {
		return err
	}
	if _, err := net.InterfaceByName(c.Interface); err != nil {
		return errors.New("LAN interface does not exist")
	}
	unlock, err := lockManagement()
	if err != nil {
		return err
	}
	defer unlock()
	if _, err := loadConfig(); err != nil {
		return err
	}
	enabled := "0"
	if c.Enabled {
		enabled = "1"
	}
	for _, setting := range []string{"enabled=" + enabled, "client=" + c.Client, "interface=" + c.Interface} {
		if _, err := command("/sbin/uci", "-q", "set", "router_speed.main."+setting); err != nil {
			_, _ = command("/sbin/uci", "-q", "revert", "router_speed")
			return err
		}
	}
	if _, err := command("/sbin/uci", "-q", "commit", "router_speed"); err != nil {
		_, _ = command("/sbin/uci", "-q", "revert", "router_speed")
		return err
	}
	// These commands affect only this collector, never networking, daed, or uhttpd.
	if c.Enabled {
		if _, err := command(servicePath, "enable"); err != nil {
			return err
		}
		if _, err := command(servicePath, "restart"); err != nil {
			return errors.New("configuration saved, but collector restart failed")
		}
	} else {
		if _, err := command(servicePath, "disable"); err != nil {
			return err
		}
		if _, err := command(servicePath, "stop"); err != nil {
			return errors.New("configuration saved, but collector stop failed")
		}
		_ = os.Remove(statePath)
	}
	return nil
}

func rpc(args []string, in io.Reader, out io.Writer) error {
	enc := json.NewEncoder(out)
	if len(args) == 1 && args[0] == "list" {
		return enc.Encode(map[string]any{
			"status": map[string]any{}, "config": map[string]any{},
			"save":        map[string]any{"enabled": true, "client": "", "interface": ""},
			"credentials": map[string]any{}, "rotate_token": map[string]any{},
		})
	}
	if len(args) != 2 || args[0] != "call" {
		return errors.New("invalid RPC invocation")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	switch args[1] {
	case "status":
		return enc.Encode(readStatus(c, statePath, time.Now()))
	case "config":
		return enc.Encode(describeConfig(c))
	case "save":
		var request struct {
			Enabled   *bool   `json:"enabled"`
			Client    *string `json:"client"`
			Interface *string `json:"interface"`
			Session   string  `json:"ubus_rpc_session,omitempty"`
		}
		dec := json.NewDecoder(io.LimitReader(in, 4096))
		dec.DisallowUnknownFields()
		if err := dec.Decode(&request); err != nil || request.Enabled == nil || request.Client == nil || request.Interface == nil {
			return errors.New("enabled, client and interface are required")
		}
		next := config{Enabled: *request.Enabled, Client: *request.Client, Interface: *request.Interface}
		if err := saveConfig(next); err != nil {
			return err
		}
		return enc.Encode(map[string]any{"ok": true, "config": describeConfig(next)})
	case "credentials", "rotate_token":
		var token string
		if args[1] == "rotate_token" {
			unlock, err := lockManagement()
			if err != nil {
				return err
			}
			defer unlock()
			token, err = replaceToken(tokenPath)
			if err != nil {
				return errors.New("could not create read-only credential")
			}
		} else {
			token, err = readToken(tokenPath)
			if err != nil {
				return err
			}
		}
		return enc.Encode(credentials{Client: c.Client, APIPath: apiPath, Token: token})
	default:
		return errors.New("unknown RPC method")
	}
}

func allowedSource(remote, client string) bool {
	if host, _, err := net.SplitHostPort(remote); err == nil {
		remote = host
	}
	address, err := netip.ParseAddr(remote)
	return err == nil && address.Is4() && address.String() == client
}

func authorized(header, token, remote, client string) bool {
	if !allowedSource(remote, client) {
		return false
	}
	if len(header) != len("Bearer ")+64 || !strings.EqualFold(header[:7], "Bearer ") || len(token) != 64 {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(header[7:]), []byte(token)) == 1
}

func apiHandler(getConfig func() (config, error), getToken func() (string, error), getStatus func(config) status) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json; charset=utf-8")
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		fail := func(code int, message string) {
			w.WriteHeader(code)
			_ = json.NewEncoder(w).Encode(map[string]string{"error": message})
		}
		if r.Method != http.MethodGet {
			w.Header().Set("Allow", "GET")
			fail(405, "method not allowed")
			return
		}
		if r.URL.Path != apiPath || r.URL.RawQuery != "" {
			fail(404, "not found")
			return
		}
		c, err := getConfig()
		if err != nil {
			fail(503, "collector configuration unavailable")
			return
		}
		if !allowedSource(r.RemoteAddr, c.Client) {
			fail(403, "access denied")
			return
		}
		token, err := getToken()
		if err != nil || !authorized(r.Header.Get("Authorization"), token, r.RemoteAddr, c.Client) {
			w.Header().Set("WWW-Authenticate", `Bearer realm="RouterSpeed"`)
			fail(401, "invalid or missing read-only credential")
			return
		}
		if !c.Enabled {
			fail(503, "collector disabled")
			return
		}
		s := getStatus(c)
		if !s.Available || s.Counters == nil {
			fail(503, "collector unavailable")
			return
		}
		_ = json.NewEncoder(w).Encode(s.Counters)
	})
}

func main() {
	var err error
	if len(os.Args) >= 2 {
		switch os.Args[1] {
		case "--rpc":
			err = rpc(os.Args[2:], os.Stdin, os.Stdout)
		case "--cgi":
			err = cgi.Serve(apiHandler(loadConfig, func() (string, error) { return readToken(tokenPath) }, func(c config) status { return readStatus(c, statePath, time.Now()) }))
		case "--ensure-token", "--init":
			if _, err = readToken(tokenPath); err != nil {
				_, err = replaceToken(tokenPath)
			}
		case "--validate":
			var c config
			c, err = loadConfig()
			if err == nil {
				_, err = net.InterfaceByName(c.Interface)
			}
		default:
			err = errors.New("unknown mode")
		}
	} else {
		err = errors.New("mode is required")
	}
	if err != nil {
		// RPC replies expose only fixed messages; never credentials or subprocess stderr.
		if len(os.Args) > 1 && os.Args[1] == "--rpc" {
			_ = json.NewEncoder(os.Stdout).Encode(map[string]string{"error": err.Error()})
		}
		fmt.Fprintln(os.Stderr, "router-speed-control: operation failed")
		os.Exit(1)
	}
}
