// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

namespace SharpDnp3;

/// <summary>
/// Base class for every error raised across the stack.
/// </summary>
/// <remarks>
/// Layer implementations define their own detailed exceptions and derive them
/// from these, so callers can classify a failure without reaching into
/// internal namespaces.
/// </remarks>
public class Dnp3Exception : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public Dnp3Exception(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public Dnp3Exception(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>
/// A received octet sequence did not conform to the protocol and could not be
/// recovered.
/// </summary>
public class MalformedException : Dnp3Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public MalformedException(string message = "dnp3: malformed data") : base(message) { }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public MalformedException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>A peer did not respond within the configured window.</summary>
public class Dnp3TimeoutException : Dnp3Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public Dnp3TimeoutException(string message = "dnp3: timeout") : base(message) { }
}

/// <summary>The session or channel has been shut down.</summary>
public class ClosedException : Dnp3Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public ClosedException(string message = "dnp3: closed") : base(message) { }
}

/// <summary>
/// The peer rejected a function code or object it does not implement.
/// </summary>
public class NotSupportedByPeerException : Dnp3Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public NotSupportedByPeerException(string message = "dnp3: not supported by peer")
        : base(message) { }
}

/// <summary>
/// A configuration value is outside the range the protocol allows.
/// </summary>
public class BadConfigException : Dnp3Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public BadConfigException(string message = "dnp3: invalid configuration") : base(message) { }
}

/// <summary>A master task exhausted its retries.</summary>
public class TaskFailedException : Dnp3Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public TaskFailedException(string message = "dnp3: task failed") : base(message) { }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public TaskFailedException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>The channel has no established connection.</summary>
public class NoConnectionException : Dnp3Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public NoConnectionException(string message = "dnp3: no connection") : base(message) { }
}
