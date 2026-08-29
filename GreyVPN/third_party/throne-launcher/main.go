package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

// The same tiny program is built twice:
//   ThroneCore.exe -> starts Throne.exe (compatibility entry point used by GreyVPN)
//   Throne.exe     -> starts ThroneCoreUpstream.exe
//
// The upstream ThroneCore binary itself stays unmodified. Its Windows parent
// check sees a parent named Throne.exe in the same directory, exactly as
// upstream expects. IPC environment and standard streams are inherited through
// both launchers.
func main() {
	self, err := os.Executable()
	if err != nil {
		fmt.Fprintln(os.Stderr, "launcher: resolve executable:", err)
		os.Exit(111)
	}

	dir := filepath.Dir(self)
	name := strings.ToLower(filepath.Base(self))
	var target string
	switch name {
	case "thronecore.exe":
		target = filepath.Join(dir, "Throne.exe")
	case "throne.exe":
		target = filepath.Join(dir, "ThroneCoreUpstream.exe")
	default:
		fmt.Fprintln(os.Stderr, "launcher: unexpected executable name:", filepath.Base(self))
		os.Exit(110)
	}

	cmd := exec.Command(target)
	cmd.Env = os.Environ()
	cmd.Stdin = os.Stdin
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			os.Exit(exitErr.ExitCode())
		}
		fmt.Fprintln(os.Stderr, "launcher: start child:", err)
		os.Exit(112)
	}
}
