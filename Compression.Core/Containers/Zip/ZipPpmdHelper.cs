using System.Buffers.Binary;
using Compression.Core.Entropy.Ppmd;

namespace FileFormat.Zip;

/// <summary>
/// ZIP compression method 98: PPMd variant I revision 1 (<see cref="Ppmd8"/>). The data starts with
/// a little-endian 16-bit word holding order - 1 (bits 0-3), memory size in MB - 1 (bits 4-11) and
/// the restore method (bits 12-15), followed by the range-coded symbols and an end marker
/// (APPNOTE.TXT section 5.10, as written by 7-Zip and WinZip).
/// </summary>
internal static class ZipPpmdHelper {
  /// <summary>Decompresses one entry's method-98 data.</summary>
  public static byte[] Decompress(byte[] compressedData, int uncompressedSize) {
    if (compressedData.Length < 2)
      throw new InvalidDataException("ZIP PPMd data too short for properties.");

    var properties = BinaryPrimitives.ReadUInt16LittleEndian(compressedData);
    var order = (properties & 0xF) + 1;
    var memorySizeMb = ((properties >> 4) & 0xFF) + 1;
    var restore = properties >> 12;
    if (order < Ppmd8.MinOrder || restore > 2)
      throw new InvalidDataException($"ZIP PPMd properties 0x{properties:X4} are invalid.");
    if (restore == 2)
      throw new NotSupportedException("ZIP PPMd restore method 2 (freeze) is not supported.");

    var model = new Ppmd8(memorySizeMb << 20, order, (Ppmd8RestoreMethod)restore);
    if (!model.StartDecoding(compressedData, 2))
      throw new InvalidDataException("ZIP PPMd data does not start a valid range-coded stream.");

    var output = new byte[uncompressedSize];
    for (var i = 0; i < output.Length; ++i) {
      var symbol = model.DecodeSymbol();
      if (symbol < 0)
        throw new InvalidDataException($"ZIP PPMd data ended after {i} of {output.Length} bytes.");

      output[i] = (byte)symbol;
    }

    if (model.InputOverrun > 0)
      throw new InvalidDataException("ZIP PPMd data is truncated.");

    return output;
  }

  /// <summary>
  /// Compresses data as ZIP method 98 the way 7-Zip does: properties, symbols, end marker.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="order">Model order (2-16).</param>
  /// <param name="memSizeMB">Model memory in megabytes (1-256).</param>
  /// <param name="restoreMethod">What the model does when memory runs out.</param>
  public static byte[] Compress(byte[] data, int order = 6, int memSizeMB = 8, Ppmd8RestoreMethod restoreMethod = Ppmd8RestoreMethod.Restart) {
    if (order is < Ppmd8.MinOrder or > Ppmd8.MaxOrderLimit)
      throw new ArgumentOutOfRangeException(nameof(order), "ZIP PPMd order must be 2-16.");
    if (memSizeMB is < 1 or > 256)
      throw new ArgumentOutOfRangeException(nameof(memSizeMB), "ZIP PPMd memory must be 1-256 MB.");

    using var output = new MemoryStream();
    var properties = (order - 1) + ((memSizeMB - 1) << 4) + ((int)restoreMethod << 12);
    output.WriteByte((byte)properties);
    output.WriteByte((byte)(properties >> 8));

    var model = new Ppmd8(memSizeMB << 20, order, restoreMethod);
    model.StartEncoding(output);
    foreach (var b in data)
      model.EncodeSymbol(b);

    model.EncodeSymbol(Ppmd8.EndMarker);
    model.FlushEncoder();
    return output.ToArray();
  }
}
