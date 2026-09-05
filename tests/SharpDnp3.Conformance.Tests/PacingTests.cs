// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The procedures covering how an outstation paces what it sends and decides
// what it will act on: the fragment series, the application confirmation, the
// retransmitted request, and the request that asks for nothing.
//
// These are the cases a master never produces when everything is working, and
// so the ones an implementation is most likely to get wrong: a repeated OPERATE
// that operates a breaker twice, a confirm for the first fragment of a series
// that discards events carried by the third, a half-received request acted on
// as though it were whole.

using SharpDnp3.App;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class PacingTests
{
    /// <summary>
    /// A master retransmits a request when it does not see the response,
    /// reusing the sequence number so the outstation can recognise the repeat.
    /// The repeat must be answered from the stored response, not executed
    /// again: running it twice operates the point twice.
    /// </summary>
    [Fact]
    public async Task DuplicateRequestIsNotExecutedTwice()
    {
        var commands = new RecordingCommandHandler();
        await using var h = new Harness(
            new OutstationConfig { Database = Requests.SmallDatabase() }, commands);

        var control = new AppControl(Fir: true, Fin: true, Con: false, Uns: false, Seq: 3);
        var crob = Requests.CrobHeader(0, ControlCode.LatchOn);

        var before = h.Count;
        await h.SendWithControlAsync(control, FuncCode.DirectOperate, crob);
        var first = await h.AwaitAsync(before);

        // The identical octets again, under the identical sequence number.
        await h.SendWithControlAsync(control, FuncCode.DirectOperate, crob);
        var second = await h.AwaitAsync(before + 1);

        Assert.Equal(1, commands.Operates);
        Assert.Equal(first.Raw.ToArray(), second.Raw.ToArray());
    }

    /// <summary>
    /// A request whose sequence number repeats but whose octets differ is new
    /// work, and must be executed rather than answered from the store.
    /// </summary>
    [Fact]
    public async Task RepeatedSequenceWithDifferentObjectsIsExecuted()
    {
        var commands = new RecordingCommandHandler();
        await using var h = new Harness(
            new OutstationConfig { Database = Requests.SmallDatabase() }, commands);

        var control = new AppControl(Fir: true, Fin: true, Con: false, Uns: false, Seq: 3);

        var before = h.Count;
        await h.SendWithControlAsync(
            control, FuncCode.DirectOperate, Requests.CrobHeader(0, ControlCode.LatchOn));
        await h.AwaitAsync(before);

        await h.SendWithControlAsync(
            control, FuncCode.DirectOperate, Requests.CrobHeader(1, ControlCode.LatchOff));
        await h.AwaitAsync(before + 1);

        Assert.Equal(2, commands.Operates);
    }

    /// <summary>
    /// Only a fragment carrying both FIR and FIN is a complete request. One
    /// with FIR clear continues a series whose beginning the outstation never
    /// saw, so acting on it means operating a point on half a message.
    /// </summary>
    [Fact]
    public async Task RequestFragmentWithoutFirIsNotExecuted()
    {
        var commands = new RecordingCommandHandler();
        await using var h = new Harness(
            new OutstationConfig { Database = Requests.SmallDatabase() }, commands);

        await h.SendWithControlAsync(
            new AppControl(Fir: false, Fin: true, Con: false, Uns: false, Seq: 1),
            FuncCode.DirectOperate,
            Requests.CrobHeader(0, ControlCode.LatchOn));

        // Nothing is executed and nothing is answered; the indication rides on
        // the next response instead.
        await Task.Delay(150);
        Assert.Equal(0, commands.Operates);
        Assert.Equal(0, h.Count);

        var resp = await h.RequestAsync(FuncCode.DelayMeasure);
        Assert.True(resp.Header.Iin.Has(Iin.ParameterError));
    }

    /// <summary>The same holds for a fragment that is not the last of a series.</summary>
    [Fact]
    public async Task RequestFragmentWithoutFinIsNotExecuted()
    {
        var commands = new RecordingCommandHandler();
        await using var h = new Harness(
            new OutstationConfig { Database = Requests.SmallDatabase() }, commands);

        await h.SendWithControlAsync(
            new AppControl(Fir: true, Fin: false, Con: false, Uns: false, Seq: 1),
            FuncCode.DirectOperate,
            Requests.CrobHeader(0, ControlCode.LatchOn));

        await Task.Delay(150);
        Assert.Equal(0, commands.Operates);
        Assert.Equal(0, h.Count);
    }

    /// <summary>
    /// A read that names nothing to read asked for nothing, which is not the
    /// same as having nothing to do. Answering with an empty success would tell
    /// the master its request was carried out.
    /// </summary>
    [Theory]
    [InlineData(FuncCode.Read)]
    [InlineData(FuncCode.Write)]
    [InlineData(FuncCode.Select)]
    [InlineData(FuncCode.Operate)]
    [InlineData(FuncCode.DirectOperate)]
    [InlineData(FuncCode.AssignClass)]
    [InlineData(FuncCode.EnableUnsolicited)]
    [InlineData(FuncCode.DisableUnsolicited)]
    public async Task EmptyRequestsThatRequireObjectsAreRejected(FuncCode fc)
    {
        var commands = new RecordingCommandHandler();
        await using var h = new Harness(
            new OutstationConfig { Database = Requests.SmallDatabase() }, commands);

        var resp = await h.RequestAsync(fc);

        Assert.True(
            resp.Header.Iin.Has(Iin.ParameterError),
            $"{fc.ToDisplayString()} with no objects should set PARAMETER_ERROR");
        Assert.Empty(resp.Objects);
        Assert.Equal(0, commands.Operates);
        Assert.Equal(0, commands.Selects);
    }

    /// <summary>
    /// The codes that legitimately carry nothing must still succeed: an empty
    /// freeze means every counter rather than none, and the restarts and time
    /// procedures take no objects at all.
    /// </summary>
    [Theory]
    [InlineData(FuncCode.DelayMeasure)]
    [InlineData(FuncCode.RecordCurrentTime)]
    [InlineData(FuncCode.ImmedFreeze)]
    public async Task EmptyRequestsThatNeedNoObjectsStillSucceed(FuncCode fc)
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = Requests.SmallDatabase(),
        });

        // Clear the restart indication first, so the only indication left to
        // look at is the one this request would raise.
        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        var resp = await h.RequestAsync(fc);
        Assert.False(
            resp.Header.Iin.Has(Iin.ParameterError),
            $"{fc.ToDisplayString()} takes no objects and must not be a parameter error");
    }

    /// <summary>
    /// Every fragment of a response series carries the request's own sequence
    /// number, so a confirm for the first cannot be told apart from one for the
    /// third. The events must therefore survive until the whole series has been
    /// sent and confirmed — dropping them on the first confirm loses whatever
    /// the later fragments carried.
    /// </summary>
    [Fact]
    public async Task IntermediateConfirmDoesNotDiscardLaterEvents()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Binary = 64, DefaultClass = Class.Class1 },

            // Small enough that the events below cannot fit in one fragment.
            MaxTxFragment = 64,
            ConfirmTimeout = TimeSpan.FromSeconds(30),
        });

        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 40; i++)
            {
                db.UpdateBinary(i, new Binary(i % 2 == 0, Flags.Online, default));
            }
        });

        await Harness.WaitForAsync(
            () => h.Outstation.Events?.Total >= 40, "events to be queued");

        var before = h.Count;
        await h.SendAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));

        var first = await h.AwaitAsync(before);
        Assert.True(first.Header.Control.Fir);
        Assert.False(first.Header.Control.Fin);
        Assert.True(first.Header.Control.Con);

        // Confirming the first fragment must release the second, not discard
        // what the second is about to carry.
        await h.SendConfirmAsync(first.Header.Control.Seq);

        var fragments = 1;
        var last = first;
        while (!last.Header.Control.Fin)
        {
            last = await h.AwaitAsync(before + fragments);
            fragments++;
            Assert.False(
                last.Header.Control.Fir,
                "only the first fragment of a series carries FIR");
            await h.SendConfirmAsync(last.Header.Control.Seq);
        }

        Assert.True(fragments > 1, "the response should have spanned several fragments");

        // Every event went out exactly once and none is still queued.
        await Harness.WaitForAsync(
            () => h.Outstation.Events?.Total == 0,
            "the whole series to be confirmed and the events dropped");
    }

    /// <summary>
    /// A response series is paced one fragment at a time. Until the master
    /// confirms the fragment in flight, the next one must not go out.
    /// </summary>
    [Fact]
    public async Task ResponseSeriesWaitsForEachConfirm()
    {
        await using var h = new Harness(new OutstationConfig
        {
            Database = new DatabaseConfig { Binary = 64, DefaultClass = Class.Class1 },
            MaxTxFragment = 64,
            ConfirmTimeout = TimeSpan.FromSeconds(30),
        });

        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        h.Outstation.Update(db =>
        {
            for (ushort i = 0; i < 40; i++)
            {
                db.UpdateBinary(i, new Binary(i % 2 == 0, Flags.Online, default));
            }
        });

        await Harness.WaitForAsync(() => h.Outstation.Events?.Total >= 40, "events to be queued");

        var before = h.Count;
        await h.SendAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));

        var first = await h.AwaitAsync(before);
        Assert.False(first.Header.Control.Fin);

        // Nothing more may arrive while the first fragment is unconfirmed.
        await Task.Delay(250);
        Assert.Equal(before + 1, h.Count);

        await h.SendConfirmAsync(first.Header.Control.Seq);
        await h.AwaitAsync(before + 1);
    }
}
