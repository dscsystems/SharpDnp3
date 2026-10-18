// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpDnp3.Outstation;

namespace SharpDnp3.Conformance.Dut;

/// <summary>One plugin call: IntegrationPlugin's method name, and its arguments by name.</summary>
internal sealed record Request
{
    [JsonPropertyName("method")]
    public string Method { get; init; } = "";

    [JsonPropertyName("args")]
    public Args? Args { get; init; }
}

/// <summary>What the plugin logs, what it returns, and why it throws.</summary>
internal sealed class Reply
{
    [JsonPropertyName("log")]
    public List<string> Log { get; } = [];

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public void Logf(FormattableString line) => Log.Add(line.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// The union of every method's arguments. A method reads the ones it takes;
/// decoding them all into one type keeps the plugin's side a list of names
/// rather than a type per method.
/// </summary>
internal sealed record Args
{
    [JsonPropertyName("value")]
    public bool Value { get; init; }

    [JsonPropertyName("kind")]
    public string? KindName { get; init; }

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("indices")]
    public List<int> Indices { get; init; } = [];

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("class")]
    public int Class { get; init; }

    [JsonPropertyName("expectation")]
    public string? Expect { get; init; }

    [JsonPropertyName("points")]
    public List<Point> Points { get; init; } = [];

    [JsonPropertyName("previous")]
    public List<Point> Previous { get; init; } = [];

    [JsonPropertyName("isClass0")]
    public bool IsClass0 { get; init; }

    [JsonPropertyName("address")]
    public int Address { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; init; }

    [JsonPropertyName("retries")]
    public int? Retries { get; init; }

    [JsonPropertyName("size")]
    public int Size { get; init; }

    [JsonPropertyName("expected")]
    public double Expected { get; init; }

    [JsonPropertyName("actual")]
    public double Actual { get; init; }

    [JsonPropertyName("analogValue")]
    public double Analog { get; init; }

    [JsonPropertyName("flag")]
    public int Flag { get; init; }

    [JsonPropertyName("delayMs")]
    public long DelayMs { get; init; }

    [JsonPropertyName("sentMs")]
    public long SentMs { get; init; }

    [JsonPropertyName("eventMs")]
    public long EventMs { get; init; }

    [JsonPropertyName("attributes")]
    public List<AttributeIn> Attributes { get; init; } = [];

    [JsonIgnore]
    public Kind Kind => Kinds.Parse(KindName);
}

/// <summary>A device attribute as dnp3c decoded it.</summary>
internal sealed record AttributeIn
{
    [JsonPropertyName("variation")]
    public int Variation { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("number")]
    public double Number { get; init; }
}

/// <summary>Carries out the plugin's calls on the device.</summary>
internal sealed class Rpc(Device d)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Kind[] AllKinds =
    [
        Kind.Binary, Kind.DoubleBit, Kind.Counter, Kind.FrozenCounter,
        Kind.Analog, Kind.BinaryOutput, Kind.AnalogOutput,
    ];

    /// <summary>Answers one HTTP request.</summary>
    public async Task ServeAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            Request? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<Request>(context.Request.InputStream, Json);
            }
            catch (JsonException ex)
            {
                response.StatusCode = 400;
                await using var w = new StreamWriter(response.OutputStream);
                await w.WriteAsync(ex.Message);
                return;
            }

            var reply = Call(request?.Method ?? "", request?.Args ?? new Args());
            response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(response.OutputStream, reply, Json);
        }
        catch (HttpListenerException)
        {
            // The plugin reads the reply or times out; a write that fails has
            // nobody left to tell.
        }
        finally
        {
            response.Close();
        }
    }

    /// <summary>Carries out one call, turning its failure into the reply's error.</summary>
    public Reply Call(string method, Args a)
    {
        var r = new Reply();
        try
        {
            Dispatch(method, a, r);
        }
        catch (Exception ex) when (ex is DutException or Dnp3Exception or InvalidOperationException or ArgumentException)
        {
            r.Error = ex.Message;
        }

        return r;
    }

    private void Dispatch(string method, Args a, Reply r)
    {
        switch (method)
        {
            case "startProcedure":
                r.Logf($"restarted the outstation with its default configuration");
                d.Reset();
                break;
            case "endProcedure":
                break;

            case "cyclePower":
                r.Logf($"restarted the outstation");
                d.Restart();
                break;
            case "setLinkLayerConfirm":
                Configure(r, s => s.LinkConfirms = a.Value, $"link-layer confirmation {OnOff(a.Value)}");
                break;
            case "setSelfAddressSupport":
                Configure(r, s => s.SelfAddress = a.Value, $"self-address support {OnOff(a.Value)}");
                break;
            case "enableUnsolicitedResponse":
                Configure(r, s => s.Unsolicited = a.Value, $"unsolicited responses {OnOff(a.Value)}");
                break;
            case "setUnsolicitedResponseTimeout":
                Configure(
                    r,
                    s => s.UnsolTimeout = TimeSpan.FromMilliseconds(a.TimeoutMs),
                    $"unsolicited confirm timeout {a.TimeoutMs} ms");
                break;
            case "setMaxUnsolicitedRetries":
                Configure(
                    r,
                    s => s.UnsolRetries = a.Retries,
                    a.Retries is { } n ? (FormattableString)$"unsolicited retries {n}" : $"unsolicited retries unlimited");
                break;
            case "setColdRestartSupport":
                d.SetColdRestart(a.Value);
                r.Logf($"cold restart support {OnOff(a.Value)}");
                break;
            case "setMasterAddress":
                Configure(r, s => s.Master = (ushort)a.Address, $"master address {a.Address}");
                break;

            // The fragment size and the installed points are changed and
            // applied at once: the procedures go on to test them without a
            // restart.
            case "setMaxFragmentSize":
                Reconfigure(r, s => s.FragmentSize = a.Size, $"maximum transmit fragment {a.Size} octets");
                break;
            case "uninstall":
            {
                var k = a.Kind;
                Reconfigure(r, s => s.Uninstalled.Add(k), $"every {k.Name()} point uninstalled");
                break;
            }

            case "reinstallPoints":
                Reconfigure(r, s => s.Uninstalled.Clear(), $"every point installed");
                break;

            // ---------- Supervisory control ----------
            case "enableRemoteSupervisoryControl":
                r.Logf($"every binary output under remote control");
                d.SetLocal(false, []);
                break;
            case "setGlobalRemoteSupervisoryControl":
                r.Logf($"every binary output under local control");
                d.SetLocal(true, []);
                break;
            case "setIndividualRemoteSupervisoryControl":
                r.Logf($"binary output {a.Index} under local control");
                d.SetLocal(false, [(ushort)a.Index]);
                break;

            // ---------- Controls ----------
            case "checkOperate":
            {
                var ok = d.TakeOperation(a.Kind, (ushort)a.Index, out var ops);
                r.Logf($"operations not yet checked: {DescribeOps(ops)}");
                if (!ok)
                {
                    throw new DutException($"{a.KindName} {a.Index} did not operate");
                }

                break;
            }

            case "checkNotOperate":
            {
                var ops = d.TakeOperations();
                r.Logf($"operations not yet checked: {DescribeOps(ops)}");
                var k = a.Kind;
                foreach (var op in ops.Where(op => op.Kind == k))
                {
                    throw new DutException($"{op.Kind.Name()} {op.Index} operated");
                }

                break;
            }

            case "checkAnalogOutputTolerance":
                r.Logf($"sent {a.Expected}, reported {a.Actual}; the device keeps the value exactly");
                if (!Verify.SameValue(Kind.AnalogOutput, a.Actual, a.Expected))
                {
                    throw new DutException(string.Format(
                        CultureInfo.InvariantCulture, "analog output reported as {0}, sent as {1}", a.Actual, a.Expected));
                }

                break;

            // ---------- Event generation ----------
            case "generateInputChanges":
            {
                // Every counter counts, so a freeze of them all finds none at
                // zero; of the other types, three points change.
                var k = a.Kind;
                Generate(r, k, k == Kind.Counter ? Device.PointsPerType : 3);
                break;
            }

            case "generateExactInputEvents":
                Generate(r, a.Kind, a.Count);
                break;
            case "generateClassEvents":
                GenerateClasses(r, a.Class, 3);
                break;
            case "generateExactClassEvents":
                GenerateClasses(r, a.Class, a.Count);
                break;
            case "generatePattern":
            {
                var k = a.Kind;
                var changes = new List<Change>();
                for (var i = 0; i < Device.Generated(k); i++)
                {
                    double v = k switch
                    {
                        Kind.Binary => i % 2,
                        Kind.DoubleBit => (double)DoubleBit.DeterminedOff + (i % 2),
                        Kind.Analog => 100 + (10 * i),
                        _ => throw new DutException($"no pattern for {k.Name()} points"),
                    };
                    changes.Add(new Change(k, i, _ => v));
                }

                Record(r, changes);
                break;
            }

            case "generateEventOn":
            {
                var k = a.Kind;
                Record(r, [new Change(k, a.Index, Changes.NextFor(k))]);
                break;
            }

            case "generateExactFrozenCounterEvents":
                Generate(r, Kind.FrozenCounter, a.Count);
                break;
            case "generateExactSmallAnalogInputEvents":
                // StepAnalog stays inside 16 bits.
                Generate(r, Kind.Analog, a.Count);
                break;
            case "generateMultiFragmentResponse":
                // Enough events of each class that a read of it does not fit
                // in one 2048-octet fragment: g2v2, g22v5 and g32v3 events take
                // 9, 13 and 13 octets with their indices.
                Record(r, [.. Changes.Of(Kind.Binary, 300), .. Changes.Of(Kind.Counter, 200), .. Changes.Of(Kind.Analog, 200)]);
                break;
            case "generateUnsolicitedEvents":
                r.Result = "performed";
                Generate(r, Kind.Binary, 1);
                break;
            case "generateNotEnoughUnsolicitedEvents":
                Generate(r, Kind.Binary, 1);
                break;
            case "generateTimeEvent":
            {
                var batch = d.Apply([new Change(Kind.Binary, 0, Changes.Toggle)]);
                if (batch.Count != 1)
                {
                    throw new DutException("the change raised no event");
                }

                lock (d.Gate)
                {
                    d.Track.TimeEvent = batch[0];
                }

                r.Logf($"generated {batch[0]}");
                break;
            }

            case "setAnalogInput":
            {
                var v = a.Analog;
                Record(r, [new Change(Kind.Analog, a.Index, _ => v)]);
                break;
            }

            case "setFlagsNormal":
            {
                var k = a.Kind;
                var changes = Enumerable.Range(0, Device.PointsPerType)
                    .Select(i => new Change(k, i, Changes.Same, Flags.Online.Value))
                    .ToList();
                r.Logf($"every {k.Name()} point ONLINE, with no other flag");
                Record(r, changes);
                break;
            }

            case "setFlagAbnormal":
            {
                var k = a.Kind;
                if (a.Indices.Count == 0)
                {
                    throw new DutException($"no {k.Name()} point to set a flag abnormal on");
                }

                var i = a.Indices[0];
                r.Logf($"{k.Name()} {i} with ONLINE clear");
                Record(r, [new Change(k, i, Changes.Same, 0)]);
                r.Result = new Dictionary<string, int> { ["index"] = i, ["flags"] = 0 };
                break;
            }

            case "changeOutputFlag":
            {
                var k = a.Kind;
                var held = d.Current();
                if (!held.TryValue(k, a.Index, out _, out var flags))
                {
                    throw new DutException($"no {k.Name()} {a.Index}");
                }

                flags ^= (byte)(1 << a.Flag);
                r.Logf($"{k.Name()} {a.Index} flag bit {a.Flag} toggled");
                Record(r, [new Change(k, a.Index, Changes.Same, flags)]);
                break;
            }

            case "changeBinaryOutputState":
                Record(r, [new Change(Kind.BinaryOutput, a.Index, Changes.Toggle)]);
                break;
            case "setAnalogOutputValue":
            {
                var v = a.Analog;
                Record(r, [new Change(Kind.AnalogOutput, a.Index, _ => v)]);
                break;
            }

            case "recordCurrentCounterValues":
            {
                var held = d.Current();
                lock (d.Gate)
                {
                    d.Track.Counters = held;
                }

                r.Logf($"counters [{string.Join(' ', held.Counter.Select(c => c.Value))}], frozen [{string.Join(' ', held.FrozenCounter.Select(c => c.Value))}]");
                break;
            }

            // ---------- Checks of static values ----------
            case "verifyAllStaticCurrentValues":
            {
                var held = d.Current();
                r.Logf($"checked {a.Points.Count} points against the database");
                Verify.Static(a.Points, held, a.IsClass0 ? AllKinds : []);
                break;
            }

            case "verifyInputCurrentValues":
            {
                var held = d.Current();
                r.Logf($"checked {a.Points.Count} points against the database");
                Verify.Static(a.Points, held, []);
                break;
            }

            case "verifyRecordedCounterValues":
            {
                Snapshot? rec;
                lock (d.Gate)
                {
                    rec = d.Track.Counters;
                }

                if (rec is null)
                {
                    throw new DutException("no counter values were recorded");
                }

                r.Logf($"checked {a.Points.Count} counters against the values recorded");
                Verify.Static(a.Points, rec, []);
                break;
            }

            // ---------- Checks of events ----------
            case "verifyInputEvents":
                VerifyExpectation(r, a.Points, [a.Kind], a.Expect);
                break;
            case "verifyFirstInputEvent":
                VerifyFirst(r, a.Points, [a.Kind]);
                break;
            case "verifyClassEvents":
                VerifyExpectation(r, a.Points, Changes.ClassKinds(ClassMask(a.Class)), a.Expect);
                break;
            case "verifyFirstClassEvent":
                VerifyFirst(r, a.Points, Changes.ClassKinds(ClassMask(a.Class)));
                break;
            case "verifyFrozenCounterEvents":
                VerifyExpectation(r, a.Points, [Kind.FrozenCounter], "LatestBatch");
                break;
            case "verifyPolledDataDoNotRepeatUnsolicitedData":
                r.Logf($"unsolicited: {Verify.Describe(a.Previous)}");
                r.Logf($"polled: {Verify.Describe(a.Points)}");
                foreach (var u in a.Previous)
                {
                    if (a.Points.FirstOrDefault(p => SamePoint(u, p)) is { } p)
                    {
                        throw new DutException($"{p} was reported unsolicited and then polled");
                    }
                }

                break;
            case "verifyResponseRetainsEvents":
                r.Logf($"earlier: {Verify.Describe(a.Previous)}");
                r.Logf($"now: {Verify.Describe(a.Points)}");
                foreach (var u in a.Previous)
                {
                    if (!a.Points.Any(p => SamePoint(u, p)))
                    {
                        throw new DutException($"{u} was reported earlier and is missing now");
                    }
                }

                break;
            case "verifyTimestamp":
                VerifyTimestamp(r, a.Points);
                break;
            case "verifyCommandEventTime":
            {
                var diff = a.EventMs - a.SentMs;
                r.Logf($"the event is {diff} ms after the command was sent");
                if (diff is < -1000 or > 1000)
                {
                    throw new DutException($"the command event is {diff} ms from when the command was sent");
                }

                break;
            }

            case "verifyDelayMeasurementAccuracy":
                // The outstation reports the time it held the request, which on
                // a host answering over loopback is a few milliseconds at most.
                r.Logf($"reported delay {a.DelayMs} ms");
                if (a.DelayMs is < 0 or > 100)
                {
                    throw new DutException($"a delay of {a.DelayMs} ms is not this device's processing time");
                }

                break;
            case "verifyDeviceAttributes":
                VerifyAttributes(r, a.Attributes, d.Attributes());
                break;

            default:
                throw new DutException($"the device under test does not implement {method}");
        }
    }

    /// <summary>Changes a setting that takes effect at the next restart.</summary>
    private void Configure(Reply r, Action<Settings> fn, FormattableString what)
    {
        lock (d.Gate)
        {
            fn(d.NextSettings);
        }

        r.Logf($"configured {what.ToString(CultureInfo.InvariantCulture)}, from the next restart");
    }

    /// <summary>Changes a setting and restarts the outstation with it.</summary>
    private void Reconfigure(Reply r, Action<Settings> fn, FormattableString what)
    {
        lock (d.Gate)
        {
            var s = d.NextSettings.Copy();
            fn(s);
            d.NextSettings = s;
        }

        r.Logf($"configured {what.ToString(CultureInfo.InvariantCulture)}, and restarted the outstation");
        d.Restart();
    }

    private static string OnOff(bool b) => b ? "on" : "off";

    /// <summary>Makes n changes of a kind as one batch.</summary>
    private void Generate(Reply r, Kind k, int n) => Record(r, Changes.Of(k, n));

    /// <summary>Applies changes and logs the events they raised.</summary>
    private void Record(Reply r, List<Change> changes)
    {
        var batch = d.Apply(changes);
        if (batch.Count <= 20)
        {
            r.Logf($"generated {batch.Count} event(s): {Verify.Describe(batch)}");
        }
        else
        {
            r.Logf($"generated {batch.Count} events");
        }
    }

    /// <summary>Turns the plugin's class number into a mask, 0 being all three.</summary>
    private static Class ClassMask(int c) => c switch
    {
        1 => SharpDnp3.Class.Class1,
        2 => SharpDnp3.Class.Class2,
        3 => SharpDnp3.Class.Class3,
        _ => SharpDnp3.Class.Class1 | SharpDnp3.Class.Class2 | SharpDnp3.Class.Class3,
    };

    /// <summary>
    /// Makes n events in a class, or n in each of classes 1, 2 and 3 for class
    /// 0, as one batch. A class's events are of the first input type in it:
    /// binary inputs, counters and analog inputs.
    /// </summary>
    private void GenerateClasses(Reply r, int cls, int n)
    {
        int[] classes = cls == 0 ? [1, 2, 3] : [cls];
        var changes = new List<Change>();
        foreach (var c in classes)
        {
            var kinds = Changes.ClassKinds(ClassMask(c));
            if (kinds.Count == 0)
            {
                throw new DutException($"no input is in class {c}");
            }

            changes.AddRange(Changes.Of(kinds[0], n));
        }

        Record(r, changes);
    }

    /// <summary>Checks events against the generated ones, as an EventExpectation says.</summary>
    private void VerifyExpectation(Reply r, List<Point> points, List<Kind> kinds, string? expectation)
    {
        List<DutEvent> latest, all;
        int checkedCount;
        lock (d.Gate)
        {
            latest = Verify.OfKinds(d.Track.Latest(), kinds);
            all = Verify.OfKinds(d.Track.All(), kinds);
            checkedCount = d.Track.Checked;
        }

        r.Logf($"reported: {Verify.Describe(points)}");
        switch (expectation)
        {
            case "LatestBatch":
                r.Logf($"expected the latest batch: {Verify.Describe(latest)}");
                Verify.Events(points, latest);
                break;
            case "RemainingLatestBatch":
            {
                var rest = latest.Skip(Math.Min(checkedCount, latest.Count)).ToList();
                r.Logf($"expected the rest of the latest batch: {Verify.Describe(rest)}");
                Verify.Events(points, rest);
                break;
            }

            case "AllGeneratedBatches":
                r.Logf($"expected every event generated: {Verify.Describe(all)}");
                Verify.Events(points, all);
                break;
            case "CurrentValuesWithOptionalEarlier":
            {
                var held = d.Current();
                r.Logf($"expected the present value of each point changed in: {Verify.Describe(latest)}");
                Verify.CurrentWithEarlier(points, latest, all, held);
                break;
            }

            case "ReturnedEventsOnly":
                Verify.Generated(points, all);
                break;
            default:
                throw new DutException($"the device under test cannot check \"{expectation}\"");
        }
    }

    /// <summary>Checks a response holding only the first event of the latest batch.</summary>
    private void VerifyFirst(Reply r, List<Point> points, List<Kind> kinds)
    {
        List<DutEvent> latest;
        lock (d.Gate)
        {
            latest = Verify.OfKinds(d.Track.Latest(), kinds);
        }

        if (latest.Count == 0)
        {
            throw new DutException("no event of the type was generated");
        }

        r.Logf($"reported: {Verify.Describe(points)}; expected {latest[0]}");
        Verify.Events(points, latest[..1]);
        lock (d.Gate)
        {
            d.Track.Checked = 1;
        }
    }

    private void VerifyTimestamp(Reply r, List<Point> points)
    {
        DutEvent? te;
        lock (d.Gate)
        {
            te = d.Track.TimeEvent;
        }

        if (te is null)
        {
            throw new DutException("no time event was generated");
        }

        r.Logf($"generated {te}");
        foreach (var p in points)
        {
            if (p.Time is { } t && Verify.SameEvent(p, te))
            {
                var diff = t - te.At.ToUnixTimeMilliseconds();
                r.Logf($"reported {p}, {diff} ms from when it happened");
                if (diff > Verify.TimestampTolerance || diff < -Verify.TimestampTolerance)
                {
                    throw new DutException($"{p}: the event happened at {te.At.UtcDateTime:HH:mm:ss.fff}");
                }

                return;
            }
        }

        throw new DutException($"the event is not among {Verify.Describe(points)}");
    }

    /// <summary>Reports whether two reports are of the same event.</summary>
    internal static bool SamePoint(Point a, Point b)
    {
        if (a.KindName != b.KindName || a.Index != b.Index || !Verify.SameValue(a.Kind, a.Value, b.Value))
        {
            return false;
        }

        return a.Time is not { } at || b.Time is not { } bt || at == bt;
    }

    private static string DescribeOps(List<Operation> ops) =>
        ops.Count == 0 ? "none" : string.Join(", ", ops.Select(op => $"{op.Kind.Name()} {op.Index}"));

    /// <summary>
    /// Checks a read of the standard attribute set against what the device
    /// reports about itself.
    /// </summary>
    internal static void VerifyAttributes(Reply r, List<AttributeIn> got, Dictionary<byte, DeviceAttribute> want)
    {
        var errs = new List<string>();
        foreach (var a in got)
        {
            var v = (byte)a.Variation;
            r.Logf($"g0v{v}: {a.Type} \"{a.Text}\" {a.Number}");
            if (v is 254 or 255)
            {
                continue;
            }

            if (!want.TryGetValue(v, out var w))
            {
                errs.Add($"g0v{v} is not an attribute the device has");
                continue;
            }

            switch (w.Type)
            {
                case AttributeType.VisibleString:
                    if (a.Type != "vstr" || a.Text != w.Text)
                    {
                        errs.Add($"g0v{v} is {a.Type} \"{a.Text}\"; the device has \"{w.Text}\"");
                    }

                    break;
                case AttributeType.UnsignedInt or AttributeType.SignedInt:
                    if ((a.Type != "uint" && a.Type != "int") || a.Number != w.Number)
                    {
                        errs.Add(string.Format(
                            CultureInfo.InvariantCulture, "g0v{0} is {1} {2}; the device has {3}", v, a.Type, a.Number, w.Number));
                    }

                    break;
                case AttributeType.Float:
                    if (a.Type != "flt" || Math.Abs(a.Number - w.Real) > 1e-6)
                    {
                        errs.Add(string.Format(
                            CultureInfo.InvariantCulture, "g0v{0} is {1} {2}; the device has {3}", v, a.Type, a.Number, w.Real));
                    }

                    break;
            }
        }

        Verify.Throw(errs);
    }
}
