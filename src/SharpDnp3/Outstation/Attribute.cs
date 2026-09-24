// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Device attributes are the outstation's answer to "what are you?". A master
// commissioning an unfamiliar panel reads them instead of trusting a drawing,
// so an outstation that reports none makes itself harder to install than it
// needs to be.
//
// Two kinds are served. What the application configures — vendor, model, serial
// number, the things only it can know — and what the session can work out for
// itself, which is the point counts and the fragment sizes it was built with.
// Deriving the second kind means they cannot drift from the database they
// describe.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Stack;

namespace SharpDnp3.Outstation;

/// <summary>Identifies an attribute on the wire.</summary>
internal readonly record struct AttributeKey(byte Set, byte Variation);

/// <summary>
/// The group 0 variations this library reports on its own behalf.
/// </summary>
/// <remarks>
/// They are named here rather than left to the display table in
/// <see cref="AttributeNumbers"/> because this is code that has to be right: a
/// number used to answer a request is not a label. The numbers are IEEE
/// 1815-2012's set 0.
/// </remarks>
internal static class DerivedAttributeNumbers
{
    public const byte AnalogOutputCount = 221;
    public const byte BinaryOutputCount = 224;
    public const byte CounterCount = 229;
    public const byte AnalogInputCount = 233;
    public const byte DoubleBitInputCount = 236;
    public const byte BinaryInputCount = 239;
    public const byte MaxTxFragment = 240;
    public const byte MaxRxFragment = 241;
}

public sealed partial class OutstationSession
{
    /// <summary>
    /// Assembles the attribute store from the configuration and from what the
    /// session knows about itself.
    /// </summary>
    /// <remarks>
    /// Configured attributes win. A device that has been told its own serial
    /// number should report that rather than anything this library inferred.
    /// </remarks>
    private static Dictionary<AttributeKey, DeviceAttribute> BuildAttributes(
        OutstationConfig cfg)
    {
        var store = new Dictionary<AttributeKey, DeviceAttribute>();

        foreach (var a in DerivedAttributes(cfg))
        {
            store[new AttributeKey(a.Set, a.Variation)] = a;
        }

        foreach (var a in cfg.Attributes)
        {
            store[new AttributeKey(a.Set, a.Variation)] = a;
        }

        return store;
    }

    /// <summary>
    /// The attributes the session can answer from its own configuration.
    /// </summary>
    /// <remarks>
    /// Only the counts and the fragment sizes: those are facts about this
    /// session, and a master reading them gets numbers that match the database
    /// it is about to poll. Everything else about a device — who made it, what
    /// it is called — is the application's to say.
    /// </remarks>
    private static List<DeviceAttribute> DerivedAttributes(OutstationConfig cfg)
    {
        var db = cfg.Database;

        (byte Variation, int Count)[] counts =
        [
            (DerivedAttributeNumbers.BinaryInputCount, db.Binary),
            (DerivedAttributeNumbers.DoubleBitInputCount, db.DoubleBitBinary),
            (DerivedAttributeNumbers.CounterCount, db.Counter),
            (DerivedAttributeNumbers.AnalogInputCount, db.Analog),
            (DerivedAttributeNumbers.BinaryOutputCount, db.BinaryOutputStatus),
            (DerivedAttributeNumbers.AnalogOutputCount, db.AnalogOutputStatus),
        ];

        var output = new List<DeviceAttribute>(counts.Length + 2);
        foreach (var (variation, n) in counts)
        {
            if (n <= 0)
            {
                // A point type the device does not have is left unreported
                // rather than reported as zero: "none" and "I did not say" are
                // different answers, and only one of them is this session's to
                // give.
                continue;
            }

            output.Add(DeviceAttribute.Uint(variation, (ulong)n));
        }

        output.Add(DeviceAttribute.Uint(
            DerivedAttributeNumbers.MaxTxFragment, (ulong)cfg.MaxTxFragment));
        output.Add(DeviceAttribute.Uint(
            DerivedAttributeNumbers.MaxRxFragment, (ulong)cfg.MaxRxFragment));

        return output;
    }

    /// <summary>
    /// Returns what answers one request, sorted so two reads of the same device
    /// produce the same fragment.
    /// </summary>
    private List<DeviceAttribute> AttributesFor(byte set, byte variation)
    {
        if (variation == AttributeNumbers.All)
        {
            var all = new List<DeviceAttribute>();
            foreach (var (key, a) in _attributes)
            {
                if (key.Set == set)
                {
                    all.Add(a);
                }
            }

            all.Sort((x, y) => x.Variation.CompareTo(y.Variation));
            return all;
        }

        return _attributes.TryGetValue(new AttributeKey(set, variation), out var one)
            ? [one]
            : [];
    }

    /// <summary>Answers a read of group 0.</summary>
    /// <remarks>
    /// Each attribute goes in its own object header, because the variation is
    /// what names it: a response carrying six attributes carries six headers.
    /// </remarks>
    private void OnAttributeRead(Association a, Received r, Fragment frag, ObjectHeader h)
    {
        // The range is the attribute set rather than a point index.
        byte set = 0;
        if (h.Range.Spec.IsStartStop())
        {
            set = (byte)h.Range.Start;
        }

        var attrs = AttributesFor(set, h.Variation);

        if (h.Variation == AttributeNumbers.List)
        {
            // Which attributes exist, rather than what they say: one list of
            // the set's variations, none of them writable because nothing here
            // accepts a write. A set with nothing in it has no list to give.
            var all = AttributesFor(set, AttributeNumbers.All);
            attrs = [];
            if (all.Count > 0)
            {
                var items = new List<AttributeListItem>(all.Count);
                foreach (var one in all)
                {
                    items.Add(new AttributeListItem(one.Variation, Writable: false));
                }

                attrs = [AttributeObjects.ListAttribute(items) with { Set = set }];
            }
        }
        if (attrs.Count == 0)
        {
            a.Iin = a.Iin.Set(Iin.ObjectUnknown);
            a.Log.Log(
                Dnp3LogLevel.Debug,
                "no such device attribute",
                ("set", set), ("variation", h.Variation));
            Respond(a, r, frag.Header, []);
            return;
        }

        var body = new List<byte>(attrs.Count * 16);
        foreach (var attr in attrs)
        {
            var value = new List<byte>(16);
            try
            {
                AttributeObjects.Append(value, attr);
            }
            catch (AttributeException ex)
            {
                // A value this device cannot encode is a configuration mistake,
                // and reporting the rest beats failing the whole read.
                a.Log.Log(
                    Dnp3LogLevel.Warn,
                    "device attribute could not be encoded",
                    ("variation", attr.Variation), ("err", ex.Message));
                a.Iin = a.Iin.Set(Iin.ParameterError);
                continue;
            }

            ObjectHeaderCodec.AppendObjectHeader(body, new ObjectHeader
            {
                Group = 0,
                Variation = attr.Variation,
                Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.StartStop8),
                Range = new ObjectRange
                {
                    Spec = RangeSpec.StartStop8,
                    Start = attr.Set,
                    Stop = attr.Set,
                    Count = 1,
                },
                Data = value.ToArray(),
            });
        }

        lock (_gate)
        {
            _stats.AttributesRead++;
        }

        Respond(a, r, frag.Header, body.ToArray());
    }

    /// <summary>Returns the group 0 header a request carries, if any.</summary>
    private static bool TryAttributeHeader(Fragment frag, out ObjectHeader header)
    {
        foreach (var h in frag.Objects)
        {
            if (h.Group == 0)
            {
                header = h;
                return true;
            }
        }

        header = default;
        return false;
    }
}
