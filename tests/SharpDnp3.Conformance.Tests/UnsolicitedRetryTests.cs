// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// What an outstation may do when a master does not confirm an unsolicited
// response.
//
// A master tells a retransmission apart from new data by the sequence number
// and nothing else. That single fact fixes the whole of this behaviour: a retry
// must keep the sequence number, and therefore must carry exactly the data the
// first attempt carried, because anything new arriving under a sequence number
// the master has already seen is matched as a duplicate and thrown away.

using SharpDnp3.App;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Tests;

public class UnsolicitedRetryTests
{
    private static OutstationConfig Config() => new()
    {
        Database = new DatabaseConfig { Binary = 8, DefaultClass = Class.Class1 },
        Unsolicited = new UnsolicitedConfig
        {
            Enabled = true,
            ConfirmTimeout = TimeSpan.FromMilliseconds(150),
            MaxRetries = 3,
        },
    };

    /// <summary>
    /// Brings the association to the point where event data may flow: the null
    /// unsolicited response confirmed, and class 1 enabled.
    /// </summary>
    private static async Task<int> ReadyAsync(Harness h)
    {
        var announcement = await h.AwaitAsync(0);
        Assert.Equal(FuncCode.UnsolicitedResponse, announcement.Header.Func);
        Assert.Empty(announcement.Objects);

        await h.SendConfirmAsync(announcement.Header.Control.Seq, unsolicited: true);
        await h.RequestAsync(FuncCode.Write, Requests.ClearRestart());

        var enabled = h.Count;
        await h.RequestAsync(FuncCode.EnableUnsolicited, FragmentFactory.ReadAllObjects(60, 2));
        return h.Count > enabled ? h.Count : enabled;
    }

    /// <summary>
    /// The retry repeats the fragment the master did not confirm, exactly as it
    /// went out the first time.
    /// </summary>
    [Fact]
    public async Task UnsolicitedRetryKeepsItsSequenceNumberAndContent()
    {
        await using var h = new Harness(Config());
        var after = await ReadyAsync(h);

        h.Outstation.Update(db =>
            db.UpdateBinary(0, new Binary(true, Flags.Online, default)));

        var first = await h.AwaitAsync(after);
        Assert.Equal(FuncCode.UnsolicitedResponse, first.Header.Func);
        Assert.NotEmpty(first.Objects);

        // Deliberately not confirmed. More events arrive while the first
        // attempt is outstanding, which is the case that goes wrong: building a
        // fresh response from whatever is queued now would put events the
        // master has never seen behind a sequence number it has already seen.
        h.Outstation.Update(db =>
            db.UpdateBinary(1, new Binary(true, Flags.Online, default)));

        var retry = await h.AwaitAsync(after + 1);

        Assert.Equal(first.Header.Control.Seq, retry.Header.Control.Seq);
        Assert.Equal(first.Raw.ToArray(), retry.Raw.ToArray());
    }

    /// <summary>
    /// Confirming a retry closes out the events it carried, and the ones that
    /// arrived while it was outstanding go out next under a new sequence
    /// number.
    /// </summary>
    [Fact]
    public async Task NewTransmissionsStillAdvanceTheSequence()
    {
        await using var h = new Harness(Config());
        var after = await ReadyAsync(h);

        h.Outstation.Update(db =>
            db.UpdateBinary(0, new Binary(true, Flags.Online, default)));

        var first = await h.AwaitAsync(after);
        await h.SendConfirmAsync(first.Header.Control.Seq, unsolicited: true);

        h.Outstation.Update(db =>
            db.UpdateBinary(1, new Binary(true, Flags.Online, default)));

        var second = await h.AwaitAsync(after + 1);

        Assert.NotEqual(first.Header.Control.Seq, second.Header.Control.Seq);
        Assert.Equal(
            (byte)((first.Header.Control.Seq + 1) % AppConstants.SeqModulus),
            second.Header.Control.Seq);
    }

    /// <summary>
    /// Once the retries are spent the outstation gives up and waits to be
    /// polled — but the events are not lost: they go back in the queue, and a
    /// class 1 read collects them.
    /// </summary>
    [Fact]
    public async Task GivingUpRequeuesTheEventsForTheNextPoll()
    {
        await using var h = new Harness(Config());
        var after = await ReadyAsync(h);

        h.Outstation.Update(db =>
            db.UpdateBinary(0, new Binary(true, Flags.Online, default)));

        await h.AwaitAsync(after);

        // Never confirmed: three retries, then the outstation stops.
        await Harness.WaitForAsync(
            () => h.Outstation.Stats.UnsolicitedTimeouts > 3,
            "the outstation to exhaust its unsolicited retries");

        var before = h.Count;
        await h.SendAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        var poll = await h.AwaitAsync(before);

        Assert.NotEmpty(poll.Objects);
    }

    /// <summary>
    /// With unlimited retries the outstation keeps repeating the response,
    /// unchanged, well past the retry count.
    /// </summary>
    [Fact]
    public async Task UnlimitedRetriesKeepRepeating()
    {
        var config = Config();
        config.Unsolicited.MaxRetries = 1;
        config.Unsolicited.UnlimitedRetries = true;

        await using var h = new Harness(config);
        var after = await ReadyAsync(h);

        h.Outstation.Update(db =>
            db.UpdateBinary(0, new Binary(true, Flags.Online, default)));

        var first = await h.AwaitAsync(after);

        await Harness.WaitForAsync(
            () => h.Outstation.Stats.UnsolicitedTimeouts >= 4,
            "several timeouts past the retry count");

        var latest = await h.AwaitAsync(after + 3);
        Assert.Equal(first.Raw.ToArray(), latest.Raw.ToArray());
    }

    /// <summary>
    /// A read while an unsolicited response awaits confirmation ends the
    /// series at its next timeout, and the events it held go back in the queue
    /// for the next poll instead of being repeated at a master that is plainly
    /// polling.
    /// </summary>
    [Fact]
    public async Task APollEndsTheSeriesAndCollectsItsEvents()
    {
        var config = Config();
        config.Unsolicited.MaxRetries = 50;

        await using var h = new Harness(config);
        var after = await ReadyAsync(h);

        h.Outstation.Update(db =>
            db.UpdateBinary(0, new Binary(true, Flags.Online, default)));

        await h.AwaitAsync(after);

        // The events are held by the unconfirmed response, so this poll does
        // not see them; it does end the series.
        await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        var timeouts = h.Outstation.Stats.UnsolicitedTimeouts;

        await Harness.WaitForAsync(
            () => h.Outstation.Stats.UnsolicitedTimeouts > timeouts,
            "the unsolicited response to time out");

        // Nothing else is sent now the series is over, so the next fragment is
        // the poll's own answer.
        var poll = await h.RequestAsync(FuncCode.Read, FragmentFactory.ReadAllObjects(60, 2));
        Assert.Equal(FuncCode.Response, poll.Header.Func);
        Assert.NotEmpty(poll.Objects);
    }
}
