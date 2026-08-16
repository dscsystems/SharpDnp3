// Copyright (C) 2026 Ricardo Olsen / DSC Systems.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option)
// any later version. It is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details, in the LICENSE file at
// the root of this repository or at <https://www.gnu.org/licenses/>.

using System.Globalization;

namespace SharpDnp3.Tools.Decode;

/// <summary>Reads hex octets out of text that was written for humans.</summary>
public static class HexInput
{
    /// <summary>Gathers octets from a file, standard input, or the command line.</summary>
    public static byte[] ReadInput(string? file, IReadOnlyList<string> args)
    {
        if (!string.IsNullOrEmpty(file))
        {
            using var reader = new StreamReader(file);
            return ReadHex(reader);
        }

        if (args.Count == 1 && args[0] == "-")
        {
            return ReadHex(Console.In);
        }

        if (args.Count > 0)
        {
            return ParseHex(string.Join(' ', args));
        }

        // No arguments and a piped stdin is the common shell idiom, so read it
        // rather than printing usage at someone who clearly meant to pipe.
        return Console.IsInputRedirected ? ReadHex(Console.In) : [];
    }

    /// <summary>Reads every line of <paramref name="reader"/> as hex.</summary>
    public static byte[] ReadHex(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var all = new List<byte>();
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            try
            {
                all.AddRange(ParseHex(line));
            }
            catch (FormatException ex)
            {
                throw new FormatException(
                    string.Format(CultureInfo.InvariantCulture, "line {0}: {1}", lineNumber, ex.Message),
                    ex);
            }
        }

        return [.. all];
    }

    /// <summary>Extracts hex octets from a line of text.</summary>
    /// <remarks>
    /// <para>
    /// The rules are deliberately explicit rather than "grab every hex digit".
    /// Two things make the naive approach wrong on exactly the input this tool
    /// exists to read. A hex dump's ASCII gutter is full of letters that are
    /// also hex digits — "cafe" in a log line is four of them — and a dump's
    /// offset column looks like two perfectly good octets. So:
    /// </para>
    /// <list type="bullet">
    /// <item>everything from a '#' onwards is a comment;</item>
    /// <item>columns are separated by runs of two or more spaces;</item>
    /// <item>a leading column of 4 to 8 hex digits is an offset and is dropped;</item>
    /// <item>when columns remain after that, the last is an ASCII gutter and is dropped;</item>
    /// <item>
    /// what survives is tokenised on whitespace and , : - and each token is
    /// accepted only if it is entirely hex digits, after an optional 0x.
    /// </item>
    /// </list>
    /// <para>
    /// That reads <c>05 64 05 C0</c>, <c>0x05,0x64</c>, <c>0564 05c0</c>, and
    /// Wireshark's hex-dump export, and refuses to invent octets out of prose.
    /// </para>
    /// </remarks>
    public static byte[] ParseHex(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var hash = s.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            s = s[..hash];
        }

        var cols = SplitColumns(s);
        if (cols.Count >= 2 && IsOffsetColumn(cols[0]))
        {
            cols.RemoveAt(0);
        }

        if (cols.Count >= 2)
        {
            // The ASCII gutter.
            cols.RemoveAt(cols.Count - 1);
        }

        var digits = new List<char>();
        foreach (var col in cols)
        {
            foreach (var rawToken in col.Split(
                [' ', '\t', ',', ':', '-', '|'], StringSplitOptions.RemoveEmptyEntries))
            {
                var token = rawToken;
                if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    token = token[2..];
                }

                if (token.Length == 0 || !IsAllHex(token))
                {
                    continue;
                }

                digits.AddRange(token);
            }
        }

        if (digits.Count % 2 != 0)
        {
            throw new FormatException(string.Format(
                CultureInfo.InvariantCulture,
                "odd number of hex digits ({0}) in \"{1}\"", digits.Count, s.Trim()));
        }

        var output = new byte[digits.Count / 2];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = (byte)((HexVal(digits[i * 2]) << 4) | HexVal(digits[(i * 2) + 1]));
        }

        return output;
    }

    /// <summary>
    /// Splits on runs of two or more spaces, which is how hex dumps separate
    /// the offset, the octets and the ASCII gutter.
    /// </summary>
    private static List<string> SplitColumns(string s)
    {
        var cols = new List<string>();
        foreach (var field in s.Split("  "))
        {
            var t = field.Trim();
            if (t.Length > 0)
            {
                cols.Add(t);
            }
        }

        return cols;
    }

    /// <summary>
    /// Reports whether a column looks like a hex-dump offset: a single run of
    /// four to eight hex digits, optionally followed by a colon.
    /// </summary>
    private static bool IsOffsetColumn(string s)
    {
        s = s.TrimEnd(':');
        return s.Length is >= 4 and <= 8 &&
               IsAllHex(s) &&
               !s.Contains(' ', StringComparison.Ordinal) &&
               !s.Contains('\t', StringComparison.Ordinal);
    }

    private static bool IsAllHex(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHexDigit(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => c - 'A' + 10,
    };
}
