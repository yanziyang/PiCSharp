using System.Collections;
using System.Numerics;

using Pi.Protocol;

using Xunit;

namespace Pi.Protocol.Tests;

public sealed class CborTests
{
    public static IEnumerable<object[]> KnownVectors()
    {
        yield return [null!, "f6"];
        yield return [false, "f4"];
        yield return [true, "f5"];
        yield return [0L, "00"];
        yield return [1L, "01"];
        yield return [10L, "0a"];
        yield return [23L, "17"];
        yield return [24L, "1818"];
        yield return [25L, "1819"];
        yield return [100L, "1864"];
        yield return [1000L, "1903e8"];
        yield return [1_000_000L, "1a000f4240"];
        yield return [1_000_000_000_000L, "1b000000e8d4a51000"];
        yield return [9_007_199_254_740_991L, "1b001fffffffffffff"];
        yield return [-1L, "20"];
        yield return [-10L, "29"];
        yield return [-24L, "37"];
        yield return [-25L, "3818"];
        yield return [-100L, "3863"];
        yield return [-1000L, "3903e7"];
        yield return [-1_000_000L, "3a000f423f"];
        yield return [-9_007_199_254_740_991L, "3b001ffffffffffffe"];
        yield return [1.1d, "fb3ff199999999999a"];
        yield return [BitConverter.UInt64BitsToDouble(0x8000_0000_0000_0000UL), "fb8000000000000000"];
        yield return [new byte[] { 1, 2, 3, 4 }, "4401020304"];
        yield return [string.Empty, "60"];
        yield return ["IETF", "6449455446"];
        yield return ["ü", "62c3bc"];
        yield return ["水", "63e6b0b4"];
        yield return ["𐅑", "64f0908591"];
        yield return [Array.Empty<object?>(), "80"];
        yield return [new object?[] { 1L, 2L, 3L }, "83010203"];
        yield return [new object?[] { 1L, new object?[] { 2L, 3L }, new object?[] { 4L, 5L } }, "8301820203820405"];
        yield return [new Dictionary<string, object?> { ["a"] = 1L, ["b"] = new object?[] { 2L, 3L } }, "a26161016162820203"];
    }

    [Theory]
    [MemberData(nameof(KnownVectors))]
    public void EncodesAndDecodesRfc8949Vector(object? expected, string wire)
    {
        byte[] encoded = CborEncoder.EncodeCbor(expected);

        Assert.Equal(wire, Convert.ToHexString(encoded).ToLowerInvariant());
        AssertCborValueEqual(expected, CborDecoder.DecodeCbor(Convert.FromHexString(wire)));
    }

    [Fact]
    public void PreservesFalseyValuesAndNullMapEntries()
    {
        Dictionary<string, object?> value = new()
        {
            ["zero"] = 0L,
            ["empty"] = string.Empty,
            ["no"] = false,
            ["nil"] = null,
        };

        object? decoded = CborDecoder.DecodeCbor(CborEncoder.EncodeCbor(value));
        IReadOnlyDictionary<string, object?> map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(decoded);

        Assert.Equal(4, map.Count);
        Assert.Equal(0L, map["zero"]);
        Assert.Equal(string.Empty, map["empty"]);
        Assert.Equal(false, map["no"]);
        Assert.Null(map["nil"]);
    }

    [Fact]
    public void PreservesLeadingUnicodeBomAndProtoKeyAsData()
    {
        Assert.Equal("\ufeff", CborDecoder.DecodeCbor(Convert.FromHexString("63efbbbf")));

        Dictionary<string, object?> value = new() { ["__proto__"] = "safe" };
        object? decoded = CborDecoder.DecodeCbor(CborEncoder.EncodeCbor(value));
        IReadOnlyDictionary<string, object?> map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(decoded);
        Assert.Equal("safe", map["__proto__"]);
    }

    public static IEnumerable<object[]> UnsupportedEncoderValues()
    {
        yield return ["NaN", double.NaN];
        yield return ["positive infinity", double.PositiveInfinity];
        yield return ["negative infinity", double.NegativeInfinity];
        yield return ["unsafe positive integer", 9_007_199_254_740_992d];
        yield return ["unsafe negative integer", -9_007_199_254_740_992d];
        yield return ["big integer", new BigInteger(1)];
        yield return ["unsupported object", new object()];
        yield return ["date", DateTime.UnixEpoch];
        yield return ["map", new Hashtable()];
    }

    [Theory]
    [MemberData(nameof(UnsupportedEncoderValues))]
    public void RejectsUnsupportedEncoderValue(string _, object value)
    {
        Assert.Throws<CborError>(() => CborEncoder.EncodeCbor(value));
    }

    [Fact]
    public void RejectsCyclesAndExcessiveEncoderDepth()
    {
        List<object?> cyclic = [];
        cyclic.Add(cyclic);
        Assert.Throws<CborError>(() => CborEncoder.EncodeCbor(cyclic));

        object? tooDeep = null;
        for (int depth = 0; depth <= CborLimits.DefaultMaxCborDepth; depth++)
        {
            tooDeep = new object?[] { tooDeep };
        }

        CborError error = Assert.Throws<CborError>(() => CborEncoder.EncodeCbor(tooDeep));
        Assert.Contains("depth", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsLossyStrings()
    {
        CborError error = Assert.Throws<CborError>(() => CborEncoder.EncodeCbor("\ud800"));
        Assert.Contains("Unicode", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> InvalidDecoderInputs()
    {
        yield return ["empty input", ""];
        yield return ["truncated integer", "18"];
        yield return ["reserved additional information", "1c"];
        yield return ["indefinite byte string", "5f"];
        yield return ["indefinite text string", "7f"];
        yield return ["indefinite array", "9f"];
        yield return ["indefinite map", "bf"];
        yield return ["tag", "c000"];
        yield return ["undefined", "f7"];
        yield return ["unsupported simple value", "e0"];
        yield return ["break outside an indefinite item", "ff"];
        yield return ["float16", "f93c00"];
        yield return ["float32", "fa3f800000"];
        yield return ["positive infinity", "fb7ff0000000000000"];
        yield return ["NaN", "fb7ff8000000000000"];
        yield return ["truncated float64", "fb3ff00000"];
        yield return ["truncated byte string", "44010203"];
        yield return ["truncated text string", "636162"];
        yield return ["truncated array", "8201"];
        yield return ["truncated map", "a16161"];
        yield return ["trailing data", "0000"];
        yield return ["non-string map key", "a10102"];
        yield return ["duplicate map key", "a2616101616102"];
        yield return ["invalid UTF-8 byte", "61ff"];
        yield return ["overlong UTF-8", "62c080"];
        yield return ["UTF-8 surrogate", "63eda080"];
        yield return ["unsafe positive integer", "1b0020000000000000"];
        yield return ["unsafe negative integer", "3b001fffffffffffff"];
        yield return ["unsafe integer encoded as float64", "fb4340000000000000"];
    }

    [Theory]
    [MemberData(nameof(InvalidDecoderInputs))]
    public void RejectsInvalidDecoderInput(string _, string wire)
    {
        Assert.Throws<CborError>(() => CborDecoder.DecodeCbor(Convert.FromHexString(wire)));
    }

    [Fact]
    public void EnforcesDepthAndDeclaredLengthLimitsBeforeTraversing()
    {
        byte[] tooDeep = new byte[CborLimits.DefaultMaxCborDepth + 2];
        Array.Fill(tooDeep, (byte)0x81, 0, tooDeep.Length - 1);
        tooDeep[^1] = 0xf6;
        CborError depthError = Assert.Throws<CborError>(() => CborDecoder.DecodeCbor(tooDeep));
        Assert.Contains("depth", depthError.Message, StringComparison.OrdinalIgnoreCase);

        byte[] oversizedBytes = DeclaredLength(0x5a, CborLimits.DefaultMaxCborByteLength + 1);
        byte[] oversizedText = DeclaredLength(0x7a, CborLimits.DefaultMaxCborByteLength + 1);
        byte[] oversizedArray = DeclaredLength(0x9a, CborLimits.DefaultMaxCborContainerLength + 1);
        byte[] oversizedMap = DeclaredLength(0xba, CborLimits.DefaultMaxCborContainerLength + 1);

        foreach (byte[] wire in new[] { oversizedBytes, oversizedText, oversizedArray, oversizedMap })
        {
            CborError error = Assert.Throws<CborError>(() => CborDecoder.DecodeCbor(wire));
            Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SupportsStricterCallerProvidedLimits()
    {
        Assert.Throws<CborError>(() => CborDecoder.DecodeCbor(Convert.FromHexString("83010203"), new CborOptions { MaxContainerLength = 2 }));
        Assert.Throws<CborError>(() => CborDecoder.DecodeCbor(Convert.FromHexString("626162"), new CborOptions { MaxByteLength = 2 }));
        Assert.Throws<CborError>(() => CborEncoder.EncodeCbor(new object?[] { 1L, 2L, 3L }, new CborOptions { MaxContainerLength = 2 }));
        Assert.Throws<CborError>(() => CborEncoder.EncodeCbor("ab", new CborOptions { MaxByteLength = 2 }));
    }

    private static byte[] DeclaredLength(byte initial, uint length)
    {
        byte[] wire = new byte[5];
        wire[0] = initial;
        wire[1] = (byte)(length >> 24);
        wire[2] = (byte)(length >> 16);
        wire[3] = (byte)(length >> 8);
        wire[4] = (byte)length;
        return wire;
    }

    private static void AssertCborValueEqual(object? expected, object? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        if (expected is double expectedNumber)
        {
            double actualNumber = Assert.IsType<double>(actual);
            if (IsNegativeZero(expectedNumber))
            {
                Assert.True(IsNegativeZero(actualNumber));
            }
            else
            {
                Assert.Equal(expectedNumber, actualNumber);
            }

            return;
        }

        if (expected is byte[] expectedBytes)
        {
            Assert.Equal(expectedBytes, Assert.IsType<byte[]>(actual));
            return;
        }

        if (expected is string or bool)
        {
            Assert.Equal(expected, actual);
            return;
        }

        if (expected is long expectedInteger)
        {
            Assert.Equal(expectedInteger, Assert.IsType<long>(actual));
            return;
        }

        if (expected is IReadOnlyList<object?> expectedList)
        {
            IReadOnlyList<object?> actualList = Assert.IsAssignableFrom<IReadOnlyList<object?>>(actual);
            Assert.Equal(expectedList.Count, actualList.Count);
            for (int index = 0; index < expectedList.Count; index++)
            {
                AssertCborValueEqual(expectedList[index], actualList[index]);
            }

            return;
        }

        if (expected is IReadOnlyDictionary<string, object?> expectedMap)
        {
            IReadOnlyDictionary<string, object?> actualMap = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(actual);
            Assert.Equal(expectedMap.Keys, actualMap.Keys);
            foreach (KeyValuePair<string, object?> entry in expectedMap)
            {
                AssertCborValueEqual(entry.Value, actualMap[entry.Key]);
            }

            return;
        }

        throw new Xunit.Sdk.XunitException($"Unsupported expected test value {expected.GetType().Name}");
    }

    private static bool IsNegativeZero(double value) => value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;
}
