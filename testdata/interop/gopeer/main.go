// gopeer is a small go-dnp3 program the SharpDnp3 interoperability tests drive.
//
// It exists because the stock dnp3-master and dnp3-outstation binaries do not
// expose the extended services (Secure Authentication, datasets, frozen
// analogs, command events, file authentication, indexed time intervals, the
// management function codes). Both roles here are built on go-dnp3's public
// packages only, and report what they see on stdout, one line per fact, for the
// C# tests to assert against.
//
//	gopeer outstation -listen 127.0.0.1:20000 -dir /tmp/files [-key HEX]
//	gopeer master -addr 127.0.0.1:20000 -scenario NAME [-key HEX]
package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"sync"
	"time"

	dnp3 "github.com/dscsystems/go-dnp3"
	"github.com/dscsystems/go-dnp3/channel"
	"github.com/dscsystems/go-dnp3/master"
	"github.com/dscsystems/go-dnp3/objects"
	"github.com/dscsystems/go-dnp3/outstation"
)

var out sync.Mutex

// say prints one line atomically.
func say(format string, args ...any) {
	out.Lock()
	defer out.Unlock()
	fmt.Printf(format+"\n", args...)
}

func main() {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "usage: gopeer outstation|master [flags]")
		os.Exit(2)
	}
	switch os.Args[1] {
	case "outstation":
		runOutstation(os.Args[2:])
	case "master":
		runMaster(os.Args[2:])
	default:
		fmt.Fprintln(os.Stderr, "unknown role", os.Args[1])
		os.Exit(2)
	}
}

// ---- outstation -----------------------------------------------------------

type commands struct{}

func (commands) SelectCROB(i uint16, c dnp3.ControlRelayOutputBlock) dnp3.CommandStatus {
	return dnp3.CommandSuccess
}
func (commands) OperateCROB(i uint16, c dnp3.ControlRelayOutputBlock, _ outstation.OperateType) dnp3.CommandStatus {
	say("OPERATED crob index=%d code=%#x", i, uint8(c.Code))
	return dnp3.CommandSuccess
}
func (commands) SelectAnalog(i uint16, v outstation.AnalogOutput) dnp3.CommandStatus {
	return dnp3.CommandSuccess
}
func (commands) OperateAnalog(i uint16, v outstation.AnalogOutput, _ outstation.OperateType) dnp3.CommandStatus {
	say("OPERATED analog index=%d value=%v", i, v.Value)
	return dnp3.CommandSuccess
}

type management struct{}

func (management) Manage(op outstation.ManagementOperation, payload []byte) bool {
	say("MANAGE op=%d payload=%s", op, hex.EncodeToString(payload))
	return true
}

func runOutstation(args []string) {
	fs := flag.NewFlagSet("outstation", flag.ExitOnError)
	listen := fs.String("listen", "127.0.0.1:20000", "listen address")
	dir := fs.String("dir", "", "directory served as files")
	key := fs.String("key", "", "hex update key for user 1; enables Secure Authentication")
	_ = fs.Parse(args)

	cfg := outstation.Config{
		LocalAddr: 10, RemoteAddr: 1,
		Database: outstation.DatabaseConfig{
			Binary: 4, Analog: 4, Counter: 3, FrozenCounter: 3, FrozenAnalog: 4,
			BinaryOutputStatus: 2, AnalogOutputStatus: 2, TimeAndInterval: 2, VirtualTerminal: 1,
			DefaultClass: dnp3.Class1,
		},
		Attributes:         []dnp3.Attribute{objects.StringAttribute(247, "before")},
		WritableAttributes: []outstation.AttributeID{{Set: 0, Variation: 247}},
		AttributeWrite: func(a dnp3.Attribute) bool {
			say("ATTRWRITE variation=%d text=%s", a.Variation, a.Text)
			return true
		},
		Management: management{},
		ActivateConfig: func(names []string) objects.ActivationResult {
			say("ACTIVATE %v", names)
			r := objects.ActivationResult{Delay: 250 * time.Millisecond}
			for _, n := range names {
				r.Statuses = append(r.Statuses, objects.ActivationStatus{Code: 0, Text: n})
			}
			return r
		},
		TerminalWrite: func(i uint16, data []byte) bool {
			say("TERMINAL index=%d data=%s", i, hex.EncodeToString(data))
			return true
		},
		Datasets: []outstation.DatasetObject{
			{Group: 85, Variation: 1, Index: 0, Data: []byte{4, 1, 2, 0}},
			{Group: 86, Variation: 1, Index: 0, Data: []byte{4, 1, 2, 0}},
			{Group: 87, Variation: 1, Index: 0, Data: []byte{0xAA, 0xBB, 0xCC}},
		},
		DatasetWrite: func(g, v uint8, data []byte) bool {
			say("DATASETWRITE group=%d variation=%d data=%s", g, v, hex.EncodeToString(data))
			return true
		},
		SelfAddress: true,
	}
	if *key != "" {
		k, err := hex.DecodeString(*key)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		cfg.SecureAuthentication = &outstation.SecureAuthenticationConfig{Users: map[uint16][]byte{1: k}}
	}
	if *dir != "" {
		h, err := outstation.OpenDir(*dir)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		cfg.Files = outstation.FileConfig{Handler: h, Authenticate: func(u, p string) bool { return u == "admin" && p == "pw" }}
	}

	o := outstation.New(cfg, nil, commands{})
	o.Update(func(db *outstation.Database) {
		for i := range 2 {
			db.Configure(dnp3.TypeBinaryOutputStatus, uint16(i), outstation.PointConfig{Class: dnp3.ClassNone, CommandEventClass: dnp3.Class2})
			db.Configure(dnp3.TypeAnalogOutputStatus, uint16(i), outstation.PointConfig{Class: dnp3.ClassNone, CommandEventClass: dnp3.Class2})
		}
		for i := range 3 {
			db.UpdateCounter(uint16(i), dnp3.Counter{Value: uint32(100 * (i + 1)), Flags: dnp3.Online})
		}
		db.UpdateAnalog(1, dnp3.Analog{Value: 42.5, Flags: dnp3.Online})
		db.UpdateBinary(2, dnp3.Binary{Value: true, Flags: dnp3.Online, Time: dnp3.Now(time.Now())})
		db.UpdateFrozenAnalog(0, dnp3.Analog{Value: 7, Flags: dnp3.Online})
		db.UpdateTimeAndInterval(1, dnp3.TimeAndInterval{Time: dnp3.Now(time.UnixMilli(1700000000000)), Interval: 5000, Units: 3})
	})

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	go func() { _ = o.Run(ctx, channel.TCPServer(*listen)) }()
	say("READY")
	// Commands arrive on stdin so a test can change the outstation while it runs.
	go func() {
		var line string
		for {
			if _, err := fmt.Scanln(&line); err != nil {
				stop()
				return
			}
			switch line {
			case "terminal-input":
				o.Update(func(db *outstation.Database) { db.UpdateVirtualTerminal(0, []byte("hello")) })
			case "dataset-update":
				o.Update(func(db *outstation.Database) {
					_ = db.UpdateDataset(outstation.DatasetObject{Group: 87, Variation: 1, Index: 0, Data: []byte{1, 2, 3, 4}}, dnp3.Class2)
				})
			case "frozen-analog":
				o.Update(func(db *outstation.Database) { db.UpdateFrozenAnalog(2, dnp3.Analog{Value: 99, Flags: dnp3.Online, Time: dnp3.Now(time.Now())}) })
			}
		}
	}()
	<-ctx.Done()
}

// ---- master ---------------------------------------------------------------

type printer struct {
	master.NopHandler
}

func (printer) HandleBinary(info master.HeaderInfo, v []dnp3.Indexed[dnp3.Binary]) {
	for _, x := range v {
		say("BINARY g%dv%d index=%d value=%v", info.GV.Group, info.GV.Variation, x.Index, x.Value.Value)
	}
}
func (printer) HandleCounter(info master.HeaderInfo, v []dnp3.Indexed[dnp3.Counter]) {
	for _, x := range v {
		say("COUNTER g%dv%d index=%d value=%d", info.GV.Group, info.GV.Variation, x.Index, x.Value.Value)
	}
}
func (printer) HandleFrozenCounter(info master.HeaderInfo, v []dnp3.Indexed[dnp3.FrozenCounter]) {
	for _, x := range v {
		say("FROZENCOUNTER g%dv%d index=%d value=%d", info.GV.Group, info.GV.Variation, x.Index, x.Value.Value)
	}
}
func (printer) HandleAnalog(info master.HeaderInfo, v []dnp3.Indexed[dnp3.Analog]) {
	for _, x := range v {
		say("ANALOG g%dv%d index=%d value=%v", info.GV.Group, info.GV.Variation, x.Index, x.Value.Value)
	}
}
func (printer) HandleFrozenAnalog(info master.HeaderInfo, v []dnp3.Indexed[dnp3.Analog]) {
	for _, x := range v {
		say("FROZENANALOG g%dv%d index=%d value=%v", info.GV.Group, info.GV.Variation, x.Index, x.Value.Value)
	}
}
func (printer) HandleCommandEvent(info master.HeaderInfo, v []dnp3.Indexed[dnp3.CommandEvent]) {
	for _, x := range v {
		say("COMMANDEVENT g%dv%d index=%d status=%d state=%v value=%v analog=%v", info.GV.Group, info.GV.Variation, x.Index, x.Value.Status, x.Value.State, x.Value.Value, x.Value.Analog)
	}
}
func (printer) HandleDataset(info master.HeaderInfo, data []byte) {
	say("DATASET g%dv%d data=%s", info.GV.Group, info.GV.Variation, hex.EncodeToString(data))
}
func (printer) HandleOctetString(info master.HeaderInfo, v []dnp3.Indexed[dnp3.OctetString]) {
	for _, x := range v {
		say("OCTETS g%dv%d index=%d data=%s", info.GV.Group, info.GV.Variation, x.Index, hex.EncodeToString(x.Value))
	}
}

func fail(format string, args ...any) {
	say("FAIL "+format, args...)
	os.Exit(1)
}

func runMaster(args []string) {
	fs := flag.NewFlagSet("master", flag.ExitOnError)
	addr := fs.String("addr", "127.0.0.1:20000", "outstation address")
	scenario := fs.String("scenario", "", "what to do")
	key := fs.String("key", "", "hex update key for user 1; enables Secure Authentication")
	remote := fs.Uint("remote", 10, "outstation link address (0xFFFC for self-address discovery)")
	_ = fs.Parse(args)

	cfg := master.Config{LocalAddr: 1, RemoteAddr: uint16(*remote), ResponseTimeout: 4 * time.Second,
		FileCredentials: &master.FileCredentials{User: "admin", Password: "pw"}}
	if *key != "" {
		k, err := hex.DecodeString(*key)
		if err != nil {
			fail("bad key: %v", err)
		}
		cfg.SecureAuthentication = &master.SecureAuthenticationConfig{User: 1, UpdateKey: k}
	}
	if *scenario == "files-open" {
		cfg.FileCredentials = nil
	}
	m := master.New(cfg, printer{})
	ctx, cancel := context.WithTimeout(context.Background(), 25*time.Second)
	defer cancel()
	go func() { _ = m.Run(ctx, channel.TCPClient(*addr, channel.DefaultRetry)) }()
	for deadline := time.Now().Add(10 * time.Second); !m.Connected(); {
		if time.Now().After(deadline) {
			fail("never connected")
		}
		time.Sleep(20 * time.Millisecond)
	}

	check := func(what string, err error) {
		if err != nil {
			fail("%s: %v", what, err)
		}
		say("OK %s", what)
	}

	switch *scenario {
	case "poll":
		check("integrity", m.IntegrityPoll(ctx))
		check("events", m.ScanClasses(ctx, dnp3.Class123))
	case "events":
		check("events", m.ScanClasses(ctx, dnp3.Class123))
	case "freeze":
		check("freeze", m.FreezeCounters(ctx, false))
		check("freeze-clear", m.FreezeCounters(ctx, true))
		check("freeze-at-time", m.FreezeAtTime(ctx, time.Now().Add(400*time.Millisecond), 0))
		time.Sleep(900 * time.Millisecond)
		check("scan-frozen", m.ScanRange(ctx, 21, 0, 0, 2))
		check("scan-frozen-analog", m.ScanRange(ctx, 31, 0, 0, 3))
	case "recorded-time":
		check("sync-time-recorded", m.SyncTimeRecorded(ctx))
	case "ranges":
		check("time-intervals", m.ScanRange(ctx, 50, 4, 0, 1))
		check("datasets", m.ScanRange(ctx, 87, 0, 0, 0))
		check("descriptors", m.ScanRange(ctx, 86, 1, 0, 0))
		check("terminals", m.ScanRange(ctx, 112, 0, 0, 0))
		check("iin", m.ScanRange(ctx, 80, 1, 0, 15))
	case "attribute-write":
		check("write", m.WriteAttribute(ctx, objects.StringAttribute(247, "from-go")))
		a, err := m.ReadAttribute(ctx, 0, 247)
		check("read", err)
		say("ATTRIBUTE variation=247 text=%s", a.Text)
	case "control":
		res, err := m.SelectAndOperate(ctx, master.Trip(1, 100))
		check("select-operate", err)
		if !res.OK() {
			fail("command not accepted: %v", res)
		}
		check("events", m.ScanClasses(ctx, dnp3.Class123))
	case "files":
		var data bytes.Buffer
		for range 3000 {
			data.WriteString("0123456789")
		}
		check("write", m.WriteFileBytes(ctx, "upload.bin", data.Bytes()))
		back, err := m.ReadFileBytes(ctx, "upload.bin")
		check("read", err)
		say("FILE size=%d equal=%v", len(back), bytes.Equal(back, data.Bytes()))
		check("delete", m.DeleteFile(ctx, "upload.bin"))
	case "files-open":
		// No credentials configured: a protected outstation must refuse.
		if _, err := m.ReadFileBytes(ctx, "hello.txt"); err == nil {
			fail("an unauthenticated open succeeded")
		}
		say("OK refused")
	case "self-address":
		check("poll", m.ScanClasses(ctx, dnp3.Class0))
	default:
		fail("unknown scenario %q", *scenario)
	}
	say("DONE")
}
