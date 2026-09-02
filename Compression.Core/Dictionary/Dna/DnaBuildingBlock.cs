using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Dna;

/// <summary>
/// Exposes 2-bit DNA sequence packing as a benchmarkable building block.
/// Each of the four canonical nucleotide symbols (A, C, G, T) is packed into 2 bits,
/// four symbols per byte. Any byte that is not one of the four symbols is recorded
/// as an "exception" (its position and original value) and a placeholder code is
/// packed in its place; exceptions are spliced back in on decode. This lets the
/// codec round-trip arbitrary byte streams while still compressing pure ACGT
/// sequences 4:1, the standard technique used by FASTA/2bit-style DNA packers.
/// Reference: W. J. Kent, "2bit sequence format",
/// https://genome.ucsc.edu/FAQ/FAQformat.html#format7; see also
/// https://en.wikipedia.org/wiki/FASTA_format for the nucleotide alphabet.
/// </summary>
public sealed class DnaBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Dna";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "DNA Sequence Compression";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "2-bit packing for ACGT nucleotides with exception escapes for other bytes";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private static readonly int[] CodeByByte = BuildCodeTable();
  private static readonly byte[] ByteByCode = [(byte)'A', (byte)'C', (byte)'G', (byte)'T'];

  private static int[] BuildCodeTable() {
    var table = new int[256];
    Array.Fill(table, -1);
    table['A'] = 0;
    table['C'] = 1;
    table['G'] = 2;
    table['T'] = 3;
    return table;
  }

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0) {
      // Exception count (0) still needs to be written for a well-formed empty stream.
      BinaryPrimitives.WriteInt32LittleEndian(header, 0);
      ms.Write(header);
      return ms.ToArray();
    }

    var exceptions = new List<(int Position, byte Value)>();
    for (var i = 0; i < data.Length; i++)
      if (CodeByByte[data[i]] < 0)
        exceptions.Add((i, data[i]));

    BinaryPrimitives.WriteInt32LittleEndian(header, exceptions.Count);
    ms.Write(header);

    Span<byte> exceptionBuf = stackalloc byte[5];
    foreach (var (position, value) in exceptions) {
      BinaryPrimitives.WriteInt32LittleEndian(exceptionBuf, position);
      exceptionBuf[4] = value;
      ms.Write(exceptionBuf);
    }

    var packed = 0;
    var bitsInByte = 0;
    for (var i = 0; i < data.Length; i++) {
      var code = CodeByByte[data[i]];
      if (code < 0)
        code = 0; // Placeholder for exception positions, overwritten on decode.

      packed = (packed << 2) | code;
      bitsInByte += 2;
      if (bitsInByte == 8) {
        ms.WriteByte((byte)packed);
        packed = 0;
        bitsInByte = 0;
      }
    }
    if (bitsInByte > 0)
      ms.WriteByte((byte)(packed << (8 - bitsInByte)));

    return ms.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalLength == 0)
      return [];

    var exceptionCount = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
    var pos = 8;

    var result = new byte[originalLength];

    var body = data[(pos + exceptionCount * 5)..];
    var bodyIndex = 0;
    var bitsAvailable = 0;
    var buffer = 0;
    for (var i = 0; i < originalLength; i++) {
      if (bitsAvailable == 0) {
        buffer = body[bodyIndex++];
        bitsAvailable = 8;
      }
      var code = (buffer >> (bitsAvailable - 2)) & 0x3;
      bitsAvailable -= 2;
      result[i] = ByteByCode[code];
    }

    for (var i = 0; i < exceptionCount; i++) {
      var entry = data[pos..];
      var position = BinaryPrimitives.ReadInt32LittleEndian(entry);
      var value = entry[4];
      result[position] = value;
      pos += 5;
    }

    return result;
  }
}
