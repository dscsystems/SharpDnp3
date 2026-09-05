// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Device attributes answer the first question anyone has about an unfamiliar
// device: what is it? Vendor, model, firmware, serial number, and how many
// points of each kind it has — read out of the device rather than off a drawing
// that may describe the one it replaced.
//
// The read is one request. A master asks for variation 254, "all attributes",
// and the outstation answers with one object header per attribute it has: the
// variation names the attribute and the range names the set.

using System.Globalization;
using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

public sealed partial class MasterSession
{
    /// <summary>Reads every device attribute in the standard set.</summary>
    /// <remarks>
    /// A device that does not implement attributes answers with
    /// NO_FUNC_CODE_SUPPORT or an object-unknown indication, which comes back
    /// as <see cref="NotSupportedByPeerException"/> — worth distinguishing from
    /// a device that has none, which answers with an empty list.
    /// </remarks>
    public Task<IReadOnlyList<DeviceAttribute>> ReadAttributesAsync(
        CancellationToken cancellationToken = default) =>
        ReadAttributeSetAsync(AttributeNumbers.StandardSet, cancellationToken);

    /// <summary>Reads every attribute in one set.</summary>
    /// <remarks>
    /// Set 0 is the standard's; a device may keep its own in others.
    /// </remarks>
    public Task<IReadOnlyList<DeviceAttribute>> ReadAttributeSetAsync(
        byte set,
        CancellationToken cancellationToken = default) =>
        ReadAttributesCoreAsync(set, AttributeNumbers.All, cancellationToken);

    /// <summary>Reads one named attribute.</summary>
    /// <remarks>
    /// Reading them all is usually better: it is the same one request, and a
    /// device answers it without the master having to know what to ask for.
    /// </remarks>
    public async Task<DeviceAttribute> ReadAttributeAsync(
        byte set,
        byte variation,
        CancellationToken cancellationToken = default)
    {
        if (variation == AttributeNumbers.All)
        {
            throw new BadConfigException(string.Format(
                CultureInfo.InvariantCulture,
                "master: dnp3: invalid configuration: variation {0} asks for all attributes; " +
                "use ReadAttributeSetAsync",
                variation));
        }

        var attrs = await ReadAttributesCoreAsync(set, variation, cancellationToken)
            .ConfigureAwait(false);

        return attrs.Count > 0
            ? attrs[0]
            : throw new Dnp3Exception(string.Format(
                CultureInfo.InvariantCulture,
                "master: the outstation reported no attribute {0} in set {1}", variation, set));
    }

    private async Task<IReadOnlyList<DeviceAttribute>> ReadAttributesCoreAsync(
        byte set,
        byte variation,
        CancellationToken cancellationToken)
    {
        var attrs = new List<DeviceAttribute>();
        Exception? failure = null;

        var t = new MasterTask
        {
            Name = "read-attributes",
            FuncCode = FuncCode.Read,
            Priority = TaskPriority.Command,
            Build = b =>
            {
                // The range is the attribute set, not a point index: group 0 is
                // the one place where an index means something else entirely.
                b.TryAddObject(new ObjectHeader
                {
                    Group = 0,
                    Variation = variation,
                    Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.StartStop8),
                    Range = new ObjectRange
                    {
                        Spec = RangeSpec.StartStop8,
                        Start = set,
                        Stop = set,
                        Count = 1,
                    },
                });
            },
            OnFragment = frag =>
            {
                foreach (var h in frag.Objects)
                {
                    if (h.Group != 0)
                    {
                        continue;
                    }

                    try
                    {
                        attrs.AddRange(DecodeAttributes(h));
                    }
                    catch (MalformedException ex)
                    {
                        failure = ex;
                        return;
                    }
                }
            },
            OnDone = iin =>
            {
                if (iin.Has(Iin.NoFuncCodeSupport))
                {
                    failure = new NotSupportedByPeerException(
                        "master: reading device attributes: dnp3: not supported by peer");
                }
                else if (iin.Has(Iin.ObjectUnknown) && attrs.Count == 0)
                {
                    // The device understood the request and has nothing to
                    // answer it with, which for an attribute read means it does
                    // not keep the one that was asked for.
                    failure = new NotSupportedByPeerException(
                        "master: the outstation does not have that attribute");
                }
            },
        };

        await RunTaskAsync(t, cancellationToken).ConfigureAwait(false);

        return failure is null ? attrs : throw failure;
    }

    /// <summary>Pulls the attributes out of one object header.</summary>
    /// <remarks>
    /// The header's range gives the set, and its count says how many sets the
    /// one variation is being reported for — normally one, since a device
    /// usually keeps its attributes in set 0 alone.
    /// </remarks>
    private static List<DeviceAttribute> DecodeAttributes(ObjectHeader h)
    {
        var count = (int)h.Count;
        if (count == 0)
        {
            count = 1;
        }

        var output = new List<DeviceAttribute>(count);
        var off = 0;
        for (var i = 0; i < count; i++)
        {
            if (off >= h.Data.Length)
            {
                break;
            }

            var set = (byte)h.Range.IndexOf((uint)i);
            var a = AttributeObjects.Parse(set, h.Variation, h.Data.Span[off..], out var n);
            output.Add(a);
            off += n;
        }

        return output;
    }
}
