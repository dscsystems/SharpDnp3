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
// dnp3-decode renders DNP3 octets as a decoded protocol tree.
//
// It reads hex from the command line, from a file, or from standard input, and
// prints the link, transport and application layers of every frame it finds.
//
//   dnp3-decode 05 64 05 C0 0A 00 01 00 B1 AC
//   dnp3-decode -x 0564050ac0...
//   tcpdump -i lo -s0 -x port 20000 | dnp3-decode -
//   dnp3-decode -f capture.hex
//
// Input is read leniently but not blindly: '#' starts a comment, a hex-dump
// offset column and ASCII gutter are recognised and dropped, and only whole hex
// tokens become octets. Output pasted from Wireshark, a vendor log or a device
// console usually works without editing.

using System.Globalization;
using System.Text;
using SharpDnp3.Decoding;
using SharpDnp3.Link;
using SharpDnp3.Tools.Decode;

string? file = null;
var showHex = false;
var quiet = false;
var stream = false;
var positional = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-f" when i + 1 < args.Length:
            file = args[++i];
            break;
        case "-x":
            showHex = true;
            break;
        case "-q":
            quiet = true;
            break;
        case "-s":
            stream = true;
            break;
        case "-h" or "--help":
            Usage();
            return 0;
        default:
            positional.Add(args[i]);
            break;
    }
}

byte[] data;
try
{
    data = HexInput.ReadInput(file, positional);
}
catch (Exception ex) when (ex is IOException or FormatException)
{
    Console.Error.WriteLine("dnp3-decode: " + ex.Message);
    return 1;
}

if (data.Length == 0)
{
    Usage();
    Console.Error.WriteLine("dnp3-decode: no input");
    return 1;
}

var output = new StringBuilder();
var (frames, errors) = Decode(output, data, showHex, stream);

Console.Out.Write(output.ToString());
if (!quiet)
{
    Console.Out.Write(string.Format(
        CultureInfo.InvariantCulture,
        "\n{0} frame(s), {1} error(s), {2} octets\n", frames, errors, data.Length));
}

return errors > 0 ? 2 : 0;

// Renders every frame in data, returning how many frames and errors it found.
static (int Frames, int Errors) Decode(StringBuilder output, byte[] data, bool showHex, bool stream)
{
    var frames = 0;
    var errors = 0;

    if (stream)
    {
        // Streaming mode keeps transport state across frames, so a fragment
        // split over several frames is reassembled and its application layer
        // decoded on the frame that completes it.
        var d = new Dnp3Decoder();
        d.Feed(data, t =>
        {
            frames++;
            if (t.Error is not null || t.App?.Error is not null)
            {
                errors++;
            }

            t.Render(output, showHex);
            output.Append('\n');
        });

        var (linkStats, transportStats) = d.Stats;
        if (linkStats.BytesDiscarded > 0 || transportStats.SegmentsDiscarded > 0)
        {
            output.AppendFormat(
                CultureInfo.InvariantCulture,
                "discarded {0} octet(s) at the link layer, {1} segment(s) at the transport layer\n",
                linkStats.BytesDiscarded, transportStats.SegmentsDiscarded);
            errors++;
        }

        return (frames, errors);
    }

    // Frame-at-a-time mode: each frame is decoded independently, which is what
    // you want when pasting frames captured out of context.
    for (var off = 0; off < data.Length;)
    {
        if (!Dnp3Decoder.TryDecodeFrame(null, data.AsSpan(off), out var t, out var n))
        {
            if (t.Error == LinkDecodeStatus.ShortFrame.ToDisplayString())
            {
                output.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "-- {0} trailing octet(s) do not form a complete frame\n", data.Length - off);
                return (frames, errors + 1);
            }

            // Not a frame at this offset. Skip an octet and look for the next
            // delimiter, the same way the streaming parser resynchronises.
            off++;
            continue;
        }

        frames++;
        if (t.App?.Error is not null)
        {
            errors++;
        }

        t.Render(output, showHex);
        output.Append('\n');
        off += n;
    }

    return (frames, errors);
}

static void Usage() => Console.Error.Write(
    """
    dnp3-decode — render DNP3 octets as a decoded protocol tree

    Usage:
      dnp3-decode [flags] <hex octets...>
      dnp3-decode [flags] -
      dnp3-decode [flags] -f FILE

    Flags:
      -f FILE   read hex from a file
      -x        include a hex dump of each frame
      -s        treat the input as one continuous stream and reassemble fragments
      -q        suppress the summary line

    Input is read leniently but not blindly: '#' starts a comment, a hex-dump
    offset column and ASCII gutter are recognised and dropped, and only whole hex
    tokens become octets. Output pasted from Wireshark, a vendor log or a device
    console usually works unedited.

    Examples:
      dnp3-decode 05 64 05 C0 0A 00 01 00 B1 AC
      dnp3-decode -x -f capture.hex
      cat capture.hex | dnp3-decode -s

    """);
