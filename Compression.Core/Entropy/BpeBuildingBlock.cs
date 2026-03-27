using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Byte Pair Encoding (BPE) as a benchmarkable building block.
/// Iteratively replaces the most frequent consecutive byte pair with a new symbol.
/// Header: 2-byte LE dictionary size, 6 bytes per entry (code, val1, val2),
/// 4-byte LE data length (in values), 2 bytes per encoded value.
/// </summary>
public sealed class BpeBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BPE";
  /// <inheritdoc/>
  public string DisplayName => "Byte Pair Encoding";
  /// <inheritdoc/>
  public string Description => "Iterative most-frequent pair replacement";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  private const int MaxIterations = 256;
  private const int FirstCode = 256;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Work with an int array so codes > 255 are representable.
    var dataArr = new int[data.Length];
    for (var i = 0; i < data.Length; i++)
      dataArr[i] = data[i];
    var dataLen = data.Length;

    var dictionary = new List<(int code, int val1, int val2)>();
    var nextCode = FirstCode;

    var pairCounts = new Dictionary<long, int>(Math.Min(dataLen, 4096));

    for (var iter = 0; iter < MaxIterations && dataLen >= 2; iter++) {
      // Find most frequent consecutive pair.
      pairCounts.Clear();
      for (var i = 0; i < dataLen - 1; i++) {
        var key = ((long)dataArr[i] << 32) | (uint)dataArr[i + 1];
        pairCounts.TryGetValue(key, out var count);
        pairCounts[key] = count + 1;
      }

      long bestKey = 0;
      var bestCount = 0;
      foreach (var (key, count) in pairCounts) {
        if (count > bestCount) {
          bestCount = count;
          bestKey = key;
        }
      }

      // Stop if the best pair doesn't save enough to justify the dictionary entry cost.
      var netSavings = bestCount * 2 - 6;
      if (netSavings <= 0)
        break;

      var b1 = (int)(bestKey >> 32);
      var b2 = (int)(bestKey & 0xFFFFFFFF);

      // Replace all occurrences in-place.
      var prevLen = dataLen;
      var writePos = 0;
      for (var i = 0; i < dataLen; i++) {
        if (i < dataLen - 1 && dataArr[i] == b1 && dataArr[i + 1] == b2) {
          dataArr[writePos++] = nextCode;
          i++; // skip next
        } else {
          dataArr[writePos++] = dataArr[i];
        }
      }
      dataLen = writePos;

      dictionary.Add((nextCode, b1, b2));
      nextCode++;

      // Stop if this iteration shrank the data by less than 0.5%.
      if ((prevLen - dataLen) * 200 < prevLen)
        break;
    }

    // Write header: 2-byte LE dictionary size.
    Span<byte> header = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)dictionary.Count);
    ms.Write(header);

    // Dictionary entries: 2-byte LE code, 2-byte LE val1, 2-byte LE val2.
    Span<byte> entry = stackalloc byte[6];
    foreach (var (code, val1, val2) in dictionary) {
      BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)code);
      BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], (ushort)val1);
      BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], (ushort)val2);
      ms.Write(entry);
    }

    // Data length (number of values) as 4-byte LE.
    Span<byte> lenBuf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(lenBuf, dataLen);
    ms.Write(lenBuf);

    // Encoded data: 2 bytes per value.
    Span<byte> valBuf = stackalloc byte[2];
    for (var i = 0; i < dataLen; i++) {
      BinaryPrimitives.WriteUInt16LittleEndian(valBuf, (ushort)dataArr[i]);
      ms.Write(valBuf);
    }

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    var dictSize = BinaryPrimitives.ReadUInt16LittleEndian(data);
    offset += 2;

    // Read dictionary in order.
    var rules = new (ushort code, ushort val1, ushort val2)[dictSize];
    for (var i = 0; i < dictSize; i++) {
      rules[i] = (
        BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]),
        BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]),
        BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..])
      );
      offset += 6;
    }

    // Read data length.
    var dataLen = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    offset += 4;

    // Read encoded data into array.
    var arr = new int[dataLen + dictSize * 2]; // extra space for expansion
    for (var i = 0; i < dataLen; i++) {
      arr[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
      offset += 2;
    }
    var currentLen = dataLen;

    // Expand codes in reverse order (highest code first).
    var buffer = new int[arr.Length * 2];
    for (var i = rules.Length - 1; i >= 0; i--) {
      var (code, val1, val2) = rules[i];
      var maxLen = currentLen * 2;
      if (buffer.Length < maxLen)
        buffer = new int[maxLen];

      var writePos = 0;
      for (var j = 0; j < currentLen; j++) {
        if (arr[j] == code) {
          buffer[writePos++] = val1;
          buffer[writePos++] = val2;
        } else {
          buffer[writePos++] = arr[j];
        }
      }

      (arr, buffer) = (buffer, arr);
      currentLen = writePos;
    }

    // All values should now be in 0-255 range.
    var result = new byte[currentLen];
    for (var i = 0; i < currentLen; i++)
      result[i] = (byte)arr[i];
    return result;
  }
}
