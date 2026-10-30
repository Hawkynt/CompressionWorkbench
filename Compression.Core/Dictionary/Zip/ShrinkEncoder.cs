namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Encodes data with ZIP Shrink (method 1), the way PKZIP 1.x does: LZW with codes widened from 9 to
/// 13 bits on demand (control code 256, 1) and a partial clear (256, 2) once all 8192 codes are used.
/// </summary>
/// <remarks>
/// Converted from hwshrink in hwzip 2.4's shrink.c (Hans Wennborg, public domain), see
/// THIRD-PARTY-NOTICE.hwzip.txt; for Hamlet it produces PKZIP 1.10's size to the byte.
/// </remarks>
public static class ShrinkEncoder {
  /// <summary>
  /// Compresses data using the ZIP Shrink algorithm.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];

    var table = new Dictionary<int, ushort>(1 << 14);
    var queue = new ShrinkDecoder.CodeQueue();
    var writer = new ZipLegacyBitWriter(data.Length);
    var codeSize = ShrinkDecoder.MinCodeSize;

    int current = data[0];
    for (var i = 1; i < data.Length; ++i) {
      var extension = data[i];
      if (table.TryGetValue(current << 8 | extension, out var next)) {
        current = next;
        continue;
      }

      WriteCode(writer, current, ref codeSize);

      var newCode = queue.RemoveNext();
      if (newCode == ShrinkDecoder.InvalidCode) {
        PartialClear(table, queue);
        writer.Write(ShrinkDecoder.ControlCode, codeSize);
        writer.Write(ShrinkDecoder.PartialClear, codeSize);
        newCode = queue.RemoveNext();
      }

      if (newCode != ShrinkDecoder.InvalidCode)
        table[current << 8 | extension] = newCode;

      current = extension;
    }

    WriteCode(writer, current, ref codeSize);
    return writer.ToArray();
  }

  private static void WriteCode(ZipLegacyBitWriter writer, int code, ref int codeSize) {
    while (code > (1 << codeSize) - 1) {
      writer.Write(ShrinkDecoder.ControlCode, codeSize);
      writer.Write(ShrinkDecoder.IncreaseCodeSize, codeSize);
      ++codeSize;
    }

    writer.Write((ulong)code, codeSize);
  }

  /// <summary>Keeps only codes that are a prefix of another; every other code goes back in the queue.</summary>
  private static void PartialClear(Dictionary<int, ushort> table, ShrinkDecoder.CodeQueue queue) {
    var isPrefix = new bool[ShrinkDecoder.MaxCode + 1];
    foreach (var key in table.Keys)
      isPrefix[key >> 8] = true;

    foreach (var (key, code) in table.ToArray())
      if (!isPrefix[code])
        table.Remove(key);

    queue.Reset();
    for (var code = ShrinkDecoder.ControlCode + 1; code <= ShrinkDecoder.MaxCode; ++code)
      if (!isPrefix[code])
        queue.Add((ushort)code);
  }
}
