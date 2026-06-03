#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Bfstm;

/// <summary>
/// Writes a WiiU/Switch <c>.bfstm</c> (FSTM) carrying DSP-ADPCM, laid out per the public FSTM
/// specification so it round-trips through <see cref="BfstmReader"/>. The container endianness is
/// caller-selectable (<see cref="Write"/>'s <c>bigEndian</c> flag) — WiiU files are big-endian,
/// Switch files little-endian — and the BOM is written accordingly.
/// <para>
/// SIMPLIFICATION (see <see cref="BfstmReader"/>): per-channel coefficient structs are written as a
/// flat <c>0x2E</c>-byte table after the stream-info body rather than through C/FSTM's reference-offset
/// indirection. Header, block table, INFO/DATA section structure and stream-info fields otherwise
/// follow the documented format.
/// </para>
/// </summary>
public sealed class BfstmWriter {

  /// <summary>Per-channel block size in bytes (Nintendo's canonical 0x2000).</summary>
  public const int BlockSize = 0x2000;

  private const int SamplesPerFrame = Codec.DspAdpcm.DspAdpcmCodec.SamplesPerFrame; // 14
  private const int BytesPerFrame = Codec.DspAdpcm.DspAdpcmCodec.BytesPerFrame;      // 8
  private const int SamplesPerBlock = BlockSize / BytesPerFrame * SamplesPerFrame;   // 14336

  /// <summary>
  /// Encodes per-channel mono PCM16 to DSP-ADPCM and serialises a complete FSTM in the requested
  /// endianness. WiiU uses <paramref name="bigEndian"/> = <see langword="true"/>; Switch uses
  /// <see langword="false"/>. All channels must share the same sample count.
  /// </summary>
  public byte[] Write(IReadOnlyList<short[]> channels, int sampleRate, bool bigEndian,
                      bool loop = false, int loopStart = 0) {
    if (channels.Count == 0)
      throw new ArgumentException("Stream needs at least one channel.", nameof(channels));
    var totalSamples = channels[0].Length;
    if (channels.Any(c => c.Length != totalSamples))
      throw new ArgumentException("All channels must have the same sample count.");

    var be = bigEndian;
    var numChannels = channels.Count;

    var encoded = new byte[numChannels][];
    var coefs = new short[numChannels][];
    for (var c = 0; c < numChannels; ++c) {
      var (adpcm, table) = Codec.DspAdpcm.DspAdpcmCodec.Encode(channels[c]);
      encoded[c] = adpcm;
      coefs[c] = table;
    }

    var numBlocks = totalSamples == 0 ? 0 : (totalSamples + SamplesPerBlock - 1) / SamplesPerBlock;
    var finalBlockSamples = totalSamples == 0 ? 0 : totalSamples - (numBlocks - 1) * SamplesPerBlock;
    var finalFrames = (finalBlockSamples + SamplesPerFrame - 1) / SamplesPerFrame;
    var finalBlockSize = finalFrames * BytesPerFrame;
    var finalBlockSizePadded = (finalBlockSize + 0x1F) & ~0x1F;

    var siRel = 0x18;
    var coefRel = siRel + 0x40;
    var infoBodyLen = coefRel + numChannels * 0x2E;
    infoBodyLen = (infoBodyLen + 0x1F) & ~0x1F;
    var infoChunkSize = 8 + infoBodyLen;
    infoChunkSize = (infoChunkSize + 0x1F) & ~0x1F;
    var info = new byte[infoChunkSize];
    info[0] = (byte)'I'; info[1] = (byte)'N'; info[2] = (byte)'F'; info[3] = (byte)'O';
    WriteU32(info.AsSpan(4), (uint)infoChunkSize, be);

    var si = 8 + siRel;
    info[si + 0] = 2;
    info[si + 1] = (byte)(loop ? 1 : 0);
    info[si + 2] = (byte)numChannels;
    info[si + 3] = 0;
    WriteU32(info.AsSpan(si + 4), (uint)sampleRate, be);
    WriteU32(info.AsSpan(si + 8), (uint)loopStart, be);
    WriteU32(info.AsSpan(si + 12), (uint)totalSamples, be);
    WriteU32(info.AsSpan(si + 16), (uint)numBlocks, be);
    WriteU32(info.AsSpan(si + 20), (uint)BlockSize, be);
    WriteU32(info.AsSpan(si + 24), (uint)SamplesPerBlock, be);
    WriteU32(info.AsSpan(si + 28), (uint)finalBlockSamples, be);
    WriteU32(info.AsSpan(si + 32), (uint)finalBlockSize, be);
    WriteU32(info.AsSpan(si + 36), (uint)finalBlockSizePadded, be);

    var coefBase = 8 + coefRel;
    for (var c = 0; c < numChannels; ++c) {
      var o = coefBase + c * 0x2E;
      for (var i = 0; i < 16; ++i)
        WriteS16(info.AsSpan(o + i * 2), coefs[c][i], be);
    }

    const int dataReserved = 0x18;
    using var dataAudio = new MemoryStream();
    for (var blk = 0; blk < numBlocks; ++blk) {
      var isFinal = blk == numBlocks - 1;
      var rawSize = isFinal ? finalBlockSize : BlockSize;
      var paddedSize = isFinal ? finalBlockSizePadded : BlockSize;
      for (var c = 0; c < numChannels; ++c) {
        var srcStart = blk * BlockSize;
        var avail = Math.Max(0, Math.Min(rawSize, encoded[c].Length - srcStart));
        var buf = new byte[paddedSize];
        if (avail > 0)
          Array.Copy(encoded[c], srcStart, buf, 0, avail);
        dataAudio.Write(buf);
      }
    }
    var audioBytes = dataAudio.ToArray();
    var dataChunkSize = 8 + dataReserved + audioBytes.Length;
    dataChunkSize = (dataChunkSize + 0x1F) & ~0x1F;
    var data = new byte[dataChunkSize];
    data[0] = (byte)'D'; data[1] = (byte)'A'; data[2] = (byte)'T'; data[3] = (byte)'A';
    WriteU32(data.AsSpan(4), (uint)dataChunkSize, be);
    audioBytes.CopyTo(data.AsSpan(8 + dataReserved));

    const int headerSize = 0x40;
    var infoOff = headerSize;
    var dataOff = infoOff + infoChunkSize;
    var fileSize = dataOff + dataChunkSize;
    var file = new byte[fileSize];

    "FSTM"u8.CopyTo(file.AsSpan(0));
    WriteU16(file.AsSpan(4), 0xFEFF, be);
    WriteU16(file.AsSpan(6), headerSize, be);
    WriteU32(file.AsSpan(8), 0x00000000, be);
    WriteU32(file.AsSpan(12), (uint)fileSize, be);
    WriteU16(file.AsSpan(16), 2, be);
    WriteU16(file.AsSpan(18), 0, be);
    WriteU16(file.AsSpan(0x14), 0x4000, be);
    WriteU32(file.AsSpan(0x18), (uint)infoOff, be);
    WriteU32(file.AsSpan(0x1C), (uint)infoChunkSize, be);
    WriteU16(file.AsSpan(0x20), 0x4002, be);
    WriteU32(file.AsSpan(0x24), (uint)dataOff, be);
    WriteU32(file.AsSpan(0x28), (uint)dataChunkSize, be);

    info.CopyTo(file.AsSpan(infoOff));
    data.CopyTo(file.AsSpan(dataOff));
    return file;
  }

  private static void WriteU16(Span<byte> s, ushort v, bool be) {
    if (be) BinaryPrimitives.WriteUInt16BigEndian(s, v);
    else BinaryPrimitives.WriteUInt16LittleEndian(s, v);
  }
  private static void WriteU32(Span<byte> s, uint v, bool be) {
    if (be) BinaryPrimitives.WriteUInt32BigEndian(s, v);
    else BinaryPrimitives.WriteUInt32LittleEndian(s, v);
  }
  private static void WriteS16(Span<byte> s, short v, bool be) {
    if (be) BinaryPrimitives.WriteInt16BigEndian(s, v);
    else BinaryPrimitives.WriteInt16LittleEndian(s, v);
  }
}
