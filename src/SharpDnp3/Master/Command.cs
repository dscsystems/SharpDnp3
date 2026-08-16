// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using System.Text;
using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

/// <summary>One control to issue at one point index.</summary>
/// <remarks>
/// Build one with <see cref="Crob"/> or one of the analog output constructors
/// rather than by hand: the encoding differs per variation, and the status
/// octet must be zero on the way out so the outstation's echo is what fills it
/// in.
/// </remarks>
public readonly record struct Command
{
    /// <summary>The point index to operate.</summary>
    public ushort Index { get; init; }

    internal GroupVar GV { get; init; }

    internal byte[] Data { get; init; }

    internal string Description { get; init; }

    /// <inheritdoc/>
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "[{0}] {1}", Index, Description);

    /// <summary>
    /// Builds a control relay output block command — the one that operates
    /// breakers, reclosers and other discrete outputs.
    /// </summary>
    public static Command Crob(ushort index, ControlRelayOutputBlock c)
    {
        // Zero on the wire; the outstation fills it in.
        c = c with { Status = CommandStatus.Success };

        var data = new List<byte>(CommandObjects.CrobSize);
        CommandObjects.AppendCrob(data, c);

        return new Command
        {
            Index = index,
            GV = GroupVar.GV(12, 1),
            Data = [.. data],
            Description = c.ToString(),
        };
    }

    /// <summary>
    /// Builds a CROB that trips a breaker with a pulse of the given duration.
    /// </summary>
    public static Command Trip(ushort index, uint pulseMillis) => Crob(index, new ControlRelayOutputBlock
    {
        Code = ControlCode.PulseOn | ControlCode.Trip,
        Count = 1,
        OnTime = pulseMillis,
    });

    /// <summary>
    /// Builds a CROB that closes a breaker with a pulse of the given duration.
    /// </summary>
    public static Command Close(ushort index, uint pulseMillis) => Crob(index, new ControlRelayOutputBlock
    {
        Code = ControlCode.PulseOn | ControlCode.Close,
        Count = 1,
        OnTime = pulseMillis,
    });

    /// <summary>Builds the CROB that holds an output on.</summary>
    public static Command LatchOn(ushort index) => Crob(index, new ControlRelayOutputBlock
    {
        Code = ControlCode.LatchOn,
        Count = 1,
    });

    /// <summary>Builds the CROB that holds an output off.</summary>
    public static Command LatchOff(ushort index) => Crob(index, new ControlRelayOutputBlock
    {
        Code = ControlCode.LatchOff,
        Count = 1,
    });

    /// <summary>Builds a group 41 variation 2 setpoint.</summary>
    public static Command AnalogOutputInt16(ushort index, short v)
    {
        var data = new List<byte>(CommandObjects.AnalogOutput16Size);
        CommandObjects.AppendAnalogOutputInt16(data, new AnalogOutputInt16(v));
        return new Command
        {
            Index = index,
            GV = GroupVar.GV(41, 2),
            Data = [.. data],
            Description = string.Format(CultureInfo.InvariantCulture, "{0} (int16)", v),
        };
    }

    /// <summary>Builds a group 41 variation 1 setpoint.</summary>
    public static Command AnalogOutputInt32(ushort index, int v)
    {
        var data = new List<byte>(CommandObjects.AnalogOutput32Size);
        CommandObjects.AppendAnalogOutputInt32(data, new AnalogOutputInt32(v));
        return new Command
        {
            Index = index,
            GV = GroupVar.GV(41, 1),
            Data = [.. data],
            Description = string.Format(CultureInfo.InvariantCulture, "{0} (int32)", v),
        };
    }

    /// <summary>Builds a group 41 variation 3 setpoint.</summary>
    public static Command AnalogOutputFloat32(ushort index, float v)
    {
        var data = new List<byte>(CommandObjects.AnalogOutputFloatSize);
        CommandObjects.AppendAnalogOutputFloat32(data, new AnalogOutputFloat32(v));
        return new Command
        {
            Index = index,
            GV = GroupVar.GV(41, 3),
            Data = [.. data],
            Description = string.Format(CultureInfo.InvariantCulture, "{0} (float32)", v),
        };
    }

    /// <summary>Builds a group 41 variation 4 setpoint.</summary>
    public static Command AnalogOutputFloat64(ushort index, double v)
    {
        var data = new List<byte>(CommandObjects.AnalogOutputDoubleSize);
        CommandObjects.AppendAnalogOutputFloat64(data, new AnalogOutputFloat64(v));
        return new Command
        {
            Index = index,
            GV = GroupVar.GV(41, 4),
            Data = [.. data],
            Description = string.Format(CultureInfo.InvariantCulture, "{0} (float64)", v),
        };
    }
}

/// <summary>Reports what the outstation made of each command.</summary>
public sealed class CommandResult
{
    /// <summary>
    /// One status per command, in the order they were sent.
    /// </summary>
    public List<CommandStatus> Statuses { get; } = [];

    /// <summary>
    /// Echoes what was sent, so a caller logging a failure has the point index
    /// to hand.
    /// </summary>
    public IReadOnlyList<Command> Commands { get; init; } = [];

    /// <summary>Reports whether every command succeeded.</summary>
    /// <remarks>
    /// A multi-command request can partially succeed, and treating that as
    /// success would tell an operator a breaker operated when it did not.
    /// </remarks>
    public bool OK()
    {
        if (Statuses.Count == 0)
        {
            return false;
        }

        foreach (var s in Statuses)
        {
            if (!s.OK())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns an exception describing the failures, or <see langword="null"/>
    /// if every command succeeded.
    /// </summary>
    public Dnp3Exception? Error()
    {
        if (OK())
        {
            return null;
        }

        if (Statuses.Count == 0)
        {
            return new Dnp3Exception("master: the outstation returned no command statuses");
        }

        var b = new StringBuilder();
        for (var i = 0; i < Statuses.Count; i++)
        {
            if (Statuses[i].OK())
            {
                continue;
            }

            if (b.Length > 0)
            {
                b.Append("; ");
            }

            var idx = i < Commands.Count ? Commands[i].Index : (ushort)0;
            b.Append(CultureInfo.InvariantCulture, $"index {idx}: {Statuses[i].ToDisplayString()}");
        }

        return new Dnp3Exception("master: command failed: " + b);
    }

    /// <summary>Throws if any command failed.</summary>
    public void ThrowIfFailed()
    {
        var err = Error();
        if (err is not null)
        {
            throw err;
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var parts = new string[Statuses.Count];
        for (var i = 0; i < Statuses.Count; i++)
        {
            var idx = i < Commands.Count ? Commands[i].Index : (ushort)0;
            parts[i] = string.Format(
                CultureInfo.InvariantCulture, "[{0}]={1}", idx, Statuses[i].ToDisplayString());
        }

        return string.Join(' ', parts);
    }
}

/// <summary>Encodes command requests and decodes their echoes.</summary>
internal static class CommandCodec
{
    /// <summary>Appends the command objects to a request.</summary>
    /// <remarks>
    /// Commands sharing a group and variation are grouped into one header with
    /// per-object index prefixes, since the indexes being operated are rarely
    /// contiguous.
    /// </remarks>
    public static void BuildCommands(FragmentBuilder b, IReadOnlyList<Command> cmds)
    {
        for (var i = 0; i < cmds.Count;)
        {
            var gv = cmds[i].GV;

            var j = i;
            while (j < cmds.Count && cmds[j].GV == gv)
            {
                j++;
            }

            var data = new List<byte>();
            for (var k = i; k < j; k++)
            {
                data.Add((byte)cmds[k].Index);
                data.AddRange(cmds[k].Data);
            }

            var added = b.TryAddObject(new ObjectHeader
            {
                Group = gv.Group,
                Variation = gv.Variation,
                Qualifier = Qualifier.Make(IndexPrefix.Index1, RangeSpec.Count8),
                Range = new ObjectRange { Spec = RangeSpec.Count8, Count = (uint)(j - i) },
                Data = data.ToArray(),
            });

            if (!added)
            {
                throw AppParseStatus.FragmentTooLarge.ToException();
            }

            i = j;
        }
    }

    /// <summary>Reads the statuses out of an outstation's echo.</summary>
    public static void ParseCommandStatuses(Fragment frag, List<CommandStatus> output)
    {
        foreach (var h in frag.Objects)
        {
            if (!ObjectRegistry.TryLookup(GroupVar.GV(h.Group, h.Variation), out var d) ||
                d.Kind != Kind.Command)
            {
                continue;
            }

            if (!d.TrySizeOctets(out var size) || size == 0)
            {
                continue;
            }

            var prefixLen = 0;
            var p = h.Qualifier.IndexPrefix;
            if (p.IsIndex())
            {
                prefixLen = p.Octets();
            }

            var data = h.Data.Span;
            var off = 0;
            for (uint n = 0; n < h.Count; n++)
            {
                if (off + prefixLen + size > data.Length)
                {
                    break;
                }

                // The status is the last octet of every command object.
                output.Add((CommandStatus)data[off + prefixLen + size - 1]);
                off += prefixLen + size;
            }
        }
    }
}
