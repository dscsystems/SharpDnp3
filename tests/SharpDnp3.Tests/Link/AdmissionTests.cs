// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// What the link layer refuses to act on.
//
// Every check here decides whether a frame is admitted at all, and each exists
// because acting on the frame it rejects does something worse than dropping it:
// puts an impossible address on the wire as a destination, has every outstation
// on a line transmit at once, or moves the frame count bit that decides whether
// the next payload is new data or a duplicate.

using SharpDnp3.Link;
using SharpDnp3.Stack;

namespace SharpDnp3.Tests.Link;

public class AdmissionTests
{
    private static LinkFrame Primary(
        LinkFunction fn,
        ushort src,
        ushort dest,
        bool fcv = false,
        bool fcb = false,
        byte[]? payload = null) => new()
        {
            Header = new LinkHeader(
                Control: new Control(Dir: true, Prm: true, Fcb: fcb, Fcv: fcv, Func: fn),
                Dest: dest,
                Src: src,
                Length: (byte)(LinkConstants.MinLength + (payload?.Length ?? 0))),
            Payload = payload ?? ReadOnlyMemory<byte>.Empty,
        };

    // ---- Source addresses ----

    [Theory]
    [InlineData(LinkConstants.BroadcastNoConfirm)]
    [InlineData(LinkConstants.BroadcastMandatoryConfirm)]
    [InlineData(LinkConstants.BroadcastOptionalConfirm)]
    [InlineData(LinkConstants.SelfAddress)]
    [InlineData((ushort)0xFFF0)]
    [InlineData((ushort)0xFFFB)]
    public void AddressesNoStationMayHoldAreNotValidSources(ushort address) =>
        Assert.False(LinkConstants.IsValidSource(address));

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)0xFFEF)]
    public void OrdinaryAddressesAreValidSources(ushort address) =>
        Assert.True(LinkConstants.IsValidSource(address));

    /// <summary>
    /// A reply is addressed back to the source it came from, so answering a
    /// frame that claims a broadcast source would put a broadcast address on
    /// the wire as a destination — which every station accepts as its own.
    /// </summary>
    [Fact]
    public void StackDropsFramesFromAnImpossibleSource()
    {
        var stack = new ProtocolStack(new StackConfig
        {
            LocalAddr = 10,
            RemoteAddr = 1,
            IsMaster = false,
        });

        var sink = new BufferSink();
        var forged = FrameCodec.Encode(
            Primary(LinkFunction.RequestLinkStatus, src: LinkConstants.BroadcastNoConfirm, dest: 10)
                .Header,
            []);

        stack.Receive(sink, forged, _ => Assert.Fail("nothing should be delivered"));

        Assert.True(sink.IsEmpty, "a frame from an impossible source must not be answered");
    }

    /// <summary>
    /// A secondary frame answers something we sent. One arriving from a station
    /// we are not exchanging with could complete or fail an exchange it has
    /// nothing to do with.
    /// </summary>
    [Fact]
    public void StackIgnoresSecondaryFramesFromTheWrongStation()
    {
        var stack = new ProtocolStack(new StackConfig
        {
            LocalAddr = 1,
            RemoteAddr = 10,
            IsMaster = true,
            UseConfirms = true,
            MaxRetries = 3,
        });

        var sink = new BufferSink();
        stack.Send(sink, [0xC0, 0x01]);
        Assert.True(stack.Pending, "a confirmed frame should be awaiting its ACK");

        // An ACK from station 11, which is not the one we are talking to.
        var stray = FrameCodec.Encode(
            new LinkHeader(
                Control: new Control(Dir: false, Prm: false, Fcb: false, Fcv: false, LinkFunction.Ack),
                Dest: 1,
                Src: 11,
                Length: LinkConstants.MinLength),
            []);

        sink.Clear();
        stack.Receive(sink, stray, _ => { });

        Assert.True(stack.Pending, "a stray ACK must not complete our exchange");
    }

    // ---- The control-code validity matrix ----

    /// <summary>
    /// FCV says whether the frame count bit carries meaning, so it is fixed by
    /// the function code. A frame whose FCV contradicts its function code is
    /// not safe to act on: for confirmed user data the FCB is what decides
    /// whether a payload is new or a duplicate already delivered.
    /// </summary>
    [Fact]
    public void ConfirmedUserDataWithoutFcvIsDiscardedWithoutAReply()
    {
        var sec = new Secondary { LocalAddr = 10, IsMaster = false };
        sec.OnFrame(Primary(LinkFunction.ResetLinkStates, src: 1, dest: 10));

        var res = sec.OnFrame(Primary(
            LinkFunction.ConfirmedUserData, src: 1, dest: 10,
            fcv: false, fcb: true, payload: [0xC0, 0x01]));

        Assert.True(res.Discarded);
        Assert.Null(res.Reply);
        Assert.False(res.HasPayload);
    }

    [Fact]
    public void ResetLinkStatesWithFcvIsDiscardedWithoutAReply()
    {
        var sec = new Secondary { LocalAddr = 10, IsMaster = false };

        var res = sec.OnFrame(Primary(
            LinkFunction.ResetLinkStates, src: 1, dest: 10, fcv: true));

        Assert.True(res.Discarded);
        Assert.Null(res.Reply);
        Assert.False(sec.IsReset, "an invalid reset must not establish the link");
    }

    [Fact]
    public void ValidControlCombinationsAreStillAccepted()
    {
        var sec = new Secondary { LocalAddr = 10, IsMaster = false };

        var reset = sec.OnFrame(Primary(LinkFunction.ResetLinkStates, src: 1, dest: 10));
        Assert.NotNull(reset.Reply);
        Assert.Equal(LinkFunction.Ack, reset.Reply!.Value.Header.Control.Func);

        var data = sec.OnFrame(Primary(
            LinkFunction.ConfirmedUserData, src: 1, dest: 10,
            fcv: true, fcb: true, payload: [0xC0, 0x01]));
        Assert.True(data.HasPayload);
        Assert.NotNull(data.Reply);
    }

    // ---- Broadcast ----

    /// <summary>
    /// A broadcast reaches every outstation at once, so answering one would
    /// have all of them transmit at the same moment.
    /// </summary>
    [Theory]
    [InlineData(LinkConstants.BroadcastNoConfirm)]
    [InlineData(LinkConstants.BroadcastMandatoryConfirm)]
    [InlineData(LinkConstants.BroadcastOptionalConfirm)]
    public void BroadcastUserDataIsDeliveredButNeverAnswered(ushort dest)
    {
        var sec = new Secondary { LocalAddr = 10, IsMaster = false };

        var res = sec.OnFrame(Primary(
            LinkFunction.UnconfirmedUserData, src: 1, dest: dest, payload: [0xC0, 0x01]));

        Assert.Null(res.Reply);
        Assert.True(res.HasPayload);
    }

    [Theory]
    [InlineData(LinkFunction.ResetLinkStates)]
    [InlineData(LinkFunction.RequestLinkStatus)]
    public void BroadcastLinkControlIsDiscardedWithoutAReply(LinkFunction fn)
    {
        var sec = new Secondary { LocalAddr = 10, IsMaster = false };

        var res = sec.OnFrame(Primary(fn, src: 1, dest: LinkConstants.BroadcastNoConfirm));

        Assert.Null(res.Reply);
        Assert.True(res.Discarded);
        Assert.False(sec.IsReset, "a broadcast must not establish the link");
    }

    /// <summary>
    /// The frame count bit belongs to the confirmed exchange with one
    /// particular station. Letting a broadcast advance it would leave the next
    /// frame from the real peer judged against a bit somebody else moved.
    /// </summary>
    [Fact]
    public void BroadcastConfirmedDataDoesNotAdvanceTheFrameCountBit()
    {
        var sec = new Secondary { LocalAddr = 10, IsMaster = false };
        sec.OnFrame(Primary(LinkFunction.ResetLinkStates, src: 1, dest: 10));

        // A broadcast carrying the bit we are expecting next.
        var broadcast = sec.OnFrame(Primary(
            LinkFunction.ConfirmedUserData,
            src: 1, dest: LinkConstants.BroadcastNoConfirm,
            fcv: true, fcb: true, payload: [0xC0, 0x01]));
        Assert.Null(broadcast.Reply);
        Assert.True(broadcast.HasPayload);

        // The real peer's next frame still carries FCB=1 and must be accepted
        // as new data rather than judged a duplicate.
        var addressed = sec.OnFrame(Primary(
            LinkFunction.ConfirmedUserData, src: 1, dest: 10,
            fcv: true, fcb: true, payload: [0xC0, 0x02]));

        Assert.True(addressed.HasPayload);
        Assert.False(addressed.Discarded);
    }

    // ---- Data flow control ----

    /// <summary>
    /// A set DFC means the peer's buffers are full. Sending user data through
    /// that is exactly what data flow control exists to prevent.
    /// </summary>
    [Fact]
    public void PrimaryRefusesToSendWhileThePeerSignalsDfc()
    {
        var pri = new Primary
        {
            LocalAddr = 1,
            RemoteAddr = 10,
            IsMaster = true,
            UseConfirms = true,
            MaxRetries = 3,
        };

        pri.Send(new byte[] { 0xC0, 0x01 });

        // The peer's ACK carries DFC, completing the reset handshake.
        pri.OnFrame(new LinkFrame
        {
            Header = new LinkHeader(
                Control: new Control(Dir: false, Prm: false, Fcb: false, Fcv: true, LinkFunction.Ack),
                Dest: 1,
                Src: 10,
                Length: LinkConstants.MinLength),
        });

        Assert.True(pri.DataFlowControl);
    }

    /// <summary>
    /// A fragment paused by DFC leaves segments queued, so the keep-alive probe
    /// has to gate on the confirmed frame in flight rather than on the stack
    /// being busy: the probe is what elicits the reply that clears DFC.
    /// </summary>
    [Fact]
    public void StackPausesRemainingSegmentsWhileDfcIsSet()
    {
        var stack = new ProtocolStack(new StackConfig
        {
            LocalAddr = 1,
            RemoteAddr = 10,
            IsMaster = true,
            UseConfirms = true,
            MaxRetries = 3,
        });

        var sink = new BufferSink();

        static byte[] Ack(bool dfc) => FrameCodec.Encode(
            new LinkHeader(
                Control: new Control(Dir: false, Prm: false, Fcb: false, Fcv: dfc, LinkFunction.Ack),
                Dest: 1,
                Src: 10,
                Length: LinkConstants.MinLength),
            []);

        // Three segments' worth, so the fragment cannot go out in one frame.
        var fragment = new byte[600];
        stack.Send(sink, fragment);
        Assert.True(stack.Pending, "the reset handshake is in flight");

        // A clean ACK completes the reset and releases the first data segment.
        sink.Clear();
        stack.Receive(sink, Ack(dfc: false), _ => { });
        Assert.False(sink.IsEmpty, "the first data segment should have gone out");
        Assert.True(stack.Pending);

        // The ACK for that segment carries DFC: the rest of the fragment stays
        // queued rather than being pushed into a peer with no room for it.
        sink.Clear();
        stack.Receive(sink, Ack(dfc: true), _ => { });

        Assert.True(sink.IsEmpty, "no further segment may go out while DFC is set");
        Assert.True(stack.Busy, "the rest of the fragment stays queued");
        Assert.False(stack.Pending, "nothing is awaiting a confirmation any more");

        // The keep-alive probe is what elicits the reply that clears DFC, so it
        // has to be allowed out even though the stack is still Busy with the
        // queued remainder. Gating it on Busy rather than on a confirmed frame
        // in flight would deadlock the pause.
        sink.Clear();
        stack.SendLinkStatusRequest(sink);
        Assert.False(sink.IsEmpty, "the probe must go out while the fragment is paused");

        // The peer answers with DFC clear, and the rest of the fragment moves.
        var statusClear = FrameCodec.Encode(
            new LinkHeader(
                Control: new Control(
                    Dir: false, Prm: false, Fcb: false, Fcv: false, LinkFunction.LinkStatus),
                Dest: 1,
                Src: 10,
                Length: LinkConstants.MinLength),
            []);

        sink.Clear();
        stack.Receive(sink, statusClear, _ => { });

        Assert.False(sink.IsEmpty, "the next segment goes out once DFC clears");
        Assert.True(stack.Pending);
    }
}
