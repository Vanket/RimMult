using RimMult.Shared;
using RimMult.Shared.Mods;
using RimMult.Shared.Net;

namespace RimMult.Tests;

public class NetHelpersTests
{
    [Theory]
    [InlineData("192.168.1.5", "192.168.1.5", ProtocolInfo.DefaultPort)]
    [InlineData("  myserver.net:27000 ", "myserver.net", 27000)]
    [InlineData("[::1]:1234", "::1", 1234)]
    [InlineData("[::1]", "::1", ProtocolInfo.DefaultPort)]
    [InlineData("fe80::1", "fe80::1", ProtocolInfo.DefaultPort)]
    public void ValidAddressesParse(string input, string host, int port)
    {
        Assert.True(ServerAddress.TryParse(input, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:")]
    [InlineData("host:abc")]
    [InlineData("host:70000")]
    [InlineData(":123")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    public void InvalidAddressesAreRejected(string input)
    {
        Assert.False(ServerAddress.TryParse(input, out _, out _));
    }

    [Fact]
    public void FramesRoundTrip()
    {
        var reliable = P2PFrame.Reliable([1, 2, 3]);
        Assert.True(P2PFrame.TryParse(reliable, reliable.Length, out var kind, out _, out var payload));
        Assert.Equal(P2PFrameKind.Reliable, kind);
        Assert.Equal([1, 2, 3], payload);

        var sequenced = P2PFrame.Sequenced([9], 65535);
        Assert.True(P2PFrame.TryParse(sequenced, sequenced.Length, out kind, out var sequence, out payload));
        Assert.Equal(P2PFrameKind.Sequenced, kind);
        Assert.Equal(65535, sequence);
        Assert.Equal([9], payload);

        var connect = P2PFrame.Connect(0xDEADBEEF);
        Assert.True(P2PFrame.TryParse(connect, connect.Length, out kind, out _, out payload));
        Assert.Equal(P2PFrameKind.Connect, kind);
        Assert.Equal(0xDEADBEEF, P2PFrame.ConnectNonce(payload));

        var control = P2PFrame.Control(P2PFrameKind.Accept);
        Assert.True(P2PFrame.TryParse(control, control.Length, out kind, out _, out payload));
        Assert.Equal(P2PFrameKind.Accept, kind);
        Assert.Empty(payload);
    }

    [Fact]
    public void FrameParsingRespectsLengthOfReusedBuffer()
    {
        // Steam reads into a reused buffer that is larger than the packet.
        var buffer = new byte[64];
        var frame = P2PFrame.Reliable([7, 8]);
        Array.Copy(frame, buffer, frame.Length);
        Assert.True(P2PFrame.TryParse(buffer, frame.Length, out _, out _, out var payload));
        Assert.Equal([7, 8], payload);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 99, 1 })]
    [InlineData(new byte[] { (byte)P2PFrameKind.Sequenced, 1 })]
    [InlineData(new byte[] { (byte)P2PFrameKind.Accept, 1 })]
    [InlineData(new byte[] { (byte)P2PFrameKind.Connect })]
    [InlineData(new byte[] { (byte)P2PFrameKind.Connect, 1, 2 })]
    public void MalformedFramesAreRejected(byte[] frame)
    {
        Assert.False(P2PFrame.TryParse(frame, frame.Length, out _, out _, out _));
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(5, 5, false)]
    [InlineData(0, 65535, true)] // wrapped around
    [InlineData(65535, 0, false)]
    public void SequenceComparisonHandlesWrapAround(int a, int b, bool newer)
    {
        Assert.Equal(newer, P2PFrame.IsNewer((ushort)a, (ushort)b));
    }

    [Fact]
    public void IdenticalModListsHaveNoDiff()
    {
        var mods = new List<ModEntry> { new("a", "A", "1", 0), new("b", "B", "1", 0) };
        Assert.True(ModListDiff.Compute(mods, mods).IsEmpty);
    }

    [Fact]
    public void DiffFindsVersionAndOrderDifferences()
    {
        var server = new List<ModEntry> { new("a", "A", "1", 0), new("b", "B", "2", 0) };

        var outdated = ModListDiff.Compute(server, [new("A", "A", "1", 0), new("b", "B", "1", 0)]);
        Assert.Equal("b", Assert.Single(outdated.DifferentVersion).PackageId);
        Assert.Empty(outdated.Missing); // package ids are case-insensitive
        Assert.False(outdated.OrderDiffers);

        var reordered = ModListDiff.Compute(server, [new("b", "B", "2", 0), new("a", "A", "1", 0)]);
        Assert.True(reordered.OrderDiffers);
        Assert.Empty(reordered.DifferentVersion);
    }

    [Fact]
    public void ModHashIgnoresCosmeticFields()
    {
        var a = ModListHash.Compute([new ModEntry("x.y", "Name", "1", 5)]);
        var b = ModListHash.Compute([new ModEntry("X.Y", "Other name", "1", 0)]);
        var c = ModListHash.Compute([new ModEntry("x.y", "Name", "2", 5)]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
