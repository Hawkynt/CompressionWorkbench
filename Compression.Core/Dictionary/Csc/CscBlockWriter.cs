namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// Frames the two output channels of a libcsc stream — range-coder bytes and raw bit-coder
/// bytes — into tagged blocks in the order they fill up.
/// </summary>
/// <remarks>
/// Every block starts with a flag byte: bit 7 selects the channel (1 = range coder, 0 = bit coder),
/// bit 6 marks a block of exactly the stream's block size. Any other block carries its size as a
/// 24-bit big-endian number after the flag byte.
/// </remarks>
internal sealed class CscBlockWriter(Stream output, uint blockSize) {
  /// <summary>The full block size from the property header.</summary>
  public uint BlockSize => blockSize;

  /// <summary>Writes one block of range-coder bytes.</summary>
  public void WriteRangeCoderBlock(ReadOnlySpan<byte> data) => this.WriteBlock(data, rangeCoder: true);

  /// <summary>Writes one block of bit-coder bytes.</summary>
  public void WriteBitCoderBlock(ReadOnlySpan<byte> data) => this.WriteBlock(data, rangeCoder: false);

  private void WriteBlock(ReadOnlySpan<byte> data, bool rangeCoder) {
    // libcsc would frame an empty block as well, but its own reader rejects a zero size, so a block
    // that would carry nothing is left out; the reader then simply finds the next one.
    if (data.IsEmpty)
      return;

    Span<byte> header = stackalloc byte[4];
    var flag = rangeCoder ? 0x80 : 0x00;
    if ((uint)data.Length == blockSize) {
      header[0] = (byte)(flag | 0x40);
      output.Write(header[..1]);
    } else {
      header[0] = (byte)flag;
      header[1] = (byte)(data.Length >> 16);
      header[2] = (byte)(data.Length >> 8);
      header[3] = (byte)data.Length;
      output.Write(header);
    }

    output.Write(data);
  }
}
