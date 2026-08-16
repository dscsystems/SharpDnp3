// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SharpDnp3.Tools.Explorer;

// What a terminal UI needs from the terminal, and nothing more: raw keystrokes,
// mouse reports, a frame buffer that only repaints the rows that changed, and —
// above all — a guarantee that the terminal is handed back the way it was found.
// A tool that leaves an operator's shell without an echo is a tool they stop
// running.

/// <summary>Puts the terminal into the state a full-screen interface needs.</summary>
public sealed class Terminal : IDisposable
{
    private const string Csi = "\u001b[";

    private readonly bool _altScreen;
    private readonly bool _mouse;
    private readonly Stream _input;
    private readonly TextWriter _output;
    private readonly byte[] _buffer = new byte[4096];
    private readonly List<byte> _pending = [];

    private byte[]? _savedTermios;
    private uint _savedInputMode;
    private uint _savedOutputMode;
    private bool _started;
    private List<string> _previous = [];
    private int _lastWidth;
    private int _lastHeight;

    /// <summary>Creates a terminal wrapper; nothing changes until it is started.</summary>
    public Terminal(bool altScreen, bool mouse)
    {
        _altScreen = altScreen;
        _mouse = mouse;

        // Standard input is taken as a plain file descriptor rather than
        // through Console: the console reader configures the terminal for its
        // own line-oriented model on the first read, which undoes raw mode and
        // leaves every keystroke waiting for an Enter that a full-screen
        // interface never asks for.
        _input = OperatingSystem.IsWindows()
            ? Console.OpenStandardInput()
            : new FileStream(
                new SafeFileHandle(IntPtr.Zero, ownsHandle: false), FileAccess.Read, 1);

        _output = Console.Out;
    }

    /// <summary>Takes the terminal over.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        EnterRawMode();

        var b = new StringBuilder();
        if (_altScreen)
        {
            b.Append(Csi).Append("?1049h");
        }

        b.Append(Csi).Append("?25l"); // hide the cursor

        if (_mouse)
        {
            // All-motion reporting is what makes tabs and buttons light up under
            // the pointer. Cell-motion would be enough for dragging the
            // scrollbar, but a control surface that does not acknowledge the
            // pointer feels broken, and the extra traffic costs a diffed
            // repaint.
            b.Append(Csi).Append("?1000h")
                .Append(Csi).Append("?1003h")
                .Append(Csi).Append("?1006h");
        }

        b.Append(Csi).Append("2J");
        Write(b.ToString());
    }

    /// <summary>Hands the terminal back the way it was found.</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;

        var b = new StringBuilder();
        if (_mouse)
        {
            b.Append(Csi).Append("?1006l")
                .Append(Csi).Append("?1003l")
                .Append(Csi).Append("?1000l");
        }

        b.Append(Csi).Append("?25h"); // show the cursor

        if (_altScreen)
        {
            b.Append(Csi).Append("?1049l");
        }
        else
        {
            b.Append('\n');
        }

        Write(b.ToString());
        LeaveRawMode();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _input.Dispose();
    }

    /// <summary>The terminal size, or a sensible default when there is no terminal.</summary>
    public static (int Width, int Height) Size()
    {
        try
        {
            var w = Console.WindowWidth;
            var h = Console.WindowHeight;
            return (w > 0 ? w : 80, h > 0 ? h : 24);
        }
        catch (IOException)
        {
            return (80, 24);
        }
        catch (PlatformNotSupportedException)
        {
            return (80, 24);
        }
    }

    /// <summary>Draws a frame, repainting only the rows that changed.</summary>
    public void Draw(string frame, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var lines = frame.Split('\n');

        // A resize invalidates every row, because what was on them was laid out
        // for a terminal that no longer exists.
        if (width != _lastWidth || height != _lastHeight)
        {
            _lastWidth = width;
            _lastHeight = height;
            _previous = [];
            Write(Csi + "2J");
        }

        var b = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i < _previous.Count && string.Equals(_previous[i], lines[i], StringComparison.Ordinal))
            {
                continue;
            }

            b.Append(Csi).Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(";1H")
                .Append(lines[i]).Append(Csi).Append('K');
        }

        // Rows the new frame does not reach must not keep showing the old one.
        for (var i = lines.Length; i < _previous.Count; i++)
        {
            b.Append(Csi).Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(";1H")
                .Append(Csi).Append('K');
        }

        if (b.Length > 0)
        {
            Write(b.ToString());
        }

        _previous = [.. lines];
    }

    /// <summary>
    /// Reads keystrokes and mouse reports until the token is cancelled, handing
    /// each to <paramref name="push"/>.
    /// </summary>
    /// <remarks>
    /// This blocks, so it belongs on a thread of its own: it is the update loop
    /// that must never block.
    /// </remarks>
    public void ReadInput(Action<IMsg> push, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(push);

        while (!cancellationToken.IsCancellationRequested)
        {
            int n;
            try
            {
                n = _input.Read(_buffer, 0, _buffer.Length);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return;
            }

            if (n <= 0)
            {
                return; // stdin closed; there is nothing left to read
            }

            _pending.AddRange(_buffer.AsSpan(0, n).ToArray());
            Decode(_pending, push);
        }
    }

    /// <summary>
    /// Turns a buffer of terminal input into messages, keeping any incomplete
    /// sequence for the next read.
    /// </summary>
    /// <remarks>
    /// A terminal delivers an escape sequence in one write, so a buffer that
    /// ends mid-sequence is either a genuinely split read or a bare escape key.
    /// Anything that has arrived complete is decoded; the remainder is kept only
    /// when it could still grow into something.
    /// </remarks>
    public static void Decode(List<byte> pending, Action<IMsg> push)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(push);

        var i = 0;
        while (i < pending.Count)
        {
            var b = pending[i];

            if (b == 0x1b)
            {
                if (i + 1 >= pending.Count)
                {
                    // A lone escape with nothing behind it is the escape key.
                    push(new KeyMsg("esc"));
                    i++;
                    continue;
                }

                var next = pending[i + 1];
                if (next == '[')
                {
                    if (!TryReadCsi(pending, i, out var consumed, out var msg))
                    {
                        // Incomplete: keep it and wait for the rest.
                        pending.RemoveRange(0, i);
                        return;
                    }

                    if (msg is not null)
                    {
                        push(msg);
                    }

                    i += consumed;
                    continue;
                }

                if (next == 'O')
                {
                    if (i + 2 >= pending.Count)
                    {
                        pending.RemoveRange(0, i);
                        return;
                    }

                    var name = Ss3Name((char)pending[i + 2]);
                    if (name is not null)
                    {
                        push(new KeyMsg(name));
                    }

                    i += 3;
                    continue;
                }

                // Anything else after an escape is a key in its own right; the
                // escape is reported so it can dismiss whatever is open.
                push(new KeyMsg("esc"));
                i++;
                continue;
            }

            switch (b)
            {
                case 0x0d or 0x0a:
                    push(new KeyMsg("enter"));
                    i++;
                    continue;
                case 0x09:
                    push(new KeyMsg("tab"));
                    i++;
                    continue;
                case 0x7f or 0x08:
                    push(new KeyMsg("backspace"));
                    i++;
                    continue;
            }

            if (b < 0x20)
            {
                push(new KeyMsg("ctrl+" + (char)(b + 96)));
                i++;
                continue;
            }

            // A printable character, which may be several bytes of UTF-8.
            var length = Utf8Length(b);
            if (i + length > pending.Count)
            {
                pending.RemoveRange(0, i);
                return;
            }

            var text = Encoding.UTF8.GetString(
                CollectionsMarshal.AsSpan(pending).Slice(i, length));
            if (text.Length > 0)
            {
                push(new KeyMsg(text));
            }

            i += length;
        }

        pending.Clear();
    }

    private static int Utf8Length(byte first)
    {
        if ((first & 0xE0) == 0xC0)
        {
            return 2;
        }

        if ((first & 0xF0) == 0xE0)
        {
            return 3;
        }

        return (first & 0xF8) == 0xF0 ? 4 : 1;
    }

    private static string? Ss3Name(char final) => final switch
    {
        'A' => "up",
        'B' => "down",
        'C' => "right",
        'D' => "left",
        'H' => "home",
        'F' => "end",
        _ => null,
    };

    /// <summary>Reads one CSI sequence: a cursor key, a page key, or a mouse report.</summary>
    private static bool TryReadCsi(List<byte> pending, int at, out int consumed, out IMsg? msg)
    {
        consumed = 0;
        msg = null;

        var i = at + 2; // past ESC [
        var start = i;
        while (i < pending.Count && !char.IsBetween((char)pending[i], '@', '~'))
        {
            i++;
        }

        if (i >= pending.Count)
        {
            return false; // the final byte has not arrived
        }

        var final = (char)pending[i];
        var body = Encoding.ASCII.GetString(
            CollectionsMarshal.AsSpan(pending).Slice(start, i - start));
        consumed = i - at + 1;

        if (body.StartsWith('<') && final is 'M' or 'm')
        {
            msg = ParseMouse(body[1..], final == 'm');
            return true;
        }

        switch (final)
        {
            case 'A':
                msg = new KeyMsg("up");
                return true;
            case 'B':
                msg = new KeyMsg("down");
                return true;
            case 'C':
                msg = new KeyMsg("right");
                return true;
            case 'D':
                msg = new KeyMsg("left");
                return true;
            case 'H':
                msg = new KeyMsg("home");
                return true;
            case 'F':
                msg = new KeyMsg("end");
                return true;
            case 'Z':
                msg = new KeyMsg("shift+tab");
                return true;

            case '~':
                var number = body.Split(';')[0];
                msg = number switch
                {
                    "1" or "7" => new KeyMsg("home"),
                    "4" or "8" => new KeyMsg("end"),
                    "5" => new KeyMsg("pgup"),
                    "6" => new KeyMsg("pgdown"),
                    _ => null,
                };
                return true;

            default:
                return true; // a sequence this interface has no use for
        }
    }

    private static IMsg? ParseMouse(string body, bool released)
    {
        var parts = body.Split(';');
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cb) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cx) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cy))
        {
            return null;
        }

        // Terminals count from one; everything above counts from zero.
        var x = cx - 1;
        var y = cy - 1;

        if ((cb & 64) != 0)
        {
            var wheel = (cb & 3) == 0 ? MouseButton.WheelUp : MouseButton.WheelDown;
            return new MouseMsg(x, y, wheel, MouseKind.Wheel);
        }

        var button = (cb & 3) switch
        {
            0 => MouseButton.Left,
            1 => MouseButton.Middle,
            2 => MouseButton.Right,
            _ => MouseButton.None,
        };

        if ((cb & 32) != 0)
        {
            return new MouseMsg(x, y, button, MouseKind.Motion);
        }

        return new MouseMsg(x, y, button, released ? MouseKind.Release : MouseKind.Click);
    }

    private void Write(string s)
    {
        _output.Write(s);
        _output.Flush();
    }

    // ---------- raw mode ----------

    // The terminal is configured through termios directly rather than by
    // shelling out to stty. A child process is not just slower: .NET restores
    // its own idea of the terminal settings whenever one exits, so a mode set by
    // stty is reverted the moment stty itself finishes — and the interface is
    // left reading line-buffered, echoed input with no clue why.

    private const int Stdin = 0;
    private const int TcsaNow = 0;
    private const int TermiosSize = 128;

    // Linux lays the flag words out as four 32-bit fields, then c_line and a
    // 32-byte control-character array; the BSDs use 64-bit flag words and put a
    // 20-byte array after them. Everything below is expressed in terms of those
    // two shapes so the same code serves both.
    private static bool WideFlags => OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();

    private static int ControlCharsAt => WideFlags ? 32 : 17;

    private static int VMin => WideFlags ? 16 : 6;

    private static int VTime => WideFlags ? 17 : 5;

    // termios bits. The input and control bits agree across Linux and the BSDs;
    // the local ones do not, so they are chosen per platform.
    private const ulong IgnBrk = 0x0001;
    private const ulong BrkInt = 0x0002;
    private const ulong ParMrk = 0x0008;
    private const ulong IStrip = 0x0020;
    private const ulong InLcr = 0x0040;
    private const ulong IgnCr = 0x0080;
    private const ulong ICrNl = 0x0100;
    private const ulong Echo = 0x0008;

    private static ulong IxOn => WideFlags ? 0x0200UL : 0x0400UL;

    private static ulong ISig => WideFlags ? 0x0080UL : 0x0001UL;

    private static ulong ICanon => WideFlags ? 0x0100UL : 0x0002UL;

    private static ulong EchoNl => WideFlags ? 0x0010UL : 0x0040UL;

    private static ulong IExten => WideFlags ? 0x0400UL : 0x8000UL;

    private static ulong CSize => WideFlags ? 0x0300UL : 0x0030UL;

    private static ulong Cs8 => WideFlags ? 0x0300UL : 0x0030UL;

    private static ulong ParEnb => WideFlags ? 0x1000UL : 0x0100UL;

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int actions, byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int isatty(int fd);

    private void EnterRawMode()
    {
        if (OperatingSystem.IsWindows())
        {
            EnterRawModeWindows();
            return;
        }

        if (isatty(Stdin) != 1)
        {
            return; // nothing to configure; a redirected run still draws
        }

        var t = new byte[TermiosSize];
        if (tcgetattr(Stdin, t) != 0)
        {
            return;
        }

        _savedTermios = (byte[])t.Clone();

        // Input: no CR/LF translation and no flow control, so every byte the
        // terminal sends arrives exactly as it was sent.
        SetFlag(t, 0, GetFlag(t, 0) &
            ~(IgnBrk | BrkInt | ParMrk | IStrip | InLcr | IgnCr | ICrNl | IxOn));

        // Local: no echo, no line buffering and no signals — ctrl+c becomes a
        // keystroke this interface can decide about rather than one that kills
        // it mid-frame.
        SetFlag(t, 3, GetFlag(t, 3) & ~(Echo | EchoNl | ICanon | ISig | IExten));

        // Eight data bits, no parity: a terminal is not a modem.
        SetFlag(t, 2, (GetFlag(t, 2) & ~(CSize | ParEnb)) | Cs8);

        // One byte is enough to return from a read, and no inter-byte timer,
        // because a keystroke must reach the interface as it is typed.
        t[ControlCharsAt + VMin] = 1;
        t[ControlCharsAt + VTime] = 0;

        // Output processing is left alone: the frame is drawn with absolute
        // cursor positioning, so turning it off buys nothing but a chance to
        // strand a newline somewhere.
        tcsetattr(Stdin, TcsaNow, t);
    }

    private void LeaveRawMode()
    {
        if (OperatingSystem.IsWindows())
        {
            LeaveRawModeWindows();
            return;
        }

        if (_savedTermios is not null)
        {
            tcsetattr(Stdin, TcsaNow, _savedTermios);
        }
    }

    private static ulong GetFlag(byte[] t, int index) => WideFlags
        ? BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(index * 8, 8))
        : BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(index * 4, 4));

    private static void SetFlag(byte[] t, int index, ulong value)
    {
        if (WideFlags)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(t.AsSpan(index * 8, 8), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(index * 4, 4), (uint)value);
        }
    }

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableWindowInput = 0x0008;
    private const uint EnableMouseInput = 0x0010;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint handle, uint mode);

    private void EnterRawModeWindows()
    {
        var input = GetStdHandle(StdInputHandle);
        var output = GetStdHandle(StdOutputHandle);

        if (GetConsoleMode(input, out _savedInputMode))
        {
            var mode = _savedInputMode;
            mode &= ~(EnableProcessedInput | EnableLineInput | EnableEchoInput);
            mode |= EnableExtendedFlags | EnableWindowInput | EnableMouseInput |
                EnableVirtualTerminalInput;
            SetConsoleMode(input, mode);
        }

        if (GetConsoleMode(output, out _savedOutputMode))
        {
            SetConsoleMode(output, _savedOutputMode | EnableVirtualTerminalProcessing);
        }
    }

    private void LeaveRawModeWindows()
    {
        if (_savedInputMode != 0)
        {
            SetConsoleMode(GetStdHandle(StdInputHandle), _savedInputMode);
        }

        if (_savedOutputMode != 0)
        {
            SetConsoleMode(GetStdHandle(StdOutputHandle), _savedOutputMode);
        }
    }
}
