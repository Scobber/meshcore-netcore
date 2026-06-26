using MeshCoreNet;

namespace meshcore_net.Tests;

public class CompanionFrameTests
{
    [Fact]
    public async Task CodecReadsFrameAfterJunkBytes()
    {
        await using var stream = new MemoryStream([0x00, 0x55, (byte)'<', 0x03, 0x00, 1, 2, 3]);

        var frame = await CompanionFrameCodec.ReadFrameAsync(stream, (byte)'<', CancellationToken.None);

        Assert.Equal([1, 2, 3], frame);
    }

    [Fact]
    public async Task CodecWritesLengthPrefixedFrame()
    {
        await using var stream = new MemoryStream();

        await CompanionFrameCodec.WriteFrameAsync(stream, (byte)'>', [1, 2, 3], CancellationToken.None);

        Assert.Equal([(byte)'>', 0x03, 0x00, 1, 2, 3], stream.ToArray());
    }
}
