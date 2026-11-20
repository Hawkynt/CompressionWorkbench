namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of <see cref="UharcRleDecoder"/> (docs/UHARC-ON-DISK.md, "RLE"): runs of four
/// or more become <c>R n x</c> (or <c>R FF lo hi x</c> from 255), runs of three <c>T x</c>, shorter
/// runs stay literal, and a literal marker byte is escaped as <c>R c</c>, which moves both markers
/// down by 0x40.
/// </summary>
internal static class UharcRleEncoder {

  private const int MaxRun = 0xFFFF;

  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var output = new MemoryStream(data.Length + data.Length / 64 + 16);
    byte triple = 0xED;
    var i = 0;
    while (i < data.Length) {
      var value = data[i];
      var length = 1;
      while (length < MaxRun && i + length < data.Length && data[i + length] == value)
        ++length;

      var run = (byte)(triple + 1);
      if (length >= 4) {
        output.WriteByte(run);
        if (length <= 254)
          output.WriteByte((byte)length);
        else {
          output.WriteByte(0xFF);
          output.WriteByte((byte)length);
          output.WriteByte((byte)(length >> 8));
        }

        output.WriteByte(value);
      } else if (length == 3) {
        output.WriteByte(triple);
        output.WriteByte(value);
      } else if (value == triple || value == run) {
        // One or two marker bytes: c even names T, odd R; c >= 2 stands for two.
        output.WriteByte(run);
        output.WriteByte((byte)((value == run ? 1 : 0) + (length == 2 ? 2 : 0)));
        triple -= 0x40;
      } else {
        for (var k = 0; k < length; ++k)
          output.WriteByte(value);
      }

      i += length;
    }

    return output.ToArray();
  }
}
