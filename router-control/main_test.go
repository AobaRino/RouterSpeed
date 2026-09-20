package main

import (
	"encoding/json"
	"errors"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestValidationRejectsUnsafeAndNonUnicastConfig(t *testing.T) {
	for _, tc := range []struct {
		client, iface string
		valid         bool
	}{
		{"192.168.233.10", "eth0", true}, {"10.0.0.5", "br-lan.2", true},
		{"127.0.0.1", "eth0", false}, {"0.0.0.0", "eth0", false},
		{"255.255.255.255", "eth0", false}, {"224.0.0.1", "eth0", false},
		{"::1", "eth0", false}, {"192.168.233.10;reboot", "eth0", false},
		{"192.168.233.10", "eth0;reboot", false}, {"192.168.233.10", "$(reboot)", false},
		{"192.168.233.10", "../../etc/passwd", false}, {"192.168.233.10", "", false},
	} {
		err := validateConfig(config{Client: tc.client, Interface: tc.iface})
		if (err == nil) != tc.valid {
			t.Errorf("validation mismatch for %q %q: %v", tc.client, tc.iface, err)
		}
	}
}

func TestAuthorizationRequiresBothCredentialAndExactIPv4(t *testing.T) {
	token := strings.Repeat("a", 64)
	for _, tc := range []struct {
		header, remote string
		valid          bool
	}{
		{"Bearer " + token, "192.168.233.10", true},
		{"bearer " + token, "192.168.233.10:12345", true},
		{"Bearer " + token, "192.168.233.11", false},
		{"Bearer " + token, "::ffff:192.168.233.10", false},
		{"Bearer " + strings.Repeat("b", 64), "192.168.233.10", false},
		{"Bearer " + token + "x", "192.168.233.10", false},
		{"", "192.168.233.10", false}, {"Basic " + token, "192.168.233.10", false},
	} {
		if authorized(tc.header, token, tc.remote, "192.168.233.10") != tc.valid {
			t.Errorf("unexpected auth result for remote %q", tc.remote)
		}
	}
}

func sampleAt(now time.Time) counters {
	return counters{Timestamp: now.UnixMilli(), Client: "192.168.233.10", Interface: "eth0", InstanceID: "00112233445566778899aabbccddeeff", MapID: 77, DirectDown: 1024, ProxyUp: 2048}
}

func writeSample(t *testing.T, path string, s counters) {
	t.Helper()
	b, err := json.Marshal(s)
	if err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(path, b, 0600); err != nil {
		t.Fatal(err)
	}
}

func TestStatusRejectsStaleRestartAndWrongClient(t *testing.T) {
	now := time.Unix(1800000000, 0)
	c := config{Enabled: true, Client: "192.168.233.10", Interface: "eth0"}
	path := filepath.Join(t.TempDir(), "status.json")
	if got := readStatus(c, path, now); got.Available || got.Counters != nil {
		t.Fatal("missing status accepted")
	}
	for _, tc := range []struct {
		name   string
		mutate func(*counters)
		valid  bool
	}{
		{"fresh", func(s *counters) { s.Timestamp -= 1000 }, true},
		{"stale", func(s *counters) { s.Timestamp -= 5001 }, false},
		{"future", func(s *counters) { s.Timestamp += 1001 }, false},
		{"wrong client", func(s *counters) { s.Client = "192.168.233.11" }, false},
		{"wrong interface", func(s *counters) { s.Interface = "eth1" }, false},
		{"map unavailable", func(s *counters) { s.MapID = 0 }, false},
		{"legacy without generation", func(s *counters) { s.InstanceID = "" }, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s := sampleAt(now)
			tc.mutate(&s)
			writeSample(t, path, s)
			got := readStatus(c, path, now)
			if got.Available != tc.valid {
				t.Fatalf("unexpected status: %+v", got)
			}
		})
	}
	writeSample(t, path, sampleAt(now))
	c.Enabled = false
	if got := readStatus(c, path, now); got.Available || got.Counters != nil {
		t.Fatal("disabled status accepted")
	}
	if err := os.WriteFile(path, []byte(`{"timestamp":99999999999999999999}`), 0600); err != nil {
		t.Fatal(err)
	}
	c.Enabled = true
	if readStatus(c, path, now).Available {
		t.Fatal("malformed counters accepted")
	}
}

func TestTokenRotationInvalidatesPriorCredential(t *testing.T) {
	path := filepath.Join(t.TempDir(), "private", "token")
	first, err := replaceToken(path)
	if err != nil {
		t.Fatal(err)
	}
	if len(first) != 64 {
		t.Fatal("wrong credential length")
	}
	loaded, err := readToken(path)
	if err != nil || loaded != first {
		t.Fatal("credential was not persisted")
	}
	second, err := replaceToken(path)
	if err != nil {
		t.Fatal(err)
	}
	if first == second {
		t.Fatal("credential did not rotate")
	}
	if authorized("Bearer "+first, second, "192.168.233.10", "192.168.233.10") {
		t.Fatal("revoked credential accepted")
	}
	if err := os.WriteFile(path, []byte(strings.Repeat("x", 129)), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err = readToken(path); err == nil {
		t.Fatal("oversized credential accepted")
	}
}

func TestAPIOnlyExposesFreshCountersToAuthorizedClient(t *testing.T) {
	token := strings.Repeat("a", 64)
	c := config{Enabled: true, Client: "192.168.233.10", Interface: "eth0"}
	s := sampleAt(time.Now())
	available := true
	h := apiHandler(func() (config, error) { return c, nil }, func() (string, error) { return token, nil }, func(config) status { return status{Enabled: true, Available: available, Counters: &s} })
	for _, tc := range []struct {
		name, method, path, remote, header string
		code                               int
	}{
		{"valid", "GET", apiPath, "192.168.233.10:3000", "Bearer " + token, 200},
		{"no token", "GET", apiPath, "192.168.233.10:3000", "", 401},
		{"bad token", "GET", apiPath, "192.168.233.10:3000", "Bearer " + strings.Repeat("b", 64), 401},
		{"wrong source", "GET", apiPath, "192.168.233.11:3000", "Bearer " + token, 403},
		{"write", "POST", apiPath, "192.168.233.10:3000", "Bearer " + token, 405},
		{"unexpected path", "GET", apiPath + "/credentials", "192.168.233.10:3000", "Bearer " + token, 404},
		{"query credential", "GET", apiPath + "?token=" + token, "192.168.233.10:3000", "Bearer " + token, 404},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := httptest.NewRequest(tc.method, tc.path, nil)
			r.RemoteAddr = tc.remote
			r.Header.Set("Authorization", tc.header)
			r.Header.Set("X-Forwarded-For", c.Client)
			w := httptest.NewRecorder()
			h.ServeHTTP(w, r)
			if w.Code != tc.code {
				t.Fatalf("status=%d want=%d", w.Code, tc.code)
			}
			if w.Header().Get("Cache-Control") != "no-store" {
				t.Fatal("missing no-store")
			}
			if strings.Contains(w.Body.String(), token) {
				t.Fatal("credential leaked")
			}
			if tc.code != 200 && strings.Contains(w.Body.String(), "directDown") {
				t.Fatal("counters leaked")
			}
		})
	}
	available = false
	r := httptest.NewRequest("GET", apiPath, nil)
	r.RemoteAddr = c.Client
	r.Header.Set("Authorization", "Bearer "+token)
	w := httptest.NewRecorder()
	h.ServeHTTP(w, r)
	if w.Code != 503 || strings.Contains(w.Body.String(), "directDown") {
		t.Fatal("stale counters were returned")
	}
	available = true
	c.Enabled = false
	w = httptest.NewRecorder()
	h.ServeHTTP(w, r)
	if w.Code != 503 || strings.Contains(w.Body.String(), "directDown") {
		t.Fatal("disabled collector exposed old counters")
	}
	c.Enabled = true
	token = strings.Repeat("b", 64)
	w = httptest.NewRecorder()
	h.ServeHTTP(w, r)
	if w.Code != 401 || strings.Contains(w.Body.String(), "directDown") {
		t.Fatal("rotated credential still exposed counters")
	}
	h = apiHandler(func() (config, error) { return config{}, errors.New("private details") }, func() (string, error) { return token, nil }, func(config) status { return status{} })
	w = httptest.NewRecorder()
	h.ServeHTTP(w, r)
	if w.Code != 503 || strings.Contains(w.Body.String(), "private details") {
		t.Fatal("configuration failure was not sanitized")
	}
}
