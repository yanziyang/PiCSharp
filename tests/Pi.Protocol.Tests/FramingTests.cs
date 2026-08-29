using Pi.Protocol;

using Xunit;

namespace Pi.Protocol.Tests;

public sealed class FramingTests
{
    [Fact]
    public void PrefixesPayloadsWithFourByteBigEndianLength()
    {
        Assert.Equal(
            new byte[] { 0, 0, 0, 3, 0xaa, 0xbb, 0xcc },
            Framing.EncodeFrame(new byte[] { 0xaa, 0xbb, 0xcc }));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Framing.EncodeFrame([]));
    }

    [Fact]
    public void ValidatesOneCompleteBoundedFrame()
    {
        Framing.AssertCompleteFrame(new byte[] { 0, 0, 0, 2, 1, 2 }, new FrameDecoderOptions { MaxFrameLength = 2 });
        Assert.Contains("complete", Assert.Throws<FrameError>(() =>
            Framing.AssertCompleteFrame(new byte[] { 0, 0, 0, 2, 1 })).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly", Assert.Throws<FrameError>(() =>
            Framing.AssertCompleteFrame(new byte[] { 0, 0, 0, 1, 1, 2 })).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", Assert.Throws<FrameError>(() =>
            Framing.AssertCompleteFrame(new byte[] { 0, 0, 0, 3, 1, 2, 3 }, new FrameDecoderOptions { MaxFrameLength = 2 })).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecodesFragmentedCoalescedAndEmptyFramesInOrder()
    {
        byte[] wire = Concatenate(
            Framing.EncodeFrame(new byte[] { 1, 2, 3 }),
            Framing.EncodeFrame([]),
            Framing.EncodeFrame(new byte[] { 4 }));

        FrameDecoder decoder = new();
        List<byte[]> frames = [];
        foreach (byte value in wire)
        {
            frames.AddRange(decoder.Push(new[] { value }));
        }

        decoder.End();
        Assert.Equal(3, frames.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, frames[0]);
        Assert.Empty(frames[1]);
        Assert.Equal(new byte[] { 4 }, frames[2]);

        FrameDecoder coalesced = new();
        Assert.Equal(frames, coalesced.Push(wire));
        coalesced.End();
    }

    [Fact]
    public void AssemblesPayloadSpanningMultipleInternalBlocks()
    {
        byte[] payload = Enumerable.Range(0, 70_000).Select(index => (byte)(index % 251)).ToArray();
        byte[] wire = Framing.EncodeFrame(payload);
        FrameDecoder decoder = new();
        List<byte[]> frames = [];
        frames.AddRange(decoder.Push(wire.AsSpan(0, 101)));
        frames.AddRange(decoder.Push(wire.AsSpan(101, 65_541 - 101)));
        frames.AddRange(decoder.Push(wire.AsSpan(65_541)));
        decoder.End();

        Assert.Single(frames);
        Assert.Equal(payload, frames[0]);
    }

    [Fact]
    public void HandlesEverySplitPointAcrossAFrame()
    {
        byte[] wire = Framing.EncodeFrame(new byte[] { 10, 20, 30, 40 });
        for (int split = 0; split <= wire.Length; split++)
        {
            FrameDecoder decoder = new();
            List<byte[]> frames = [];
            frames.AddRange(decoder.Push(wire.AsSpan(0, split)));
            frames.AddRange(decoder.Push(wire.AsSpan(split)));
            decoder.End();

            Assert.Single(frames);
            Assert.Equal(new byte[] { 10, 20, 30, 40 }, frames[0]);
        }
    }

    [Fact]
    public void CopiesPayloadBytesInsteadOfAliasingInputChunks()
    {
        byte[] chunk = Framing.EncodeFrame(new byte[] { 1, 2, 3 });
        FrameDecoder decoder = new();
        IReadOnlyList<byte[]> frames = decoder.Push(chunk);
        chunk.AsSpan().Fill(9);

        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(frames));
    }

    [Fact]
    public void AcceptsEmptyChunksAndCleanEmptyStream()
    {
        FrameDecoder decoder = new();
        Assert.Empty(decoder.Push([]));
        decoder.End();
    }

    [Fact]
    public void RejectsTruncatedStreamsAtEnd()
    {
        foreach (byte[] wire in new[] { new byte[] { 0, 0, 0 }, new byte[] { 0, 0, 0, 2, 1 } })
        {
            FrameDecoder decoder = new();
            Assert.Empty(decoder.Push(wire));
            Assert.Throws<FrameError>(decoder.End);
        }
    }

    [Fact]
    public void RejectsOversizedDeclaredLengthAsSoonAsHeaderIsComplete()
    {
        FrameDecoder decoder = new(new FrameDecoderOptions { MaxFrameLength = 3 });
        Assert.Contains("limit", Assert.Throws<FrameError>(() => decoder.Push(new byte[] { 0, 0, 0, 4 })).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failed", Assert.Throws<FrameError>(() => decoder.Push(new byte[] { 1 })).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptsFrameExactlyAtConfiguredMaximum()
    {
        FrameDecoder decoder = new(new FrameDecoderOptions { MaxFrameLength = 3 });
        Assert.Equal(new[] { new byte[] { 1, 2, 3 } }, decoder.Push(Framing.EncodeFrame(new byte[] { 1, 2, 3 })));
        decoder.End();
    }

    [Fact]
    public void CannotBePushedAfterEnd()
    {
        FrameDecoder decoder = new();
        decoder.End();
        Assert.Contains("ended", Assert.Throws<FrameError>(() => decoder.Push([])).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ended", Assert.Throws<FrameError>(decoder.End).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(1.5d)]
    [InlineData(double.NaN)]
    [InlineData(16_777_216_000d)]
    public void RejectsInvalidMaximumFrameLength(double maxFrameLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameDecoder(new FrameDecoderOptions { MaxFrameLength = maxFrameLength }));
    }

    private static byte[] Concatenate(params byte[][] chunks)
    {
        int length = chunks.Sum(chunk => chunk.Length);
        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }

        return result;
    }
}
