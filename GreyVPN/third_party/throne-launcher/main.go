package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
)

// GreyVPN launches this executable as engines/throne/Throne.exe.
// ThroneCore's upstream Windows parent check intentionally accepts only a
// parent named Throne.exe in the same directory. This launcher keeps the
// upstream ThroneCore binary unmodified and forwards its inherited IPC
// environment and standard streams.
func main() {
	self, err := os.Executable()
	if err != nil {
		fmt.Fprintln(os.Stderr, "launcher: resolve executable:", err)
		os.Exit(111)
	}
	core := filepath.Join(filepath.Dir(self), "ThroneCore.exe")
	cmd := exec.Command(core)
	cmd.Env = os.Environ()
	cmd.Stdin = os.Stdin
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			os.Exit(exitErr.ExitCode())
		}
		fmt.Fprintln(os.Stderr, "launcher: start ThroneCore:", err)
		os.Exit(112)
	}
}
