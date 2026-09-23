// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// IIN2.0, IIN2.1 and IIN2.2 describe the request a response answers. These pin
// that they are reported for the bad request and gone from the next response,
// and exercise the master's extension requests against a real outstation.

using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Master;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tests;

public sealed class RequestErrorIinTests
{
    private static async Task<(MasterSession Master, CancellationTokenSource Cts)> StartAsync()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (m, o) = Pipe.Create();
        var outstation = new OutstationSession(new OutstationConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            Database = new DatabaseConfig { Binary = 4, Counter = 2, Analog = 2, DefaultClass = Class.Class1 },
        });
        _ = outstation.RunAsync(o, cts.Token);
        var master = new MasterSession(new MasterConfig { LocalAddr = 1, RemoteAddr = 10, ResponseTimeout = TimeSpan.FromSeconds(2) });
        _ = master.RunAsync(m, cts.Token);
        while (!master.Connected)
        {
            await Task.Delay(10, cts.Token);
        }

        await master.IntegrityPollAsync(cts.Token);
        return (master, cts);
    }

    private static ObjectHeader All(byte g, byte v) => FragmentFactory.ReadAllObjects(g, v);

    [Fact]
    public async Task ObjectUnknownIsNotLatched()
    {
        var (master, cts) = await StartAsync();
        using (cts)
        {
            var bad = await master.SendRequestAsync(FuncCode.Read, [All(200, 1)], cancellationToken: cts.Token);
            Assert.True(bad.Iin.Has(Iin.ObjectUnknown));

            var good = await master.SendRequestAsync(FuncCode.Read, [All(60, 1)], cancellationToken: cts.Token);
            Assert.False(good.Iin.Has(Iin.ObjectUnknown));
            Assert.False(good.Iin.HasError());
            cts.Cancel();
        }
    }

    [Fact]
    public async Task NoFuncCodeSupportIsNotLatched()
    {
        var (master, cts) = await StartAsync();
        using (cts)
        {
            var bad = await master.SendRequestAsync(FuncCode.InitializeAppl, [], cancellationToken: cts.Token);
            Assert.True(bad.Iin.Has(Iin.NoFuncCodeSupport));

            var good = await master.SendRequestAsync(FuncCode.Read, [All(60, 1)], cancellationToken: cts.Token);
            Assert.False(good.Iin.HasError());
            cts.Cancel();
        }
    }

    [Fact]
    public async Task ExtensionRequestsAreAccepted()
    {
        var (master, cts) = await StartAsync();
        using (cts)
        {
            await master.FreezeCountersAsync(FreezeMode.Freeze, cancellationToken: cts.Token);
            await master.AssignClassAsync(Class.Class2, 30, cancellationToken: cts.Token);
            await master.ClearRestartAsync(cts.Token);
            var (_, rtt) = await master.MeasureDelayAsync(cts.Token);
            Assert.True(rtt >= TimeSpan.Zero);
            Assert.False(master.LastIin.HasError());
            cts.Cancel();
        }
    }
}
