package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
)

func newInstanceID() (string, error) {
	var random [16]byte
	if _, err := rand.Read(random[:]); err != nil {
		return "", fmt.Errorf("generate collector instance ID: %w", err)
	}
	return hex.EncodeToString(random[:]), nil
}

type stateFile struct{ path string }

func openStateFile(path string) (*stateFile, error) {
	if path == "" {
		return nil, nil
	}
	directory := filepath.Dir(path)
	if err := os.MkdirAll(directory, 0700); err != nil {
		return nil, fmt.Errorf("create state directory: %w", err)
	}
	info, err := os.Stat(directory)
	if err != nil {
		return nil, fmt.Errorf("inspect state directory: %w", err)
	}
	// Never change permissions of an existing general-purpose directory such
	// as /tmp. Require a dedicated private directory instead.
	if runtime.GOOS != "windows" && info.Mode().Perm() != 0700 {
		return nil, fmt.Errorf("state directory must have mode 0700")
	}
	return &stateFile{path: path}, nil
}

func (s *stateFile) write(snapshot counters) error {
	if s == nil {
		return nil
	}
	file, err := os.CreateTemp(filepath.Dir(s.path), ".status-*.tmp")
	if err != nil {
		return fmt.Errorf("create temporary state file: %w", err)
	}
	temporary := file.Name()
	defer os.Remove(temporary)
	// CreateTemp uses 0600. The replacement retains these permissions and is
	// atomic because the temporary file resides in the same directory.
	if err := json.NewEncoder(file).Encode(snapshot); err != nil {
		file.Close()
		return fmt.Errorf("encode collector state: %w", err)
	}
	if err := file.Close(); err != nil {
		return fmt.Errorf("close collector state: %w", err)
	}
	if err := os.Rename(temporary, s.path); err != nil {
		return fmt.Errorf("replace collector state: %w", err)
	}
	return nil
}
