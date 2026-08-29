//go:build windows

package main

import (
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"errors"
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
//                     ThroneCoreUpstream.exe and bridges its IPC to GreyVPN.
//
// Upstream ThroneCore on Windows requires BOTH:
//   1. its direct parent executable to be Throne.exe in the same directory; and
//   2. the named-pipe server PID to equal that parent PID.
//
// Making Throne.exe the pipe bridge satisfies both upstream checks without
// patching ThroneCore or using its debug build.
//
// The pinned upstream Start handler also directly dereferences the proto2
// optional bool fields need_extra_process (3) and need_xray (9). Older/minimal
// callers may legally omit those fields, which makes upstream panic. For Start
// requests only, the bridge adds explicit false values when those fields are
// absent. Existing values are preserved. This is wire-compatible protobuf
// normalization; the upstream binary remains byte-for-byte unchanged.
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
	// Requests are frame-aware so we can normalize the two optional Start bools.
	go func() { bridgeDone <- forwardRequests(innerConn, outerConn) }()
	// Responses are never modified.
	go copyPipe(bridgeDone, outerConn, innerConn)

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

func forwardRequests(dst io.Writer, src io.Reader) error {
	for {
		var fixed [6]byte // uint32 request id + uint16 method length
		if _, err := io.ReadFull(src, fixed[:]); err != nil {
			return err
		}
		methodLen := int(binary.LittleEndian.Uint16(fixed[4:6]))
		if methodLen < 0 || methodLen > 4096 {
			return fmt.Errorf("invalid IPC method length: %d", methodLen)
		}
		method := make([]byte, methodLen)
		if _, err := io.ReadFull(src, method); err != nil {
			return err
		}

		var lenBuf [4]byte
		if _, err := io.ReadFull(src, lenBuf[:]); err != nil {
			return err
		}
		payloadLen := binary.LittleEndian.Uint32(lenBuf[:])
		if payloadLen > 32*1024*1024 {
			return fmt.Errorf("invalid IPC payload length: %d", payloadLen)
		}
		payload := make([]byte, int(payloadLen))
		if _, err := io.ReadFull(src, payload); err != nil {
			return err
		}

		if string(method) == "Start" {
			var err error
			payload, err = ensureProtoBool(payload, 3, false) // need_extra_process
			if err != nil {
				return fmt.Errorf("normalize Start need_extra_process: %w", err)
			}
			payload, err = ensureProtoBool(payload, 9, false) // need_xray
			if err != nil {
				return fmt.Errorf("normalize Start need_xray: %w", err)
			}
		}

		binary.LittleEndian.PutUint32(lenBuf[:], uint32(len(payload)))
		if err := writeFull(dst, fixed[:]); err != nil {
			return err
		}
		if err := writeFull(dst, method); err != nil {
			return err
		}
		if err := writeFull(dst, lenBuf[:]); err != nil {
			return err
		}
		if err := writeFull(dst, payload); err != nil {
			return err
		}
	}
}

func ensureProtoBool(payload []byte, wantedField int, value bool) ([]byte, error) {
	present, err := protoFieldPresent(payload, wantedField)
	if err != nil {
		return nil, err
	}
	if present {
		return payload, nil
	}
	out := append([]byte(nil), payload...)
	out = appendVarint(out, uint64(wantedField<<3)) // wire type 0
	if value {
		out = appendVarint(out, 1)
	} else {
		out = appendVarint(out, 0)
	}
	return out, nil
}

func protoFieldPresent(data []byte, wantedField int) (bool, error) {
	for offset := 0; offset < len(data); {
		tag, next, err := readVarint(data, offset)
		if err != nil {
			return false, err
		}
		offset = next
		field := int(tag >> 3)
		wire := int(tag & 7)
		if field <= 0 {
			return false, errors.New("invalid protobuf field number")
		}
		if field == wantedField {
			return true, nil
		}
		switch wire {
		case 0:
			_, offset, err = readVarint(data, offset)
			if err != nil {
				return false, err
			}
		case 1:
			offset += 8
		case 2:
			length, n, e := readVarint(data, offset)
			if e != nil {
				return false, e
			}
			offset = n
			if length > uint64(len(data)-offset) {
				return false, errors.New("truncated protobuf length-delimited field")
			}
			offset += int(length)
		case 5:
			offset += 4
		default:
			return false, fmt.Errorf("unsupported protobuf wire type %d", wire)
		}
		if offset < 0 || offset > len(data) {
			return false, errors.New("truncated protobuf field")
		}
	}
	return false, nil
}

func readVarint(data []byte, offset int) (uint64, int, error) {
	var value uint64
	for shift := uint(0); shift < 64; shift += 7 {
		if offset >= len(data) {
			return 0, offset, errors.New("truncated protobuf varint")
		}
		b := data[offset]
		offset++
		value |= uint64(b&0x7f) << shift
		if b&0x80 == 0 {
			return value, offset, nil
		}
	}
	return 0, offset, errors.New("invalid protobuf varint")
}

func appendVarint(dst []byte, value uint64) []byte {
	for value >= 0x80 {
		dst = append(dst, byte(value)|0x80)
		value >>= 7
	}
	return append(dst, byte(value))
}

func writeFull(dst io.Writer, data []byte) error {
	for len(data) > 0 {
		n, err := dst.Write(data)
		if err != nil {
			return err
		}
		if n <= 0 {
			return io.ErrShortWrite
		}
		data = data[n:]
	}
	return nil
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
