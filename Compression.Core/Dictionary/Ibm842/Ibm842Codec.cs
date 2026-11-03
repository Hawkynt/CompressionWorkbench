namespace Compression.Core.Dictionary.Ibm842;

/// <summary>
/// IBM 842 as the Linux kernel's software implementation (<c>lib/842</c>) reads and writes it,
/// implemented from <c>docs/IBM842-ON-DISK.md</c>: 5-bit templates over 8-byte groups with 2-, 4-
/// and 8-byte ring-buffer indexes, repeat, zeros and short-data codes, an end code, a CRC-32 and
/// padding to a multiple of 8 bytes.
/// </summary>
public static class Ibm842Codec {
  private const int OpBits = 5;
  private const int OpRepeat = 0x1B;
  private const int OpZeros = 0x1C;
  private const int OpShortData = 0x1D;
  private const int OpEnd = 0x1E;
  private const int OpsMax = 0x1A;
  private const int RepeatBits = 6;
  private const int ShortDataBits = 3;

  // Actions: high nibble 1 = index, 2 = data, 4 = no-op; low nibble = byte count. Copied from
  // decomp_ops in Linux lib/842/842_decompress.c.
  private const byte D2 = 0x22, D4 = 0x24, D8 = 0x28, I2 = 0x12, I4 = 0x14, I8 = 0x18, N0 = 0x40;

  private static readonly byte[][] Templates = [
    [D8, N0, N0, N0], [D4, D2, I2, N0], [D4, I2, D2, N0], [D4, I2, I2, N0], [D4, I4, N0, N0],
    [D2, I2, D4, N0], [D2, I2, D2, I2], [D2, I2, I2, D2], [D2, I2, I2, I2], [D2, I2, I4, N0],
    [I2, D2, D4, N0], [I2, D4, I2, N0], [I2, D2, I2, D2], [I2, D2, I2, I2], [I2, D2, I4, N0],
    [I2, I2, D4, N0], [I2, I2, D2, I2], [I2, I2, I2, D2], [I2, I2, I2, I2], [I2, I2, I4, N0],
    [I4, D4, N0, N0], [I4, D2, I2, N0], [I4, I2, D2, N0], [I4, I2, I2, N0], [I4, I4, N0, N0],
    [I8, N0, N0, N0],
  ];

  private static int IndexBits(int size) => size == 4 ? 9 : 8;

  private static int RingSize(int size) => size << IndexBits(size);

  /// <summary>Compresses <paramref name="data"/> into an 842 stream.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var writer = new BitWriter(data.Length / 2 + 32);
    var latest = new[] { new Dictionary<ulong, int>(), new Dictionary<ulong, int>(), new Dictionary<ulong, int>() };
    var groups = data.Length / 8;
    var g = 0;
    while (g < groups) {
      var position = g * 8;
      var group = data.Slice(position, 8);
      if (g > 0 && group.SequenceEqual(data.Slice(position - 8, 8))) {
        var run = 1;
        while (run < 64 && g + run < groups && data.Slice(position + run * 8, 8).SequenceEqual(group))
          ++run;
        writer.Write(OpRepeat, OpBits);
        writer.Write((ulong)(run - 1), RepeatBits);
        for (var r = 0; r < run; ++r)
          Remember(latest, data, position + r * 8);
        g += run;
        continue;
      }

      if (group.IndexOfAnyExcept((byte)0) < 0)
        writer.Write(OpZeros, OpBits);
      else
        WriteTemplate(writer, latest, data, position);
      Remember(latest, data, position);
      ++g;
    }

    var tail = data.Length - groups * 8;
    if (tail > 0) {
      writer.Write(OpShortData, OpBits);
      writer.Write((ulong)tail, ShortDataBits);
      for (var i = groups * 8; i < data.Length; ++i)
        writer.Write(data[i], 8);
    }

    writer.Write(OpEnd, OpBits);
    writer.Write(Crc32(data), 32);
    return writer.ToPaddedArray();
  }

  /// <summary>Chooses the cheapest template whose indexes all resolve, and writes it.</summary>
  private static void WriteTemplate(BitWriter writer, Dictionary<ulong, int>[] latest, ReadOnlySpan<byte> data, int position) {
    var bestCost = int.MaxValue;
    var best = 0;
    Span<int> indexes = stackalloc int[4];
    Span<int> bestIndexes = stackalloc int[4];
    for (var t = 0; t < OpsMax; ++t) {
      var cost = OpBits;
      var offset = 0;
      var feasible = true;
      for (var a = 0; a < 4 && feasible; ++a) {
        var action = Templates[t][a];
        var size = action & 0x0F;
        switch (action & 0x70) {
          case 0x20:
            cost += size * 8;
            break;
          case 0x10:
            var index = Find(latest, data, position, position + offset, size);
            if (index < 0)
              feasible = false;
            else {
              indexes[a] = index;
              cost += IndexBits(size);
            }

            break;
        }

        offset += size;
      }

      if (feasible && cost < bestCost) {
        bestCost = cost;
        best = t;
        indexes.CopyTo(bestIndexes);
      }
    }

    writer.Write((ulong)best, OpBits);
    var at = position;
    for (var a = 0; a < 4; ++a) {
      var action = Templates[best][a];
      var size = action & 0x0F;
      switch (action & 0x70) {
        case 0x20:
          writer.Write(ReadBigEndian(data.Slice(at, size)), size * 8);
          break;
        case 0x10:
          writer.Write((ulong)bestIndexes[a], IndexBits(size));
          break;
      }

      at += size;
    }
  }

  /// <summary>The ring index of the latest equal granule a reader at <paramref name="groupStart"/> can still reach, or -1.</summary>
  private static int Find(Dictionary<ulong, int>[] latest, ReadOnlySpan<byte> data, int groupStart, int at, int size) {
    var table = latest[size == 2 ? 0 : size == 4 ? 1 : 2];
    if (!table.TryGetValue(ReadBigEndian(data.Slice(at, size)), out var q))
      return -1;
    if (q < groupStart - RingSize(size) || q + size > groupStart)
      return -1;
    return q % RingSize(size) / size;
  }

  private static void Remember(Dictionary<ulong, int>[] latest, ReadOnlySpan<byte> data, int position) {
    for (var i = 0; i < 8; i += 2)
      latest[0][ReadBigEndian(data.Slice(position + i, 2))] = position + i;
    for (var i = 0; i < 8; i += 4)
      latest[1][ReadBigEndian(data.Slice(position + i, 4))] = position + i;
    latest[2][ReadBigEndian(data.Slice(position, 8))] = position;
  }

  private static ulong ReadBigEndian(ReadOnlySpan<byte> bytes) {
    ulong value = 0;
    foreach (var b in bytes)
      value = value << 8 | b;
    return value;
  }

  /// <summary>Decompresses an 842 stream.</summary>
  /// <exception cref="InvalidDataException">The stream is truncated, malformed or fails its CRC.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    var reader = new BitReader(data);
    var output = new List<byte>(data.Length * 2);
    while (true) {
      var op = (int)reader.Read(OpBits);
      if (op == OpEnd)
        break;
      switch (op) {
        case OpRepeat: {
          var count = (int)reader.Read(RepeatBits) + 1;
          if (output.Count < 8)
            throw new InvalidDataException("842 repeat before any output.");
          for (var r = 0; r < count; ++r)
            for (var i = 0; i < 8; ++i)
              output.Add(output[^8]);
          break;
        }
        case OpZeros:
          for (var i = 0; i < 8; ++i)
            output.Add(0);
          break;
        case OpShortData: {
          var count = (int)reader.Read(ShortDataBits);
          if (count == 0)
            throw new InvalidDataException("842 short data of zero bytes.");
          for (var i = 0; i < count; ++i)
            output.Add((byte)reader.Read(8));
          break;
        }
        case < OpsMax:
          DecodeTemplate(op, ref reader, output);
          break;
        default:
          throw new InvalidDataException($"842 operation 0x{op:X2} is undefined.");
      }
    }

    var crc = (uint)reader.Read(32);
    var result = output.ToArray();
    if (crc != Crc32(result))
      throw new InvalidDataException("842 CRC mismatch.");
    return result;
  }

  private static void DecodeTemplate(int op, ref BitReader reader, List<byte> output) {
    foreach (var action in Templates[op]) {
      var size = action & 0x0F;
      switch (action & 0x70) {
        case 0x20: {
          var value = reader.Read(size * 8);
          for (var i = size - 1; i >= 0; --i)
            output.Add((byte)(value >> (i * 8)));
          break;
        }
        case 0x10: {
          var index = (long)reader.Read(IndexBits(size));
          long ring = RingSize(size);
          long total = output.Count - output.Count % 8;
          var offset = index * size;
          if (total > ring) {
            var section = total - total % ring;
            if (offset >= total - section)
              section -= ring;
            offset += section;
          }

          if (offset + size > total)
            throw new InvalidDataException("842 index points past the output.");
          for (var i = 0; i < size; ++i)
            output.Add(output[(int)offset + i]);
          break;
        }
      }
    }
  }

  /// <summary>CRC-32, polynomial 0x04C11DB7, not reflected, initial value 0, no final XOR (Linux <c>crc32_be(0, …)</c>).</summary>
  private static uint Crc32(ReadOnlySpan<byte> data) {
    var crc = 0u;
    foreach (var b in data) {
      crc ^= (uint)b << 24;
      for (var i = 0; i < 8; ++i)
        crc = (crc & 0x80000000u) != 0 ? crc << 1 ^ 0x04C11DB7u : crc << 1;
    }

    return crc;
  }

  private sealed class BitWriter(int capacity) {
    private readonly List<byte> _bytes = new(capacity);
    private ulong _buffer;
    private int _count;

    public void Write(ulong value, int bits) {
      for (var i = bits - 1; i >= 0; --i) {
        this._buffer = this._buffer << 1 | (value >> i & 1);
        if (++this._count == 8) {
          this._bytes.Add((byte)this._buffer);
          this._buffer = 0;
          this._count = 0;
        }
      }
    }

    public byte[] ToPaddedArray() {
      if (this._count > 0)
        this._bytes.Add((byte)(this._buffer << (8 - this._count)));
      while (this._bytes.Count % 8 != 0)
        this._bytes.Add(0);
      return [.. this._bytes];
    }
  }

  private ref struct BitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private long _bit;

    public ulong Read(int bits) {
      if (this._bit + bits > (long)this._data.Length * 8)
        throw new InvalidDataException("842 stream is truncated.");
      ulong value = 0;
      for (var i = 0; i < bits; ++i, ++this._bit)
        value = value << 1 | (uint)(this._data[(int)(this._bit >> 3)] >> (7 - (int)(this._bit & 7)) & 1);
      return value;
    }
  }
}
