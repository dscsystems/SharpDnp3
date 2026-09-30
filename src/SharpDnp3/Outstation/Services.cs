// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// Extended outstation services: writable attributes, the current-time and
// internal-indication reads, indexed time intervals, virtual terminals,
// application and configuration management, and datasets.

using System.Buffers.Binary;
using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Stack;

namespace SharpDnp3.Outstation;

/// <summary>Identifies an attribute allowed to accept writes.</summary>
/// <param name="Set">The attribute set.</param>
/// <param name="Variation">The attribute's number.</param>
public readonly record struct AttributeId(byte Set, byte Variation);

/// <summary>An application or configuration function code a device may implement.</summary>
public enum ManagementOperation : byte
{
    /// <summary>INITIALIZE_DATA.</summary>
    InitializeData = 15,

    /// <summary>INITIALIZE_APPL.</summary>
    InitializeApplication = 16,

    /// <summary>START_APPL.</summary>
    StartApplication = 17,

    /// <summary>STOP_APPL.</summary>
    StopApplication = 18,

    /// <summary>SAVE_CONFIG.</summary>
    SaveConfiguration = 19,

    /// <summary>ACTIVATE_CONFIG.</summary>
    ActivateConfiguration = 31,
}

/// <summary>
/// Performs a device-specific application or configuration operation.
/// </summary>
/// <remarks>
/// The payload is the complete object section of the request, copied for the
/// handler. A false result refuses the operation with PARAMETER_ERROR. With no
/// handler configured the outstation reports NO_FUNC_CODE_SUPPORT.
/// </remarks>
public interface IManagementHandler
{
    /// <summary>Carries out the operation.</summary>
    bool Manage(ManagementOperation operation, byte[] objects);
}

public sealed partial class OutstationSession
{
    private bool AttributeWritable(byte set, byte variation)
    {
        if (variation == 0 || variation >= AttributeNumbers.All)
        {
            return false;
        }

        foreach (var id in _cfg.WritableAttributes)
        {
            if (id.Set == set && id.Variation == variation)
            {
                return true;
            }
        }

        return false;
    }

    private void WriteAttribute(Association a, ObjectHeader h)
    {
        // An attribute range names its set. Require one set and one value.
        if (!h.Range.Spec.IsStartStop() || h.Range.Start != h.Range.Stop || h.Range.Start > 255 ||
            h.Count != 1 || h.Qualifier.IndexPrefix != IndexPrefix.None)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        var set = (byte)h.Range.Start;
        var key = new AttributeKey(set, h.Variation);
        DeviceAttribute old;
        lock (_attributes)
        {
            if (!_attributes.TryGetValue(key, out old))
            {
                a.Iin = a.Iin.Set(Iin.ObjectUnknown);
                return;
            }
        }

        if (!AttributeObjects.TryParse(set, h.Variation, h.Data.Span, out var value, out var consumed, out _) ||
            consumed != h.Data.Length || old.Type != value.Type || !AttributeWritable(set, h.Variation))
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        if (_cfg.AttributeWrite is { } callback)
        {
            var copy = value with { Octets = value.Octets.ToArray() };
            if (!callback(copy))
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
                return;
            }
        }

        lock (_attributes)
        {
            _attributes[key] = value with { Octets = value.Octets.ToArray() };
        }
    }

    private void ReadCurrentTime(Association a, ResponseBuilder b, ObjectHeader h)
    {
        if (h.Range.Spec != RangeSpec.AllObjects &&
            (!h.Range.Spec.IsCount() || h.Count != 1 || h.Qualifier.IndexPrefix != IndexPrefix.None))
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        var data = new List<byte>(CommandObjects.Time48Size);
        CommandObjects.AppendTime48(data, Timestamp.Now(_appl.Now()));
        b.Add(new ObjectHeader
        {
            Group = 50,
            Variation = 1,
            Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
            Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
            Data = data.ToArray(),
        });
    }

    private void ReadIin(Association a, ResponseBuilder b, ObjectHeader h)
    {
        uint start = 0;
        uint stop = 15;
        if (h.Range.Spec.IsStartStop())
        {
            start = h.Range.Start;
            stop = h.Range.Stop;
        }
        else if (h.Range.Spec != RangeSpec.AllObjects)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        if (start > stop || stop > 15)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        var bits = (ushort)(CurrentIin(a).Value >> (int)start);
        var data = new byte[(stop - start + 8) / 8];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(bits >> (8 * i));
        }

        var n = (stop - start + 1) % 8;
        if (n != 0)
        {
            data[^1] &= (byte)((1 << (int)n) - 1);
        }

        b.Add(ResponseWriter.RangeObjectHeader(GroupVar.GV(80, 1), (ushort)start, (ushort)stop, data));
    }

    /// <summary>Walks a data-bearing header, respecting index prefixes.</summary>
    private static bool EachIndexedValue(ObjectHeader h, int size, Action<uint, ReadOnlyMemory<byte>> fn)
    {
        var prefix = h.Qualifier.IndexPrefix.Octets();
        if (size <= 0 ||
            (!h.Range.Spec.IsStartStop() && (!h.Range.Spec.IsCount() || !h.Qualifier.IndexPrefix.IsIndex())))
        {
            return false;
        }

        if ((ulong)h.Count * (ulong)(size + prefix) != (ulong)h.Data.Length)
        {
            return false;
        }

        for (uint i = 0; i < h.Count; i++)
        {
            var off = (int)i * (size + prefix);
            var index = h.Range.Start + i;
            if (prefix > 0)
            {
                index = prefix == 1
                    ? h.Data.Span[off]
                    : BinaryPrimitives.ReadUInt16LittleEndian(h.Data.Span[off..]);
            }

            fn(index, h.Data.Slice(off + prefix, size));
        }

        return true;
    }

    private void ReadTimeIntervals(Association a, ResponseBuilder b, ObjectHeader h)
    {
        var count = _db.Counts().TimeAndInterval;
        if (!TryForEachPointRun(h, (start, stop) =>
        {
            for (var i = (int)start; i <= stop && i < count; i++)
            {
                _db.TryGetTimeAndInterval((ushort)i, out var v);
                var data = new List<byte>(11);
                CommandObjects.AppendTime48(data, v.Time);
                ObjectConvert.AppendUInt32(data, v.Interval);
                data.Add(v.Units);
                b.Add(ResponseWriter.RangeObjectHeader(GroupVar.GV(50, 4), (ushort)i, (ushort)i, data.ToArray()));
            }
        }))
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
        }
    }

    private void WriteTimeIntervals(Association a, ObjectHeader h)
    {
        var ok = EachIndexedValue(h, 11, (index, data) =>
        {
            var span = data.Span;
            if (index > 65535 || span[10] > 9 || !_db.UpdateTimeAndInterval(
                    (ushort)index,
                    new TimeAndInterval(
                        CommandObjects.ParseTime48(span),
                        BinaryPrimitives.ReadUInt32LittleEndian(span[6..10]),
                        span[10])))
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
            }
        });
        if (!ok)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
        }
    }

    private void ReadTerminals(Association a, ResponseBuilder b, ObjectHeader h)
    {
        var count = _db.Counts().VirtualTerminal;
        if (!TryForEachPointRun(h, (start, stop) =>
        {
            for (var i = (int)start; i <= stop && i < count; i++)
            {
                _db.TryGetVirtualTerminal((ushort)i, out var v);
                if (v.Length > 0)
                {
                    b.Add(ResponseWriter.RangeObjectHeader(
                        GroupVar.GV(112, (byte)v.Length), (ushort)i, (ushort)i, v));
                }
            }
        }))
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
        }
    }

    private void WriteTerminals(Association a, ObjectHeader h)
    {
        if (_cfg.TerminalWrite is not { } write)
        {
            a.Iin = a.Iin.Set(Iin.NoFuncCodeSupport);
            return;
        }

        var terminals = _db.Counts().VirtualTerminal;
        var ok = EachIndexedValue(h, h.Variation, (index, data) =>
        {
            if (index >= (uint)terminals || !write((ushort)index, data.ToArray()))
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
            }
        });
        if (!ok)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
        }
    }

    private void OnManagement(Association a, Received r, Fragment frag)
    {
        if (frag.Header.Func == FuncCode.ActivateConfig && _cfg.ActivateConfig is { } activate)
        {
            var names = new List<string>();
            foreach (var h in frag.Objects)
            {
                if (h.Group != 70 || h.Variation != 8 || h.Qualifier != FreeFormat.Qualifier)
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    Respond(a, r, frag.Header, []);
                    return;
                }

                List<ReadOnlyMemory<byte>> data;
                try
                {
                    data = FreeFormat.Objects(h);
                }
                catch (MalformedException)
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    Respond(a, r, frag.Header, []);
                    return;
                }

                foreach (var v in data)
                {
                    if (v.Length == 0)
                    {
                        a.Iin = a.Iin.Set(Iin.ParameterError);
                        Respond(a, r, frag.Header, []);
                        return;
                    }

                    names.Add(System.Text.Encoding.UTF8.GetString(v.Span));
                }
            }

            if (names.Count == 0)
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
                Respond(a, r, frag.Header, []);
                return;
            }

            var value = new List<byte>();
            try
            {
                ActivationCodec.Append(value, activate(names));
            }
            catch (BadConfigException)
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
                Respond(a, r, frag.Header, []);
                return;
            }

            var header = new ObjectHeader
            {
                Group = 91,
                Variation = 1,
                Qualifier = Qualifier.Make(IndexPrefix.None, RangeSpec.Count8),
                Range = new ObjectRange { Spec = RangeSpec.Count8, Count = 1 },
                Data = value.ToArray(),
            };
            if (header.Size + AppConstants.ResponseHeaderSize > _cfg.MaxTxFragment)
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
                Respond(a, r, frag.Header, []);
                return;
            }

            var body = new List<byte>();
            ObjectHeaderCodec.AppendObjectHeader(body, header);
            Respond(a, r, frag.Header, body.ToArray());
            return;
        }

        if (_cfg.Management is not { } handler)
        {
            a.Iin = a.Iin.Set(Iin.NoFuncCodeSupport);
        }
        else if (!handler.Manage(
                     (ManagementOperation)(byte)frag.Header.Func,
                     frag.Raw.Span[frag.Header.Size..].ToArray()))
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
        }

        if (r.Broadcast)
        {
            return;
        }

        Respond(a, r, frag.Header, []);
    }

    private void ReadDatasets(Association a, ResponseBuilder b, ObjectHeader h)
    {
        var variation = h.Variation == 0 ? (byte)1 : h.Variation;
        if (!Database.DatasetVariationKnown(h.Group, variation))
        {
            a.Iin = a.Iin.Set(Iin.ObjectUnknown);
            return;
        }

        if (!TryForEachPointRun(h, (start, stop) =>
        {
            for (var i = (int)start; i <= stop; i++)
            {
                if (!_db.TryGetDataset(h.Group, variation, (ushort)i, out var value))
                {
                    continue;
                }

                var header = h.Group == 86 && variation == 2
                    ? ResponseWriter.RangeObjectHeader(GroupVar.GV(86, 2), (ushort)i, (ushort)i, value)
                    : FreeFormat.Build(h.Group, variation, value);
                if (header.Size + AppConstants.ResponseHeaderSize > _cfg.MaxTxFragment)
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    continue;
                }

                b.Add(header);
            }
        }))
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
        }
    }

    private void WriteDatasets(Association a, ObjectHeader h)
    {
        if (!Database.DatasetVariationKnown(h.Group, h.Variation) || (h.Group == 86 && h.Variation == 2))
        {
            a.Iin = a.Iin.Set(Iin.ObjectUnknown);
            return;
        }

        if (_cfg.DatasetWrite is not { } write)
        {
            a.Iin = a.Iin.Set(Iin.NoFuncCodeSupport);
            return;
        }

        if (h.Qualifier != FreeFormat.Qualifier)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        List<ReadOnlyMemory<byte>> values;
        try
        {
            values = FreeFormat.Objects(h);
        }
        catch (MalformedException)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        if ((uint)values.Count != h.Count)
        {
            a.Iin = a.Iin.Set(Iin.ParameterError);
            return;
        }

        foreach (var v in values)
        {
            if (h.Group == 85 || (h.Group == 86 && h.Variation == 1))
            {
                try
                {
                    DatasetCodec.ParseDescriptor(v.Span);
                }
                catch (MalformedException)
                {
                    a.Iin = a.Iin.Set(Iin.ParameterError);
                    continue;
                }
            }

            // The backend resolves prototype-defined fields, validates the
            // complete value, and applies the device-specific write. No partial
            // decoding.
            if (!write(h.Group, h.Variation, v.ToArray()))
            {
                a.Iin = a.Iin.Set(Iin.ParameterError);
            }
        }
    }
}
