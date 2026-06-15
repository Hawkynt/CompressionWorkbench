#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Ast;

/// <summary>
/// Writes a big-endian GameCube/Wii <c>.ast</c> (STRM) carrying PCM16 big-endian audio (codec 1),
/// laid out per the public AST specification so it round-trips through <see cref="AstReader"/>.
/// Audio is split into <c>"BLCK"</c> blocks of <see cref="BlockSize"/> bytes per channel
/// (the final block holds whatever remains, unpadded). PCM16 is bit-exact (lossless).
/// </summary>
public sealed class AstWriter {

  /// <summary>Per-channel block size in bytes.</summary>
  public const int BlockSize = 0x2760 * 2; // 0x4EC0 bytes/channel (Nintendo's common AST block).

  /// <summary>
  /// Serialises per-channel mono PCM16 into a PCM16BE AST. All channels must share the same
  /// sample count.
  /// </summary>
  public byte[] Write(IReadOnlyList<short[]> channels, int sampleRate, bool loop = false,
                      int loopStart = 0, int loopEnd = 0) {
    if (channels.Count == 0)
      throw new ArgumentException("AST needs at least one channel.", nameof(channels));
    var sampleCount = channels[0].Length;
    if (channels.Any(c => c.Length != sampleCount))
      throw new ArgumentException("All channels must have the same sample count.");

    var numChannels = channels.Count;
    var samplesPerBlock = BlockSize / 2;
    var numBlocks = sampleCount == 0 ? 0 : (sampleCount + samplesPerBlock - 1) / samplesPerBlock;

    using var ms = new MemoryStream();
    var header = new byte[0x40];
    "STRM"u8.CopyTo(header);
    // dataSize patched after we know the total block payload size.
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 1);                       // codec = PCM16BE
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 16);                     // bit depth
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), (ushort)numChannels);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), (ushort)(loop ? 1 : 0));
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)sampleRate);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), (uint)sampleCount);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), (uint)loopStart);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), (uint)(loop ? loopEnd : sampleCount));
    ms.Write(header);

    var dataSize = 0;
    for (var blk = 0; blk < numBlocks; ++blk) {
      var start = blk * samplesPerBlock;
      var count = Math.Min(samplesPerBlock, sampleCount - start);
      var blockBytes = count * 2;

      var block = new byte[32];
      "BLCK"u8.CopyTo(block);
      BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), (uint)blockBytes);
      ms.Write(block);
      dataSize += 32;

      for (var c = 0; c < numChannels; ++c) {
        var payload = new byte[blockBytes];
        for (var i = 0; i < count; ++i)
          BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(i * 2), channels[c][start + i]);
        ms.Write(payload);
        dataSize += blockBytes;
      }

      if (blk == 0)
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), (uint)blockBytes); // firstBlockSize
    }

    var file = ms.ToArray();
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)dataSize);
    // firstBlockSize was patched into the local header copy; write it into the file too.
    file.AsSpan(32, 4).Clear();
    if (numBlocks > 0) {
      var firstCount = Math.Min(samplesPerBlock, sampleCount);
      BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(32), (uint)(firstCount * 2));
    }
    return file;
  }
}
