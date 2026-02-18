//go:build !windows

package speedtest

import "syscall"

func setSocketBuffers(network, address string, c syscall.RawConn) error {
	var seterr error
	err := c.Control(func(fd uintptr) {
		// Large receive buffer for high-BDP download (e.g. Starlink ~1 MB BDP).
		// Only set RCVBUF - leave SNDBUF at kernel default so upload byte
		// counting via CountingReader stays accurate (no large send buffer
		// absorbing bytes before they hit the wire).
		if e := syscall.SetsockoptInt(int(fd), syscall.SOL_SOCKET, syscall.SO_RCVBUF, 2<<20); e != nil {
			seterr = e
		}
	})
	if err != nil {
		return err
	}
	return seterr
}
