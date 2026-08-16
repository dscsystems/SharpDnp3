// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Drives an outstation with hand-built request fragments and checks the
// fragments it sends back.
//
// The integration tests prove a master and an outstation agree with each other,
// which is necessary but not sufficient: two implementations sharing a
// misreading of the standard agree perfectly. These tests instead assert what
// the standard says an outstation must do, working from octets a master never
// has to construct — an unknown function code, a request for a group the device
// does not have, a broadcast.
//
// They are modelled on the DNP Users Group's Level 2 procedures. They are not a
// substitute for certified conformance testing, and nothing here should be read
// as a conformance claim.

using SharpDnp3.App;
using SharpDnp3.Channels;
using SharpDnp3.Objects;
using SharpDnp3.Outstation;
using SharpDnp3.Stack;

namespace SharpDnp3.Conformance.Tests;

/// <summary>The two link addresses every procedure uses.</summary>
internal static class Addresses
{
    public const ushort Master = 1;
    public const ushort Outstation = 10;
}

/// <summary>Records what the outstation was asked to operate.</summary>
internal sealed class RecordingCommandHandler : ICommandHandler
{
    private readonly Lock _gate = new();

    public int Selects { get; private set; }

    public int Operates { get; private set; }

    public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c)
    {
        lock (_gate)
        {
            Selects++;
        }

        return CommandStatus.Success;
    }

    public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op)
    {
        lock (_gate)
        {
            Operates++;
        }

        return CommandStatus.Success;
    }

    public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v)
    {
        lock (_gate)
        {
            Selects++;
        }

        return CommandStatus.Success;
    }

    public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op)
    {
        lock (_gate)
        {
            Operates++;
        }

        return CommandStatus.Success;
    }
}

/// <summary>
/// Drives an outstation over a pipe using the raw stack, so a test can send
/// exactly the octets it means to.
/// </summary>
internal sealed class Harness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IChannel _masterChannel;
    private readonly IChannel _outstationChannel;
    private readonly Stream _conn;
    private readonly ProtocolStack _txStack;
    private readonly BufferSink _sink = new();
    private readonly Task _outstationTask;
    private readonly Task _readTask;

    private readonly Lock _gate = new();
    private readonly List<byte[]> _fragments = [];

    public OutstationSession Outstation { get; }

    /// <summary>The sequence number the last request went out with.</summary>
    public byte Seq { get; private set; }

    public Harness(OutstationConfig config, ICommandHandler? commands = null)
    {
        if (config.LocalAddr == 0)
        {
            config.LocalAddr = Addresses.Outstation;
        }

        if (config.RemoteAddr == 0)
        {
            config.RemoteAddr = Addresses.Master;
        }

        if (config.ConfirmTimeout == TimeSpan.Zero)
        {
            config.ConfirmTimeout = TimeSpan.FromSeconds(1);
        }

        var (masterChannel, outstationChannel) = Pipe.Create();
        _masterChannel = masterChannel;
        _outstationChannel = outstationChannel;

        Outstation = new OutstationSession(config, null, commands);
        _outstationTask = Outstation.RunAsync(_outstationChannel, _cts.Token);

        _conn = _masterChannel.ConnectAsync(_cts.Token).GetAwaiter().GetResult();

        _txStack = new ProtocolStack(new StackConfig
        {
            LocalAddr = Addresses.Master,
            RemoteAddr = Addresses.Outstation,
            IsMaster = true,
        });

        // A reader collecting whatever the outstation sends, so a test can
        // assert on unsolicited traffic as well as on answers.
        _readTask = ReadLoopAsync(_cts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buf = new byte[ProtocolStack.ReadChunk];
        var rxStack = new ProtocolStack(new StackConfig
        {
            LocalAddr = Addresses.Master,
            RemoteAddr = Addresses.Outstation,
            IsMaster = true,
        });
        var discard = new BufferSink();

        try
        {
            while (true)
            {
                var n = await _conn.ReadAsync(buf, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    return;
                }

                rxStack.Receive(discard, buf.AsSpan(0, n), r =>
                {
                    lock (_gate)
                    {
                        _fragments.Add(r.Fragment.ToArray());
                    }
                });

                discard.Clear();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or Dnp3Exception)
        {
            // The harness is going away.
        }
    }

    private async Task FlushAsync()
    {
        if (_sink.IsEmpty)
        {
            return;
        }

        var pending = _sink.Pending.ToArray();
        _sink.Clear();
        await _conn.WriteAsync(pending, _cts.Token).ConfigureAwait(false);
        await _conn.FlushAsync(_cts.Token).ConfigureAwait(false);
    }

    /// <summary>Transmits a request built from a function code and objects.</summary>
    public async Task SendAsync(FuncCode fc, params ObjectHeader[] objects)
    {
        Seq = (byte)((Seq + 1) % AppConstants.SeqModulus);
        var frag = FragmentFactory.BuildRequest(
            new AppControl(Fir: true, Fin: true, Con: false, Uns: false, Seq), fc, objects);

        _txStack.Send(_sink, frag);
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Transmits a request to a specific link address, for broadcast tests.
    /// </summary>
    public async Task SendToAsync(ushort dest, FuncCode fc, params ObjectHeader[] objects)
    {
        Seq = (byte)((Seq + 1) % AppConstants.SeqModulus);
        var frag = FragmentFactory.BuildRequest(
            new AppControl(Fir: true, Fin: true, Con: false, Uns: false, Seq), fc, objects);

        _txStack.SendTo(_sink, dest, frag);
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Acknowledges a response so the outstation may drop its events.
    /// </summary>
    public async Task SendConfirmAsync(byte seq)
    {
        var dst = new List<byte>(AppConstants.RequestHeaderSize);
        HeaderCodec.AppendHeader(dst, new AppHeader(
            new AppControl(Fir: true, Fin: true, Con: false, Uns: false, seq),
            FuncCode.Confirm,
            Iin.None));

        _txStack.Send(_sink, [.. dst]);
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>How many fragments have arrived.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _fragments.Count;
            }
        }
    }

    /// <summary>
    /// Waits for the next response fragment beyond those already seen.
    /// </summary>
    public async Task<Fragment> AwaitAsync(int after)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            byte[]? raw = null;
            lock (_gate)
            {
                if (_fragments.Count > after)
                {
                    raw = _fragments[after];
                }
            }

            if (raw is not null)
            {
                var status = FragmentParser.ParseFragment(null, raw, out var frag, out var error);
                Assert.True(
                    status == AppParseStatus.Ok,
                    $"the outstation sent a fragment its own parser rejects: {error}");
                Assert.True(frag.Header.IsResponse, "expected a response fragment");
                return frag;
            }

            await Task.Delay(2).ConfigureAwait(false);
        }

        Assert.Fail("no response within 3s");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Sends and returns the single response it produces.</summary>
    public async Task<Fragment> RequestAsync(FuncCode fc, params ObjectHeader[] objects)
    {
        var before = Count;
        await SendAsync(fc, objects).ConfigureAwait(false);
        return await AwaitAsync(before).ConfigureAwait(false);
    }

    /// <summary>Waits for a condition, failing the test if it never holds.</summary>
    public static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(2).ConfigureAwait(false);
        }

        Assert.Fail($"{what} did not happen within 3s");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _conn.Dispose();
        _masterChannel.Close();
        _outstationChannel.Close();

        await Task.WhenAny(Task.WhenAll(_outstationTask, _readTask), Task.Delay(2000))
            .ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>Shared builders the procedures use.</summary>
internal static class Requests
{
    public static DatabaseConfig SmallDatabase() => new()
    {
        Binary = 4,
        Analog = 3,
        Counter = 2,
        BinaryOutputStatus = 2,
        AnalogOutputStatus = 2,
        DefaultClass = Class.Class1,
    };

    /// <summary>Builds a one-command CROB header with a one-octet index prefix.</summary>
    public static ObjectHeader CrobHeader(byte index, ControlCode code)
    {
        var data = new List<byte> { index };
        CommandObjects.AppendCrob(data, new ControlRelayOutputBlock { Code = code, Count = 1 });

        return new ObjectHeader
        {
            Group = 12,
            Variation = 1,
            Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data.ToArray(),
        };
    }

    /// <summary>Builds the write that clears the DEVICE_RESTART indication.</summary>
    public static ObjectHeader ClearRestart() => new()
    {
        Group = 80,
        Variation = 1,
        Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.StartStop8),
        Range = new ObjectRange { Spec = RangeSpec.StartStop8, Start = 7, Stop = 7, Count = 1 },
        Data = new byte[] { 0x00 },
    };

    /// <summary>Builds a group 50 time write of the given variation.</summary>
    public static ObjectHeader TimeWrite(byte variation, DateTimeOffset when)
    {
        var ms = Dnp3Time.ToDnp3(when);
        return new ObjectHeader
        {
            Group = 50,
            Variation = variation,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = new[]
            {
                (byte)ms, (byte)(ms >> 8), (byte)(ms >> 16),
                (byte)(ms >> 24), (byte)(ms >> 32), (byte)(ms >> 40),
            },
        };
    }

    /// <summary>Reads the status out of the outstation's command echo.</summary>
    public static CommandStatus CommandStatusOf(Fragment resp)
    {
        foreach (var o in resp.Objects)
        {
            if (o.Group is not (12 or 41))
            {
                continue;
            }

            if (!ObjectRegistry.TryLookup(GroupVar.GV(o.Group, o.Variation), out var d))
            {
                continue;
            }

            d.TrySizeOctets(out var size);
            var prefix = o.Qualifier.IndexPrefix.Octets();
            if (o.Data.Length < prefix + size)
            {
                continue;
            }

            return (CommandStatus)o.Data.Span[prefix + size - 1];
        }

        Assert.Fail("the response carried no command echo");
        throw new InvalidOperationException("unreachable");
    }
}
