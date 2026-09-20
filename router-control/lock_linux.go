//go:build linux

package main

import (
	"errors"
	"os"
	"syscall"
	"time"
)

func lockManagement() (func(), error) {
	f, err := os.OpenFile("/var/lock/router-speed-control.lock", os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, errors.New("management lock unavailable")
	}
	deadline := time.Now().Add(5 * time.Second)
	for {
		err = syscall.Flock(int(f.Fd()), syscall.LOCK_EX|syscall.LOCK_NB)
		if err == nil {
			return func() { _ = syscall.Flock(int(f.Fd()), syscall.LOCK_UN); _ = f.Close() }, nil
		}
		if time.Now().After(deadline) {
			_ = f.Close()
			return nil, errors.New("another configuration change is in progress")
		}
		time.Sleep(50 * time.Millisecond)
	}
}
