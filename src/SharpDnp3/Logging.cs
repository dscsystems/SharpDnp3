// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using System.Text;

namespace SharpDnp3;

/// <summary>How severe a log record is.</summary>
public enum Dnp3LogLevel
{
    /// <summary>Protocol detail useful when diagnosing a session.</summary>
    Debug,

    /// <summary>A normal, noteworthy event such as a connection.</summary>
    Info,

    /// <summary>Something went wrong but the session carries on.</summary>
    Warn,

    /// <summary>Something went wrong that ends the session.</summary>
    Error,
}

/// <summary>Receives protocol and session events.</summary>
/// <remarks>
/// Deliberately a small interface of its own rather than a dependency on a
/// logging framework: the library carries no third-party packages, and a
/// consumer that wants one needs only a dozen lines to adapt this to it.
/// Records are structured — a message plus key/value pairs — because a
/// protocol log is searched, not read.
/// </remarks>
public interface IDnp3Logger
{
    /// <summary>Reports whether records at this level would be kept.</summary>
    bool IsEnabled(Dnp3LogLevel level);

    /// <summary>Writes one record.</summary>
    void Log(Dnp3LogLevel level, string message, params ReadOnlySpan<(string Key, object? Value)> fields);
}

/// <summary>Discards every record.</summary>
public sealed class NullDnp3Logger : IDnp3Logger
{
    /// <summary>The shared instance.</summary>
    public static NullDnp3Logger Instance { get; } = new();

    private NullDnp3Logger() { }

    /// <inheritdoc/>
    public bool IsEnabled(Dnp3LogLevel level) => false;

    /// <inheritdoc/>
    public void Log(
        Dnp3LogLevel level,
        string message,
        params ReadOnlySpan<(string Key, object? Value)> fields)
    {
    }
}

/// <summary>Writes records to a <see cref="TextWriter"/>, one line each.</summary>
public sealed class TextWriterDnp3Logger : IDnp3Logger
{
    private readonly TextWriter _writer;
    private readonly Dnp3LogLevel _minimum;
    private readonly Lock _gate = new();

    /// <summary>
    /// Creates a logger writing to <paramref name="writer"/>, keeping records
    /// at <paramref name="minimum"/> and above.
    /// </summary>
    public TextWriterDnp3Logger(TextWriter writer, Dnp3LogLevel minimum = Dnp3LogLevel.Info)
    {
        _writer = writer;
        _minimum = minimum;
    }

    /// <inheritdoc/>
    public bool IsEnabled(Dnp3LogLevel level) => level >= _minimum;

    /// <inheritdoc/>
    public void Log(
        Dnp3LogLevel level,
        string message,
        params ReadOnlySpan<(string Key, object? Value)> fields)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var b = new StringBuilder();
        b.Append(DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        b.Append(' ');
        b.Append(level.ToString().ToUpperInvariant().PadRight(5));
        b.Append(' ');
        b.Append(message);

        foreach (var (key, value) in fields)
        {
            b.Append(' ').Append(key).Append('=').Append(
                Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        lock (_gate)
        {
            _writer.WriteLine(b.ToString());
        }
    }
}

/// <summary>Adds the fields every record from one session carries.</summary>
internal sealed class ScopedLogger : IDnp3Logger
{
    private readonly IDnp3Logger _inner;
    private readonly (string Key, object? Value)[] _scope;

    public ScopedLogger(IDnp3Logger inner, params (string Key, object? Value)[] scope)
    {
        _inner = inner;
        _scope = scope;
    }

    public bool IsEnabled(Dnp3LogLevel level) => _inner.IsEnabled(level);

    public void Log(
        Dnp3LogLevel level,
        string message,
        params ReadOnlySpan<(string Key, object? Value)> fields)
    {
        if (!_inner.IsEnabled(level))
        {
            return;
        }

        var combined = new (string Key, object? Value)[_scope.Length + fields.Length];
        _scope.CopyTo(combined, 0);
        fields.CopyTo(combined.AsSpan(_scope.Length));
        _inner.Log(level, message, combined);
    }
}
