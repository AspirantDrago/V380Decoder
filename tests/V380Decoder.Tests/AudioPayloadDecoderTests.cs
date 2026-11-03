using System.Security.Cryptography;
using V380Decoder.src;
using Xunit;

namespace V380Decoder.Tests;

public class AudioPayloadDecoderTests
{
    // FFmpeg-generated 440 Hz mono IMA WAV, 8 kHz, two 256-byte blocks.
    // The expected SHA-256 is from FFmpeg decoding and encoding as pcm_alaw.
    private static readonly byte[] Blocks = Convert.FromBase64String("AAAAAHd3d6esqwoxRTMikLy+uokwRTMTkdu8rIogRCQTgcrMupkgYzMUgbrcupogUzQjAcrMu5oYRDQjArrNrKoAQ0QiAqm9vKoYUlMyAajMy6oIMjYzA6jNy6oJMkUzEpjMvKuJMjY0EqDbvKsKMTY0EpDavLuZMTY0IpDa27qJIERDIpG6vruLIEVDIoG6zbuaEEQ0IwG6vryaKFM0I4K5vryaGFI0MxG5vryqCFNTMgGozMuqCDI2MwOozcuqCTJFMxKYzLyriTI2NBKg27yrCjE2NBKQ2ry7mTE2NCKQ2tu6iSBEQyKRur67iyBFQyKBus27mhBENCMBur68mjPwNQAxVDIimNvbqgowRCQikNrLq4ohNSUTkcq8rIongICAgICAgACIAIgAiACIgACIAIiAgICAAAgICAgIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIg=");

    [Fact]
    public void MatchesIndependentFfmpegImaAndAlawReference()
    {
        byte[] samples = [..ImaAdpcm.DecodeToAlaw(Blocks.AsSpan(0, 256)),
                          ..ImaAdpcm.DecodeToAlaw(Blocks.AsSpan(256, 256))];
        Assert.Equal(1010, samples.Length);
        Assert.Equal("768E8F8717FCBE8A03A6ADF2F65027F03107DD38E3D7E2FA115A57878278AE31", Convert.ToHexString(SHA256.HashData(samples)));
    }

    [Fact]
    public void AlawMatchesFfmpegForEverySigned16BitSample()
    {
        var encoded = Enumerable.Range(short.MinValue, 65536).Select(ImaAdpcm.EncodeAlaw).ToArray();
        Assert.Equal("DE19FF5B8D6A1950AA98385B61C7F7740A2CA88926FA86AFD5986341F343A82C", Convert.ToHexString(SHA256.HashData(encoded)));
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(18, true)]
    [InlineData(20, true)]
    [InlineData(16, false)]
    [InlineData(18, false)]
    [InlineData(20, false)]
    public void DetectsMediaBoundaryBeforeDecryptionAndProducesPcma(int headerSize, bool encrypted)
    {
        byte[] key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var decoder = new AudioPayloadDecoder(key);
        byte[] frame = Frame(headerSize, encrypted, key);
        Assert.False(decoder.TryDecodeIma(frame, encrypted, out _, out _));
        Assert.True(decoder.TryDecodeIma(frame, encrypted, out var samples, out bool changed));
        Assert.True(changed);
        Assert.Equal(headerSize, decoder.HeaderSize);
        Assert.Equal(encrypted, decoder.Decrypts);
        Assert.Equal("768E8F8717FCBE8A03A6ADF2F65027F03107DD38E3D7E2FA115A57878278AE31", Convert.ToHexString(SHA256.HashData(samples)));
        Assert.True(decoder.TryDecodeIma(frame, encrypted, out _, out changed));
        Assert.False(changed);
        decoder.Reset();
        Assert.False(decoder.TryDecodeIma(frame, encrypted, out _, out _));
    }

    [Fact]
    public void WrongKeyMalformedAndTruncatedFramesRemainMuted()
    {
        byte[] key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var decoder = new AudioPayloadDecoder(new byte[16]);
        byte[] frame = Frame(16, true, key);
        for (int i = 0; i < 3; i++) Assert.False(decoder.TryDecodeIma(frame, true, out _, out _));
        Assert.False(decoder.TryDecodeIma(frame[..^1], true, out _, out _));
        Assert.False(decoder.TryDecodeIma(new byte[8], true, out _, out _));
        var invalid = Frame(16, false, key);
        invalid[18] = 89;
        Assert.False(decoder.TryDecodeIma(invalid, false, out _, out _));
        invalid[18] = 0;
        invalid[19] = 1;
        Assert.False(decoder.TryDecodeIma(invalid, false, out _, out _));
    }

    [Fact]
    public void InvalidFrameBreaksLayoutConfirmation()
    {
        var key = new byte[16];
        var decoder = new AudioPayloadDecoder(key);
        byte[] frame = Frame(16, true, key);
        Assert.False(decoder.TryDecodeIma(frame, true, out _, out _));
        Assert.False(decoder.TryDecodeIma(new byte[4], true, out _, out _));
        Assert.False(decoder.TryDecodeIma(frame, true, out _, out _));
        Assert.True(decoder.TryDecodeIma(frame, true, out _, out _));
    }

    private static byte[] Frame(int headerSize, bool encrypted, byte[] key)
    {
        byte[] data = (byte[])Blocks.Clone();
        if (encrypted)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            data = aes.EncryptEcb(data, PaddingMode.None);
        }
        return [..new byte[headerSize], ..data];
    }
}
