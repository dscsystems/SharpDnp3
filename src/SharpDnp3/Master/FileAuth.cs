// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using System.Globalization;
using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Master;

/// <summary>
/// Credentials for AUTHENTICATE_FILE. DNP3 file authentication sends the
/// password on the wire; protect the channel when credentials require
/// confidentiality.
/// </summary>
/// <param name="User">The user name.</param>
/// <param name="Password">The password.</param>
public sealed record FileCredentials(string User, string Password);

public sealed partial class MasterSession
{
    /// <summary>Exchanges credentials for a file authentication key.</summary>
    /// <returns>The key the outstation issued.</returns>
    public async Task<uint> AuthenticateFileAsync(
        string user, string password, CancellationToken cancellationToken = default)
    {
        var t = new FileTransferState { Name = string.Empty, RequestId = 0 };
        await RunTaskAsync(FileAuthTask(t, new FileCredentials(user, password), null), cancellationToken)
            .ConfigureAwait(false);
        return t.Error is null ? t.Key : throw t.Error;
    }

    private static MasterTask FileAuthTask(FileTransferState t, FileCredentials credentials, MasterTask? next)
    {
        var task = new MasterTask
        {
            Name = "file-authenticate",
            FuncCode = FuncCode.AuthenticateFile,
            Priority = TaskPriority.Command,
            Build = b =>
            {
                if (credentials.User.Length + credentials.Password.Length >
                    FreeFormat.MaxFreeFormatObject - FileObjects.FileAuthSize)
                {
                    throw new BadConfigException();
                }

                var obj = new List<byte>();
                FileObjects.AppendAuth(obj, new FileAuth { User = credentials.User, Password = credentials.Password });
                if (!b.TryAddObject(FreeFormat.Build(70, 2, System.Runtime.InteropServices
                        .CollectionsMarshal.AsSpan(obj))))
                {
                    throw AppParseStatus.FragmentTooLarge.ToException();
                }
            },
            OnFragment = frag =>
            {
                if (!TryFileObject(frag, 2, out var obj))
                {
                    t.Fail(new MalformedException("master: file authentication: dnp3: malformed data"));
                    return;
                }

                try
                {
                    t.Key = FileObjects.ParseAuth(obj.Span).Key;
                }
                catch (MalformedException ex)
                {
                    t.Fail(ex);
                }
            },
        };
        task.OnDone = iin =>
        {
            t.CheckSupported(iin, "authenticate");
            if (t.Key == 0 && t.Error is null)
            {
                t.Fail(new Dnp3Exception(string.Format(
                    CultureInfo.InvariantCulture, "master: file authentication: permission denied")));
            }
        };
        task.Next = () => t.Error is not null ? null : next;
        return task;
    }
}
