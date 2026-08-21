namespace Compression.Core.Dictionary.Lzo;

/// <summary>
/// Decompresses a genuine LZO1X stream — the one lzop, the kernel and squashfs
/// all write.
/// </summary>
/// <remarks>
/// <para>What stood here before decoded a private encoding that carried the LZO1X
/// name. It round-tripped perfectly against the compressor beside it and against
/// nothing else: lzop could not read what this project wrote and this project
/// could not read what lzop wrote, each rejecting the other in one command. Every
/// test of it was a round trip through both halves of the same private format, so
/// the two agreed with each other and the name on the tin was wrong.</para>
///
/// <para>Implemented from the published description of the stream format (the
/// kernel's LZO documentation, <c>Documentation/staging/lzo.rst</c>), not from any
/// implementation of it, and checked against streams lzop itself produced.</para>
///
/// <para>The format is a sequence of instructions, each beginning with one byte
/// whose top bits choose how the rest is read. The awkward part is the state: how
/// many literals the previous instruction copied, held at 0..4, which changes what
/// a token below 16 means. A token of 0..15 is a long literal run when nothing was
/// just copied and a short match when something was.</para>
/// </remarks>
public static class Lzo1xDecompressor {

  /// <summary>
  /// Decompresses an LZO1X stream into a buffer of the size the container declared.
  /// </summary>
  /// <param name="data">The compressed data.</param>
  /// <param name="uncompressedSize">The size the caller expects, from the container.</param>
  /// <returns>The decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The stream is malformed.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int uncompressedSize) {
    var result = Run(data, uncompressedSize, out var produced);
    if (produced != uncompressedSize) ThrowSizeMismatch(produced, uncompressedSize);
    return result;
  }

  /// <summary>
  /// Decompresses a stream whose exact length is not known in advance, only a
  /// ceiling.
  /// </summary>
  /// <remarks>
  /// A squashfs metadata block is like this: the container says a block is at most
  /// eight kilobytes and never says how much of that it used, so insisting the
  /// output fill the buffer refuses every block that is shorter — which is nearly
  /// all of them.
  /// </remarks>
  public static byte[] DecompressUpTo(ReadOnlySpan<byte> data, int maxOutput) {
    var result = Run(data, maxOutput, out var produced);
    return produced == result.Length ? result : result.AsSpan(0, produced).ToArray();
  }

  private static byte[] Run(ReadOnlySpan<byte> data, int uncompressedSize, out int produced) {
    produced = 0;
    if (data.IsEmpty) return [];

    var output = new byte[uncompressedSize];
    var outPos = 0;
    var inPos = 0;

    // How many literals the previous instruction copied, held at four. It is what
    // tells a token under sixteen apart from a literal run.
    var state = 0;


    // ── The first byte is special ──────────────────────────────────────────
    // Seventeen announces a bitstream version and is skipped; anything above it
    // opens the stream with a literal run of that many bytes less seventeen.
    var first = data[0];
    if (first == 17 && data.Length >= 5) {
      // Seventeen opens a versioned stream, and the version byte follows it. Only
      // in a stream long enough to have one: seventeen is also the token of the
      // instruction that ends a stream, so the three bytes 0x11 0x00 0x00 on their
      // own are an empty stream and not a version announcement.
      inPos = 2;
    } else if (first > 17) {
      inPos = 1;
      var run = first - 17;
      CopyLiterals(data, ref inPos, output, ref outPos, run);
      state = Math.Min(4, run);
    }

    while (true) {
      var token = Next(data, ref inPos);

      if (token < 16) {
        if (state == 0) {
          // A long literal run: three plus the four bits, or the extended form.
          var length = token != 0 ? token + 3 : Extend(data, ref inPos, 18);
          CopyLiterals(data, ref inPos, output, ref outPos, length);
          state = 4;
          continue;
        }

        // A short match, and how far back it reaches depends on whether the
        // previous instruction copied a few literals or a whole run.
        var h = Next(data, ref inPos);
        if (state < 4) {
          CopyMatch(output, ref outPos, (h << 2) + (token >> 2) + 1, 2);
        } else {
          CopyMatch(output, ref outPos, (h << 2) + (token >> 2) + 2049, 3);
        }
        state = token & 3;
        CopyLiterals(data, ref inPos, output, ref outPos, state);
        continue;
      }

      if (token < 32) {
        // The long-distance form, and the one that ends the stream.
        var length = (token & 7) != 0 ? (token & 7) + 2 : Extend(data, ref inPos, 9);
        var low = Next(data, ref inPos);
        var high = Next(data, ref inPos);
        var pair = low | (high << 8);
        var distance = 16384 + ((token & 8) << 11) + (pair >> 2);

        // A distance of exactly the base means there was no match: this is the
        // end of the stream, and the three bytes 0x11 0x00 0x00 are how every
        // LZO1X stream finishes.
        if (distance == 16384) break;

        CopyMatch(output, ref outPos, distance, length);
        state = pair & 3;
        CopyLiterals(data, ref inPos, output, ref outPos, state);
        continue;
      }

      if (token < 64) {
        var length = (token & 31) != 0 ? (token & 31) + 2 : Extend(data, ref inPos, 33);
        var low = Next(data, ref inPos);
        var high = Next(data, ref inPos);
        var pair = low | (high << 8);

        CopyMatch(output, ref outPos, (pair >> 2) + 1, length);
        state = pair & 3;
        CopyLiterals(data, ref inPos, output, ref outPos, state);
        continue;
      }

      // The two short forms, which carry their distance in the token and one
      // following byte.
      int matchLength;
      int back;
      if (token < 128) {
        matchLength = 3 + ((token >> 5) & 1);
        back = ((token >> 2) & 7) + (Next(data, ref inPos) << 3) + 1;
      } else {
        matchLength = 5 + ((token >> 5) & 3);
        back = ((token >> 2) & 7) + (Next(data, ref inPos) << 3) + 1;
      }

      CopyMatch(output, ref outPos, back, matchLength);
      state = token & 3;
      CopyLiterals(data, ref inPos, output, ref outPos, state);
    }

    produced = outPos;
    return output;
  }

  private static byte Next(ReadOnlySpan<byte> data, ref int inPos) {
    if (inPos >= data.Length) ThrowTruncated();
    return data[inPos++];
  }

  /// <summary>
  /// A length that did not fit its bits carries on in the following bytes: every
  /// zero adds 255, and the first non-zero byte adds its own value.
  /// </summary>
  private static int Extend(ReadOnlySpan<byte> data, ref int inPos, int baseLength) {
    var length = baseLength;
    while (true) {
      var b = Next(data, ref inPos);
      if (b != 0) return length + b;
      length += 255;
    }
  }

  private static void CopyLiterals(ReadOnlySpan<byte> data, ref int inPos,
      byte[] output, ref int outPos, int count) {
    if (count < 0) ThrowTruncated();
    if (inPos + count > data.Length) ThrowTruncated();
    if (outPos + count > output.Length) ThrowOutputOverflow();

    data.Slice(inPos, count).CopyTo(output.AsSpan(outPos));
    inPos += count;
    outPos += count;
  }

  /// <summary>Copies a match out of what has already been written.</summary>
  /// <remarks>
  /// One byte at a time, because a match may reach into what it is writing — that
  /// overlap is how the format expresses a run.
  /// </remarks>
  private static void CopyMatch(byte[] output, ref int outPos, int distance, int length) {
    if (distance <= 0 || distance > outPos) ThrowInvalidDistance();
    if (outPos + length > output.Length) ThrowOutputOverflow();

    var from = outPos - distance;
    for (var i = 0; i < length; ++i) output[outPos + i] = output[from + i];
    outPos += length;
  }

  private static void ThrowTruncated() =>
    throw new InvalidDataException("LZO1X compressed data is truncated.");

  private static void ThrowInvalidDistance() =>
    throw new InvalidDataException("LZO1X compressed data contains an invalid back-reference distance.");

  private static void ThrowOutputOverflow() =>
    throw new InvalidDataException("LZO1X decompressed data exceeds the declared uncompressed size.");

  private static void ThrowSizeMismatch(int actual, int expected) =>
    throw new InvalidDataException($"LZO1X decompressed size mismatch: expected {expected} bytes, got {actual} bytes.");
}
