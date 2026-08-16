// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The DNP3 application layer framing: the fragment header, function codes,
// internal indications, and the object headers with their qualifier, range and
// prefix encodings.
//
// It does not decode object data. Walking a fragment requires knowing how
// large each object is, which is a property of its group and variation; that
// knowledge is supplied through the IObjectSizer interface so this namespace
// stays independent of the object codecs.
//
// Nothing here performs I/O or reads a clock.

using System.Globalization;

namespace SharpDnp3.App;

/// <summary>An application layer function code.</summary>
public enum FuncCode : byte
{
    // ---- Request function codes, sent by a master to an outstation ----

    /// <summary>Confirm receipt of a response.</summary>
    Confirm = 0,
    /// <summary>Request data.</summary>
    Read = 1,
    /// <summary>Store data.</summary>
    Write = 2,
    /// <summary>Arm an output for a subsequent operate.</summary>
    Select = 3,
    /// <summary>Operate a previously selected output.</summary>
    Operate = 4,
    /// <summary>Operate an output without a preceding select.</summary>
    DirectOperate = 5,
    /// <summary>Operate an output without a select and without a response.</summary>
    DirectOperateNR = 6,
    /// <summary>Freeze counters immediately.</summary>
    ImmedFreeze = 7,
    /// <summary>Freeze counters immediately, without a response.</summary>
    ImmedFreezeNR = 8,
    /// <summary>Freeze and clear counters.</summary>
    FreezeClear = 9,
    /// <summary>Freeze and clear counters, without a response.</summary>
    FreezeClearNR = 10,
    /// <summary>Freeze counters at a given time.</summary>
    FreezeAtTime = 11,
    /// <summary>Freeze counters at a given time, without a response.</summary>
    FreezeAtTimeNR = 12,
    /// <summary>Reinitialise the outstation completely.</summary>
    ColdRestart = 13,
    /// <summary>Reinitialise the communications process.</summary>
    WarmRestart = 14,
    /// <summary>Initialise the data set.</summary>
    InitializeData = 15,
    /// <summary>Initialise an application.</summary>
    InitializeAppl = 16,
    /// <summary>Start an application.</summary>
    StartAppl = 17,
    /// <summary>Stop an application.</summary>
    StopAppl = 18,
    /// <summary>Save the configuration.</summary>
    SaveConfig = 19,
    /// <summary>Enable unsolicited responses.</summary>
    EnableUnsolicited = 20,
    /// <summary>Disable unsolicited responses.</summary>
    DisableUnsolicited = 21,
    /// <summary>Assign points to event classes.</summary>
    AssignClass = 22,
    /// <summary>Measure the outstation's turnaround delay.</summary>
    DelayMeasure = 23,
    /// <summary>Record the current time.</summary>
    RecordCurrentTime = 24,
    /// <summary>Open a file.</summary>
    OpenFile = 25,
    /// <summary>Close a file.</summary>
    CloseFile = 26,
    /// <summary>Delete a file.</summary>
    DeleteFile = 27,
    /// <summary>Get file information.</summary>
    GetFileInfo = 28,
    /// <summary>Authenticate a file operation.</summary>
    AuthenticateFile = 29,
    /// <summary>Abort a file transfer.</summary>
    AbortFile = 30,
    /// <summary>Activate a configuration.</summary>
    ActivateConfig = 31,
    /// <summary>Secure authentication request.</summary>
    AuthRequest = 32,
    /// <summary>Secure authentication request expecting no acknowledgement.</summary>
    AuthRequestNoAck = 33,

    // ---- Response function codes, sent by an outstation to a master ----

    /// <summary>A solicited response.</summary>
    Response = 129,
    /// <summary>An unsolicited response.</summary>
    UnsolicitedResponse = 130,
    /// <summary>A secure authentication response.</summary>
    AuthResponse = 131,
}

/// <summary>Naming and classification helpers for <see cref="FuncCode"/>.</summary>
public static class FuncCodeExtensions
{
    private static readonly Dictionary<FuncCode, string> Names = new()
    {
        [FuncCode.Confirm] = "CONFIRM",
        [FuncCode.Read] = "READ",
        [FuncCode.Write] = "WRITE",
        [FuncCode.Select] = "SELECT",
        [FuncCode.Operate] = "OPERATE",
        [FuncCode.DirectOperate] = "DIRECT_OPERATE",
        [FuncCode.DirectOperateNR] = "DIRECT_OPERATE_NR",
        [FuncCode.ImmedFreeze] = "IMMED_FREEZE",
        [FuncCode.ImmedFreezeNR] = "IMMED_FREEZE_NR",
        [FuncCode.FreezeClear] = "FREEZE_CLEAR",
        [FuncCode.FreezeClearNR] = "FREEZE_CLEAR_NR",
        [FuncCode.FreezeAtTime] = "FREEZE_AT_TIME",
        [FuncCode.FreezeAtTimeNR] = "FREEZE_AT_TIME_NR",
        [FuncCode.ColdRestart] = "COLD_RESTART",
        [FuncCode.WarmRestart] = "WARM_RESTART",
        [FuncCode.InitializeData] = "INITIALIZE_DATA",
        [FuncCode.InitializeAppl] = "INITIALIZE_APPL",
        [FuncCode.StartAppl] = "START_APPL",
        [FuncCode.StopAppl] = "STOP_APPL",
        [FuncCode.SaveConfig] = "SAVE_CONFIG",
        [FuncCode.EnableUnsolicited] = "ENABLE_UNSOLICITED",
        [FuncCode.DisableUnsolicited] = "DISABLE_UNSOLICITED",
        [FuncCode.AssignClass] = "ASSIGN_CLASS",
        [FuncCode.DelayMeasure] = "DELAY_MEASURE",
        [FuncCode.RecordCurrentTime] = "RECORD_CURRENT_TIME",
        [FuncCode.OpenFile] = "OPEN_FILE",
        [FuncCode.CloseFile] = "CLOSE_FILE",
        [FuncCode.DeleteFile] = "DELETE_FILE",
        [FuncCode.GetFileInfo] = "GET_FILE_INFO",
        [FuncCode.AuthenticateFile] = "AUTHENTICATE_FILE",
        [FuncCode.AbortFile] = "ABORT_FILE",
        [FuncCode.ActivateConfig] = "ACTIVATE_CONFIG",
        [FuncCode.AuthRequest] = "AUTHENTICATE_REQ",
        [FuncCode.AuthRequestNoAck] = "AUTHENTICATE_REQ_NO_ACK",
        [FuncCode.Response] = "RESPONSE",
        [FuncCode.UnsolicitedResponse] = "UNSOLICITED_RESPONSE",
        [FuncCode.AuthResponse] = "AUTHENTICATE_RESP",
    };

    /// <summary>Renders the code using the protocol's spelling.</summary>
    public static string ToDisplayString(this FuncCode f) =>
        Names.TryGetValue(f, out var name)
            ? name
            : string.Format(CultureInfo.InvariantCulture, "FUNC_{0}", (byte)f);

    /// <summary>
    /// Reports whether the code identifies a fragment carrying an IIN field,
    /// which changes how the header is parsed.
    /// </summary>
    public static bool IsResponse(this FuncCode f) =>
        f is FuncCode.Response or FuncCode.UnsolicitedResponse or FuncCode.AuthResponse;

    /// <summary>
    /// Reports whether the code is defined by the standard. An outstation
    /// answers an unknown code with IIN2.NO_FUNC_CODE_SUPPORT.
    /// </summary>
    public static bool IsKnown(this FuncCode f) => Names.ContainsKey(f);

    /// <summary>
    /// Reports whether the code is one of the "no response" variants, which an
    /// outstation must execute without answering.
    /// </summary>
    public static bool NoReply(this FuncCode f) => f is
        FuncCode.DirectOperateNR or FuncCode.ImmedFreezeNR or FuncCode.FreezeClearNR or
        FuncCode.FreezeAtTimeNR or FuncCode.AuthRequestNoAck;

    /// <summary>
    /// Reports whether object headers in a fragment with this function code are
    /// followed by object data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a property of the object header alone. In a READ request an
    /// object header is a <em>specification</em> — "give me group 30 variation
    /// 2, indexes 0 through 15" — and no data follows it, even though that same
    /// header in a response would introduce sixteen analog values. A parser
    /// that sized the header from the object table in both cases would run off
    /// the end of every read request it saw.
    /// </para>
    /// <para>
    /// The same holds for the freeze, unsolicited-enable and assign-class
    /// requests, whose headers name points rather than carrying them.
    /// </para>
    /// <para>
    /// Known limitation: FREEZE_AT_TIME is genuinely mixed — its leading group
    /// 50 variation 2 object carries a time and interval, while the counter
    /// headers after it are specifications. It is treated as carrying data
    /// here, which is right for the first header and wrong for the rest.
    /// Resolving that needs per-object semantics rather than a per-fragment
    /// rule.
    /// </para>
    /// </remarks>
    public static bool CarriesObjectData(this FuncCode f) => f switch
    {
        FuncCode.Read or
        FuncCode.ImmedFreeze or FuncCode.ImmedFreezeNR or
        FuncCode.FreezeClear or FuncCode.FreezeClearNR or
        FuncCode.EnableUnsolicited or FuncCode.DisableUnsolicited or
        FuncCode.AssignClass or
        FuncCode.Confirm or
        FuncCode.ColdRestart or FuncCode.WarmRestart or
        FuncCode.DelayMeasure or FuncCode.RecordCurrentTime or
        FuncCode.InitializeData or FuncCode.SaveConfig or
        FuncCode.GetFileInfo or FuncCode.DeleteFile => false,
        _ => true,
    };

    /// <summary>
    /// Reports whether the code operates output points, which is the set an
    /// outstation may want to gate behind authorisation.
    /// </summary>
    public static bool IsControl(this FuncCode f) => f is
        FuncCode.Select or FuncCode.Operate or
        FuncCode.DirectOperate or FuncCode.DirectOperateNR;
}
