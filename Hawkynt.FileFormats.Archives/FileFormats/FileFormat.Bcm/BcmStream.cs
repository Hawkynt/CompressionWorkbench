// Converted from BCM 1.65 bcm.cpp (Apache-2.0).
// Copyright (C) 2008-2021 Ilya Muravyov
// See THIRD-PARTY-NOTICE.bcm.txt in this directory.
using Compression.Core.Checksums;
using Compression.Core.Transforms;

namespace FileFormat.Bcm;

/// <summary>
/// BCM by Ilya Muravyov, the open-source 1.x format (<c>BCM!</c>): blocks transformed by the
/// suffix-array Burrows-Wheeler Transform and coded bit by bit with a small context-mixing model
/// and a 32-bit binary arithmetic coder.
/// </summary>
/// <remarks>
/// <para>Layout: the 4-byte id <c>BCM!</c>, then one arithmetic-coded stream holding, per block,
/// its length and one-based BWT primary index as 32-bit values at probability 1/2 followed by its
/// transformed bytes; a zero length ends the blocks and the CRC-32 of the whole input follows.</para>
/// <para>BCM 1.10 to 1.65 write identical files. BCM 2.x writes a different format (<c>BCM2</c>)
/// whose source was never published; it is recognised and refused.</para>
/// </remarks>
public static class BcmStream {

  /// <summary>The id BCM 1.x writes first: "BCM!".</summary>
  internal static readonly byte[] Magic = "BCM!"u8.ToArray();

  /// <summary>The id BCM 2.x writes first: "BCM2".</summary>
  internal static readonly byte[] Magic2 = "BCM2"u8.ToArray();

  /// <summary>bcm's default block size, 16 MiB (<c>-b16</c>).</summary>
  public const int DefaultBlockSize = 1 << 24;

  // bcm takes -b# in MiB as an int: 2047 MiB is the largest it can express.
  internal const int MaximumBlockSize = 2047 << 20;

  /// <summary>Compresses with bcm's default 16 MiB blocks.</summary>
  public static void Compress(Stream input, Stream output) => Compress(input, output, DefaultBlockSize);

  /// <summary>Compresses with the given block size, as <c>bcm -b</c> does.</summary>
  public static void Compress(Stream input, Stream output, int blockSize) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize, MaximumBlockSize);

    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    output.Write(Magic);
    output.Write(CompressPayload(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), blockSize));
  }

  /// <summary>Decompresses a BCM 1.x file.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> id = stackalloc byte[4];
    input.ReadExactly(id);
    if (id.SequenceEqual(Magic2))
      throw new NotSupportedException("BCM: this file was written by BCM 2.x, whose format is unpublished; only the BCM 1.x format (\"BCM!\") is supported.");
    if (!id.SequenceEqual(Magic))
      throw new InvalidDataException("Not a BCM stream (bad magic).");

    DecompressPayload(input, output);
  }

  /// <summary>The coded stream after the id: blocks, end marker and CRC-32, flushed.</summary>
  internal static byte[] CompressPayload(ReadOnlySpan<byte> data, int blockSize) {
    // bcm shrinks the block size to the file size before it reads anything.
    if (blockSize > data.Length)
      blockSize = data.Length;

    using var output = new MemoryStream();
    var model = new Model(output, null);
    var crc = new Crc32();
    for (var offset = 0; offset < data.Length; offset += blockSize) {
      var block = data.Slice(offset, Math.Min(blockSize, data.Length - offset));
      crc.Update(block);
      var (transformed, primaryIndex) = SuffixBurrowsWheelerTransform.Forward(block);
      model.Put32((uint)block.Length);
      model.Put32((uint)primaryIndex);
      foreach (var value in transformed)
        model.Put(value);
    }

    model.Put32(0);
    model.Put32(crc.Value);
    model.Flush();
    return output.ToArray();
  }

  /// <summary>Decodes the coded stream after the id.</summary>
  internal static void DecompressPayload(Stream input, Stream output) {
    var model = new Model(null, input);
    model.Init();
    var crc = new Crc32();
    var blockSize = 0;
    int n;
    while ((n = (int)model.Get32()) > 0) {
      if (blockSize == 0) {
        if (n > MaximumBlockSize)
          throw new InvalidDataException($"BCM: block size {n} is larger than bcm can write.");
        blockSize = n;
      }

      var primaryIndex = (int)model.Get32();
      if (n > blockSize || primaryIndex < 1 || primaryIndex > n)
        throw new InvalidDataException("BCM: corrupt input (block size or BWT index out of range).");

      var transformed = new byte[n];
      for (var i = 0; i < n; ++i)
        transformed[i] = (byte)model.Get();

      var block = SuffixBurrowsWheelerTransform.Inverse(transformed, primaryIndex);
      crc.Update(block);
      output.Write(block);
    }

    if (n < 0)
      throw new InvalidDataException("BCM: corrupt input (negative block size).");
    if (model.Get32() != crc.Value)
      throw new InvalidDataException("BCM: CRC error.");
  }

  /// <summary>
  /// bcm's coder and model: a 32-bit carryless binary arithmetic coder, three adaptive bit
  /// counters (order 0, the previous byte, the byte before it) and an SSE stage interpolated
  /// over 17 buckets and keyed on whether the last three bytes were a run.
  /// </summary>
  private sealed class Model(Stream? output, Stream? input) {
    private uint _low;
    private uint _high = 0xFFFFFFFF;
    private uint _code;

    private readonly ushort[] _counter0 = InitialCounters(256);
    private readonly ushort[] _counter1 = InitialCounters(256 * 256);
    private readonly ushort[] _counter2 = InitialSse();
    private int _run;
    private int _c1;
    private int _c2;

    private static ushort[] InitialCounters(int count) {
      var counters = new ushort[count];
      Array.Fill(counters, (ushort)(1 << 15));
      return counters;
    }

    private static ushort[] InitialSse() {
      var counters = new ushort[2 * 256 * 17];
      for (var i = 0; i < 2 * 256; ++i)
        for (var k = 0; k <= 16; ++k)
          counters[i * 17 + k] = (ushort)((k << 12) - (k == 16 ? 1 : 0));
      return counters;
    }

    private static void Update1(ushort[] counters, int index, int rate) => counters[index] += (ushort)((counters[index] ^ 0xFFFF) >> rate);

    private static void Update0(ushort[] counters, int index, int rate) => counters[index] -= (ushort)(counters[index] >> rate);

    public void Flush() {
      for (var i = 0; i < 4; ++i) {
        output!.WriteByte((byte)(_low >> 24));
        _low <<= 8;
      }
    }

    public void Init() {
      for (var i = 0; i < 4; ++i)
        _code = _code << 8 | ReadByte();
    }

    // getc: EOF is -1, which ORs in as all ones.
    private uint ReadByte() {
      var value = input!.ReadByte();
      return value < 0 ? 0xFFFFFFFF : (uint)value;
    }

    private void EncodeBit(int bit, uint p, int pLog) {
      var mid = _low + (uint)(((ulong)(_high - _low) * p) >> pLog);
      if (bit != 0)
        _high = mid;
      else
        _low = mid + 1;

      while ((_low ^ _high) < 1u << 24) {
        output!.WriteByte((byte)(_low >> 24));
        _low <<= 8;
        _high = _high << 8 | 255;
      }
    }

    private int DecodeBit(uint p, int pLog) {
      var mid = _low + (uint)(((ulong)(_high - _low) * p) >> pLog);
      var bit = _code <= mid ? 1 : 0;
      if (bit != 0)
        _high = mid;
      else
        _low = mid + 1;

      while ((_low ^ _high) < 1u << 24) {
        _low <<= 8;
        _high = _high << 8 | 255;
        _code = _code << 8 | ReadByte();
      }

      return bit;
    }

    public void Put32(uint x) {
      for (var i = 1u << 31; i > 0; i >>= 1)
        EncodeBit((x & i) != 0 ? 1 : 0, 1, 1);
    }

    public uint Get32() {
      var x = 0u;
      for (var i = 0; i < 32; ++i)
        x += x + (uint)DecodeBit(1, 1);
      return x;
    }

    // The probability of a one at this bit, out of 2^18, and the SSE cells it interpolated.
    private uint Predict(int ctx, int f, out int sse) {
      int p0 = _counter0[ctx];
      int p1 = _counter1[_c1 << 8 | ctx];
      int p2 = _counter1[_c2 << 8 | ctx];
      var p = ((p0 + p1) * 7 + p2 + p2) >> 4;

      var j = p >> 12;
      sse = (f * 256 + ctx) * 17 + j;
      int x1 = _counter2[sse];
      int x2 = _counter2[sse + 1];
      var ssep = x1 + (((x2 - x1) * (p & 4095)) >> 12);
      return (uint)(p + ssep + ssep + ssep);
    }

    private void Update(int ctx, int sse, int bit) {
      if (bit != 0) {
        Update1(_counter0, ctx, 2);
        Update1(_counter1, _c1 << 8 | ctx, 4);
        Update1(_counter2, sse, 6);
        Update1(_counter2, sse + 1, 6);
      } else {
        Update0(_counter0, ctx, 2);
        Update0(_counter1, _c1 << 8 | ctx, 4);
        Update0(_counter2, sse, 6);
        Update0(_counter2, sse + 1, 6);
      }
    }

    private void EndByte(int ctx) {
      _c2 = _c1;
      _c1 = ctx - 256;
      _run = _c1 == _c2 ? _run + 1 : 0;
    }

    public void Put(int c) {
      var f = _run > 2 ? 1 : 0;
      var ctx = 1;
      for (var i = 128; i > 0; i >>= 1) {
        var p = Predict(ctx, f, out var sse);
        var bit = (c & i) != 0 ? 1 : 0;
        EncodeBit(bit, p, 18);
        Update(ctx, sse, bit);
        ctx += ctx + bit;
      }

      EndByte(ctx);
    }

    public int Get() {
      var f = _run > 2 ? 1 : 0;
      var ctx = 1;
      while (ctx < 256) {
        var p = Predict(ctx, f, out var sse);
        var bit = DecodeBit(p, 18);
        Update(ctx, sse, bit);
        ctx += ctx + bit;
      }

      EndByte(ctx);
      return _c1;
    }
  }
}
