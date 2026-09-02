using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Elias Omega coding as a benchmarkable building block.
/// A recursive universal code for positive integers: the value is prefixed with the
/// bit-length of its binary representation, and that length is itself recursively
/// prefixed the same way, until a group collapses to the value 1. A terminating "0"
/// bit closes the code. Byte values are mapped to positive integers as (value + 1).
/// Reference: P. Elias, "Universal Codeword Sets and Representations of the
/// Integers", IEEE Trans. Information Theory, 1975; see also
/// https://en.wikipedia.org/wiki/Elias_omega_coding for the canonical
/// encode/decode procedure implemented here.
/// </summary>
public sealed class OmegaBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Omega";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Omega Coding";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Elias Omega: recursive universal code for positive integers";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write 4-byte LE uncompressed size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var writer = new BitWriter<MsbBitOrder>(ms);
    foreach (var b in data)
      EncodeOmega(writer, b + 1);
    writer.FlushBits();

    return ms.ToArray();
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    using var ms = new MemoryStream(data[4..].ToArray());
    var reader = new BitReader<MsbBitOrder>(ms);

    var result = new byte[originalSize];
    for (var i = 0; i < originalSize; i++) {
      var value = DecodeOmega(reader);
      if (value is < 1 or > 256)
        throw new InvalidDataException("Invalid Omega code in compressed data.");
      result[i] = (byte)(value - 1);
    }

    return result;
  }

  /// <summary>
  /// Encodes a single positive integer using the Elias Omega procedure.
  /// Collects the chain of successive length-groups, then emits them from the
  /// innermost (smallest) group outward, followed by the terminating zero bit.
  /// </summary>
  private static void EncodeOmega(BitWriter<MsbBitOrder> writer, int value) {
    var chain = new List<int>();
    var n = value;
    while (n > 1) {
      chain.Add(n);
      n = BitLength(n) - 1;
    }

    for (var i = chain.Count - 1; i >= 0; i--) {
      var group = chain[i];
      writer.WriteBits((uint)group, BitLength(group));
    }

    writer.WriteBit(0);
  }

  /// <summary>
  /// Decodes a single positive integer using the canonical Elias Omega procedure:
  /// start with N = 1; if the next bit is 0, stop; otherwise read N further bits
  /// (in addition to the 1-bit already read) to form the new value of N.
  /// </summary>
  private static int DecodeOmega(BitReader<MsbBitOrder> reader) {
    var n = 1;
    while (true) {
      var bit = reader.ReadBit();
      if (bit == 0)
        return n;

      var group = 1;
      for (var i = 0; i < n; i++)
        group = (group << 1) | reader.ReadBit();
      n = group;
    }
  }

  /// <summary>Number of bits in the natural binary representation of a positive integer.</summary>
  private static int BitLength(int value) {
    var len = 0;
    while (value > 0) {
      len++;
      value >>= 1;
    }
    return len;
  }
}
