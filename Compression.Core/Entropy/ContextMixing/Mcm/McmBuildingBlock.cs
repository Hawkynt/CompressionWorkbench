using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.Mcm;

/// <summary>
/// mcm 0.84's context-mixing coder as a building block, in the one framing mcm itself uses for a
/// bare stream — its archive index: the LEB128 length, the mid-level (6-input) CM stream with the
/// text model set, memory level 6 and LZP, then the LEB128 check value 1234.
/// </summary>
/// <remarks>
/// Specification: <c>docs/MCM-ON-DISK.md</c> (derived from mcm's GPL-3.0 source, which was not
/// copied). Proven against mcm 0.84: every archive index mcm writes decodes, and ours re-encodes
/// to mcm's bytes.
/// </remarks>
public sealed class McmBuildingBlock : IBuildingBlock {
  private const int Inputs = 6;
  private const int MemoryLevel = 6;
  private const int Check = 1234;

  /// <inheritdoc/>
  public string Id => "BB_Mcm";
  /// <inheritdoc/>
  public string DisplayName => "MCM context mixing";
  /// <inheritdoc/>
  public string Description => "mcm 0.84 context mixing (mid level, text model, LZP) in the framing of mcm's archive index";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    WriteLeb(output, data.Length);
    output.Write(new McmContextMixer(Inputs, McmModelProfile.Text, MemoryLevel, lzp: true).Encode(data));
    WriteLeb(output, Check);
    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var input = data.ToArray();
    var position = 0;
    var length = ReadLeb(input, ref position);
    if (length > int.MaxValue)
      throw new InvalidDataException("MCM stream length is implausible.");
    var output = new McmContextMixer(Inputs, McmModelProfile.Text, MemoryLevel, lzp: true)
      .Decode(input, position, input.Length, (int)length, out var consumed);
    position += consumed;
    if (ReadLeb(input, ref position) != Check)
      throw new InvalidDataException("MCM stream check value is wrong.");
    return output;
  }

  private static void WriteLeb(Stream stream, long value) {
    var v = (ulong)value;
    while (v >= 0x80) {
      stream.WriteByte((byte)(0x80 | (v & 0x7F)));
      v >>= 7;
    }
    stream.WriteByte((byte)v);
  }

  private static long ReadLeb(byte[] data, ref int position) {
    long value = 0;
    for (var shift = 0; shift < 64; shift += 7) {
      if (position >= data.Length)
        throw new InvalidDataException("MCM stream is truncated.");
      var b = data[position++];
      value |= (long)(b & 0x7F) << shift;
      if ((b & 0x80) == 0)
        return value;
    }
    throw new InvalidDataException("MCM LEB128 value is too long.");
  }
}
