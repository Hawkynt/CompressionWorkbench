using System.Buffers.Binary;

namespace FileFormat.DxArchive;

/// <summary>The LZ and Huffman coders of DxLib archives (docs/DXA-ON-DISK.md, "LZ" and "Huffman").</summary>
internal static class DxCodecs {
  /// <summary>Expands an LZ-coded block: 9-byte head (expanded size, coded size, key byte), then codes.</summary>
  public static byte[] LzDecode(ReadOnlySpan<byte> src) {
    if (src.Length < 9)
      throw new InvalidDataException("DXA: truncated LZ block.");
    var size = BinaryPrimitives.ReadUInt32LittleEndian(src);
    var coded = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
    if (coded < 9 || coded > src.Length || size > int.MaxValue)
      throw new InvalidDataException("DXA: LZ block sizes do not fit.");
    int key = src[8];
    var output = new byte[size];
    int sp = 9, end = (int)coded, dp = 0;
    while (sp < end) {
      if (src[sp] != key) {
        if (dp >= output.Length)
          throw new InvalidDataException("DXA: LZ block expands past its size.");
        output[dp++] = src[sp++];
        continue;
      }
      if (sp + 1 >= end)
        throw new InvalidDataException("DXA: truncated LZ code.");
      if (src[sp + 1] == key) {
        if (dp >= output.Length)
          throw new InvalidDataException("DXA: LZ block expands past its size.");
        output[dp++] = (byte)key;
        sp += 2;
        continue;
      }
      int code = src[sp + 1];
      if (code > key)
        --code;
      sp += 2;
      var length = code >> 3;
      if ((code & 4) != 0) {
        if (sp >= end)
          throw new InvalidDataException("DXA: truncated LZ code.");
        length |= src[sp++] << 5;
      }
      length += 4;
      int distance;
      switch (code & 3) {
        case 0:
          if (sp + 1 > end)
            throw new InvalidDataException("DXA: truncated LZ code.");
          distance = src[sp++];
          break;
        case 1:
          if (sp + 2 > end)
            throw new InvalidDataException("DXA: truncated LZ code.");
          distance = BinaryPrimitives.ReadUInt16LittleEndian(src[sp..]);
          sp += 2;
          break;
        case 2:
          if (sp + 3 > end)
            throw new InvalidDataException("DXA: truncated LZ code.");
          distance = BinaryPrimitives.ReadUInt16LittleEndian(src[sp..]) | (src[sp + 2] << 16);
          sp += 3;
          break;
        default:
          throw new InvalidDataException("DXA: invalid LZ distance size.");
      }
      ++distance;
      if (distance > dp || length > output.Length - dp)
        throw new InvalidDataException("DXA: an LZ copy reaches outside its output.");
      for (var i = 0; i < length; ++i, ++dp)
        output[dp] = output[dp - distance];
    }
    if (dp != output.Length)
      throw new InvalidDataException($"DXA: LZ block expands to {dp} bytes instead of {size}.");
    return output;
  }

  private sealed class MsbReader(ReadOnlySpan<byte> data) {
    private readonly byte[] _data = data.ToArray();
    private long _bit;
    public long Bytes => (this._bit + 7) / 8;
    public ulong Read(int bits) {
      ulong result = 0;
      for (var i = 0; i < bits; ++i) {
        var at = this._bit / 8;
        if (at >= this._data.Length)
          throw new InvalidDataException("DXA: truncated Huffman header.");
        result = (result << 1) | (uint)((this._data[at] >> (int)(7 - this._bit % 8)) & 1);
        ++this._bit;
      }
      return result;
    }
  }

  /// <summary>Expands a Huffman-coded block (frequency table header, then codes).</summary>
  public static byte[] HuffmanDecode(ReadOnlySpan<byte> press) {
    var header = new MsbReader(press);
    var originalSize = header.Read((int)header.Read(6) + 1);
    header.Read((int)header.Read(6) + 1); // coded size
    Span<ushort> weight = stackalloc ushort[256];
    for (var i = 0; i < 256; ++i) {
      var bits = ((int)header.Read(3) + 1) * 2;
      var minus = header.Read(1) == 1;
      var value = (ushort)header.Read(bits);
      weight[i] = i == 0 ? value : minus ? (ushort)(weight[i - 1] - value) : (ushort)(weight[i - 1] + value);
    }
    if (originalSize > int.MaxValue)
      throw new InvalidDataException("DXA: Huffman block too large.");
    var data = press[(int)header.Bytes..];

    // The tree: nodes 0..255 are the byte values; joined nodes are numbered from 256 up to the root, 510.
    var nodeWeight = new ulong[511];
    var parent = new int[511];
    var child0 = new int[511];
    var child1 = new int[511];
    for (var i = 0; i < 511; ++i) {
      nodeWeight[i] = i < 256 ? weight[i] : 0UL;
      parent[i] = -1;
    }
    var remaining = 256;
    var next = 256;
    while (remaining > 1) {
      int min1 = -1, min2 = -1;
      for (int i = 0, index = 0; i < remaining; ++index) {
        if (parent[index] != -1)
          continue;
        ++i;
        if (min1 == -1 || nodeWeight[min1] > nodeWeight[index]) {
          min2 = min1;
          min1 = index;
        } else if (min2 == -1 || nodeWeight[min2] > nodeWeight[index])
          min2 = index;
      }
      nodeWeight[next] = nodeWeight[min1] + nodeWeight[min2];
      child0[next] = min1;
      child1[next] = min2;
      parent[min1] = next;
      parent[min2] = next;
      ++next;
      --remaining;
    }

    var output = new byte[originalSize];
    long bit = 0;
    for (var i = 0; i < output.Length; ++i) {
      var node = 510;
      while (node > 255) {
        var at = bit >> 3;
        if (at >= data.Length)
          throw new InvalidDataException("DXA: truncated Huffman data.");
        var b = (data[(int)at] >> (int)(bit & 7)) & 1;
        ++bit;
        node = b == 0 ? child0[node] : child1[node];
      }
      output[i] = (byte)node;
    }
    return output;
  }
}
