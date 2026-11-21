// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Objects;
using SharpDnp3.Security;
using SharpDnp3.Stack;

namespace SharpDnp3.Master;

public sealed partial class MasterSession
{
    private Iin _lastReportedIin;
    private Class _unsolicitedDisabled;
    private static readonly Iin RequestErrors =
        Iin.ParameterError | Iin.ObjectUnknown | Iin.NoFuncCodeSupport | Iin.AlreadyExecuting;

    private static readonly (Iin Mask, string Kind)[] RemoteIndications =
    [
        (Iin.DeviceRestart, SecurityEventKind.RemoteRestart),
        (Iin.ConfigCorrupt, SecurityEventKind.RemoteConfigCorrupt),
        (Iin.DeviceTrouble, SecurityEventKind.RemoteDeviceTrouble),
        (Iin.LocalControl, SecurityEventKind.RemoteLocalControl),
        (Iin.EventBufferOverflow, SecurityEventKind.RemoteEventOverflow),
    ];

    private void ObserveRemote(SecurityObservation observation, Received received, Fragment fragment)
    {
        var iin = fragment.Header.Iin;
        foreach (var (mask, kind) in RemoteIndications)
        {
            if (iin.Has(mask) && !_lastReportedIin.Has(mask))
            {
                observation.Application(kind, received, fragment.Header);
            }
        }

        _lastReportedIin = iin;
    }

    private static void ObserveRemoteRejections(
        SecurityObservation observation, Received received, Fragment fragment, MasterTask task)
    {
        var header = fragment.Header;
        if (header.Iin.HasAny(RequestErrors))
        {
            observation.Application(SecurityEventKind.RemoteRejected, received, header,
                detail: task.Name);
        }

        if (task.FuncCode.IsControl())
        {
            var statuses = new List<CommandStatus>();
            CommandCodec.ParseCommandStatuses(fragment, statuses);
            for (var i = 0; i < statuses.Count; i++)
            {
                var status = statuses[i];
                if (!status.OK())
                {
                    observation.Emit(new(SecurityEventKind.RemoteRejected)
                    {
                        LinkSrc = received.Source,
                        LinkDest = received.Dest,
                        AppFunc = task.FuncCode.ToDisplayString(),
                        AppSeq = header.Control.Seq,
                        CommandStatus = status.ToDisplayString(),
                        Detail = task.Name,
                    });
                }
            }
        }

        foreach (var obj in fragment.Objects)
        {
            if (obj.Group != 70 || obj.Variation is not (4 or 6))
            {
                continue;
            }

            try
            {
                var data = FreeFormat.FirstObject(obj).Span;
                var status = obj.Variation == 4 ? FileObjects.ParseCommandStatus(data).Status
                    : FileObjects.ParseTransportStatus(data).Status;
                if (!status.OK())
                {
                    observation.Emit(new(SecurityEventKind.FileRejected)
                    {
                        LinkSrc = received.Source,
                        LinkDest = received.Dest,
                        AppFunc = task.FuncCode.ToDisplayString(),
                        AppSeq = header.Control.Seq,
                        Group = obj.Group,
                        Variation = obj.Variation,
                        FileStatus = status.ToDisplayString(),
                        Detail = task.Name,
                    });
                }
            }
            catch (MalformedException)
            {
                // The task's normal response parser reports this failure.
            }
        }
    }
}
