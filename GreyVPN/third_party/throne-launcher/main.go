//go:build windows

package main

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"github.com/tailscale/go-winio"
)

const outerSocketEnv = "GREYVPN_THRONE_OUTER_SOCKET"

// The same tiny program is built once and copied under two names:
//
//   ThroneCore.exe -> compatibility shim started by GreyVPN. It starts Throne.exe.
//   Throne.exe     -> owns an inner named pipe, starts the byte-for-byte upstream
//                     ThroneCoreUpstream.exe and transparently bridges its IPC to
//                     GreyVPN's outer named pipe.
//
// Upstream ThroneCore on Windows requires BOTH:
//   1. its direct parent executable to be Throne.exe in the same directory; and
//   2. the named-pipe server PID to equal that parent PID.
//
// Making Throne.exe the pipe bridge satisfies both upstream checks without
// patching ThroneCore or using its debug build.
func main() {
	self, err := os.Executable()
	if err != nil {
		fatal(111, "resolve executable", err)
	}

	dir := filepath.Dir(self)
	switch strings.ToLower(filepath.Base(self)) {
	case "thronecore.exe":
		runShim(dir)
	case "throne.exe":
		runBridge(dir)
	default:
		fatal(110, "unexpected executable name", filepath.Base(self))
	}
}

func runShim(dir string) {
	outer := os.Getenv("THRONE_CORE_SOCKET")
	if outer == "" {
		fatal(113, "THRONE_CORE_SOCKET is not set", nil)
	}

	cmd := exec.Command(filepath.Join(dir, "Throne.exe"))
	cmd.Env = setEnv(os.Environ(), outerSocketEnv, outer)
	inheritStreams(cmd)
	exitWithChild(cmd, "start Throne IPC bridge")
}

func runBridge(dir string) {
	outer := os.Getenv(outerSocketEnv)
	if outer == "" {
		outer = os.Getenv("THRONE_CORE_SOCKET")
	}
	if outer == "" {
		fatal(114, "outer GreyVPN pipe is not set", nil)
	}

	inner := `\\.\pipe\GreyVPN-ThroneCore-Inner-` + fmt.Sprint(os.Getpid()) + "-" + randomSuffix()
	listener, err := winio.ListenPipe(inner, &winio.PipeConfig{
		MessageMode:      false,
		InputBufferSize:  64 * 1024,
		OutputBufferSize: 64 * 1024,
	})
	if err != nil {
		fatal(115, "create inner ThroneCore pipe", err)
	}
	defer listener.Close()

	upstream := filepath.Join(dir, "ThroneCoreUpstream.exe")
	cmd := exec.Command(upstream)
	cmd.Env = setEnv(removeEnv(os.Environ(), outerSocketEnv), "THRONE_CORE_SOCKET", inner)
	inheritStreams(cmd)
	if err := cmd.Start(); err != nil {
		fatal(116, "start upstream ThroneCore", err)
	}

	innerCh := make(chan acceptResult, 1)
	go func() {
		conn, acceptErr := listener.Accept()
		innerCh <- acceptResult{conn: conn, err: acceptErr}
	}()

	var innerConn io.ReadWriteCloser
	select {
	case accepted := <-innerCh:
		if accepted.err != nil {
			killAndWait(cmd)
			fatal(117, "accept upstream ThroneCore pipe", accepted.err)
		}
		innerConn = accepted.conn.(io.ReadWriteCloser)
	case <-time.After(12 * time.Second):
		killAndWait(cmd)
		fatal(118, "upstream ThroneCore did not connect to inner pipe", nil)
	}
	defer innerConn.Close()

	timeout := 12 * time.Second
	outerConn, err := winio.DialPipe(outer, &timeout)
	if err != nil {
		killAndWait(cmd)
		fatal(119, "connect to GreyVPN outer pipe", err)
	}
	defer outerConn.Close()

	bridgeDone := make(chan error, 2)
	go copyPipe(bridgeDone, outerConn, innerConn)
	go copyPipe(bridgeDone, innerConn, outerConn)

	// One side closing means the IPC session is over. Close both handles so the
	// opposite copier unblocks, then terminate the child if it has not exited.
	<-bridgeDone
	_ = outerConn.Close()
	_ = innerConn.Close()
	killAndWait(cmd)
}

type acceptResult struct {
	conn io.ReadWriteCloser
	err  error
}

func copyPipe(done chan<- error, dst io.Writer, src io.Reader) {
	_, err := io.Copy(dst, src)
	done <- err
}

func inheritStreams(cmd *exec.Cmd) {
	cmd.Stdin = os.Stdin
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
}

func exitWithChild(cmd *exec.Cmd, action string) {
	if err := cmd.Run(); err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			os.Exit(exitErr.ExitCode())
		}
		fatal(112, action, err)
	}
}

func killAndWait(cmd *exec.Cmd) {
	if cmd == nil || cmd.Process == nil {
		return
	}
	if cmd.ProcessState == nil || !cmd.ProcessState.Exited() {
		_ = cmd.Process.Kill()
	}
	_ = cmd.Wait()
}

func setEnv(env []string, key, value string) []string {
	prefix := strings.ToUpper(key) + "="
	out := make([]string, 0, len(env)+1)
	for _, item := range env {
		if strings.HasPrefix(strings.ToUpper(item), prefix) {
			continue
		}
		out = append(out, item)
	}
	return append(out, key+"="+value)
}

func removeEnv(env []string, key string) []string {
	prefix := strings.ToUpper(key) + "="
	out := make([]string, 0, len(env))
	for _, item := range env {
		if !strings.HasPrefix(strings.ToUpper(item), prefix) {
			out = append(out, item)
		}
	}
	return out
}

func randomSuffix() string {
	var b [8]byte
	if _, err := rand.Read(b[:]); err != nil {
		return fmt.Sprintf("%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(b[:])
}

func fatal(code int, action string, err any) {
	if err == nil {
		fmt.Fprintln(os.Stderr, "launcher:", action)
	} else {
		fmt.Fprintln(os.Stderr, "launcher:", action+":", err)
	}
	os.Exit(code)
}
