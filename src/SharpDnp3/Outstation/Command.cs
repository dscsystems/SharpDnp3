// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Stack;

namespace SharpDnp3.Outstation;

/// <summary>Says how a command reached the outstation.</summary>
/// <remarks>
/// The distinction matters to an implementation that logs or authorises
/// controls: a direct operate arrived with no prior select, so nothing was
/// reserved, and a no-reply operate will get no acknowledgement whatever the
/// outcome.
/// </remarks>
public enum OperateType : byte
{
    /// <summary>
    /// A DIRECT_OPERATE: execute immediately, answer with the outcome.
    /// </summary>
    Direct = 0,

    /// <summary>
    /// A DIRECT_OPERATE_NR: execute immediately and send no response at all.
    /// </summary>
    DirectNoAck,

    /// <summary>An OPERATE following a successful SELECT.</summary>
    Selected,
}

/// <summary>Naming helpers for <see cref="OperateType"/>.</summary>
public static class OperateTypeExtensions
{
    /// <summary>Renders the type using the protocol tools' spelling.</summary>
    public static string ToDisplayString(this OperateType o) => o switch
    {
        OperateType.Direct => "direct-operate",
        OperateType.DirectNoAck => "direct-operate-no-reply",
        OperateType.Selected => "operate-after-select",
        _ => "OperateType(?)",
    };
}

/// <summary>
/// An analog output command, carried as a <see cref="double"/> regardless of
/// the variation it arrived in.
/// </summary>
/// <remarks>
/// The variation is kept because it is information the handler may need — a
/// setpoint sent as a 16-bit integer cannot express what one sent as a double
/// can — but a handler that does not care can read
/// <see cref="AnalogOutputCommand.Value"/> and ignore it.
/// </remarks>
/// <param name="Value">The commanded value.</param>
/// <param name="Variation">
/// The group 41 variation the command arrived in: 1 for 32-bit integer, 2 for
/// 16-bit, 3 for single precision, 4 for double.
/// </param>
public readonly record struct AnalogOutputCommand(double Value, byte Variation);

/// <summary>Executes controls.</summary>
/// <remarks>
/// <para>
/// <c>Select</c> is called for a SELECT and must not operate anything: it
/// reports whether the outstation <em>would</em> accept the command.
/// <c>Operate</c> is called for an OPERATE, a DIRECT_OPERATE or a
/// DIRECT_OPERATE_NR, and is the call that actually moves the plant.
/// </para>
/// <para>
/// Both are called from the session loop, so a handler that takes a long time
/// stalls the protocol. Anything slow belongs behind a queue — but note that
/// returning success before the operation completes is a claim the outstation
/// cannot take back.
/// </para>
/// </remarks>
public interface ICommandHandler
{
    /// <summary>Reports whether a CROB would be accepted.</summary>
    CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c);

    /// <summary>Executes a CROB.</summary>
    CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op);

    /// <summary>Reports whether an analog setpoint would be accepted.</summary>
    CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v);

    /// <summary>Executes an analog setpoint.</summary>
    CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op);
}

/// <summary>Refuses every command with NOT_SUPPORTED.</summary>
/// <remarks>
/// It is the default, and it is deliberately a refusal rather than a success:
/// an outstation whose controls are not wired up must say so, not silently
/// report that a breaker operated.
/// </remarks>
public sealed class RejectingCommandHandler : ICommandHandler
{
    /// <inheritdoc/>
    public CommandStatus SelectCrob(ushort index, ControlRelayOutputBlock c) =>
        CommandStatus.NotSupported;

    /// <inheritdoc/>
    public CommandStatus OperateCrob(ushort index, ControlRelayOutputBlock c, OperateType op) =>
        CommandStatus.NotSupported;

    /// <inheritdoc/>
    public CommandStatus SelectAnalog(ushort index, AnalogOutputCommand v) =>
        CommandStatus.NotSupported;

    /// <inheritdoc/>
    public CommandStatus OperateAnalog(ushort index, AnalogOutputCommand v, OperateType op) =>
        CommandStatus.NotSupported;
}

/// <summary>The state a SELECT leaves behind for the OPERATE that follows.</summary>
internal sealed class Selection
{
    public bool Active;

    /// <summary>
    /// The raw object portion of the select request. The operate must reproduce
    /// it exactly.
    /// </summary>
    public byte[] Objects = [];

    public byte Seq;

    public DateTimeOffset Expires;

    /// <summary>Discards the selection.</summary>
    public void Clear()
    {
        Active = false;
        Objects = [];
    }

    /// <summary>
    /// Reports whether an operate request corresponds to the live selection.
    /// </summary>
    /// <remarks>
    /// The comparison is over the raw object octets, which is the strictest
    /// reading of the standard's "same objects" requirement and the right one.
    /// A looser check — matching only indexes, say — would let an OPERATE trip
    /// a breaker after a SELECT that named a close, and the whole point of
    /// select-before-operate is that the operator confirms exactly what was
    /// proposed.
    /// </remarks>
    public CommandStatus Matches(ReadOnlySpan<byte> objectsRaw, byte seq, DateTimeOffset now)
    {
        if (!Active)
        {
            return CommandStatus.NoSelect;
        }

        if (now > Expires)
        {
            return CommandStatus.Timeout;
        }

        if (seq != NextSeq(Seq))
        {
            // The operate must be the request immediately after the select.
            // Anything else means a request went missing in between, and the
            // operator may not be acting on what they think they are.
            return CommandStatus.NoSelect;
        }

        return !objectsRaw.SequenceEqual(Objects) ? CommandStatus.NoSelect : CommandStatus.Success;
    }

    private static byte NextSeq(byte s) => (byte)((s + 1) % AppConstants.SeqModulus);
}

/// <summary>The status assigned to one command object.</summary>
internal readonly record struct CommandOutcome(ushort Index, CommandStatus Status);

public sealed partial class OutstationSession
{
    /// <summary>
    /// Handles SELECT, OPERATE, DIRECT_OPERATE and DIRECT_OPERATE_NR.
    /// </summary>
    /// <remarks>
    /// The response echoes the request's objects with each status filled in,
    /// which is how a master learns which point in a multi-command request
    /// failed.
    /// </remarks>
    private void OnCommand(Received r, Fragment frag)
    {
        var now = _appl.Now();
        var fc = frag.Header.Func;
        var objectsRaw = frag.Raw[frag.Header.Size..];

        // A SELECT reserves; everything else operates.
        if (fc == FuncCode.Select)
        {
            OnSelect(r, frag, objectsRaw, now);
            return;
        }

        var opType = fc switch
        {
            FuncCode.Operate => OperateType.Selected,
            FuncCode.DirectOperateNR => OperateType.DirectNoAck,
            _ => OperateType.Direct,
        };

        // An OPERATE is only honoured against a live, matching selection.
        var selectStatus = CommandStatus.Success;
        if (opType == OperateType.Selected)
        {
            selectStatus = _sel.Matches(objectsRaw.Span, frag.Header.Control.Seq, now);

            // The selection is consumed either way: a failed operate must not
            // leave a reservation an operator could stumble into later.
            _sel.Clear();
        }

        var (body, outcomes) = ExecuteCommands(frag, opType, selectStatus);
        LogCommands(fc, opType, outcomes);

        if (fc.NoReply() || r.Broadcast)
        {
            return;
        }

        Respond(r, frag.Header, body);
    }

    /// <summary>Reserves the commands without operating anything.</summary>
    private void OnSelect(
        Received r,
        Fragment frag,
        ReadOnlyMemory<byte> objectsRaw,
        DateTimeOffset now)
    {
        var (body, outcomes) = ExecuteCommands(frag, OperateType.Direct, CommandStatus.Success);

        var allOK = true;
        foreach (var o in outcomes)
        {
            if (!o.Status.OK())
            {
                allOK = false;
                break;
            }
        }

        if (allOK)
        {
            _sel.Active = true;
            _sel.Objects = objectsRaw.ToArray();
            _sel.Seq = frag.Header.Control.Seq;
            _sel.Expires = now + _cfg.SelectTimeout;
        }
        else
        {
            _sel.Clear();
        }

        LogCommands(FuncCode.Select, OperateType.Direct, outcomes);
        if (r.Broadcast)
        {
            return;
        }

        Respond(r, frag.Header, body);
    }

    /// <summary>
    /// Walks the request's command objects, running each through the handler
    /// and building the echo response.
    /// </summary>
    /// <remarks>
    /// <paramref name="selectStatus"/> lets a failed select-before-operate
    /// check short-circuit every command in the request without touching the
    /// handler, which is what keeps a stale OPERATE from moving anything.
    /// </remarks>
    private (byte[] Body, List<CommandOutcome> Outcomes) ExecuteCommands(
        Fragment frag,
        OperateType opType,
        CommandStatus selectStatus)
    {
        var body = new List<byte>();
        var outcomes = new List<CommandOutcome>();

        var selecting = frag.Header.Func == FuncCode.Select;

        foreach (var h in frag.Objects)
        {
            if (!ObjectRegistry.TryLookup(GroupVar.GV(h.Group, h.Variation), out var d) ||
                d.Kind != Kind.Command)
            {
                _iin = _iin.Set(Iin.ObjectUnknown);
                continue;
            }

            if (!d.TrySizeOctets(out var size) || size == 0)
            {
                _iin = _iin.Set(Iin.ObjectUnknown);
                continue;
            }

            var prefix = h.Qualifier.IndexPrefix;
            if (!prefix.IsIndex())
            {
                // A command with no index prefix cannot say which point it
                // means.
                _iin = _iin.Set(Iin.ParameterError);
                continue;
            }

            var prefixLen = prefix.Octets();
            var data = h.Data.Span;
            var echo = new List<byte>(data.Length);

            var off = 0;
            for (uint n = 0; n < h.Count; n++)
            {
                if (off + prefixLen + size > data.Length)
                {
                    break;
                }

                var index = (ushort)ReadPrefix(data[off..], prefixLen);
                var raw = data.Slice(off + prefixLen, size);

                var status = selectStatus;
                if (status.OK())
                {
                    status = RunCommand(h.Group, h.Variation, index, raw, selecting, opType);
                }

                outcomes.Add(new CommandOutcome(index, status));

                echo.AddRange(data.Slice(off, prefixLen + size));

                // The status octet is the last of every command object, so
                // overwriting it in place turns the echoed request into the
                // response the master expects.
                echo[^1] = (byte)status;

                off += prefixLen + size;
            }

            ObjectHeaderCodec.AppendObjectHeader(body, new ObjectHeader
            {
                Group = h.Group,
                Variation = h.Variation,
                Qualifier = h.Qualifier,
                Range = h.Range,
                Data = echo.ToArray(),
            });
        }

        return ([.. body], outcomes);
    }

    /// <summary>Decodes one command object and hands it to the handler.</summary>
    private CommandStatus RunCommand(
        byte group,
        byte variation,
        ushort index,
        ReadOnlySpan<byte> raw,
        bool selecting,
        OperateType opType)
    {
        switch (group)
        {
            case 12:
            {
                var c = CommandObjects.ParseCrob(raw);
                return selecting
                    ? _cmds.SelectCrob(index, c)
                    : _cmds.OperateCrob(index, c, opType);
            }

            case 41:
            {
                var v = ParseAnalogOutput(variation, raw);
                return selecting
                    ? _cmds.SelectAnalog(index, v)
                    : _cmds.OperateAnalog(index, v, opType);
            }

            default:
                return CommandStatus.NotSupported;
        }
    }

    /// <summary>
    /// Decodes any of the four group 41 variations into a common form.
    /// </summary>
    private static AnalogOutputCommand ParseAnalogOutput(byte variation, ReadOnlySpan<byte> raw)
    {
        var value = variation switch
        {
            1 => CommandObjects.ParseAnalogOutputInt32(raw).Value,
            2 => CommandObjects.ParseAnalogOutputInt16(raw).Value,
            3 => CommandObjects.ParseAnalogOutputFloat32(raw).Value,
            4 => CommandObjects.ParseAnalogOutputFloat64(raw).Value,
            _ => 0d,
        };

        return new AnalogOutputCommand(value, variation);
    }

    private void LogCommands(FuncCode fc, OperateType opType, List<CommandOutcome> outcomes)
    {
        foreach (var o in outcomes)
        {
            _log.Log(
                Dnp3LogLevel.Info,
                "command",
                ("func", fc.ToDisplayString()),
                ("operate", opType.ToDisplayString()),
                ("index", o.Index),
                ("status", o.Status.ToDisplayString()),
                ("result", o.Status.OK() ? "ok" : "rejected"));

            lock (_gate)
            {
                if (o.Status.OK())
                {
                    _stats.CommandsExecuted++;
                }
                else
                {
                    _stats.CommandsRejected++;
                }
            }
        }
    }

    /// <summary>Expires a stale selection.</summary>
    /// <remarks>
    /// A reservation that outlives its window must not be operable: an operator
    /// who selected a breaker, walked away, and came back minutes later should
    /// have to select again rather than operate on a decision they no longer
    /// remember making.
    /// </remarks>
    private void CheckSelectTimeout(DateTimeOffset now)
    {
        if (_sel.Active && now > _sel.Expires)
        {
            _log.Log(Dnp3LogLevel.Debug, "selection expired", ("seq", _sel.Seq));
            _sel.Clear();
        }
    }

    /// <summary>Reads a little-endian per-object index prefix.</summary>
    internal static uint ReadPrefix(ReadOnlySpan<byte> buf, int width) => width switch
    {
        1 => buf[0],
        2 => (uint)(buf[0] | (buf[1] << 8)),
        4 => (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24)),
        _ => 0,
    };
}
