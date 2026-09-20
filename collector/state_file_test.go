package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func TestAtomicStateFileReplacesCompleteSnapshotAndCleansTemporaryFiles(t *testing.T) {
	path := filepath.Join(t.TempDir(), "private", "status.json")
	state, err := openStateFile(path)
	if err != nil {
		t.Fatal(err)
	}
	first := counters{InstanceID: "first", Timestamp: 1000, DirectDown: 4000, Client: "192.168.233.10", Interface: "eth0", MapID: 4}
	second := counters{InstanceID: "second", Timestamp: 2000, ProxyUp: 8000, Client: "192.168.233.10", Interface: "eth0", MapID: 5}
	for _, snapshot := range []counters{first, second} {
		if err := state.write(snapshot); err != nil {
			t.Fatal(err)
		}
		data, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		var decoded counters
		if err := json.Unmarshal(data, &decoded); err != nil {
			t.Fatal(err)
		}
		if decoded != snapshot {
			t.Fatal("state file did not contain the complete latest snapshot")
		}
	}
	entries, err := os.ReadDir(filepath.Dir(path))
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 1 || entries[0].Name() != "status.json" {
		t.Fatal("temporary state files were retained")
	}
	if runtime.GOOS != "windows" {
		fileInfo, _ := os.Stat(path)
		directoryInfo, _ := os.Stat(filepath.Dir(path))
		if fileInfo.Mode().Perm() != 0600 || directoryInfo.Mode().Perm() != 0700 {
			t.Fatal("state file/directory permissions are not private")
		}
	}
}

func TestStateFileFailureRetainsOriginalAndRemovesTemporaryFile(t *testing.T) {
	directory := t.TempDir()
	state, err := openStateFile(filepath.Join(directory, "status.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(state.path, 0700); err != nil {
		t.Fatal(err)
	}
	if err := state.write(counters{InstanceID: "example"}); err == nil {
		t.Fatal("replacing a directory should fail")
	}
	entries, err := os.ReadDir(directory)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 1 || !entries[0].IsDir() {
		t.Fatal("failure left a temporary file or modified existing destination")
	}
}

func TestOptionalStateAndInstanceIDs(t *testing.T) {
	state, err := openStateFile("")
	if err != nil || state != nil {
		t.Fatal("empty state path must disable output")
	}
	if err := state.write(counters{}); err != nil {
		t.Fatal(err)
	}
	first, err := newInstanceID()
	if err != nil {
		t.Fatal(err)
	}
	second, err := newInstanceID()
	if err != nil {
		t.Fatal(err)
	}
	if first == second || len(first) != 32 || strings.Trim(first, "0123456789abcdef") != "" {
		t.Fatal("instance IDs must be distinct random 128-bit hex strings")
	}
}
