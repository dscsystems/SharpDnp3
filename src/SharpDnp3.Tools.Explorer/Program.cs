// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.
//
// dnp3-explorer is a terminal browser for a DNP3 outstation.
//
// It connects, polls, shows what came back, and lets an operator issue controls
// — the things you actually want when pointing at an unfamiliar device and
// asking "what is this thing reporting, and does it respond?".
//
//   dnp3-explorer -host 10.0.0.5:20000
//   dnp3-explorer -demo                  # a simulated outstation in-process
//
// Demo mode runs a full outstation inside the same program, connected over an
// in-memory pipe. It is the real link, transport, application and object layers
// with no socket and no hardware, which makes the tool usable — and demonstrable
// — without a device to point at.

using System.Globalization;
using System.Threading.Channels;
using SharpDnp3;
using SharpDnp3.Tools.Explorer;

var host = "";
var serial = "";
var baud = 9600;
ushort local = 1;
ushort remote = 10;
var demo = false;
var poll = TimeSpan.FromSeconds(5);
var timeout = TimeSpan.FromSeconds(5);

var mouse = true;
var inline = false;
var direct = false;
var noConfirm = false;
uint pulse = 1000;
var stale = TimeSpan.FromSeconds(30);

for (var i = 0; i < args.Length; i++)
{
    try
    {
        switch (args[i])
        {
            case "-host" when i + 1 < args.Length: host = args[++i]; break;
            case "-serial" when i + 1 < args.Length: serial = args[++i]; break;
            case "-baud" when i + 1 < args.Length:
                baud = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "-local" when i + 1 < args.Length:
                local = ushort.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "-remote" when i + 1 < args.Length:
                remote = ushort.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "-demo": demo = true; break;
            case "-poll" when i + 1 < args.Length: poll = Duration.Parse(args[++i]); break;
            case "-timeout" when i + 1 < args.Length: timeout = Duration.Parse(args[++i]); break;
            case "-mouse": mouse = true; break;
            case "-no-mouse": mouse = false; break;
            case "-inline": inline = true; break;
            case "-direct": direct = true; break;
            case "-no-confirm": noConfirm = true; break;
            case "-pulse" when i + 1 < args.Length:
                pulse = uint.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "-stale" when i + 1 < args.Length: stale = Duration.Parse(args[++i]); break;
            case "-h" or "--help": Usage(); return 0;
            default:
                Console.Error.WriteLine($"dnp3-explorer: unknown argument {args[i]}");
                Usage();
                return 1;
        }
    }
    catch (Exception ex) when (ex is FormatException or OverflowException)
    {
        Console.Error.WriteLine($"dnp3-explorer: {args[i]}: {ex.Message}");
        return 1;
    }
}

if (host.Length == 0 && serial.Length == 0 && !demo)
{
    Usage();
    Console.Error.WriteLine("dnp3-explorer: give -host, -serial, or -demo");
    return 1;
}

var start = new LinkParams
{
    Demo = demo,
    Serial = serial,
    Baud = baud,
    Host = host,
    Local = local,
    Remote = remote,
    Timeout = timeout,
    Poll = poll,
};

try
{
    start.Validate();
}
catch (FormatException ex)
{
    Console.Error.WriteLine("dnp3-explorer: " + ex.Message);
    return 1;
}

using var cts = new CancellationTokenSource();

// Messages are dropped rather than blocking the session when the interface
// falls behind: an operator's scrollback is not worth a missed poll.
var msgs = Channel.CreateBounded<IMsg>(new BoundedChannelOptions(2048)
{
    FullMode = BoundedChannelFullMode.DropWrite,
});

var conn = new Connection(msgs, cts.Token);
conn.Supervisor = new Supervisor(conn, cts.Token);

// The session is brought up through the same path a reconnect uses, so the one
// an operator reaches for after changing an address is the path that has been
// exercised since startup rather than a second implementation.
await conn.Supervisor.StartAsync(start).ConfigureAwait(false);

var model = new Model(conn)
{
    MouseEnabled = mouse,

    // The alternate screen is the default: the tool lays out a fixed frame and
    // fills the terminal with it, which is what makes a table, a scrollbar and a
    // footer possible at all. -inline gives back the old behaviour for the times
    // when leaving the session in the scrollback is worth more than the layout —
    // logging a commissioning run, or a terminal that cannot switch screens.
    AltMode = !inline,
    Sbo = !direct,
    Confirm = !noConfirm,
    PulseMs = pulse,
    StaleAge = stale,
};

using var terminal = new Terminal(model.AltMode, model.MouseEnabled);
terminal.Start();

var input = new Thread(() => terminal.ReadInput(
    m => msgs.Writer.TryWrite(m), cts.Token))
{
    IsBackground = true,
    Name = "terminal input",
};
input.Start();

var ticker = TickAsync(msgs, cts.Token);

try
{
    while (!model.Quitting)
    {
        var (w, h) = Terminal.Size();
        model.Width = w;
        model.Height = h;
        terminal.Draw(model.Render(), w, h);

        if (!await msgs.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
        {
            break;
        }

        while (msgs.Reader.TryRead(out var msg))
        {
            var cmd = model.Update(msg);
            if (cmd is not null)
            {
                Dispatch(cmd);
            }

            if (model.Quitting)
            {
                break;
            }
        }
    }
}
catch (OperationCanceledException)
{
    // Shutting down.
}
finally
{
    await cts.CancelAsync().ConfigureAwait(false);
    terminal.Stop();

    await conn.Supervisor.StopAsync().ConfigureAwait(false);
    try
    {
        await ticker.ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }
}

return 0;

// Runs one action off the update loop and feeds its result back in, so a slow
// outstation never freezes the interface.
void Dispatch(Cmd cmd) => _ = Task.Run(async () =>
{
    try
    {
        var result = await cmd().ConfigureAwait(false);
        if (result is not null)
        {
            conn.Push(result);
        }
    }
    catch (Exception ex) when (ex is Dnp3Exception or IOException or OperationCanceledException)
    {
        conn.Push(new CommandResultMsg("action failed: " + ex.Message));
    }
});

// Drives the age column, the clock and the rate meter without needing a repaint
// on every protocol event.
static async Task TickAsync(Channel<IMsg> msgs, CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            msgs.Writer.TryWrite(new TickMsg(DateTimeOffset.Now));
        }
    }
    catch (OperationCanceledException)
    {
    }
}

static void Usage() => Console.Error.Write(
    """
    dnp3-explorer — a terminal browser for a DNP3 outstation

    Usage:
      dnp3-explorer -host HOST:PORT [flags]
      dnp3-explorer -demo

    Connection (all of it editable while running, with C):
      -host ADDR       outstation address
      -serial PORT     use a serial port instead of TCP
      -baud RATE       serial line rate                  (default 9600)
      -local N         master link address               (default 1)
      -remote N        outstation link address           (default 10)
      -poll DUR        event class poll interval         (default 5s, 0 disables)
      -timeout DUR     response timeout                  (default 5s)
      -demo            run a simulated outstation in-process

    Interface:
      -no-mouse        disable the mouse
      -inline          draw inline instead of taking the whole terminal
      -stale DUR       fade points not updated for this long   (default 30s)

    Controls:
      -direct          direct operate instead of select-before-operate
      -no-confirm      issue controls without asking first
      -pulse MS        pulse duration for trip and close  (default 1000)

    Keys:
      1-5 / tab        switch screens
      up/down, j/k     move the cursor; pgup/pgdn by a page
      enter            act on the selected row (control, setpoint, inspector)
      i / p            integrity poll / poll event classes 1, 2 and 3
      s                range scan a group
      t / T            set the outstation clock (T measures the link delay)
      u / U            enable / disable unsolicited reporting
      R                restart the outstation
      c / o            close / open the selected binary output
      b                write a deadband to the selected analog input
      S                switch between select-before-operate and direct operate
      C                edit the connection and reconnect in place
      / and esc        filter the list, and clear the filter
      < > and r        change and reverse the sort column
      d                the point inspector
      f                follow the newest row
      x                clear the current list
      e                export the current list as CSV
      ? or 5           the full key and mouse reference
      q                quit

    Mouse:
      click a tab, a row, a column heading or a footer button; click a
      selected row again to act on it, right-click one for the inspector,
      scroll with the wheel, and drag the scrollbar.

    Examples:
      dnp3-explorer -demo
      dnp3-explorer -host 10.0.0.5:20000 -remote 10 -poll 2s

    """);
