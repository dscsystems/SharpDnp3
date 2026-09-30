// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// A full master against a full outstation with symmetric Secure Authentication
// on both ends.

using SharpDnp3.Master;
using SharpDnp3.Outstation;

namespace SharpDnp3.Tests;

public class SecureAuthenticationTests
{
    private static TestPair Pair(byte[] masterKey, byte[] outstationKey, Func<ushort, byte, bool>? authorize = null) =>
        new(
            m => m.SecureAuthentication = new SharpDnp3.Master.SecureAuthenticationConfig
            {
                User = 1,
                UpdateKey = masterKey,
            },
            o =>
            {
                var sa = new SharpDnp3.Outstation.SecureAuthenticationConfig { Authorize = authorize };
                sa.Users[1] = outstationKey;
                o.SecureAuthentication = sa;
                o.Attributes.Add(DeviceAttribute.String(247, "before"));
                o.WritableAttributes.Add(new AttributeId(0, 247));
            });

    [Fact]
    public async Task KeyExchangeAndAuthenticatedWrite()
    {
        var key = Enumerable.Repeat((byte)0x71, 16).ToArray();
        await using var pair = Pair(key, key);
        await pair.WaitConnectedAsync();

        await pair.Master.WriteAttributeAsync(DeviceAttribute.String(247, "after"));
        var v = await pair.Master.ReadAttributeAsync(0, 247);
        Assert.Equal("after", v.Text);
    }

    [Fact]
    public async Task WrongUpdateKeyCannotWrite()
    {
        await using var pair = Pair(
            Enumerable.Repeat((byte)1, 16).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray());
        await pair.WaitConnectedAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            pair.Master.WriteAttributeAsync(DeviceAttribute.String(247, "after"), cts.Token));
    }

    [Fact]
    public async Task AuthorizationRefusal()
    {
        var key = Enumerable.Repeat((byte)0x71, 16).ToArray();
        await using var pair = Pair(key, key, (_, f) => f != 2);
        await pair.WaitConnectedAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            pair.Master.WriteAttributeAsync(DeviceAttribute.String(247, "after"), cts.Token));
        Assert.IsNotType<Dnp3TimeoutException>(ex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecureControlsIncludingNoReply(bool noReply)
    {
        var key = Enumerable.Repeat((byte)0x32, 32).ToArray();
        await using var pair = new TestPair(
            m => m.SecureAuthentication = new SharpDnp3.Master.SecureAuthenticationConfig
            {
                User = 1,
                UpdateKey = key,
                MaxMessages = 1,
            },
            o =>
            {
                var sa = new SharpDnp3.Outstation.SecureAuthenticationConfig { MaxMessages = 1 };
                sa.Users[1] = key;
                o.SecureAuthentication = sa;
            });
        await pair.WaitConnectedAsync();

        if (noReply)
        {
            await pair.Master.DirectOperateNoReplyAsync([Command.LatchOn(0)]);
        }
        else
        {
            var result = await pair.Master.SelectAndOperateAsync(Command.LatchOn(0));
            Assert.True(result.OK());
        }

        await TestPair.WaitForAsync(() => pair.Commands.Read(c => c.Operated.Count) >= 1, "the operate");
    }
}
