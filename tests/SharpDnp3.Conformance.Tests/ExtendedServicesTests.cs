// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Frozen analogs, indexed time intervals, writable attributes, self-address
// discovery, virtual terminals, application management, datasets and file
// authentication, driven with hand-built fragments. Each mirrors a procedure in
// go-dnp3's conformance suite.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class ExtendedServicesTests
{
    private static ObjectHeader ReadSet(byte set, byte variation) => new()
    {
        Group = 0,
        Variation = variation,
        Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.StartStop8),
        Range = new ObjectRange { Spec = RangeSpec.StartStop8, Start = set, Stop = set, Count = 1 },
    };

    [Fact]
    public async Task FrozenAnalogLifecycleAndRouting()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Analog = 2, FrozenAnalog = 2 },
        });
        h.Outstation.Update(db =>
        {
            db.Configure(PointType.FrozenAnalog, 1, new PointConfig { Class = Class.Class2, StaticVariation = 8, EventVariation = 8 });
            db.UpdateAnalog(1, new Analog(12.5, Flags.Online, default));
        });
        await Harness.WaitForAsync(() => h.Outstation.Database.TryGetAnalog(1, out var a, out _) && a.Value == 12.5, "analog");

        var r = await h.RequestAsync(FuncCode.FreezeClear, FragmentFactory.ReadRange(30, 0, 1, 1));
        Assert.False(r.Header.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {r.Header.Iin}");
        h.Outstation.Database.TryGetFrozenAnalog(1, out var frozen, out _);
        Assert.Equal(12.5, frozen.Value);
        Assert.True(frozen.Time.IsValid);
        h.Outstation.Database.TryGetAnalog(1, out var running, out _);
        Assert.Equal(0, running.Value);

        r = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(31, 0, 1, 1));
        var o = Assert.Single(r.Objects);
        Assert.Equal((byte)8, o.Variation);
        ObjectRegistry.TryAnalogCodec(GroupVar.GV(31, 8), out var codec);
        Assert.Equal(12.5, codec.Parse(o.Data.Span, default).Value);

        r = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(33, 0));
        Assert.Equal((byte)33, Assert.Single(r.Objects).Group);
        Assert.True(r.Header.Control.Con);
        await h.SendConfirmAsync(r.Header.Control.Seq);
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 0, "the event to be confirmed");
    }

    [Fact]
    public async Task MixedCounterAnalogFreezeSchedule()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Counter = 1, FrozenCounter = 1, Analog = 1, FrozenAnalog = 1 },
        });
        h.Outstation.Update(db =>
        {
            db.UpdateCounter(0, new Counter(19, Flags.Online, default));
            db.UpdateAnalog(0, new Analog(7.5, Flags.Online, default));
        });
        await Harness.WaitForAsync(() => h.Outstation.Database.TryGetAnalog(0, out var a, out _) && a.Value == 7.5, "analog");

        var data = new List<byte>();
        CommandObjects.AppendTime48(data, Timestamp.Now(DateTimeOffset.UtcNow.AddMilliseconds(100)));
        data.AddRange(new byte[4]);
        var time = new ObjectHeader
        {
            Group = 50,
            Variation = 2,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data.ToArray(),
        };
        var r = await h.RequestAsync(
            FuncCode.FreezeAtTime, time, FragmentFactory.ReadAllObjects(20, 0), FragmentFactory.ReadAllObjects(30, 0));
        Assert.False(r.Header.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {r.Header.Iin}");
        await Harness.WaitForAsync(
            () => h.Outstation.Database.TryGetFrozenAnalog(0, out var f, out _) && f.Value == 7.5, "the frozen analog");
        h.Outstation.Database.TryGetFrozenAnalog(0, out var analog, out _);
        h.Outstation.Database.TryGetFrozenCounter(0, out var counter, out _);
        Assert.Equal(19u, counter.Value);
        Assert.Equal(analog.Time.Time, counter.Time.Time);
    }

    [Fact]
    public async Task TimeIinAndIndexedIntervals()
    {
        await using var h = new Harness(new OutstationConfig { Database = new DatabaseConfig { TimeAndInterval = 2 } });
        var r = await h.RequestAsync(
            FuncCode.Read, FragmentFactory.ReadAllObjects(50, 1), FragmentFactory.ReadRange(80, 1, 7, 7));
        Assert.Equal(2, r.Objects.Count);
        Assert.NotEqual(default, CommandObjects.ParseTime48(r.Objects[0].Data.Span).Time);
        Assert.Equal(1, r.Objects[1].Data.Span[0]); // DEVICE_RESTART, packed at bit 0 of a range that starts at 7

        var at = DateTimeOffset.FromUnixTimeSeconds(123456);
        var data = new List<byte> { 1 };
        CommandObjects.AppendTime48(data, Timestamp.Now(at));
        ObjectConvert.AppendUInt32(data, 17);
        data.Add(2);
        var hdr = new ObjectHeader
        {
            Group = 50,
            Variation = 4,
            Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data.ToArray(),
        };
        r = await h.RequestAsync(FuncCode.Write, hdr);
        Assert.False(r.Header.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {r.Header.Iin}");
        Assert.True(h.Outstation.Database.TryGetTimeAndInterval(1, out var v));
        Assert.Equal(17u, v.Interval);
        Assert.Equal(at, v.Time.Time);

        r = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(50, 4, 1, 1));
        Assert.Equal(2, Assert.Single(r.Objects).Data.Span[10]);

        // An interval unit above 9 is not a unit.
        var bad = data.ToArray();
        bad[^1] = 12;
        r = await h.RequestAsync(FuncCode.Write, hdr with { Data = bad });
        Assert.True(r.Header.Iin.Has(Iin.ParameterError), $"IIN = {r.Header.Iin}");
    }

    [Fact]
    public async Task WritableAttributePropertiesAndTypeValidation()
    {
        var cfg = new OutstationConfig { Database = new DatabaseConfig() };
        cfg.Attributes.Add(DeviceAttribute.String(247, "before"));
        cfg.WritableAttributes.Add(new AttributeId(0, 247));
        await using var h = new Harness(cfg);

        var r = await h.RequestAsync(FuncCode.Read, ReadSet(0, 255));
        var list = r.Objects.Where(o => o.Group == 0 && o.Variation == 255)
            .Select(o => AttributeObjects.Parse(0, 255, o.Data.Span, out _))
            .Single();
        Assert.Contains(list.List(), it => it.Variation == 247 && it.Writable);

        var data = new List<byte>();
        AttributeObjects.Append(data, DeviceAttribute.String(247, "after"));
        r = await h.RequestAsync(FuncCode.Write, ReadSet(0, 247) with { Data = data.ToArray() });
        Assert.False(r.Header.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {r.Header.Iin}");

        r = await h.RequestAsync(FuncCode.Read, ReadSet(0, 247));
        var read = AttributeObjects.Parse(0, 247, r.Objects.Single(o => o.Group == 0).Data.Span, out _);
        Assert.Equal("after", read.Text);

        // The value keeps its configured type.
        data.Clear();
        AttributeObjects.Append(data, DeviceAttribute.Uint(247, 5));
        r = await h.RequestAsync(FuncCode.Write, ReadSet(0, 247) with { Data = data.ToArray() });
        Assert.True(r.Header.Iin.Has(Iin.ParameterError), $"IIN = {r.Header.Iin}");
    }

    [Fact]
    public async Task SelfAddressDiscovery()
    {
        await using var h = new Harness(new OutstationConfig { Database = Requests.SmallDatabase(), SelfAddress = true });
        var before = h.Count;
        await h.SendToAsync(0xFFFC, FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        var r = await h.AwaitAsync(before);
        Assert.False(r.Header.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {r.Header.Iin}");
        Assert.NotEmpty(r.Objects);
    }

    [Fact]
    public async Task SelfAddressIsIgnoredUnlessEnabled()
    {
        await using var h = new Harness(new OutstationConfig { Database = Requests.SmallDatabase() });
        var before = h.Count;
        await h.SendToAsync(0xFFFC, FuncCode.Read, FragmentFactory.ReadAllObjects(60, 1));
        await h.ExpectSilenceAsync(before);
    }

    [Fact]
    public async Task VirtualTerminalWriteAndEvent()
    {
        byte[]? got = null;
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { VirtualTerminal = 1, DefaultClass = Class.Class1 },
            TerminalWrite = (_, data) =>
            {
                got = data;
                return true;
            },
        });

        var r = await h.RequestAsync(
            FuncCode.Write, FragmentFactory.ReadRange(112, 3, 0, 0) with { Data = "cmd"u8.ToArray() });
        Assert.False(r.Header.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {r.Header.Iin}");
        Assert.Equal("cmd"u8.ToArray(), got);

        h.Outstation.Update(db => db.UpdateVirtualTerminal(0, "reply"u8));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 1, "the first input");
        h.Outstation.Update(db => db.UpdateVirtualTerminal(0, "reply"u8));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 2, "a successive identical input");

        r = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(113, 0));
        var o = Assert.Single(r.Objects);
        Assert.Equal(2u, o.Count);
        Assert.Equal("reply"u8.ToArray(), o.Data.Span[1..6].ToArray());
        Assert.True(r.Header.Control.Con);
    }

    /// <summary>A static read of a virtual terminal returns what it last held, in the variation that is its length.</summary>
    [Fact]
    public async Task VirtualTerminalStaticReadReturnsTheLastInput()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { VirtualTerminal = 2, DefaultClass = Class.None },
        });
        Assert.Empty((await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(112, 0, 0, 1))).Objects);

        h.Outstation.Update(db => db.UpdateVirtualTerminal(1, "hello"u8));
        Fragment? found = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (found is null && DateTime.UtcNow < deadline)
        {
            var attempt = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadRange(112, 0, 0, 1));
            if (attempt.Objects.Count == 1)
            {
                found = attempt;
            }
        }

        Assert.NotNull(found);
        var o = found.Objects[0];
        Assert.Equal((byte)5, o.Variation);
        Assert.Equal("hello"u8.ToArray(), o.Data.ToArray());
        Assert.Equal(1u, o.Range.Start);
    }

    private sealed class Managing : IManagementHandler
    {
        public List<ManagementOperation> Calls { get; } = [];

        public bool Manage(ManagementOperation operation, byte[] objects)
        {
            Calls.Add(operation);
            return true;
        }
    }

    [Fact]
    public async Task ApplicationManagementAndActivation()
    {
        var mgr = new Managing();
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig(),
            Management = mgr,
            ActivateConfig = files => files is ["device.cfg"]
                ? new ActivationResult(TimeSpan.FromSeconds(2), [new ActivationStatus(0, files[0])])
                : new ActivationResult(TimeSpan.Zero, [new ActivationStatus(1, string.Empty)]),
        });

        foreach (var fc in new[] { FuncCode.InitializeData, FuncCode.InitializeAppl, FuncCode.StartAppl, FuncCode.StopAppl, FuncCode.SaveConfig })
        {
            ObjectHeader[] headers = fc is FuncCode.InitializeAppl or FuncCode.StartAppl or FuncCode.StopAppl
                ? [FreeFormat.Build(90, 1, "app"u8)]
                : [];
            var resp = await h.RequestAsync(fc, headers);
            Assert.False(resp.Header.Iin.HasAny(Iin.RequestErrorMask), $"{fc}: IIN = {resp.Header.Iin}");
        }

        Assert.Equal(5, mgr.Calls.Count);

        var r = await h.RequestAsync(FuncCode.ActivateConfig, FreeFormat.Build(70, 8, "device.cfg"u8));
        var o = Assert.Single(r.Objects);
        Assert.Equal((byte)91, o.Group);
        var v = ActivationCodec.Parse(o.Data.Span);
        Assert.Equal(TimeSpan.FromSeconds(2), v.Delay);
        Assert.Equal("device.cfg", v.Statuses[0].Text);
    }

    [Fact]
    public async Task DatasetStorageReadWriteAndSnapshot()
    {
        var descriptor = new List<byte>();
        DatasetCodec.AppendDescriptor(descriptor, [new DatasetElement(0, 0, 0, [3])]);
        byte[] present = [1, 3, 6, 0, 0, 0, 0, 0, 0, 1, 42];
        byte[]? written = null;

        var cfg = new OutstationConfig
        {
            Database = new DatabaseConfig(),
            DatasetWrite = (g, v, data) =>
            {
                if (g != 87 || v != 1)
                {
                    return false;
                }

                written = data;
                return true;
            },
        };
        cfg.Datasets.Add(new DatasetObject { Group = 85, Variation = 1, Index = 3, Data = descriptor.ToArray() });
        cfg.Datasets.Add(new DatasetObject { Group = 86, Variation = 1, Index = 3, Data = descriptor.ToArray() });
        cfg.Datasets.Add(new DatasetObject { Group = 86, Variation = 2, Index = 3, Data = [1] });
        cfg.Datasets.Add(new DatasetObject { Group = 87, Variation = 1, Index = 3, Data = present });
        await using var h = new Harness(cfg);

        var r = await h.RequestAsync(
            FuncCode.Read,
            FragmentFactory.ReadRange(85, 1, 3, 3),
            FragmentFactory.ReadRange(86, 2, 3, 3),
            FragmentFactory.ReadRange(87, 1, 3, 3));
        Assert.Equal(3, r.Objects.Count);
        Assert.False(r.Header.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {r.Header.Iin}");
        Assert.Equal(present, FreeFormat.FirstObject(r.Objects[2]).ToArray());

        r = await h.RequestAsync(FuncCode.Write, FreeFormat.Build(87, 1, present));
        Assert.False(r.Header.Iin.HasAny(Iin.RequestErrorMask), $"IIN = {r.Header.Iin}");
        Assert.Equal(present, written);

        h.Outstation.Update(db => db.UpdateDataset(
            new DatasetObject { Group = 87, Variation = 1, Index = 3, Data = present }, Class.Class2));
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 1, "the snapshot event");
        r = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(88, 0));
        var o = Assert.Single(r.Objects);
        Assert.Equal((byte)88, o.Group);
        Assert.True(r.Header.Control.Con);
        Assert.Equal(present, FreeFormat.FirstObject(o).ToArray());
        await h.SendConfirmAsync(r.Header.Control.Seq);
        await Harness.WaitForAsync(() => h.Outstation.Events!.Total == 0, "the snapshot to be confirmed");
    }

    [Fact]
    public async Task FileAuthenticationRequiredAndExpires()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sharpdnp3-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "data"), "content");
            var cfg = new OutstationConfig { Database = new DatabaseConfig() };
            cfg.Files.Handler = new DirectoryFileHandler(dir);
            cfg.Files.Timeout = TimeSpan.FromMilliseconds(100);
            cfg.Files.Authenticate = (user, password) => user == "operator" && password == "secret";
            await using var h = new Harness(cfg);

            static ObjectHeader Open(uint key)
            {
                var obj = new List<byte>();
                FileObjects.AppendCommand(obj, new FileCommand
                {
                    Name = "data",
                    Mode = FileOpenMode.Read,
                    MaxBlockSize = 16,
                    Key = key,
                    RequestId = 1,
                });
                return FreeFormat.Build(70, 3, obj.ToArray());
            }

            static FileCommandStatus Status(Fragment f) =>
                FileObjects.ParseCommandStatus(FreeFormat.FirstObject(f.Objects.Single(o => o.Group == 70)).Span);

            var r = await h.RequestAsync(FuncCode.OpenFile, Open(0));
            Assert.Equal(FileStatus.PermissionDenied, Status(r).Status);

            foreach (var password in new[] { "wrong", "secret" })
            {
                var auth = new List<byte>();
                FileObjects.AppendAuth(auth, new FileAuth { User = "operator", Password = password });
                r = await h.RequestAsync(FuncCode.AuthenticateFile, FreeFormat.Build(70, 2, auth.ToArray()));
                var reply = FileObjects.ParseAuth(FreeFormat.FirstObject(Assert.Single(r.Objects)).Span);
                Assert.True(string.IsNullOrEmpty(reply.User) && string.IsNullOrEmpty(reply.Password), "credentials leaked");
                if (password == "wrong")
                {
                    Assert.Equal(0u, reply.Key);
                    continue;
                }

                Assert.NotEqual(0u, reply.Key);
                r = await h.RequestAsync(FuncCode.OpenFile, Open(reply.Key));
                var status = Status(r);
                Assert.Equal(FileStatus.Success, status.Status);

                var close = new List<byte>();
                FileObjects.AppendCommandStatus(close, new FileCommandStatus { Handle = status.Handle });
                r = await h.RequestAsync(FuncCode.CloseFile, FreeFormat.Build(70, 4, close.ToArray()));
                Assert.Equal(FileStatus.Success, Status(r).Status);

                await Task.Delay(120);
                r = await h.RequestAsync(FuncCode.OpenFile, Open(reply.Key));
                Assert.Equal(FileStatus.PermissionDenied, Status(r).Status);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
