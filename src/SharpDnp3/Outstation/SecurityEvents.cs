// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Security;
using SharpDnp3.Stack;

namespace SharpDnp3.Outstation;

public sealed partial class OutstationSession
{
    private bool CheckRequestPolicy(Association a, SecurityObservation security, Received received, Fragment fragment)
    {
        var function = fragment.Header.Func;
        if (function is FuncCode.Confirm || function.IsControl())
        {
            return false;
        }

        var functions = security.Policy.Functions;
        if (functions is not null &&
            (functions.Blocks(function) ||
             function == FuncCode.Write && fragment.Objects.Any(h => functions.BlocksWrite(h.Group))))
        {
            security.Application(SecurityEventKind.AccessFunctionBlocked, received, fragment.Header, SecurityAction.Blocked);
            a.Iin = a.Iin.Set(Iin.NoFuncCodeSupport);
        }
        else
        {
            var (kind, rate) = RequestRate(fragment, security.Policy.Limits);
            if (kind is null || security.Flood(kind, rate, mayThrottle: true) == SecurityAction.Observed)
            {
                return false;
            }

            a.Iin = a.Iin.Set(Iin.ParameterError);
        }

        if (!security.Disconnect && !received.Broadcast && !function.NoReply())
        {
            Respond(a, received, fragment.Header, []);
        }

        return true;
    }

    private static (string? Kind, SecurityRate Rate) RequestRate(Fragment fragment, SecurityLimits limits) =>
        fragment.Header.Func switch
        {
            FuncCode.Read when fragment.Objects.Any(h => h.Group == 60 && h.Variation == 1) =>
                (SecurityEventKind.FloodIntegrity, limits.Integrity),
            FuncCode.Read when fragment.Objects.Any(h => h.Group == 60) =>
                (SecurityEventKind.FloodEventPolls, limits.EventPolls),
            FuncCode.ColdRestart or FuncCode.WarmRestart => (SecurityEventKind.FloodRestart, limits.Restart),
            FuncCode.Write when fragment.Objects.Any(h => h.Group == 50 && h.Variation is 1 or 3) =>
                (SecurityEventKind.FloodTimeWrite, limits.TimeWrite),
            FuncCode.InitializeData or FuncCode.InitializeAppl or FuncCode.StartAppl or FuncCode.StopAppl or
                FuncCode.SaveConfig or FuncCode.ActivateConfig => (SecurityEventKind.FloodAppControl, limits.AppControl),
            FuncCode.OpenFile or FuncCode.DeleteFile => (SecurityEventKind.FloodFileOps, limits.FileOps),
            _ => default,
        };

    private void AuditRequest(SecurityObservation security, Received received, Fragment fragment)
    {
        var function = fragment.Header.Func;
        if (!HandlesFunc(function))
        {
            security.Application(SecurityEventKind.AccessUnsupportedFunction, received, fragment.Header, SecurityAction.Rejected);
            security.Scan($"function:{(byte)function}");
            return;
        }

        if (Sa is null && (function.IsControl() || CriticalFunction(function)) &&
            security.Duplicate(received.Fragment.Span))
        {
            security.Application(SecurityEventKind.CmdDuplicate, received, fragment.Header);
        }

        if (security.Policy.DisableAudit)
        {
            return;
        }

        var kind = function switch
        {
            FuncCode.ColdRestart => SecurityEventKind.CtrlColdRestart,
            FuncCode.WarmRestart => SecurityEventKind.CtrlWarmRestart,
            FuncCode.InitializeAppl => SecurityEventKind.CtrlInitializeApplication,
            FuncCode.InitializeData => SecurityEventKind.CtrlInitializeData,
            FuncCode.StartAppl => SecurityEventKind.CtrlStartApplication,
            FuncCode.StopAppl => SecurityEventKind.CtrlStopApplication,
            FuncCode.SaveConfig => SecurityEventKind.CtrlSaveConfig,
            FuncCode.ActivateConfig => SecurityEventKind.CtrlActivateConfig,
            FuncCode.RecordCurrentTime => SecurityEventKind.CtrlRecordCurrentTime,
            FuncCode.DelayMeasure => SecurityEventKind.CtrlDelayMeasure,
            FuncCode.EnableUnsolicited => SecurityEventKind.CtrlUnsolEnable,
            FuncCode.DisableUnsolicited => SecurityEventKind.CtrlUnsolDisable,
            FuncCode.AssignClass => SecurityEventKind.CtrlAssignClass,
            FuncCode.ImmedFreeze or FuncCode.ImmedFreezeNR => SecurityEventKind.CtrlFreeze,
            FuncCode.FreezeClear or FuncCode.FreezeClearNR => SecurityEventKind.CtrlFreezeClear,
            FuncCode.FreezeAtTime or FuncCode.FreezeAtTimeNR => SecurityEventKind.CtrlFreezeAtTime,
            FuncCode.DirectOperateNR => SecurityEventKind.CmdNoAckOperate,
            _ => null,
        };
        if (kind is not null)
        {
            security.Application(kind, received, fragment.Header, detail: "Request received");
        }

        if (function != FuncCode.Write)
        {
            return;
        }

        foreach (var obj in fragment.Objects)
        {
            kind = obj.Group switch
            {
                0 => SecurityEventKind.CtrlAttributeWrite,
                34 => SecurityEventKind.CtrlDeadbandWrite,
                50 when obj.Variation == 4 => SecurityEventKind.CtrlTimeIntervalWrite,
                50 when obj.Variation is 1 or 3 => SecurityEventKind.CtrlTimeWrite,
                80 => SecurityEventKind.CtrlRestartIINCleared,
                >= 85 and <= 87 => SecurityEventKind.CtrlDatasetWrite,
                112 => SecurityEventKind.CtrlTerminalWrite,
                _ => null,
            };
            if (kind is not null)
            {
                security.Application(kind, received, fragment.Header, obj: obj, detail: "Write requested");
            }
        }
    }

    private static CommandStatus CheckCommandPolicy(
        SecurityObservation security, AppHeader request, ObjectHeader obj, ushort index, ControlCode code)
    {
        var functions = security.Policy.Functions;
        CommandStatus status;
        if (security.Disconnect)
        {
            return security.Policy.ThrottleStatus;
        }

        if (security.Flood(SecurityEventKind.FloodCommands, security.Policy.Limits.Commands, mayThrottle: true)
            != SecurityAction.Observed)
        {
            return security.Policy.ThrottleStatus;
        }

        if (functions?.Blocks(request.Func) == true)
        {
            status = functions.DenyStatus;
        }
        else if (security.Policy.CommandGuard is { } guard)
        {
            try
            {
                status = guard(security.Peer ?? new SecurityPeer(), request.Func, obj.Group, index, code);
            }
            catch (Exception)
            {
                status = CommandStatus.NotAuthorized;
            }
        }
        else
        {
            status = CommandStatus.Success;
        }

        if (!status.OK())
        {
            CommandObservation(security, SecurityEventKind.AccessFunctionBlocked, request, obj,
                index, code, status, SecurityAction.Blocked);
        }

        return status;
    }

    private static bool InvalidCommandFields(ObjectHeader obj, ReadOnlySpan<byte> raw, ControlCode code)
    {
        if (obj.Group == 12)
        {
            var operation = code.OpType();
            return operation != ControlCode.Nul && (operation.Value > ControlCode.LatchOff.Value || raw[1] == 0 ||
                (operation == ControlCode.PulseOn || operation == ControlCode.PulseOff) &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw[2..]) == 0);
        }

        return obj.Group == 41 && !double.IsFinite(ParseAnalogOutput(obj.Variation, raw).Value);
    }

    private static void CommandObservation(
        SecurityObservation security, string kind, AppHeader request, ObjectHeader obj,
        uint index, ControlCode code, CommandStatus status, SecurityAction action = SecurityAction.Observed) =>
        security.Emit(new(kind)
        {
            Action = action,
            AppFunc = request.Func.ToDisplayString(),
            AppSeq = request.Control.Seq,
            Group = obj.Group,
            Variation = obj.Variation,
            Index = index,
            ControlCode = obj.Group == 12 ? code.Value : null,
            CommandStatus = status.ToDisplayString(),
        });

    private void ReportIndication(int bits, bool enabled)
    {
        if (_cfg.Security is null)
        {
            return;
        }

        foreach (var association in Attached())
        {
            if (association.Stack.Security is { Policy.DisableAudit: false } security)
            {
                security.Emit(new(SecurityEventKind.CtrlIndicationChange)
                {
                    Iin = (ushort)bits,
                    Value = enabled ? "set" : "cleared",
                });
            }
        }
    }

    private void ReportClockStep(Association a, Received received, AppHeader header, DateTimeOffset target)
    {
        if (a.Stack.Security is { } security && security.Policy.ClockStepThreshold > TimeSpan.Zero)
        {
            var difference = target - _appl.Now();
            if (difference.Duration() >= security.Policy.ClockStepThreshold)
            {
                security.Application(SecurityEventKind.CtrlClockStep, received, header,
                    detail: $"Requested clock correction of {difference:c}");
            }
        }
    }

    private static void ReportUnknown(
        Association a, Received received, AppHeader header, ObjectHeader obj, string kind)
    {
        if (a.Stack.Security is { } security)
        {
            security.Application(kind, received, header, SecurityAction.Rejected, obj);
            security.Scan($"{kind}:{obj.Group}:{obj.Variation}:{obj.Range.Start}:{obj.Range.Stop}");
        }
    }

    private static void ReportSuspiciousFileName(Association a, Received r, AppHeader header, string name)
    {
        if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
            name.Split('/').Contains("..") || name.Any(char.IsControl))
        {
            ReportFile(a, r, header, SecurityEventKind.FileSuspiciousName, name);
        }
    }

    private static void ReportFile(
        Association a, Received received, AppHeader header, string kind, string? name = null, FileStatus? status = null)
    {
        a.Stack.Security?.Emit(new(kind)
        {
            Action = status is { } result && !result.OK() ? SecurityAction.Rejected : SecurityAction.Observed,
            AppFunc = header.Func.ToDisplayString(),
            AppSeq = header.Control.Seq,
            LinkSrc = received.Source,
            LinkDest = received.Dest,
            FileName = name is null ? null : new string(name.Take(256).Select(c => char.IsControl(c) ? '?' : c).ToArray()),
            FileStatus = status?.ToDisplayString(),
        });
    }

    private static void ConfirmMissed(Association a)
    {
        if (a.Stack.Security is { } security && ++security.ConfirmTimeouts == security.Policy.ConfirmWithheldAfter)
        {
            security.Emit(new(SecurityEventKind.DoSConfirmWithheld) { Count = security.ConfirmTimeouts });
        }
    }

    private static void Confirmed(Association a)
    {
        if (a.Stack.Security is { } security)
        {
            security.ConfirmTimeouts = 0;
        }
    }

    private static void UnexpectedConfirm(Association a, AppHeader header) =>
        a.Stack.Security?.Emit(new(SecurityEventKind.UnsolUnexpectedConfirm)
        {
            AppFunc = FuncCode.Confirm.ToDisplayString(),
            AppSeq = header.Control.Seq,
            Action = SecurityAction.Ignored,
        });
}
