// Command goprobe reads device attributes and files from an outstation using
// the go-dnp3 master library, and prints what it got.
//
// It exists for one reason: to prove that what SharpDnp3's outstation puts on
// the wire is read correctly by a second, independent implementation. The other
// interop tests drive our master against go-dnp3's outstation, which validates
// our parser against their encoder; this validates our encoder against their
// parser, which is the half those tests cannot reach — go-dnp3's own master CLI
// has no subcommand for either feature.
//
// Build it into the directory GO_DNP3_BIN names:
//
//	go build -o "$GO_DNP3_BIN/goprobe" ./testdata/interop/goprobe
//
// The interop tests skip when it is not there.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/dscsystems/go-dnp3/channel"
	"github.com/dscsystems/go-dnp3/master"
)

func main() {
	host := flag.String("host", "127.0.0.1:20000", "outstation to connect to")
	local := flag.Uint("local", 1, "master link address")
	remote := flag.Uint("remote", 10, "outstation link address")
	what := flag.String("do", "attributes", "attributes, ls, get or put")
	path := flag.String("path", "/", "file or directory to act on")
	flag.Parse()

	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	defer cancel()

	ch := channel.TCPClient(*host, channel.DefaultRetry)
	defer ch.Close()

	s := master.New(master.Config{
		LocalAddr:       uint16(*local),
		RemoteAddr:      uint16(*remote),
		ResponseTimeout: 10 * time.Second,
	}, nil)

	runCtx, stop := context.WithCancel(ctx)
	defer stop()
	go func() { _ = s.Run(runCtx, ch) }()

	// Wait for the link before asking for anything.
	deadline := time.Now().Add(20 * time.Second)
	for !s.Connected() {
		if time.Now().After(deadline) {
			fail("could not connect to %s", *host)
		}
		time.Sleep(20 * time.Millisecond)
	}

	switch *what {
	case "attributes":
		attrs, err := s.ReadAttributes(ctx)
		if err != nil {
			fail("reading attributes: %v", err)
		}
		for _, a := range attrs {
			fmt.Printf("%-32s %s\n", a.Name(), a.Value())
		}

	case "ls":
		entries, err := s.ReadDirectory(ctx, *path)
		if err != nil {
			fail("listing %s: %v", *path, err)
		}
		for _, e := range entries {
			fmt.Println(e.String())
		}

	case "get":
		content, err := s.ReadFileBytes(ctx, *path)
		if err != nil {
			fail("reading %s: %v", *path, err)
		}
		os.Stdout.Write(content)

	case "stat":
		info, err := s.FileInfo(ctx, *path)
		if err != nil {
			fail("stat %s: %v", *path, err)
		}
		fmt.Println(info.String())

	default:
		fail("unknown action %q", *what)
	}
}

func fail(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "goprobe: "+format+"\n", args...)
	os.Exit(1)
}
