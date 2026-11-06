using Compression.Core.Checksums;

namespace FileFormat.StuffIt;

/// <summary>
/// Encoder for StuffIt method 15 (Arsenic), the inverse of <see cref="StuffItArsenicDecoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each block takes the input through four stages:
/// <list type="number">
///   <item>The final run-length stage: four equal bytes are followed by a count of 0–255 further repeats.</item>
///   <item>The Burrows-Wheeler transform, with the row of the unrotated block as its index.</item>
///   <item>Move-to-front. Runs of index 0 are written as bijective base-2 digits (selector 0 = 1, selector 1 = 2),
///   index 1 as selector 2, and larger indices as a bit-length class (selector 3–9) plus their value within the class.</item>
///   <item>Adaptive arithmetic coding with the decoder's 26-bit precision and its models.</item>
/// </list>
/// </para>
/// <para>
/// The stream starts with "As", the block size exponent less 9 in four bits and a flag that is 0
/// while blocks follow. Each block carries a randomisation flag (always 0 here) and the BWT index,
/// and ends with selector 10 and a flag that is set only on the last block, which is followed by
/// the CRC-32 of the whole output. Multi-bit fields go through the binary model least significant
/// bit first. The arithmetic coder keeps the decoder's 26-bit window; a carry ripples back into
/// bits already written, and the final window is flushed in full.
/// </para>
/// </remarks>
internal static class StuffItArsenicEncoder {
  private const int NumBits = 26;
  private const long Window = 1L << NumBits;
  private const int One = 1 << (NumBits - 1);
  private const int Half = 1 << (NumBits - 2);

  /// <summary>Default block size exponent: 128 KiB blocks.</summary>
  public const int DefaultBlockBits = 17;

  /// <summary>The smallest block size exponent that holds <paramref name="length"/> bytes, between 9 and <see cref="DefaultBlockBits"/>.</summary>
  public static int BlockBitsFor(int length) {
    var bits = 9;
    while (bits < DefaultBlockBits && (1 << bits) < length + length / 8 + 16) ++bits;
    return bits;
  }

  private sealed class Encoder {
    private readonly List<byte> _bits = []; // one entry per output bit, most significant first
    private long _low;
    private int _range = One;

    public void Encode(StuffItArsenicModel model, int symbol) {
      var index = symbol - model.First;
      int cumulative = 0;
      for (var i = 0; i < index; ++i) cumulative += model.Freq(i);
      var size = model.Freq(index);
      var factor = this._range / model.Total;
      var lowIncrement = (long)factor * cumulative;
      this._low += lowIncrement;
      if (cumulative + size == model.Total) this._range -= (int)lowIncrement;
      else this._range = size * factor;
      if (this._low >= Window) {
        this._low -= Window;
        this.Carry();
      }
      while (this._range <= Half) {
        this._bits.Add((byte)((this._low >> (NumBits - 1)) & 1));
        this._low = (this._low << 1) & (Window - 1);
        this._range <<= 1;
      }
      model.Increase(index);
    }

    public void EncodeBits(StuffItArsenicModel model, int value, int bits) {
      for (var i = 0; i < bits; ++i) this.Encode(model, (value >> i) & 1);
    }

    private void Carry() {
      var i = this._bits.Count - 1;
      while (i >= 0 && this._bits[i] == 1) this._bits[i--] = 0;
      if (i < 0) throw new InvalidOperationException("Arsenic arithmetic coder carried past the first bit.");
      this._bits[i] = 1;
    }

    public byte[] Finish() {
      for (var i = NumBits - 1; i >= 0; --i) this._bits.Add((byte)((this._low >> i) & 1));
      var output = new byte[(this._bits.Count + 7) / 8];
      for (var i = 0; i < this._bits.Count; ++i)
        if (this._bits[i] != 0) output[i >> 3] |= (byte)(0x80 >> (i & 7));
      return output;
    }
  }

  /// <summary>Compresses <paramref name="data"/> into an Arsenic stream with 2^<paramref name="blockBits"/>-byte blocks.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, int blockBits = DefaultBlockBits) {
    if (blockBits is < 9 or > 24) throw new ArgumentOutOfRangeException(nameof(blockBits));
    if (data.Length == 0) throw new ArgumentException("Arsenic has no encoding of empty data.", nameof(data));
    var blockSize = 1 << blockBits;

    var enc = new Encoder();
    var initial = new StuffItArsenicModel(0, 1, 1, 256);
    var selector = new StuffItArsenicModel(0, 10, 8, 1024);
    StuffItArsenicModel[] mtfModels = [
      new(2, 3, 8, 1024), new(4, 7, 4, 1024), new(8, 15, 4, 1024), new(16, 31, 4, 1024),
      new(32, 63, 2, 1024), new(64, 127, 2, 1024), new(128, 255, 1, 1024),
    ];

    enc.EncodeBits(initial, 'A', 8);
    enc.EncodeBits(initial, 's', 8);
    enc.EncodeBits(initial, blockBits - 9, 4);
    enc.Encode(initial, 0); // blocks follow

    var position = 0;
    var block = new byte[blockSize];
    while (position < data.Length) {
      var length = RunLengthBlock(data, ref position, block);
      var lastColumn = new byte[length];
      var index = Bwt(block.AsSpan(0, length), lastColumn);

      enc.Encode(initial, 0); // not randomised
      enc.EncodeBits(initial, index, blockBits);
      WriteSymbols(enc, lastColumn, selector, mtfModels);
      enc.Encode(selector, 10);
      selector.Reset();
      foreach (var m in mtfModels) m.Reset();

      var last = position >= data.Length;
      enc.Encode(initial, last ? 1 : 0);
      if (last) enc.EncodeBits(initial, unchecked((int)Crc32.Compute(data)), 32);
    }
    return enc.Finish();
  }

  /// <summary>
  /// Fills <paramref name="block"/> with the run-length form of the input from
  /// <paramref name="position"/>, stopping before a token would overflow it; runs never
  /// continue across blocks because the decoder restarts the stage at every block.
  /// </summary>
  private static int RunLengthBlock(ReadOnlySpan<byte> data, ref int position, byte[] block) {
    var n = 0;
    while (position < data.Length) {
      var b = data[position];
      var run = 1;
      while (position + run < data.Length && data[position + run] == b && run < 4 + 255) ++run;
      if (run < 4) {
        if (n + run > block.Length) break;
        for (var k = 0; k < run; ++k) block[n++] = b;
      } else {
        if (n + 5 > block.Length) break;
        block[n++] = b;
        block[n++] = b;
        block[n++] = b;
        block[n++] = b;
        block[n++] = (byte)(run - 4);
      }
      position += run;
    }
    if (n == 0) throw new InvalidOperationException("Arsenic block is too small for one run.");
    return n;
  }

  /// <summary>Burrows-Wheeler transform of <paramref name="block"/>; returns the row of the unrotated block.</summary>
  internal static int Bwt(ReadOnlySpan<byte> block, Span<byte> lastColumn) {
    var n = block.Length;
    var order = new int[n];
    var rank = new int[n];
    var next = new int[n];
    for (var i = 0; i < n; ++i) {
      order[i] = i;
      rank[i] = block[i];
    }
    for (var k = 1; ; k <<= 1) {
      var step = k % n;
      var keys = new long[n];
      for (var i = 0; i < n; ++i) keys[i] = ((long)rank[i] << 32) | (uint)rank[(i + step) % n];
      Array.Sort(order, (a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : a.CompareTo(b));
      next[order[0]] = 0;
      for (var i = 1; i < n; ++i) next[order[i]] = next[order[i - 1]] + (keys[order[i]] != keys[order[i - 1]] ? 1 : 0);
      (rank, next) = (next, rank);
      if (rank[order[n - 1]] == n - 1 || k >= n) break;
    }
    var index = -1;
    for (var i = 0; i < n; ++i) {
      var start = order[i];
      lastColumn[i] = block[(start + n - 1) % n];
      if (start == 0) index = i;
    }
    return index;
  }

  private static void WriteSymbols(Encoder enc, ReadOnlySpan<byte> lastColumn, StuffItArsenicModel selector, StuffItArsenicModel[] mtfModels) {
    Span<byte> mtf = stackalloc byte[256];
    for (var i = 0; i < 256; ++i) mtf[i] = (byte)i;
    var zeros = 0;
    foreach (var b in lastColumn) {
      var index = mtf.IndexOf(b);
      if (index == 0) {
        ++zeros;
        continue;
      }
      WriteZeroRun(enc, selector, zeros);
      zeros = 0;
      mtf[..index].CopyTo(mtf[1..(index + 1)]);
      mtf[0] = b;
      if (index == 1) {
        enc.Encode(selector, 2);
        continue;
      }
      var cls = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)index); // 1..7
      enc.Encode(selector, cls + 2);
      enc.Encode(mtfModels[cls - 1], index);
    }
    WriteZeroRun(enc, selector, zeros);
  }

  private static void WriteZeroRun(Encoder enc, StuffItArsenicModel selector, int count) {
    while (count > 0) {
      if ((count & 1) == 1) {
        enc.Encode(selector, 0);
        count = (count - 1) >> 1;
      } else {
        enc.Encode(selector, 1);
        count = (count - 2) >> 1;
      }
    }
  }
}
