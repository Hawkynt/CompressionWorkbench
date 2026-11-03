using Compression.Registry;

namespace Compression.Core.Dictionary.Shoco;

/// <summary>
/// Exposes shoco (Christian Schramm, 2014), the short-string compressor, as a benchmarkable
/// building block. The stream is exactly what <c>shoco_compress</c> writes with the default
/// model: a run of characters from a 32-character alphabet, each after the first named by its
/// rank among its predecessor's most likely successors, is packed into 1, 2 or 4 bytes behind a
/// unary tier header; any other ASCII byte is copied, and a byte with the top bit set is written
/// behind a <c>0x00</c> sentinel.
/// </summary>
/// <remarks>
/// <para>
/// Converted from shoco's <c>shoco.c</c> with the default model's tables from
/// <c>shoco_model.h</c> (MIT, revision <c>7c92fa9c4161ea97da90cc78e38cfb18476ba882</c>; see
/// <c>THIRD-PARTY-NOTICE.shoco.txt</c>). <c>shoco_compress</c> stops at the first zero byte,
/// because it compresses C strings; this block writes a zero byte behind the same sentinel
/// instead, which <c>shoco_decompress</c> reads back as a zero byte. For input without zero
/// bytes the output is byte-identical to <c>shoco_compress</c>.
/// </para>
/// <para>The stream carries no length; it ends where its bytes end.</para>
/// </remarks>
public sealed class ShocoBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Shoco";
  /// <inheritdoc/>
  public string DisplayName => "Shoco";
  /// <inheritdoc/>
  public string Description => "shoco short-string compression with its default English model (successor packs in 1/2/4 bytes)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var output = new List<byte>(data.Length + 16);
    Span<int> indices = stackalloc int[ShocoModel.MaxSuccessorN + 1];
    var position = 0;
    while (position < data.Length) {
      var lead = ShocoModel.IdsByChar[data[position]];
      if (lead >= 0) {
        indices[0] = lead;
        var last = lead;
        var rest = data.Length - position;
        int consecutive;
        for (consecutive = 1; consecutive <= ShocoModel.MaxSuccessorN; ++consecutive) {
          if (consecutive == rest)
            break;
          var current = ShocoModel.IdsByChar[data[position + consecutive]];
          if (current < 0)
            break;
          var successor = ShocoModel.SuccessorIds[last * 32 + current];
          if (successor < 0)
            break;
          indices[consecutive] = successor;
          last = current;
        }

        if (consecutive >= 2 && FindBestPack(indices, consecutive) is var pack and >= 0) {
          var word = ShocoModel.PackWords[pack];
          var unpacked = ShocoModel.PackUnpacked[pack];
          for (var i = 0; i < unpacked; ++i)
            word |= (uint)indices[i] << ShocoModel.PackOffsets[pack * 8 + i];
          for (var i = 0; i < ShocoModel.PackPacked[pack]; ++i)
            output.Add((byte)(word >> (24 - 8 * i)));
          position += unpacked;
          continue;
        }
      }

      var b = data[position++];
      if (b == 0 || b >= 0x80)
        output.Add(0x00);
      output.Add(b);
    }

    return [.. output];
  }

  /// <inheritdoc/>
  /// <exception cref="InvalidDataException">A sentinel or pack is cut off, or a pack names no character.</exception>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var output = new List<byte>(data.Length * 2);
    var position = 0;
    while (position < data.Length) {
      var mark = DecodeHeader(data[position]);
      if (mark < 0) {
        if (data[position] == 0x00 && ++position >= data.Length)
          throw new InvalidDataException("shoco stream ends after a sentinel byte.");
        output.Add(data[position++]);
        continue;
      }

      if (mark >= ShocoModel.PackCount)
        throw new InvalidDataException($"shoco pack header 0x{data[position]:X2} names no pack.");
      var packed = ShocoModel.PackPacked[mark];
      if (position + packed > data.Length)
        throw new InvalidDataException("shoco stream ends inside a pack.");

      uint word = 0;
      for (var i = 0; i < packed; ++i)
        word |= (uint)data[position + i] << (24 - 8 * i);

      var last = ShocoModel.CharsById[(int)(word >> ShocoModel.PackOffsets[mark * 8]) & ShocoModel.PackMasks[mark * 8]];
      output.Add(last);
      for (var i = 1; i < ShocoModel.PackUnpacked[mark]; ++i) {
        var row = last - ShocoModel.MinChar;
        var rank = (int)(word >> ShocoModel.PackOffsets[mark * 8 + i]) & ShocoModel.PackMasks[mark * 8 + i];
        var next = row is >= 0 and < ShocoModel.SuccessorRows ? ShocoModel.CharsBySuccessor[row * 16 + rank] : (byte)0;
        if (next == 0)
          throw new InvalidDataException("shoco pack names a successor the model does not have.");
        output.Add(next);
        last = next;
      }

      position += packed;
    }

    return [.. output];
  }

  /// <summary>The largest pack whose fields hold every index, as shoco's <c>find_best_encoding</c> chooses; -1 for none.</summary>
  private static int FindBestPack(ReadOnlySpan<int> indices, int consecutive) {
    for (var pack = ShocoModel.PackCount - 1; pack >= 0; --pack) {
      var unpacked = ShocoModel.PackUnpacked[pack];
      if (consecutive < unpacked)
        continue;
      var fits = true;
      for (var i = 0; i < unpacked && fits; ++i)
        fits = indices[i] <= ShocoModel.PackMasks[pack * 8 + i];
      if (fits)
        return pack;
    }

    return -1;
  }

  /// <summary>Leading one-bits of the byte, minus one: -1 for a plain byte, else the pack index.</summary>
  private static int DecodeHeader(byte value) {
    var mark = -1;
    while ((value & 0x80) != 0) {
      value <<= 1;
      ++mark;
    }

    return mark;
  }
}
