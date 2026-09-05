using System.Buffers.Binary;
using System.Security.Cryptography;
using Codec.WavPack;
using Compression.Registry;
using FileFormat.WavPack;
using NUnit.Framework;

namespace Compression.Tests.Audio;

/// <summary>
/// WavPack pinned against streams our own encoder did not write.
/// </summary>
/// <remarks>
/// <para>
/// The metadata sub-block id byte was decoded a bit out of position — odd-size
/// read as bit 5 and large-size as bit 6, where the format puts them at 6 and 7.
/// The reader still recovered the right id, so a small sub-block parsed fine and
/// every round-trip test passed; the encoder wrote the same wrong bits, so the
/// pair agreed with itself and with nothing else. Any block whose bitstream ran
/// past 510 bytes — which is every block of real audio — was framed wrongly in
/// both directions.
/// </para>
/// <para>
/// Round-tripping our own output cannot catch that. These tests decode a stream
/// from libavcodec and check our own output against what the format requires,
/// which is what makes the two encoders disagree out loud.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WavPackConformanceOracleTests {

  /// <summary>
  /// 0.03 s of a 440 Hz sine, 44.1 kHz stereo, encoded by ffmpeg 9.0.1: two
  /// blocks, each with a bitstream sub-block past the 510-byte boundary, and the
  /// APEv2 tag ffmpeg appends by default.
  /// </summary>
  private const string FfmpegWavPack =
    "d3Zwa/ACAAAQBAAAKwUAAAAAAAAABAAAMRi8BNNkGGYCAVdWAwIAeQB+BAQAAAAAfvjn9wUGAAAAAAAAwQbcBQYHilkBAPn/93Ce" +
    "+P/36fr/HUWQUIAAZhhBQoFQxsFg+oFQgoQybjIanAAFQATDauHEYrdBBA2MoIUJtQCiFgYGJd5CRLiBCUHDtDAgkCAhBJBRMBmb" +
    "aCwQSoATIAICAH5gkPXANNZgoIZajzwkgGPikVFqYGhxi3vgFmtRgigylEDGPfithRFEECHIMAEwuAEIZQQIJWwSZERAw4QGyaOF" +
    "1otHyxvIOEOCSLhlAqEEkT2YaHDuARl6QDAIcE+cUQtnYHCLIvMrQWTM6sE0IMHEgMgADNPCkJmEci82w4hbHBrcMhlnFCDoHzij" +
    "hmlwRDgCEAC3PDLEkVDCTIsSwj3Ylu0JQ4IJJNSTTYjfgWEyAGFEIKEGogQwINwDZ8ABTUBgYsMEwwQARMJg+MkAzhAtExhFIEbj" +
    "0bCA3HCD4AFSb9SADDDKCDUoMZwQvwMaFBiiQbgH00KUAQEC6AEoe+DUk0kIJWhQgzOghmmZxG8IA0YB0TKBGpgypIQSsIYb7sUj" +
    "A4AGJGhQAxEwBMK4H7mfvGXIKAM0CDXQIAb1RC3IGAVgFNDinjx6sFowQAgTBKD+ByeAjBMSsoEIbJiWRw9WAqbNNAC2MGKcAYyI" +
    "RwMCghEQ/24zDQwZBAhRD9TgBsAEIUBGwCQUARAMbnFGTCAmo9RC9A+EgkGR2KDEhk0IRWIE8mixBzYAPdgWJUAQwEAGaFDL78iQ" +
    "oEEt0xCPHjBkABoQeDA9AGUYEggN0wAUMUogIpwA4f4HJYQblFFAINPghunBo8ER4gb14IRRAgCMIgFgiy1EA/1OEA1mMgIEiQjQ" +
    "Mj0g9cbRw2qxCeQWJdyASAglSCgaSP0viAYFwkCmgUiMYHhkZBpePSBDwkwLIBrUANTCiBAg1PA/DYAJMiTI0OAWJRb1wg0LGxgh" +
    "SAmjAMjYIBhKAHd2cGsKAwAAEAQAAAAAAAAABAAAKwEAACEYvAQPwILxAgFXVgMCAH4AfgQEAAAAAHIMawwFBgAAAAAAANwFdgRt" +
    "BopmAQD///6/CfP/797/v//vyfz/2///+//EzP///X9jJv///b+MVf///l/A+vT/7/9xK/7/+z+Nl/9//2cv/P/3fxSh///+5x75" +
    "//c/k7f/f/9DiP//+x8e2/+//6Dr///+Q6z8//sPRvz/9w8O/f/751v///uHGfz/+5OL///+oMf/fz8t+v/3O5/+//fRjv9/Tzr/" +
    "//Xp/xZxn3zI/o+Y/4f2//fN/0+X/38k+/8nlP9/MP3/o5//9/T/v6KH/1s3/9d5/09k/5v1//s1P/z6z+7/Z+3LHw/7S/zTs9b/" +
    "Aty/gqzHgX1b1y/z3sd8ffeb3wt9Xt8P42OQ/49za6Df57l+kl8LfF/a8pSeh23ejveaycs0j8gu+YHujF2OTLz63id6/O6Jckv9" +
    "7fnRd0xeQn1D5dXFx1nebj7n/uA77J/+GPBVF4Oubxi+V457rI9IMJ/u/3wptubptC9XPn9QH7d+G/p04ZdVnzM+N3vNcxjhPbRp" +
    "EGOnejwy5rxZVYhqXKj9oMy7JkMNcbFvEk+wTnGjFCM8s1ffAnfH8IDz31crwrfSB3E772r2/6O43R9vobd+TP4a/bX6+uAb6Me4" +
    "yzWXV/knqzuIszR/Rp5UtO13yqvbh9F3xn2Cu6qcxg2ZNiYjwITty1i5SFHzUDJKtBSEDCMS7gtJl01MeeJkW4rR1hE6iE8Dolvh" +
    "1Tjg+GiwFq0Dx0D3d7cA9Ef+O0uwsJ+7D+Pnf3E/XZ9Aj98T65E5+B1ip6IzyRhwaouzhqLpZdYICTLYsZVZjswqhgbTQ1TJRlEs" +
    "8J2VoVXYnZIX1qO40n3Kv0B0FBnFBtEpvP5fXPQEwr7uf3VQ8937yn0n//97/V/qu92/1j/FP9zX6FtytbcWWywtWNby5qhpZQRs" +
    "2QJWIIovDUd98IGB+MCZsISB4bqoGDoBLArO04xwVr6Xfk6LchBCpI7hKxh+BkFQRVRBR0VY0AcAADwAAAABAAAAAAAAoAAAAAAA" +
    "AAAADAAAAAAAAABlbmNvZGVyAExhdmY2My4xLjEwMUFQRVRBR0VY0AcAADwAAAABAAAAAAAAgAAAAAAAAAAA";

  /// <summary>SHA-256 of the interleaved 16-bit PCM libavcodec decodes from that stream.</summary>
  private const string FfmpegPcmDigest =
    "1ff0b0f3136ee976444c22884d3842de4dffb401cd2cbf8cb4c9e37172fa1984";

  private const int SubBlockIdMask = 0x3F;
  private const int SubBlockOddSize = 0x40;
  private const int SubBlockLargeSize = 0x80;
  private const int HeaderSize = 32;

  private static byte[] Sine(int frames, int channels, int sampleRate, int bytesPerSample) {
    var pcm = new byte[frames * channels * bytesPerSample];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < channels; ++c) {
        var value = (int)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * ((1L << (bytesPerSample * 8 - 1)) - 1) * 0.8);
        var offset = (i * channels + c) * bytesPerSample;
        // WAVE keeps 8-bit samples unsigned and everything wider signed.
        if (bytesPerSample == 1)
          pcm[offset] = (byte)(value + 128);
        else
          for (var b = 0; b < bytesPerSample; ++b)
            pcm[offset + b] = (byte)(value >> (b * 8));
      }

    return pcm;
  }

  private static byte[] Encode(byte[] pcm, int channels, int sampleRate, int bitsPerSample) {
    using var input = new MemoryStream(pcm, writable: false);
    using var output = new MemoryStream();
    WavPackCodec.Compress(input, output, channels, sampleRate, bitsPerSample);
    return output.ToArray();
  }

  // ── decode side: a stream we did not write ────────────────────────────────

  /// <summary>
  /// The bitstream sub-block here is past 510 bytes, so it carries the large-size
  /// flag. Reading that flag from the wrong bit walks the sub-block scan off into
  /// the payload, and the decorrelation metadata that comes back is nonsense.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void DecodeMatchesLibavcodecByteForByte() {
    var encoded = Convert.FromBase64String(FfmpegWavPack);

    using var input = new MemoryStream(encoded, writable: false);
    using var pcm = new MemoryStream();
    WavPackCodec.Decompress(input, pcm);

    var digest = Convert.ToHexString(SHA256.HashData(pcm.ToArray())).ToLowerInvariant();
    Assert.That(digest, Is.EqualTo(FfmpegPcmDigest),
      "decoded PCM must be byte-for-byte what libavcodec decodes from the same stream");
  }

  /// <summary>
  /// APEv2 is WavPack's own tagging format, so a tagged file is the ordinary case
  /// and not a damaged one. The reader used to know only the legacy ID3v1 trailer
  /// and rejected everything ffmpeg or the reference tools produce.
  /// </summary>
  [Test]
  public void ApeTaggedStreamIsReadRatherThanRefused() {
    var encoded = Convert.FromBase64String(FfmpegWavPack);

    using var input = new MemoryStream(encoded, writable: false);
    var entries = new WavPackFormatDescriptor().List(input, password: null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(entry => entry.Name.EndsWith(".wv", StringComparison.OrdinalIgnoreCase)),
        Is.True, "no wvpk blocks were listed");
      Assert.That(entries.Any(entry => entry.Name.Equals("tags.ini", StringComparison.OrdinalIgnoreCase)),
        Is.True, "the APEv2 tag was not surfaced");
    });
  }

  /// <summary>The trailer must be recognised as a whole, not merely stepped over.</summary>
  [Test]
  public void ApeTrailerIsAccountedForExactly() {
    var encoded = Convert.FromBase64String(FfmpegWavPack);
    var blockEnd = 0;
    foreach (var (offset, size) in Blocks(encoded))
      blockEnd = offset + size;

    Assert.Multiple(() => {
      Assert.That(blockEnd, Is.LessThan(encoded.Length), "this stream is supposed to carry a trailer");
      Assert.That(ApeTagReader.IsTrailingMetadata(encoded, blockEnd), Is.True);
      // One byte short of the tag start is not a tag, and must not be taken for one.
      Assert.That(ApeTagReader.IsTrailingMetadata(encoded, blockEnd - 1), Is.False);
    });
  }

  // ── encode side: what the format requires of us ───────────────────────────

  /// <summary>
  /// The reference decoder refuses a file outright when the bitstream sub-block
  /// declares an odd byte count, and it decodes to silence when the magnitude
  /// field under-reports the samples. Neither shows up in a round-trip against
  /// our own reader.
  /// </summary>
  [TestCase(1, 44_100, 16)]
  [TestCase(2, 44_100, 16)]
  [TestCase(2, 48_000, 24)]
  [TestCase(2, 8_000, 8)]
  [TestCase(6, 44_100, 16)]
  [Category("RoundTrip")]
  public void EncodedBlocksSatisfyTheFormatsRequirements(int channels, int sampleRate, int bitsPerSample) {
    var bytesPerSample = bitsPerSample / 8;
    var frames = sampleRate / 2;
    var pcm = Sine(frames, channels, sampleRate, bytesPerSample);
    var encoded = Encode(pcm, channels, sampleRate, bitsPerSample);

    var blocks = Blocks(encoded).ToList();
    Assert.That(blocks, Is.Not.Empty);

    foreach (var (offset, size) in blocks) {
      Assert.That(size, Is.LessThanOrEqualTo(128 * 1024),
        $"block at {offset} is past the size the reference decoder will read");

      var subBlocks = SubBlocks(encoded, offset, size).ToList();
      var bitstream = subBlocks.SingleOrDefault(sub => sub.Id == 0x0A);
      Assert.That(bitstream.Id, Is.EqualTo(0x0A), $"block at {offset} has no bitstream sub-block");
      Assert.That(bitstream.Size % 2, Is.Zero,
        $"block at {offset} declares an odd-sized bitstream, which the reference decoder refuses");
      Assert.That(subBlocks.Any(sub => sub.Id == 0x05), Is.True,
        $"block at {offset} omits the entropy medians");

      var flags = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(offset + 24));
      Assert.That((flags >> 18) & 0x1F, Is.GreaterThan(0),
        $"block at {offset} reports a zero sample magnitude, which decodes as silence");
    }

    // Beyond stereo the channels span several blocks, and only the channel-info
    // sub-block says how many there are.
    if (channels > 2) {
      var first = blocks[0];
      Assert.That(SubBlocks(encoded, first.Offset, first.Size).Any(sub => sub.Id == 0x0D), Is.True,
        "a multichannel stream must declare its channel layout");
    }
  }

  /// <summary>
  /// The per-block check value the reference verifies: <c>crc = crc * 3 + sample</c>
  /// over the interleaved samples, seeded all-ones.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void EncodedBlocksCarryTheCheckValueTheReferenceVerifies() {
    const int channels = 2, sampleRate = 44_100, bitsPerSample = 16;
    var pcm = Sine(sampleRate / 2, channels, sampleRate, bitsPerSample / 8);
    var encoded = Encode(pcm, channels, sampleRate, bitsPerSample);

    var sampleIndex = 0;
    foreach (var (offset, _) in Blocks(encoded)) {
      var blockSamples = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(offset + 20));
      var stored = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(offset + 28));

      var crc = 0xFFFFFFFFu;
      for (var s = 0; s < blockSamples; ++s)
        for (var c = 0; c < channels; ++c) {
          var frame = (sampleIndex + s) * channels + c;
          crc = unchecked(crc * 3 + (uint)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(frame * 2)));
        }

      Assert.That(stored, Is.EqualTo(crc), $"block at {offset} stores the wrong check value");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(offset + 16)), Is.EqualTo((uint)sampleIndex),
        $"block at {offset} stores the wrong block index");
      sampleIndex += blockSamples;
    }

    Assert.That(sampleIndex, Is.EqualTo(sampleRate / 2), "the blocks do not cover the stream");
  }

  /// <summary>
  /// One block per stream stops being readable once the audio runs past a few
  /// seconds, because the reference reads a block into a fixed buffer.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void LongStreamsAreSplitIntoBlocksTheReferenceCanRead() {
    const int channels = 2, sampleRate = 44_100, bitsPerSample = 16;
    var pcm = Sine(sampleRate * 6, channels, sampleRate, bitsPerSample / 8);
    var encoded = Encode(pcm, channels, sampleRate, bitsPerSample);

    var blocks = Blocks(encoded).ToList();
    Assert.That(blocks, Has.Count.GreaterThan(1), "a six-second stream must not be one block");
    Assert.That(blocks.Select(b => b.Size), Has.All.LessThanOrEqualTo(128 * 1024));

    // and it still round-trips
    using var input = new MemoryStream(encoded, writable: false);
    using var decoded = new MemoryStream();
    WavPackCodec.Decompress(input, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(pcm));
  }

  // ── minimal structural walkers, deliberately independent of the codec ─────

  private static IEnumerable<(int Offset, int Size)> Blocks(byte[] file) {
    var offset = 0;
    while (offset + HeaderSize <= file.Length && file.AsSpan(offset, 4).SequenceEqual("wvpk"u8)) {
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4)) + 8;
      if (size < HeaderSize || offset + size > file.Length) yield break;
      yield return (offset, size);
      offset += size;
    }
  }

  private static IEnumerable<(int Id, int Size)> SubBlocks(byte[] file, int blockOffset, int blockSize) {
    var offset = blockOffset + HeaderSize;
    var end = blockOffset + blockSize;
    while (offset < end) {
      var id = file[offset++];
      int size;
      if ((id & SubBlockLargeSize) != 0) {
        if (offset + 3 > end) yield break;
        size = (file[offset] | (file[offset + 1] << 8) | (file[offset + 2] << 16)) << 1;
        offset += 3;
      } else {
        if (offset >= end) yield break;
        size = file[offset++] << 1;
      }

      if ((id & SubBlockOddSize) != 0) --size;
      if (size < 0 || offset + size > end) yield break;

      yield return (id & SubBlockIdMask, size);
      offset += size + (size & 1);
    }
  }
}
