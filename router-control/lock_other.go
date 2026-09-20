//go:build !linux

package main

import "errors"

func lockManagement() (func(), error) {
	return nil, errors.New("management is only supported on the router")
}
