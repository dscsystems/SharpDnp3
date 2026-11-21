// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

namespace SharpDnp3.Security;

/// <summary>Stable event identifiers shared with go-dnp3.</summary>
public static class SecurityEventKind
{
    /// <summary>The <c>conn.accepted</c> event identifier.</summary>
    public const string ConnAccepted = "conn.accepted";
    /// <summary>The <c>conn.connected</c> event identifier.</summary>
    public const string ConnConnected = "conn.connected";
    /// <summary>The <c>conn.unexpected</c> event identifier.</summary>
    public const string ConnUnexpected = "conn.unexpected";
    /// <summary>The <c>conn.unexpected_mac</c> event identifier.</summary>
    public const string ConnUnexpectedMAC = "conn.unexpected_mac";
    /// <summary>The <c>conn.rejected_busy</c> event identifier.</summary>
    public const string ConnRejectedBusy = "conn.rejected_busy";
    /// <summary>The <c>conn.rate</c> event identifier.</summary>
    public const string ConnRate = "conn.rate";
    /// <summary>The <c>conn.no_data</c> event identifier.</summary>
    public const string ConnNoData = "conn.no_data";
    /// <summary>The <c>conn.idle_timeout</c> event identifier.</summary>
    public const string ConnIdleTimeout = "conn.idle_timeout";
    /// <summary>The <c>conn.closed</c> event identifier.</summary>
    public const string ConnClosed = "conn.closed";
    /// <summary>The <c>conn.keepalive_failed</c> event identifier.</summary>
    public const string ConnKeepaliveFailed = "conn.keepalive_failed";
    /// <summary>The <c>conn.dial_failed</c> event identifier.</summary>
    public const string ConnDialFailed = "conn.dial_failed";
    /// <summary>The <c>conn.accept_error</c> event identifier.</summary>
    public const string ConnAcceptError = "conn.accept_error";
    /// <summary>The <c>conn.udp_foreign_source</c> event identifier.</summary>
    public const string ConnUDPForeignSource = "conn.udp_foreign_source";
    /// <summary>The <c>tls.cert_rejected</c> event identifier.</summary>
    public const string TLSCertRejected = "tls.cert_rejected";
    /// <summary>The <c>tls.no_client_cert</c> event identifier.</summary>
    public const string TLSNoClientCert = "tls.no_client_cert";
    /// <summary>The <c>tls.local_cert_rejected</c> event identifier.</summary>
    public const string TLSLocalCertRejected = "tls.local_cert_rejected";
    /// <summary>The <c>tls.plaintext_on_tls_port</c> event identifier.</summary>
    public const string TLSPlaintextOnTLSPort = "tls.plaintext_on_tls_port";
    /// <summary>The <c>tls.non_tls_probe</c> event identifier.</summary>
    public const string TLSNonTLSProbe = "tls.non_tls_probe";
    /// <summary>The <c>tls.handshake_failed</c> event identifier.</summary>
    public const string TLSHandshakeFailed = "tls.handshake_failed";
    /// <summary>The <c>tls.handshake_timeout</c> event identifier.</summary>
    public const string TLSHandshakeTimeout = "tls.handshake_timeout";
    /// <summary>The <c>tls.weak_params</c> event identifier.</summary>
    public const string TLSWeakParams = "tls.weak_params";
    /// <summary>The <c>tls.cert_expiring</c> event identifier.</summary>
    public const string TLSCertExpiring = "tls.cert_expiring";
    /// <summary>The <c>tls.cert_changed</c> event identifier.</summary>
    public const string TLSCertChanged = "tls.cert_changed";
    /// <summary>The <c>peer.mac_changed</c> event identifier.</summary>
    public const string PeerMACChanged = "peer.mac_changed";
    /// <summary>The <c>peer.mac_changed_midsession</c> event identifier.</summary>
    public const string PeerMACChangedMidsession = "peer.mac_changed_midsession";
    /// <summary>The <c>peer.mac_pinning_violation</c> event identifier.</summary>
    public const string PeerMACPinningViolation = "peer.mac_pinning_violation";
    /// <summary>The <c>peer.ip_changed</c> event identifier.</summary>
    public const string PeerIPChanged = "peer.ip_changed";
    /// <summary>The <c>peer.mac_shared</c> event identifier.</summary>
    public const string PeerMACShared = "peer.mac_shared";
    /// <summary>The <c>peer.mac_unavailable</c> event identifier.</summary>
    public const string PeerMACUnavailable = "peer.mac_unavailable";
    /// <summary>The <c>peer.link_addr_changed</c> event identifier.</summary>
    public const string PeerLinkAddrChanged = "peer.link_addr_changed";
    /// <summary>The <c>link.crc_error</c> event identifier.</summary>
    public const string LinkCRCError = "link.crc_error";
    /// <summary>The <c>link.bad_length</c> event identifier.</summary>
    public const string LinkBadLength = "link.bad_length";
    /// <summary>The <c>link.garbage</c> event identifier.</summary>
    public const string LinkGarbage = "link.garbage";
    /// <summary>The <c>link.foreign_dest</c> event identifier.</summary>
    public const string LinkForeignDest = "link.foreign_dest";
    /// <summary>The <c>link.wrong_dir</c> event identifier.</summary>
    public const string LinkWrongDir = "link.wrong_dir";
    /// <summary>The <c>link.invalid_source</c> event identifier.</summary>
    public const string LinkInvalidSource = "link.invalid_source";
    /// <summary>The <c>link.unexpected_master</c> event identifier.</summary>
    public const string LinkUnexpectedMaster = "link.unexpected_master";
    /// <summary>The <c>link.unexpected_source</c> event identifier.</summary>
    public const string LinkUnexpectedSource = "link.unexpected_source";
    /// <summary>The <c>link.invalid_control</c> event identifier.</summary>
    public const string LinkInvalidControl = "link.invalid_control";
    /// <summary>The <c>link.not_reset</c> event identifier.</summary>
    public const string LinkNotReset = "link.not_reset";
    /// <summary>The <c>link.unsupported_function</c> event identifier.</summary>
    public const string LinkUnsupportedFunction = "link.unsupported_function";
    /// <summary>The <c>link.broadcast</c> event identifier.</summary>
    public const string LinkBroadcast = "link.broadcast";
    /// <summary>The <c>link.self_address</c> event identifier.</summary>
    public const string LinkSelfAddress = "link.self_address";
    /// <summary>The <c>transport.discard</c> event identifier.</summary>
    public const string TransportDiscard = "transport.discard";
    /// <summary>The <c>proto.app_malformed</c> event identifier.</summary>
    public const string ProtoAppMalformed = "proto.app_malformed";
    /// <summary>The <c>proto.incomplete_request</c> event identifier.</summary>
    public const string ProtoIncompleteRequest = "proto.incomplete_request";
    /// <summary>The <c>proto.confirm_with_objects</c> event identifier.</summary>
    public const string ProtoConfirmWithObjects = "proto.confirm_with_objects";
    /// <summary>The <c>proto.request_con_uns</c> event identifier.</summary>
    public const string ProtoRequestCONUNS = "proto.request_con_uns";
    /// <summary>The <c>proto.inconsistent_qualifier</c> event identifier.</summary>
    public const string ProtoInconsistentQualifier = "proto.inconsistent_qualifier";
    /// <summary>The <c>proto.no_objects</c> event identifier.</summary>
    public const string ProtoNoObjects = "proto.no_objects";
    /// <summary>The <c>proto.wrong_direction</c> event identifier.</summary>
    public const string ProtoWrongDirection = "proto.wrong_direction";
    /// <summary>The <c>proto.invalid_response</c> event identifier.</summary>
    public const string ProtoInvalidResponse = "proto.invalid_response";
    /// <summary>The <c>proto.parameter_error</c> event identifier.</summary>
    public const string ProtoParameterError = "proto.parameter_error";
    /// <summary>The <c>proto.field_invalid</c> event identifier.</summary>
    public const string ProtoFieldInvalid = "proto.field_invalid";
    /// <summary>The <c>proto.secure_auth_unsupported</c> event identifier.</summary>
    public const string ProtoSecureAuthUnsupported = "proto.secure_auth_unsupported";
    /// <summary>The <c>access.unsupported_function</c> event identifier.</summary>
    public const string AccessUnsupportedFunction = "access.unsupported_function";
    /// <summary>The <c>access.unknown_object</c> event identifier.</summary>
    public const string AccessUnknownObject = "access.unknown_object";
    /// <summary>The <c>access.unknown_point</c> event identifier.</summary>
    public const string AccessUnknownPoint = "access.unknown_point";
    /// <summary>The <c>access.scan</c> event identifier.</summary>
    public const string AccessScan = "access.scan";
    /// <summary>The <c>access.function_blocked</c> event identifier.</summary>
    public const string AccessFunctionBlocked = "access.function_blocked";
    /// <summary>The <c>access.broadcast_misuse</c> event identifier.</summary>
    public const string AccessBroadcastMisuse = "access.broadcast_misuse";
    /// <summary>The <c>access.oversized_request</c> event identifier.</summary>
    public const string AccessOversizedRequest = "access.oversized_request";
    /// <summary>The <c>auth.challenge</c> event identifier.</summary>
    public const string AuthChallenge = "auth.challenge";
    /// <summary>The <c>auth.success</c> event identifier.</summary>
    public const string AuthSuccess = "auth.success";
    /// <summary>The <c>auth.failure</c> event identifier.</summary>
    public const string AuthFailure = "auth.failure";
    /// <summary>The <c>auth.authorization_denied</c> event identifier.</summary>
    public const string AuthAuthorizationDenied = "auth.authorization_denied";
    /// <summary>The <c>auth.reply_timeout</c> event identifier.</summary>
    public const string AuthReplyTimeout = "auth.reply_timeout";
    /// <summary>The <c>auth.unknown_user</c> event identifier.</summary>
    public const string AuthUnknownUser = "auth.unknown_user";
    /// <summary>The <c>auth.key_change</c> event identifier.</summary>
    public const string AuthKeyChange = "auth.key_change";
    /// <summary>The <c>auth.key_change_failed</c> event identifier.</summary>
    public const string AuthKeyChangeFailed = "auth.key_change_failed";
    /// <summary>The <c>auth.key_expired</c> event identifier.</summary>
    public const string AuthKeyExpired = "auth.key_expired";
    /// <summary>The <c>auth.error_received</c> event identifier.</summary>
    public const string AuthErrorReceived = "auth.error_received";
    /// <summary>The <c>auth.broadcast_critical</c> event identifier.</summary>
    public const string AuthBroadcastCritical = "auth.broadcast_critical";
    /// <summary>The <c>auth.object_smuggled</c> event identifier.</summary>
    public const string AuthObjectSmuggled = "auth.object_smuggled";
    /// <summary>The <c>auth.brute_force</c> event identifier.</summary>
    public const string AuthBruteForce = "auth.brute_force";
    /// <summary>The <c>auth.master_challenge_failed</c> event identifier.</summary>
    public const string AuthMasterChallengeFailed = "auth.master_challenge_failed";
    /// <summary>The <c>auth.master_error_received</c> event identifier.</summary>
    public const string AuthMasterErrorReceived = "auth.master_error_received";
    /// <summary>The <c>auth.master_key_change</c> event identifier.</summary>
    public const string AuthMasterKeyChange = "auth.master_key_change";
    /// <summary>The <c>auth.file_failed</c> event identifier.</summary>
    public const string AuthFileFailed = "auth.file_failed";
    /// <summary>The <c>seq.fcb_repeat</c> event identifier.</summary>
    public const string SeqFCBRepeat = "seq.fcb_repeat";
    /// <summary>The <c>seq.test_link_nack</c> event identifier.</summary>
    public const string SeqTestLinkNack = "seq.test_link_nack";
    /// <summary>The <c>seq.repeat_request</c> event identifier.</summary>
    public const string SeqRepeatRequest = "seq.repeat_request";
    /// <summary>The <c>seq.reused_seq</c> event identifier.</summary>
    public const string SeqReusedSeq = "seq.reused_seq";
    /// <summary>The <c>seq.response_stale</c> event identifier.</summary>
    public const string SeqResponseStale = "seq.response_stale";
    /// <summary>The <c>seq.response_repeat</c> event identifier.</summary>
    public const string SeqResponseRepeat = "seq.response_repeat";
    /// <summary>The <c>seq.unsol_duplicate</c> event identifier.</summary>
    public const string SeqUnsolDuplicate = "seq.unsol_duplicate";
    /// <summary>The <c>cmd.duplicate</c> event identifier.</summary>
    public const string CmdDuplicate = "cmd.duplicate";
    /// <summary>The <c>cmd.received</c> event identifier.</summary>
    public const string CmdReceived = "cmd.received";
    /// <summary>The <c>cmd.rejected</c> event identifier.</summary>
    public const string CmdRejected = "cmd.rejected";
    /// <summary>The <c>cmd.brute_force</c> event identifier.</summary>
    public const string CmdBruteForce = "cmd.brute_force";
    /// <summary>The <c>cmd.operate_without_select</c> event identifier.</summary>
    public const string CmdOperateWithoutSelect = "cmd.operate_without_select";
    /// <summary>The <c>cmd.select_timeout</c> event identifier.</summary>
    public const string CmdSelectTimeout = "cmd.select_timeout";
    /// <summary>The <c>cmd.operate_seq_gap</c> event identifier.</summary>
    public const string CmdOperateSeqGap = "cmd.operate_seq_gap";
    /// <summary>The <c>cmd.operate_mismatch</c> event identifier.</summary>
    public const string CmdOperateMismatch = "cmd.operate_mismatch";
    /// <summary>The <c>cmd.select_abandoned</c> event identifier.</summary>
    public const string CmdSelectAbandoned = "cmd.select_abandoned";
    /// <summary>The <c>cmd.no_ack_operate</c> event identifier.</summary>
    public const string CmdNoAckOperate = "cmd.no_ack_operate";
    /// <summary>The <c>cmd.local_mode</c> event identifier.</summary>
    public const string CmdLocalMode = "cmd.local_mode";
    /// <summary>The <c>ctrl.cold_restart</c> event identifier.</summary>
    public const string CtrlColdRestart = "ctrl.cold_restart";
    /// <summary>The <c>ctrl.warm_restart</c> event identifier.</summary>
    public const string CtrlWarmRestart = "ctrl.warm_restart";
    /// <summary>The <c>ctrl.already_executing</c> event identifier.</summary>
    public const string CtrlAlreadyExecuting = "ctrl.already_executing";
    /// <summary>The <c>ctrl.stop_application</c> event identifier.</summary>
    public const string CtrlStopApplication = "ctrl.stop_application";
    /// <summary>The <c>ctrl.start_application</c> event identifier.</summary>
    public const string CtrlStartApplication = "ctrl.start_application";
    /// <summary>The <c>ctrl.initialize_application</c> event identifier.</summary>
    public const string CtrlInitializeApplication = "ctrl.initialize_application";
    /// <summary>The <c>ctrl.initialize_data</c> event identifier.</summary>
    public const string CtrlInitializeData = "ctrl.initialize_data";
    /// <summary>The <c>ctrl.save_config</c> event identifier.</summary>
    public const string CtrlSaveConfig = "ctrl.save_config";
    /// <summary>The <c>ctrl.activate_config</c> event identifier.</summary>
    public const string CtrlActivateConfig = "ctrl.activate_config";
    /// <summary>The <c>ctrl.time_write</c> event identifier.</summary>
    public const string CtrlTimeWrite = "ctrl.time_write";
    /// <summary>The <c>ctrl.clock_step</c> event identifier.</summary>
    public const string CtrlClockStep = "ctrl.clock_step";
    /// <summary>The <c>ctrl.record_current_time</c> event identifier.</summary>
    public const string CtrlRecordCurrentTime = "ctrl.record_current_time";
    /// <summary>The <c>ctrl.delay_measure</c> event identifier.</summary>
    public const string CtrlDelayMeasure = "ctrl.delay_measure";
    /// <summary>The <c>ctrl.unsol_disable</c> event identifier.</summary>
    public const string CtrlUnsolDisable = "ctrl.unsol_disable";
    /// <summary>The <c>ctrl.unsol_enable</c> event identifier.</summary>
    public const string CtrlUnsolEnable = "ctrl.unsol_enable";
    /// <summary>The <c>ctrl.assign_class</c> event identifier.</summary>
    public const string CtrlAssignClass = "ctrl.assign_class";
    /// <summary>The <c>ctrl.deadband_write</c> event identifier.</summary>
    public const string CtrlDeadbandWrite = "ctrl.deadband_write";
    /// <summary>The <c>ctrl.freeze</c> event identifier.</summary>
    public const string CtrlFreeze = "ctrl.freeze";
    /// <summary>The <c>ctrl.freeze_clear</c> event identifier.</summary>
    public const string CtrlFreezeClear = "ctrl.freeze_clear";
    /// <summary>The <c>ctrl.freeze_at_time</c> event identifier.</summary>
    public const string CtrlFreezeAtTime = "ctrl.freeze_at_time";
    /// <summary>The <c>ctrl.restart_iin_cleared</c> event identifier.</summary>
    public const string CtrlRestartIINCleared = "ctrl.restart_iin_cleared";
    /// <summary>The <c>ctrl.attribute_write</c> event identifier.</summary>
    public const string CtrlAttributeWrite = "ctrl.attribute_write";
    /// <summary>The <c>ctrl.terminal_write</c> event identifier.</summary>
    public const string CtrlTerminalWrite = "ctrl.terminal_write";
    /// <summary>The <c>ctrl.dataset_write</c> event identifier.</summary>
    public const string CtrlDatasetWrite = "ctrl.dataset_write";
    /// <summary>The <c>ctrl.time_interval_write</c> event identifier.</summary>
    public const string CtrlTimeIntervalWrite = "ctrl.time_interval_write";
    /// <summary>The <c>ctrl.indication_change</c> event identifier.</summary>
    public const string CtrlIndicationChange = "ctrl.indication_change";
    /// <summary>The <c>remote.restart</c> event identifier.</summary>
    public const string RemoteRestart = "remote.restart";
    /// <summary>The <c>remote.config_corrupt</c> event identifier.</summary>
    public const string RemoteConfigCorrupt = "remote.config_corrupt";
    /// <summary>The <c>remote.device_trouble</c> event identifier.</summary>
    public const string RemoteDeviceTrouble = "remote.device_trouble";
    /// <summary>The <c>remote.local_control</c> event identifier.</summary>
    public const string RemoteLocalControl = "remote.local_control";
    /// <summary>The <c>remote.event_overflow</c> event identifier.</summary>
    public const string RemoteEventOverflow = "remote.event_overflow";
    /// <summary>The <c>remote.rejected</c> event identifier.</summary>
    public const string RemoteRejected = "remote.rejected";
    /// <summary>The <c>unsol.unexpected_confirm</c> event identifier.</summary>
    public const string UnsolUnexpectedConfirm = "unsol.unexpected_confirm";
    /// <summary>The <c>unsol.response_without_request</c> event identifier.</summary>
    public const string UnsolResponseWithoutRequest = "unsol.response_without_request";
    /// <summary>The <c>unsol.while_disabled</c> event identifier.</summary>
    public const string UnsolWhileDisabled = "unsol.while_disabled";
    /// <summary>The <c>file.directory_list</c> event identifier.</summary>
    public const string FileDirectoryList = "file.directory_list";
    /// <summary>The <c>file.read_opened</c> event identifier.</summary>
    public const string FileReadOpened = "file.read_opened";
    /// <summary>The <c>file.write_opened</c> event identifier.</summary>
    public const string FileWriteOpened = "file.write_opened";
    /// <summary>The <c>file.delete</c> event identifier.</summary>
    public const string FileDelete = "file.delete";
    /// <summary>The <c>file.rejected</c> event identifier.</summary>
    public const string FileRejected = "file.rejected";
    /// <summary>The <c>file.suspicious_name</c> event identifier.</summary>
    public const string FileSuspiciousName = "file.suspicious_name";
    /// <summary>The <c>file.invalid_handle</c> event identifier.</summary>
    public const string FileInvalidHandle = "file.invalid_handle";
    /// <summary>The <c>file.oversized</c> event identifier.</summary>
    public const string FileOversized = "file.oversized";
    /// <summary>The <c>file.timeout</c> event identifier.</summary>
    public const string FileTimeout = "file.timeout";
    /// <summary>The <c>file.aborted</c> event identifier.</summary>
    public const string FileAborted = "file.aborted";
    /// <summary>The <c>flood.commands</c> event identifier.</summary>
    public const string FloodCommands = "flood.commands";
    /// <summary>The <c>flood.integrity</c> event identifier.</summary>
    public const string FloodIntegrity = "flood.integrity";
    /// <summary>The <c>flood.event_polls</c> event identifier.</summary>
    public const string FloodEventPolls = "flood.event_polls";
    /// <summary>The <c>flood.restart</c> event identifier.</summary>
    public const string FloodRestart = "flood.restart";
    /// <summary>The <c>flood.time_write</c> event identifier.</summary>
    public const string FloodTimeWrite = "flood.time_write";
    /// <summary>The <c>flood.app_control</c> event identifier.</summary>
    public const string FloodAppControl = "flood.app_control";
    /// <summary>The <c>flood.link_status</c> event identifier.</summary>
    public const string FloodLinkStatus = "flood.link_status";
    /// <summary>The <c>flood.link_reset</c> event identifier.</summary>
    public const string FloodLinkReset = "flood.link_reset";
    /// <summary>The <c>flood.auth_challenges</c> event identifier.</summary>
    public const string FloodAuthChallenges = "flood.auth_challenges";
    /// <summary>The <c>flood.file_ops</c> event identifier.</summary>
    public const string FloodFileOps = "flood.file_ops";
    /// <summary>The <c>flood.frames</c> event identifier.</summary>
    public const string FloodFrames = "flood.frames";
    /// <summary>The <c>flood.unsolicited</c> event identifier.</summary>
    public const string FloodUnsolicited = "flood.unsolicited";
    /// <summary>The <c>dos.rx_backlog</c> event identifier.</summary>
    public const string DoSRxBacklog = "dos.rx_backlog";
    /// <summary>The <c>dos.write_stall</c> event identifier.</summary>
    public const string DoSWriteStall = "dos.write_stall";
    /// <summary>The <c>dos.slow_frame</c> event identifier.</summary>
    public const string DoSSlowFrame = "dos.slow_frame";
    /// <summary>The <c>dos.confirm_withheld</c> event identifier.</summary>
    public const string DoSConfirmWithheld = "dos.confirm_withheld";
    /// <summary>The <c>dos.event_overflow</c> event identifier.</summary>
    public const string DoSEventOverflow = "dos.event_overflow";
    /// <summary>The <c>dos.link_retries_exhausted</c> event identifier.</summary>
    public const string DoSLinkRetriesExhausted = "dos.link_retries_exhausted";
    /// <summary>The <c>dos.queue_drop</c> event identifier.</summary>
    public const string DoSQueueDrop = "dos.queue_drop";
    /// <summary>The <c>meta.events_dropped</c> event identifier.</summary>
    public const string MetaEventsDropped = "meta.events_dropped";
    /// <summary>The <c>meta.sink_panic</c> event identifier.</summary>
    public const string MetaSinkPanic = "meta.sink_panic";

    internal static readonly IReadOnlyDictionary<string, KindInfo> Catalog =
        new Dictionary<string, KindInfo>(StringComparer.Ordinal)
        {
            [ConnAccepted] = new("connection", SecuritySeverity.Info, null, false),
            [ConnConnected] = new("connection", SecuritySeverity.Info, null, false),
            [ConnUnexpected] = new("connection", SecuritySeverity.Warning, "T0849", false),
            [ConnUnexpectedMAC] = new("connection", SecuritySeverity.Alert, "T0849", true),
            [ConnRejectedBusy] = new("connection", SecuritySeverity.Notice, null, false),
            [ConnRate] = new("connection", SecuritySeverity.Alert, "T0814", false),
            [ConnNoData] = new("connection", SecuritySeverity.Notice, "T0846", false),
            [ConnIdleTimeout] = new("connection", SecuritySeverity.Warning, "T0814", false),
            [ConnClosed] = new("connection", SecuritySeverity.Info, null, false),
            [ConnKeepaliveFailed] = new("connection", SecuritySeverity.Notice, null, false),
            [ConnDialFailed] = new("connection", SecuritySeverity.Notice, null, false),
            [ConnAcceptError] = new("connection", SecuritySeverity.Critical, "T0814", false),
            [ConnUDPForeignSource] = new("connection", SecuritySeverity.Warning, "T0849", false),
            [TLSCertRejected] = new("tls", SecuritySeverity.Alert, "T0849", true),
            [TLSNoClientCert] = new("tls", SecuritySeverity.Alert, "T0849", false),
            [TLSLocalCertRejected] = new("tls", SecuritySeverity.Warning, "T0830", false),
            [TLSPlaintextOnTLSPort] = new("tls", SecuritySeverity.Warning, null, false),
            [TLSNonTLSProbe] = new("tls", SecuritySeverity.Notice, "T0846", false),
            [TLSHandshakeFailed] = new("tls", SecuritySeverity.Warning, null, false),
            [TLSHandshakeTimeout] = new("tls", SecuritySeverity.Warning, "T0814", false),
            [TLSWeakParams] = new("tls", SecuritySeverity.Notice, null, false),
            [TLSCertExpiring] = new("tls", SecuritySeverity.Notice, null, false),
            [TLSCertChanged] = new("tls", SecuritySeverity.Notice, "T0830", false),
            [PeerMACChanged] = new("peer", SecuritySeverity.Alert, "T0830", true),
            [PeerMACChangedMidsession] = new("peer", SecuritySeverity.Critical, "T0830", true),
            [PeerMACPinningViolation] = new("peer", SecuritySeverity.Critical, "T0849", true),
            [PeerIPChanged] = new("peer", SecuritySeverity.Info, null, false),
            [PeerMACShared] = new("peer", SecuritySeverity.Notice, "T0830", false),
            [PeerMACUnavailable] = new("peer", SecuritySeverity.Info, null, false),
            [PeerLinkAddrChanged] = new("peer", SecuritySeverity.Warning, "T0849", false),
            [LinkCRCError] = new("link", SecuritySeverity.Notice, null, false),
            [LinkBadLength] = new("link", SecuritySeverity.Notice, null, false),
            [LinkGarbage] = new("link", SecuritySeverity.Notice, null, false),
            [LinkForeignDest] = new("link", SecuritySeverity.Notice, null, false),
            [LinkWrongDir] = new("link", SecuritySeverity.Warning, "T0849", false),
            [LinkInvalidSource] = new("link", SecuritySeverity.Warning, null, false),
            [LinkUnexpectedMaster] = new("link", SecuritySeverity.Warning, "T0849", false),
            [LinkUnexpectedSource] = new("link", SecuritySeverity.Warning, "T0830", false),
            [LinkInvalidControl] = new("link", SecuritySeverity.Warning, null, false),
            [LinkNotReset] = new("link", SecuritySeverity.Notice, null, false),
            [LinkUnsupportedFunction] = new("link", SecuritySeverity.Warning, null, false),
            [LinkBroadcast] = new("link", SecuritySeverity.Notice, null, false),
            [LinkSelfAddress] = new("link", SecuritySeverity.Notice, "T0846", false),
            [TransportDiscard] = new("transport", SecuritySeverity.Notice, null, false),
            [ProtoAppMalformed] = new("protocol", SecuritySeverity.Warning, null, false),
            [ProtoIncompleteRequest] = new("protocol", SecuritySeverity.Warning, null, false),
            [ProtoConfirmWithObjects] = new("protocol", SecuritySeverity.Warning, null, false),
            [ProtoRequestCONUNS] = new("protocol", SecuritySeverity.Warning, null, false),
            [ProtoInconsistentQualifier] = new("protocol", SecuritySeverity.Warning, null, false),
            [ProtoNoObjects] = new("protocol", SecuritySeverity.Warning, null, false),
            [ProtoWrongDirection] = new("protocol", SecuritySeverity.Warning, null, false),
            [ProtoInvalidResponse] = new("protocol", SecuritySeverity.Warning, "T0856", false),
            [ProtoParameterError] = new("protocol", SecuritySeverity.Notice, null, false),
            [ProtoFieldInvalid] = new("protocol", SecuritySeverity.Warning, null, false),
            [ProtoSecureAuthUnsupported] = new("protocol", SecuritySeverity.Notice, "T0846", false),
            [AccessUnsupportedFunction] = new("access", SecuritySeverity.Warning, "T0888", false),
            [AccessUnknownObject] = new("access", SecuritySeverity.Warning, "T0861", false),
            [AccessUnknownPoint] = new("access", SecuritySeverity.Warning, "T0861", false),
            [AccessScan] = new("access", SecuritySeverity.Alert, "T0861", false),
            [AccessFunctionBlocked] = new("access", SecuritySeverity.Warning, "T0855", false),
            [AccessBroadcastMisuse] = new("access", SecuritySeverity.Alert, "T0855", false),
            [AccessOversizedRequest] = new("access", SecuritySeverity.Warning, "T0814", false),
            [AuthChallenge] = new("auth", SecuritySeverity.Info, null, false),
            [AuthSuccess] = new("auth", SecuritySeverity.Info, null, false),
            [AuthFailure] = new("auth", SecuritySeverity.Alert, "T0855", true),
            [AuthAuthorizationDenied] = new("auth", SecuritySeverity.Alert, "T0855", false),
            [AuthReplyTimeout] = new("auth", SecuritySeverity.Warning, null, false),
            [AuthUnknownUser] = new("auth", SecuritySeverity.Warning, "T0806", false),
            [AuthKeyChange] = new("auth", SecuritySeverity.Notice, null, false),
            [AuthKeyChangeFailed] = new("auth", SecuritySeverity.Alert, "T0830", false),
            [AuthKeyExpired] = new("auth", SecuritySeverity.Info, null, false),
            [AuthErrorReceived] = new("auth", SecuritySeverity.Notice, null, false),
            [AuthBroadcastCritical] = new("auth", SecuritySeverity.Warning, null, false),
            [AuthObjectSmuggled] = new("auth", SecuritySeverity.Alert, "T0866", false),
            [AuthBruteForce] = new("auth", SecuritySeverity.Alert, "T0806", false),
            [AuthMasterChallengeFailed] = new("auth", SecuritySeverity.Alert, null, false),
            [AuthMasterErrorReceived] = new("auth", SecuritySeverity.Warning, null, false),
            [AuthMasterKeyChange] = new("auth", SecuritySeverity.Notice, null, false),
            [AuthFileFailed] = new("auth", SecuritySeverity.Warning, "T0806", false),
            [SeqFCBRepeat] = new("sequence", SecuritySeverity.Notice, null, false),
            [SeqTestLinkNack] = new("sequence", SecuritySeverity.Notice, null, false),
            [SeqRepeatRequest] = new("sequence", SecuritySeverity.Info, null, false),
            [SeqReusedSeq] = new("sequence", SecuritySeverity.Notice, "T0830", false),
            [SeqResponseStale] = new("sequence", SecuritySeverity.Notice, null, false),
            [SeqResponseRepeat] = new("sequence", SecuritySeverity.Info, null, false),
            [SeqUnsolDuplicate] = new("sequence", SecuritySeverity.Info, null, false),
            [CmdDuplicate] = new("command", SecuritySeverity.Warning, "T0855", false),
            [CmdReceived] = new("command", SecuritySeverity.Notice, null, false),
            [CmdRejected] = new("command", SecuritySeverity.Notice, null, false),
            [CmdBruteForce] = new("command", SecuritySeverity.Alert, "T0806", false),
            [CmdOperateWithoutSelect] = new("command", SecuritySeverity.Warning, null, false),
            [CmdSelectTimeout] = new("command", SecuritySeverity.Notice, null, false),
            [CmdOperateSeqGap] = new("command", SecuritySeverity.Warning, "T0830", false),
            [CmdOperateMismatch] = new("command", SecuritySeverity.Alert, "T0830", false),
            [CmdSelectAbandoned] = new("command", SecuritySeverity.Info, null, false),
            [CmdNoAckOperate] = new("command", SecuritySeverity.Notice, null, false),
            [CmdLocalMode] = new("command", SecuritySeverity.Info, null, false),
            [CtrlColdRestart] = new("control", SecuritySeverity.Alert, "T0816", true),
            [CtrlWarmRestart] = new("control", SecuritySeverity.Alert, "T0816", true),
            [CtrlAlreadyExecuting] = new("control", SecuritySeverity.Notice, null, false),
            [CtrlStopApplication] = new("control", SecuritySeverity.Alert, "T0858", true),
            [CtrlStartApplication] = new("control", SecuritySeverity.Warning, "T0858", false),
            [CtrlInitializeApplication] = new("control", SecuritySeverity.Warning, "T0858", false),
            [CtrlInitializeData] = new("control", SecuritySeverity.Warning, null, false),
            [CtrlSaveConfig] = new("control", SecuritySeverity.Alert, "T0843", false),
            [CtrlActivateConfig] = new("control", SecuritySeverity.Alert, "T0843", false),
            [CtrlTimeWrite] = new("control", SecuritySeverity.Notice, null, false),
            [CtrlClockStep] = new("control", SecuritySeverity.Alert, null, false),
            [CtrlRecordCurrentTime] = new("control", SecuritySeverity.Info, null, false),
            [CtrlDelayMeasure] = new("control", SecuritySeverity.Info, null, false),
            [CtrlUnsolDisable] = new("control", SecuritySeverity.Warning, "T0804", false),
            [CtrlUnsolEnable] = new("control", SecuritySeverity.Info, null, false),
            [CtrlAssignClass] = new("control", SecuritySeverity.Warning, "T0878", false),
            [CtrlDeadbandWrite] = new("control", SecuritySeverity.Warning, "T0836", false),
            [CtrlFreeze] = new("control", SecuritySeverity.Info, null, false),
            [CtrlFreezeClear] = new("control", SecuritySeverity.Warning, null, false),
            [CtrlFreezeAtTime] = new("control", SecuritySeverity.Info, null, false),
            [CtrlRestartIINCleared] = new("control", SecuritySeverity.Info, null, false),
            [CtrlAttributeWrite] = new("control", SecuritySeverity.Warning, "T0836", false),
            [CtrlTerminalWrite] = new("control", SecuritySeverity.Alert, "T0807", false),
            [CtrlDatasetWrite] = new("control", SecuritySeverity.Notice, null, false),
            [CtrlTimeIntervalWrite] = new("control", SecuritySeverity.Notice, null, false),
            [CtrlIndicationChange] = new("control", SecuritySeverity.Notice, null, false),
            [RemoteRestart] = new("remote", SecuritySeverity.Notice, "T0816", false),
            [RemoteConfigCorrupt] = new("remote", SecuritySeverity.Warning, null, false),
            [RemoteDeviceTrouble] = new("remote", SecuritySeverity.Notice, null, false),
            [RemoteLocalControl] = new("remote", SecuritySeverity.Info, null, false),
            [RemoteEventOverflow] = new("remote", SecuritySeverity.Warning, "T0815", false),
            [RemoteRejected] = new("remote", SecuritySeverity.Notice, null, false),
            [UnsolUnexpectedConfirm] = new("unsolicited", SecuritySeverity.Notice, null, false),
            [UnsolResponseWithoutRequest] = new("unsolicited", SecuritySeverity.Warning, "T0856", false),
            [UnsolWhileDisabled] = new("unsolicited", SecuritySeverity.Warning, "T0856", false),
            [FileDirectoryList] = new("file", SecuritySeverity.Info, "T0846", false),
            [FileReadOpened] = new("file", SecuritySeverity.Notice, "T0845", false),
            [FileWriteOpened] = new("file", SecuritySeverity.Alert, "T0843", true),
            [FileDelete] = new("file", SecuritySeverity.Alert, "T0809", true),
            [FileRejected] = new("file", SecuritySeverity.Warning, null, false),
            [FileSuspiciousName] = new("file", SecuritySeverity.Alert, "T0866", false),
            [FileInvalidHandle] = new("file", SecuritySeverity.Warning, null, false),
            [FileOversized] = new("file", SecuritySeverity.Warning, "T0814", false),
            [FileTimeout] = new("file", SecuritySeverity.Notice, null, false),
            [FileAborted] = new("file", SecuritySeverity.Notice, null, false),
            [FloodCommands] = new("flood", SecuritySeverity.Alert, "T0806", false),
            [FloodIntegrity] = new("flood", SecuritySeverity.Warning, "T0814", false),
            [FloodEventPolls] = new("flood", SecuritySeverity.Warning, "T0814", false),
            [FloodRestart] = new("flood", SecuritySeverity.Alert, "T0816", false),
            [FloodTimeWrite] = new("flood", SecuritySeverity.Warning, null, false),
            [FloodAppControl] = new("flood", SecuritySeverity.Alert, "T0858", false),
            [FloodLinkStatus] = new("flood", SecuritySeverity.Warning, "T0814", false),
            [FloodLinkReset] = new("flood", SecuritySeverity.Warning, "T0814", false),
            [FloodAuthChallenges] = new("flood", SecuritySeverity.Warning, "T0814", false),
            [FloodFileOps] = new("flood", SecuritySeverity.Warning, "T0814", false),
            [FloodFrames] = new("flood", SecuritySeverity.Warning, "T0814", false),
            [FloodUnsolicited] = new("flood", SecuritySeverity.Warning, "T0814", false),
            [DoSRxBacklog] = new("resource", SecuritySeverity.Warning, "T0814", false),
            [DoSWriteStall] = new("resource", SecuritySeverity.Warning, "T0814", false),
            [DoSSlowFrame] = new("resource", SecuritySeverity.Warning, "T0814", false),
            [DoSConfirmWithheld] = new("resource", SecuritySeverity.Warning, "T0804", false),
            [DoSEventOverflow] = new("resource", SecuritySeverity.Warning, "T0815", false),
            [DoSLinkRetriesExhausted] = new("resource", SecuritySeverity.Notice, null, false),
            [DoSQueueDrop] = new("resource", SecuritySeverity.Warning, null, false),
            [MetaEventsDropped] = new("meta", SecuritySeverity.Warning, null, true),
            [MetaSinkPanic] = new("meta", SecuritySeverity.Critical, null, true),
        };

    internal readonly record struct KindInfo(string Category, SecuritySeverity Severity, string? Technique, bool AlwaysDeliver);
}
