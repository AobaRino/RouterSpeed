//go:build !linux || !amd64

package main

import (
	"fmt"
	"os"
)

func main() {
	fmt.Fprintln(os.Stderr, "router-speed-collector runs on Linux amd64; portable parsing tests can run on this platform")
	os.Exit(1)
}
