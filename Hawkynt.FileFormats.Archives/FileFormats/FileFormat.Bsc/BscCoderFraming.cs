// Converted from libbsc's coder/coder.cpp (Apache-2.0).
// Copyright (c) 2009-2025 Ilya Grebnov <ilya.grebnov@gmail.com>
// See THIRD-PARTY-NOTICE.libbsc.txt in this directory.
using System.Buffers.Binary;

namespace FileFormat.Bsc;

/// <summary>Encodes one QLFC sub-block into at most <paramref name="outputSize"/> bytes, or returns null.</summary>
internal delegate byte[]? BscBlockEncoder(ReadOnlySpan<byte> input, int outputSize);

/// <summary>
/// <c>bsc_coder_compress_serial</c>: the BWT output is QLFC-coded whole, or for 256 KiB and more
/// in <c>bsc_coder_num_blocks</c> sub-blocks cut where the rank changes, each coded on its own and
/// stored when coding does not shrink it. A count byte and, for several sub-blocks, a table of
/// (decoded size, coded size) pairs lead the stream.
/// </summary>
internal static class BscCoderFraming {

  public static byte[]? Compress(ReadOnlySpan<byte> input, BscBlockEncoder encodeBlock) {
    var n = input.Length;
    var blocks = NumBlocks(n);
    if (blocks == 1) {
      var single = encodeBlock(input, n - 1);
      if (single is null)
        return null;
      var framed = new byte[single.Length + 1];
      framed[0] = 1;
      single.CopyTo(framed, 1);
      return framed;
    }

    var (starts, sizes) = SplitBlocks(input, blocks);
    var output = new List<byte>(n) { (byte)blocks };
    output.AddRange(new byte[8 * blocks]);
    var table = new byte[8 * blocks];
    var outputPosition = 1 + 8 * blocks;

    for (var block = 0; block < blocks; ++block) {
      var inputSize = sizes[block];
      var outputSize = Math.Min(inputSize, n - outputPosition);
      var part = input.Slice(starts[block], inputSize);
      var encoded = encodeBlock(part, outputSize);
      if (encoded is null) {
        if (outputPosition + inputSize >= n)
          return null;
        encoded = part.ToArray();
      }

      BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(8 * block), inputSize);
      BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(8 * block + 4), encoded.Length);
      output.AddRange(encoded);
      outputPosition += encoded.Length;
    }

    var result = output.ToArray();
    table.CopyTo(result, 1);
    return result;
  }

  // bsc_coder_num_blocks: the same size steps as LZP's.
  private static int NumBlocks(int n) {
    ReadOnlySpan<int> counts = [128, 96, 64, 48, 32, 24, 16, 12, 8, 6, 4, 2];
    foreach (var count in counts)
      if (n >= (long)count * count * 65536)
        return count;
    return 1;
  }

  // bsc_coder_split_blocks: equal shares of the rank changes sampled every 32 bytes, or equal
  // sizes when there are too few changes.
  private static (int[] Starts, int[] Sizes) SplitBlocks(ReadOnlySpan<byte> input, int blocks) {
    var n = input.Length;
    var starts = new int[blocks];
    var sizes = new int[blocks];

    var rankSize = 0;
    for (var i = 1; i < n; i += 32)
      if (input[i] != input[i - 1])
        ++rankSize;

    if (rankSize > blocks) {
      var blockRankSize = rankSize / blocks;
      starts[0] = 0;
      rankSize = 0;
      for (int id = 0, i = 1; i < n; i += 32) {
        if (input[i] == input[i - 1])
          continue;
        if (++rankSize != blockRankSize)
          continue;

        rankSize = 0;
        sizes[id] = i - starts[id];
        ++id;
        starts[id] = i;
        if (id == blocks - 1)
          break;
      }

      sizes[blocks - 1] = n - starts[blocks - 1];
    } else
      for (var p = 0; p < blocks; ++p) {
        starts[p] = n / blocks * p;
        sizes[p] = p != blocks - 1 ? n / blocks : n - n / blocks * (blocks - 1);
      }

    return (starts, sizes);
  }
}
