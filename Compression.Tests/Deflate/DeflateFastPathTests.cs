using Compression.Core.Deflate;
using Bcl = System.IO.Compression;

namespace Compression.Tests.Deflate;

/// <summary>
/// The encoder's four-byte chains with their three-byte side table and run linking, and the
/// decoder's unchecked fast loop with its careful edge loop: round trips across every level,
/// data shape and boundary size, both directions against the platform's zlib, independence
/// from how input and output are chunked, and rejection — never a crash — of damaged input.
/// </summary>
[TestFixture]
public class DeflateFastPathTests {

  public enum Shape { Text, Random, Runs, ShortRepeats, Mixed }

  private static byte[] Make(Shape shape, int size, int seed = 11) {
    var rng = new Random(seed);
    var data = new byte[size];
    switch (shape) {
      case Shape.Text: {
        string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "deflate", "window",
          "huffman", "length", "distance", "block", "stream", "header", "trailer", "checksum", "archive", "format"];
        var text = new System.Text.StringBuilder(size + 16);
        while (text.Length < size)
          text.Append(words[rng.Next(words.Length)]).Append(rng.Next(12) == 0 ? ".\n" : " ");
        return System.Text.Encoding.ASCII.GetBytes(text.ToString(0, size));
      }
      case Shape.Random:
        rng.NextBytes(data);
        return data;
      case Shape.Runs:
        // Runs of one value, long and short, with a little noise between them.
        for (var i = 0; i < size;) {
          var run = Math.Min(rng.Next(4) == 0 ? rng.Next(1, 1200) : rng.Next(1, 9), size - i);
          data.AsSpan(i, run).Fill((byte)rng.Next(4));
          i += run;
          if (i < size && rng.Next(3) == 0)
            data[i++] = (byte)rng.Next(256);
        }

        return data;
      case Shape.ShortRepeats: {
        // A small vocabulary of three-byte tokens between random bytes, as in machine code:
        // plenty of matches of exactly three bytes, few longer ones.
        var tokens = new byte[48][];
        for (var t = 0; t < tokens.Length; ++t) {
          tokens[t] = new byte[3];
          rng.NextBytes(tokens[t]);
        }

        for (var i = 0; i < size;) {
          if (rng.Next(3) == 0) {
            data[i++] = (byte)rng.Next(256);
            continue;
          }

          var token = tokens[rng.Next(tokens.Length)];
          var n = Math.Min(3, size - i);
          token.AsSpan(0, n).CopyTo(data.AsSpan(i));
          i += n;
        }

        return data;
      }
      default: {
        var shapes = new[] { Shape.Text, Shape.Random, Shape.Runs, Shape.ShortRepeats };
        for (var i = 0; i < size;) {
          var n = Math.Min(rng.Next(1, 40_000), size - i);
          Make(shapes[rng.Next(shapes.Length)], n, rng.Next()).CopyTo(data, i);
          i += n;
        }

        return data;
      }
    }
  }

  private static byte[] BclInflate(byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var inflate = new Bcl.DeflateStream(input, Bcl.CompressionMode.Decompress);
    using var output = new MemoryStream();
    inflate.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] BclDeflate(byte[] data, int level) {
    using var output = new MemoryStream();
    using (var deflate = new Bcl.DeflateStream(output, new Bcl.ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true))
      deflate.Write(data);
    return output.ToArray();
  }

  public static IEnumerable<int> Levels => Enumerable.Range(1, 9);

  // Empty and tiny inputs, the match limits (3, 258, MinLookahead 262), a symbol buffer's worth
  // (16 Ki), the farthest distance the encoder emits (32768 - 262) and the window, its
  // double (where the buffer slides) and past that.
  public static IEnumerable<int> Sizes => [0, 1, 2, 3, 4, 5, 257, 258, 259, 262, 263, 16383, 16384, 16385,
    32505, 32506, 32507, 32767, 32768, 32769, 65535, 65536, 65537, 98305, 200_003];

  // ── round trips ───────────────────────────────────────────────────────

  [Test, Category("RoundTrip"), Category("Boundary")]
  public void EveryLevelShapeAndSize_RoundTrips_ThroughBothDecoders(
    [ValueSource(nameof(Levels))] int level,
    [Values] Shape shape,
    [ValueSource(nameof(Sizes))] int size) {
    var data = Make(shape, size);

    var compressed = DeflateCompressor.Compress(data, (DeflateCompressionLevel)level);

    Assert.That(DeflateDecompressor.Decompress(compressed), Is.EqualTo(data), "our decoder");
    Assert.That(BclInflate(compressed), Is.EqualTo(data), "platform zlib");
  }

  [Test, Category("ThemVsUs"), Category("RoundTrip")]
  public void PlatformZlibOutput_AtEveryLevel_IsReadByOurDecoder(
    [Values(1, 2, 6, 9)] int zlibLevel,
    [Values] Shape shape) {
    // zlib-ng's level 1 writes fixed-Huffman blocks, the others dynamic ones with their own
    // block boundaries and code-length choices.
    var data = Make(shape, 300_000, seed: 5);

    var compressed = BclDeflate(data, zlibLevel);

    Assert.That(DeflateDecompressor.Decompress(compressed), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip"), Category("EdgeCase")]
  public void SkewedAlphabets_ForceSubTables_RoundTrip([Values(1, 6, 9)] int level) {
    // Literal frequencies halving from one value to the next give codes of up to 15 bits,
    // past the decoder's 11-bit root table; distances spread over many orders of magnitude
    // do the same for the 8-bit distance root.
    var rng = new Random(3);
    using var buffer = new MemoryStream();
    while (buffer.Length < 400_000) {
      var value = 0;
      while (value < 40 && rng.Next(2) == 0)
        ++value;
      buffer.WriteByte((byte)value);

      if (rng.Next(16) == 0 && buffer.Length > 40_000) {
        var distance = 1 + (int)Math.Min(32_000, Math.Pow(2, rng.NextDouble() * 15));
        var length = rng.Next(3, 40);
        for (var i = 0; i < length; ++i)
          buffer.WriteByte(buffer.GetBuffer()[buffer.Length - distance]);
      }
    }

    var data = buffer.ToArray();
    foreach (var compressed in new[] { DeflateCompressor.Compress(data, (DeflateCompressionLevel)level), BclDeflate(data, level) })
      Assert.That(DeflateDecompressor.Decompress(compressed), Is.EqualTo(data));
  }

  // ── determinism ───────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Output_DoesNotDependOnWriteSplits(
    [Values(1, 3, 4, 6, 9)] int level,
    [Values(Shape.Runs, Shape.ShortRepeats, Shape.Mixed)] Shape shape,
    [Values(1, 5, 4096, 65543)] int chunk) {
    var data = Make(shape, 150_000, seed: 8);
    var whole = DeflateCompressor.Compress(data, (DeflateCompressionLevel)level);

    using var output = new MemoryStream();
    var compressor = new DeflateCompressor(output, (DeflateCompressionLevel)level);
    for (var offset = 0; offset < data.Length; offset += chunk)
      compressor.Write(data.AsSpan(offset, Math.Min(chunk, data.Length - offset)));
    compressor.Finish();

    Assert.That(output.ToArray(), Is.EqualTo(whole));
  }

  [Test, Category("ThemVsUs")]
  public void Ratio_NotWorseThanPlatformZlib_AtTheSameLevel([Values(6, 9)] int level, [Values(Shape.Text, Shape.ShortRepeats)] Shape shape) {
    // Levels 6 and 9 run zlib's own search limits, so they should land where zlib-ng does or
    // better; a percent of slack absorbs the two encoders' different block boundaries.
    var data = Make(shape, 500_000, seed: 2);

    var ours = DeflateCompressor.Compress(data, (DeflateCompressionLevel)level).Length;
    var platform = BclDeflate(data, level).Length;

    Assert.That(ours, Is.LessThanOrEqualTo(platform * 1.01), $"ours {ours} vs zlib-ng {platform}");
  }

  // ── decoder chunking ──────────────────────────────────────────────────

  [Test, Category("EdgeCase")]
  public void Decoder_AnyInputAndOutputChunking_GivesTheSameBytes(
    [Values(1, 13, 4096)] int maxRead,
    [Values(1, 300, 65536)] int outputChunk) {
    var data = Make(Shape.Mixed, 180_000, seed: 4);
    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);

    var decoder = new DeflateDecompressor(new ChunkedStream(compressed, maxRead));
    var output = new byte[data.Length + 1];
    var total = 0;
    int n;
    while ((n = decoder.Decompress(output.AsSpan(total, Math.Min(outputChunk, output.Length - total)))) > 0)
      total += n;

    Assert.That(output.AsSpan(0, total).ToArray(), Is.EqualTo(data));
  }

  // ── decoder hardening ─────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Decoder_DamagedStreams_AreRejectedOrDecoded_NeverCrash([Values(1, 6)] int level, [Values(Shape.Text, Shape.Mixed)] Shape shape) {
    // Flipped bits anywhere — header, tables, symbols — must end in InvalidDataException or
    // EndOfStreamException (or, by chance, some output), never an out-of-range access.
    var data = Make(shape, 40_000, seed: 6);
    var compressed = DeflateCompressor.Compress(data, (DeflateCompressionLevel)level);
    var rng = new Random(level * 31 + (int)shape);

    for (var trial = 0; trial < 600; ++trial) {
      var damaged = (byte[])compressed.Clone();
      for (var flips = rng.Next(1, 4); flips > 0; --flips)
        damaged[rng.Next(damaged.Length)] ^= (byte)(1 << rng.Next(8));

      try {
        DeflateDecompressor.Decompress(damaged);
      } catch (InvalidDataException) {
      } catch (EndOfStreamException) {
      }
    }
  }

  [Test, Category("ErrorHandling")]
  public void Decoder_EveryTruncation_IsRejectedOrAPrefix() {
    var data = Make(Shape.Mixed, 6_000, seed: 9);
    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);

    for (var length = 0; length < compressed.Length; ++length) {
      byte[] decoded;
      try {
        decoded = DeflateDecompressor.Decompress(compressed.AsSpan(0, length));
      } catch (EndOfStreamException) {
        continue;
      } catch (InvalidDataException) {
        continue;
      }

      // A stream cut exactly between two blocks reads as ending there; whatever is decoded
      // must be the start of the original, never garbage.
      Assert.That(data.AsSpan().StartsWith(decoded), Is.True, $"a {length}-byte prefix decoded to something else");
    }
  }

  /// <summary>A forward-only stream that hands out at most <c>maxRead</c> bytes per read.</summary>
  private sealed class ChunkedStream(byte[] data, int maxRead) : Stream {
    private int _position;
    public override int Read(byte[] buffer, int offset, int count) {
      var n = Math.Min(Math.Min(count, maxRead), data.Length - this._position);
      Array.Copy(data, this._position, buffer, offset, n);
      this._position += n;
      return n;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
