// DiskDoubler's method 2/5 splay-tree coder, its Stac LZS variant (method 7) and the delta step,
// converted from XADMaster (https://github.com/MacPaw/XADMaster: XADDiskDoublerMethod2Handle.m,
// XADStacLZSHandle.m, XADDeltaHandle.m; LGPL-2.1-or-later, Copyright (c) 2017-present MacPaw Way Ltd.
// and the original The Unarchiver authors). See THIRD-PARTY-NOTICE.XADMaster.txt. The encoders are our
// own, written against these decoders.

namespace FileFormat.DiskDoubler;

/// <summary>DiskDoubler's adaptive splay-tree coder (methods 2 and 5), Stac LZS (method 7) and delta step.</summary>
internal static class DiskDoublerCodecs {
  #region Splay trees (methods 2 and 5)

  /// <summary>
  /// A set of splay trees over 256 leaves; each byte is coded in the tree the
  /// previous byte selects (byte mod tree count), and that tree is splayed toward the byte.
  /// Bits are read most significant first.
  /// </summary>
  private sealed class SplayTrees {
    private readonly byte[][] _parents;
    private readonly ushort[][] _left;
    private readonly ushort[][] _right;
    private readonly int _count;
    public int Current;

    public SplayTrees(int count) {
      this._count = count;
      this._parents = new byte[count][];
      this._left = new ushort[count][];
      this._right = new ushort[count][];
      for (var t = 0; t < count; ++t) {
        var parents = this._parents[t] = new byte[512];
        var left = this._left[t] = new ushort[256];
        var right = this._right[t] = new ushort[256];
        for (var j = 0; j < 256; ++j) {
          parents[2 * j] = (byte)j;
          parents[2 * j + 1] = (byte)j;
          left[j] = (ushort)(j * 2);
          right[j] = (ushort)(j * 2 + 1);
        }
      }
    }

    public int Child(int node, int bit) => bit == 1 ? this._right[this.Current][node] : this._left[this.Current][node];

    /// <summary>The bits from the root to <paramref name="value"/>'s leaf in the current tree.</summary>
    public void PathTo(int value, List<int> bits) {
      bits.Clear();
      var parents = this._parents[this.Current];
      var right = this._right[this.Current];
      for (var node = value + 0x100; node != 1;) {
        int parent = parents[node];
        bits.Add(right[parent] == node ? 1 : 0);
        node = parent;
      }
      bits.Reverse();
    }

    public void Update(int value) {
      var parents = this._parents[this.Current];
      var left = this._left[this.Current];
      var right = this._right[this.Current];
      var node = value + 0x100;
      while (true) {
        int parent = parents[node];
        if (parent == 1)
          break;
        int grandparent = parents[parent];
        int uncle = left[grandparent];
        if (uncle == parent) {
          uncle = right[grandparent];
          right[grandparent] = (ushort)node;
        } else
          left[grandparent] = (ushort)node;
        if (left[parent] != node)
          right[parent] = (ushort)uncle;
        else
          left[parent] = (ushort)uncle;
        parents[node] = (byte)grandparent;
        parents[uncle] = (byte)parent;
        node = grandparent;
        if (node == 1)
          break;
      }
      this.Current = value % this._count;
    }
  }

  public static byte[] SplayDecode(ReadOnlySpan<byte> input, long size, int treeCount) {
    var trees = new SplayTrees(treeCount);
    var output = new byte[size];
    var bit = 0L;
    var totalBits = (long)input.Length * 8;
    for (var i = 0; i < output.Length; ++i) {
      var node = 1;
      while (node < 0x100) {
        if (bit >= totalBits)
          throw new InvalidDataException("DiskDoubler splay-tree stream ends early.");
        var b = (input[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1;
        ++bit;
        node = trees.Child(node, b);
      }
      var value = node - 0x100;
      trees.Update(value);
      output[i] = (byte)value;
    }
    return output;
  }

  public static byte[] SplayEncode(ReadOnlySpan<byte> data, int treeCount) {
    var trees = new SplayTrees(treeCount);
    var writer = new MsbBitWriter();
    var path = new List<int>();
    foreach (var value in data) {
      trees.PathTo(value, path);
      foreach (var b in path)
        writer.Write((uint)b, 1);
      trees.Update(value);
    }
    return writer.ToArray();
  }

  #endregion

  #region Stac LZS (method 7)

  private const int LzsWindow = 2048;

  /// <summary>
  /// Stac LZS as DiskDoubler stores it: flag 0 + 8-bit literal, or flag 1 + offset (1 + 7 bits, or
  /// 0 + 11 bits, an 11-bit offset whose top 7 bits are zero ending the stream) + length code
  /// (2-bit 2..4, 4-bit 5..7, 1111 then 4-bit additions while 15).
  /// </summary>
  public static byte[] LzsDecode(ReadOnlySpan<byte> input, long size) {
    var output = new byte[size];
    var reader = new MsbBitReader(input);
    var pos = 0;
    while (pos < output.Length) {
      if (reader.Read(1) == 0) {
        output[pos++] = (byte)reader.Read(8);
        continue;
      }
      int offset;
      if (reader.Read(1) == 1)
        offset = (int)reader.Read(7);
      else {
        var high = (int)reader.Read(7);
        if (high == 0)
          break;
        offset = (high << 4) | (int)reader.Read(4);
      }
      int length = reader.Read(2) switch {
        0 => 2,
        1 => 3,
        2 => 4,
        _ => 5 + (int)reader.Read(2),
      };
      if (length == 8)
        for (int code; ; ) {
          code = (int)reader.Read(4);
          length += code;
          if (code != 15)
            break;
        }
      if (offset == 0 || offset > pos)
        throw new InvalidDataException("DiskDoubler LZS match reaches before the start of the data.");
      if (length > output.Length - pos)
        throw new InvalidDataException("DiskDoubler LZS match runs past the fork.");
      for (var k = 0; k < length; ++k, ++pos)
        output[pos] = output[pos - offset];
    }
    if (pos != output.Length)
      throw new InvalidDataException("DiskDoubler LZS stream ends early.");
    return output;
  }

  public static byte[] LzsEncode(ReadOnlySpan<byte> data) {
    var writer = new MsbBitWriter();
    var head = new int[1 << 12];
    Array.Fill(head, -1);
    var prev = new int[data.Length];
    int Hash(ReadOnlySpan<byte> d, int i) => ((d[i] << 4) ^ (d[i + 1] << 2) ^ d[i + 2]) & 0xFFF;
    for (var pos = 0; pos < data.Length;) {
      int bestLen = 0, bestOff = 0;
      if (pos + 2 < data.Length) {
        var h = Hash(data, pos);
        for (int cand = head[h], chain = 0; cand >= 0 && chain < 64; cand = prev[cand], ++chain) {
          var off = pos - cand;
          if (off >= LzsWindow)
            break;
          var len = 0;
          while (pos + len < data.Length && data[cand + len] == data[pos + len] && len < 1 << 16)
            ++len;
          if (len > bestLen) {
            bestLen = len;
            bestOff = off;
          }
        }
      }
      var step = bestLen >= 2 ? bestLen : 1;
      for (var k = 0; k < step; ++k)
        if (pos + k + 2 < data.Length) {
          var h = Hash(data, pos + k);
          prev[pos + k] = head[h];
          head[h] = pos + k;
        }
      if (bestLen < 2) {
        writer.Write(0, 1);
        writer.Write(data[pos], 8);
        ++pos;
        continue;
      }
      writer.Write(1, 1);
      if (bestOff < 128) {
        writer.Write(1, 1);
        writer.Write((uint)bestOff, 7);
      } else {
        writer.Write(0, 1);
        writer.Write((uint)bestOff, 11);
      }
      switch (bestLen) {
        case <= 4: writer.Write((uint)(bestLen - 2), 2); break;
        case <= 7: writer.Write((uint)(0b1100 | (bestLen - 5)), 4); break;
        default:
          writer.Write(0b1111, 4);
          for (var rest = bestLen - 8; ; rest -= 15) {
            if (rest >= 15) {
              writer.Write(15, 4);
              continue;
            }
            writer.Write((uint)rest, 4);
            break;
          }
          break;
      }
      pos += bestLen;
    }
    // End marker: an 11-bit offset whose top 7 bits are zero.
    writer.Write(0b100000000, 9);
    return writer.ToArray();
  }

  #endregion

  /// <summary>Delta type 1: each output byte is the stored byte plus the previous output byte.</summary>
  public static void UndoDelta(byte[] data) {
    byte previous = 0;
    for (var i = 0; i < data.Length; ++i)
      previous = data[i] = (byte)(data[i] + previous);
  }

  public static byte[] ApplyDelta(ReadOnlySpan<byte> data) {
    var output = new byte[data.Length];
    byte previous = 0;
    for (var i = 0; i < data.Length; ++i) {
      output[i] = (byte)(data[i] - previous);
      previous = data[i];
    }
    return output;
  }

  private ref struct MsbBitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private long _bit;

    public uint Read(int count) {
      uint value = 0;
      for (var i = 0; i < count; ++i, ++this._bit) {
        if (this._bit >= (long)this._data.Length * 8)
          throw new InvalidDataException("DiskDoubler stream ends early.");
        value = (value << 1) | (uint)((this._data[(int)(this._bit >> 3)] >> (7 - (int)(this._bit & 7))) & 1);
      }
      return value;
    }
  }

  private sealed class MsbBitWriter {
    private readonly List<byte> _bytes = [];
    private int _current, _used;

    public void Write(uint value, int count) {
      for (var i = count - 1; i >= 0; --i) {
        this._current = (this._current << 1) | (int)((value >> i) & 1);
        if (++this._used == 8) {
          this._bytes.Add((byte)this._current);
          this._current = this._used = 0;
        }
      }
    }

    public byte[] ToArray() {
      var result = new List<byte>(this._bytes);
      if (this._used > 0)
        result.Add((byte)(this._current << (8 - this._used)));
      return [.. result];
    }
  }
}
