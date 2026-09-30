using System.Text;

namespace FileFormat.YEnc;

/// <summary>
/// yEnc binary-to-text encoder.
/// </summary>
public static class YEncEncoder {

  /// <summary>Encodes binary data as yEnc.</summary>
  public static void Encode(Stream output, string filename, byte[] data) {
    var crc = Compression.Core.Checksums.Crc32.Compute(data);
    // yEnc is raw 8-bit: Latin-1 writes every char below 256 as the byte of the same value.
    using var writer = new StreamWriter(output, Encoding.Latin1, leaveOpen: true);
    writer.NewLine = "\r\n";
    writer.WriteLine($"=ybegin line=128 size={data.Length} name={Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(filename))}");

    var col = 0;
    for (var i = 0; i < data.Length; i++) {
      var b = (byte)((data[i] + 42) & 0xFF);
      // Critical characters that must be escaped
      if (b is 0x00 or 0x0A or 0x0D or 0x3D or 0x09) {  // NUL, LF, CR, =, TAB
        writer.Write('=');
        writer.Write((char)((b + 64) & 0xFF));
        col += 2;
      } else {
        writer.Write((char)b);
        col++;
      }
      if (col >= 128) {
        writer.WriteLine();
        col = 0;
      }
    }
    if (col > 0) writer.WriteLine();
    writer.WriteLine($"=yend size={data.Length} crc32={crc:x8}");
    writer.Flush();
  }
}
