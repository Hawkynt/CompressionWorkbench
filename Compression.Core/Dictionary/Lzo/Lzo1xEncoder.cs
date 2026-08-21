namespace Compression.Core.Dictionary.Lzo;

/// <summary>
/// Writes a genuine LZO1X stream — one lzop, the kernel and squashfs all read.
/// </summary>
/// <remarks>
/// <para>Implemented from the published description of the stream format (the
/// kernel's LZO documentation), not from any implementation of it, and checked by
/// handing what it writes to lzop.</para>
///
/// <para>One rule of the format shapes the whole thing: a literal run written as
/// its own instruction is at least four bytes long, because its length is three
/// plus the token's low bits. Runs of one to three bytes exist only as the tail of
/// a match instruction, carried in its two state bits — so a match is written
/// first and those bits are set afterwards, once it is known how many literals
/// follow it. Skipping such matches instead was simpler and gave up most of the
/// compression: nearly twice the size of what lzop writes.</para>
///
/// <para>Three of the format's match instructions are used: the two compact forms
/// for short matches close behind, and the sixteen-kilobyte form for everything
/// else. The two longest-distance forms are left out, which costs a little on
/// large inputs and removes a good deal of the opportunity to be subtly wrong.
/// </para>
/// </remarks>
public static class Lzo1xEncoder {

  private const int MinMatch = 3;
  private const int MaxDistance = 16384;
  private const int HashBits = 14;
  private const int HashSize = 1 << HashBits;

  /// <summary>Compresses into the LZO1X stream format.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var output = new List<byte>(data.Length / 2 + 64);

    // Nothing in, nothing out. A container never asks for a zero-length block
    // back, and a stream holding only the end marker would have to be told apart
    // from a version announcement, which shares its first byte.
    if (data.Length == 0) return [];

    // One to three bytes cannot be a literal run of their own, and the opening
    // instruction is the only place a run that short can be said outright.
    if (data.Length < 4) {
      output.Add((byte)(data.Length + 17));
      for (var i = 0; i < data.Length; ++i) output.Add(data[i]);
      WriteEndOfStream(output);
      return [.. output];
    }

    var hashTable = new int[HashSize];
    Array.Fill(hashTable, -1);

    var literalStart = 0;
    var pos = 0;

    // Where the last match instruction keeps its state bits, so a short literal
    // run that follows can be folded into it.
    var stateBitsAt = -1;

    // Stop looking for matches early enough that the bytes left over are a run
    // this can express: never one, two or three.
    var searchLimit = data.Length - MinMatch;

    while (pos < searchLimit) {
      var hash = Hash(data, pos);
      var candidate = hashTable[hash];
      hashTable[hash] = pos;

      var distance = candidate < 0 ? 0 : pos - candidate;
      if (distance <= 0 || distance > MaxDistance || !Matches(data, candidate, pos)) {
        ++pos;
        continue;
      }

      // How far the match runs, stopping short of the end so that what follows
      // is either nothing or a run of at least four.
      var length = MinMatch;
      var maxLength = data.Length - pos;
      while (length < maxLength && data[candidate + length] == data[pos + length]) ++length;

      var trailing = data.Length - (pos + length);
      if (trailing is > 0 and < 4) length -= 4 - trailing;
      if (length < MinMatch) { ++pos; continue; }

      var runLength = pos - literalStart;
      if (runLength >= 4) {
        WriteLiteralRun(output, data, literalStart, runLength);
      } else if (runLength > 0) {
        // One to three literals ride in the previous match's state bits. With no
        // previous match there is nowhere to put them, so the match is skipped
        // and they go out as part of a longer run later.
        if (stateBitsAt < 0) { ++pos; continue; }

        output[stateBitsAt] |= (byte)runLength;
        for (var i = 0; i < runLength; ++i) output.Add(data[literalStart + i]);
      }

      stateBitsAt = WriteMatch(output, distance, length);

      // Every byte the match covered goes into the table too, so a later match
      // can start inside it.
      for (var i = pos + 1; i < pos + length && i < searchLimit; ++i)
        hashTable[Hash(data, i)] = i;

      pos += length;
      literalStart = pos;
    }

    var tail = data.Length - literalStart;
    if (tail >= 4) {
      WriteLiteralRun(output, data, literalStart, tail);
    } else if (tail > 0) {
      if (stateBitsAt < 0)
        throw new InvalidOperationException(
          $"LZO1X: {tail} trailing literal(s) with no match to carry them.");

      output[stateBitsAt] |= (byte)tail;
      for (var i = 0; i < tail; ++i) output.Add(data[literalStart + i]);
    }
    WriteEndOfStream(output);
    return [.. output];
  }

  private static int Hash(ReadOnlySpan<byte> data, int at) {
    var v = (data[at] << 16) | (data[at + 1] << 8) | data[at + 2];
    return (v * 0x1E35A7BD >> (24 - HashBits)) & (HashSize - 1);
  }

  private static bool Matches(ReadOnlySpan<byte> data, int candidate, int pos) =>
    data[candidate] == data[pos]
    && data[candidate + 1] == data[pos + 1]
    && data[candidate + 2] == data[pos + 2];

  /// <summary>
  /// A run of literal bytes as its own instruction, which is four bytes at least.
  /// </summary>
  private static void WriteLiteralRun(List<byte> output, ReadOnlySpan<byte> data, int start, int length) {
    if (length == 0) return;
    if (length < 4)
      throw new InvalidOperationException(
        $"LZO1X: a literal run of its own is four bytes or more; asked for {length}.");

    if (length <= 18) {
      output.Add((byte)(length - 3));
    } else {
      output.Add(0);
      var remainder = length - 18;
      while (remainder > 255) { output.Add(0); remainder -= 255; }
      output.Add((byte)remainder);
    }

    for (var i = 0; i < length; ++i) output.Add(data[start + i]);
  }

  /// <summary>
  /// A match reaching back up to sixteen kilobytes, with no literals after it.
  /// </summary>
  private static int WriteMatch(List<byte> output, int distance, int length) {
    if (distance is < 1 or > MaxDistance)
      throw new InvalidOperationException($"LZO1X: a distance of {distance} is outside this form.");
    if (length < MinMatch)
      throw new InvalidOperationException($"LZO1X: a match of {length} is too short.");

    // The two compact forms: a short match close behind fits in two bytes, with
    // its state bits in the token itself.
    var back = distance - 1;
    if (back < 2048 && length is >= 3 and <= 8) {
      var token = length <= 4
        ? 0x40 | ((length - 3) << 5) | ((back & 7) << 2)
        : 0x80 | ((length - 5) << 5) | ((back & 7) << 2);
      var at = output.Count;
      output.Add((byte)token);
      output.Add((byte)(back >> 3));
      return at;
    }

    if (length <= 33) {
      output.Add((byte)(0x20 | (length - 2)));
    } else {
      output.Add(0x20);
      var remainder = length - 33;
      while (remainder > 255) { output.Add(0); remainder -= 255; }
      output.Add((byte)remainder);
    }

    // The pair holds the distance less one; its two low bits are the state bits.
    var pair = back << 2;
    var pairAt = output.Count;
    output.Add((byte)(pair & 0xFF));
    output.Add((byte)(pair >> 8));
    return pairAt;
  }

  /// <summary>The three bytes every LZO1X stream ends with.</summary>
  /// <remarks>
  /// A long-distance match instruction whose distance works out to exactly the
  /// base of that form, which names nothing and so means the stream is over.
  /// </remarks>
  private static void WriteEndOfStream(List<byte> output) {
    output.Add(0x11);
    output.Add(0x00);
    output.Add(0x00);
  }
}
