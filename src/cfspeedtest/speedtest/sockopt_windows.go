//go:build windows

package speedtest

import "syscall"

func setSocketBuffers(network, address string, c syscall.RawConn) error {
	var seterr error
	err := c.Control(func(fd uintptr) {
		if e := syscall.SetsockoptInt(syscall.Handle(fd), syscall.SOL_SOCKET, syscall.SO_RCVBUF, 2<<20); e != nil {
			seterr = e
		}
	})
	if err != nil {
		return err
	}
	return seterr
}
