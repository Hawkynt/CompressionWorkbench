#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Brstm;

/// <summary>
/// Writes a big-endian Wii <c>.brstm</c> (RSTM) carrying DSP-ADPCM, laid out per the public
/// BRSTM specification so it round-trips through <see cref="BrstmReader"/>. Channels are
/// DSP-ADPCM encoded independently (see <c>Codec.DspAdpcm</c>); the audio is written as
/// channel-interleaved blocks of <see cref="BlockSize"/> bytes per channel (the final block
/// padded to a 0x20 boundary, as Nintendo does).
/// </summary>
public sealed class BrstmWriter {

  /// <summary>Per-channel block size in bytes (Nintendo's canonical 0x2000).</summary>
  public const int BlockSize = 0x2000;

  private const int SamplesPerFrame = Codec.DspAdpcm.DspAdpcmCodec.SamplesPerFrame; // 14
  private const int BytesPerFrame = Codec.DspAdpcm.DspAdpcmCodec.BytesPerFrame;      // 8
  private const int SamplesPerBlock = BlockSize / BytesPerFrame * SamplesPerFrame;   // 14336

  /// <summary>
  /// Encodes per-channel mono PCM16 to DSP-ADPCM and serialises a complete BE BRSTM.
  /// All channels must share the same sample count.
  /// </summary>
  public byte[] Write(IReadOnlyList<short[]> channels, int sampleRate, bool loop = false, int loopStart = 0) {
    if (channels.Count == 0)
      throw new ArgumentException("BRSTM needs at least one channel.", nameof(channels));
    var totalSamples = channels[0].Length;
    if (channels.Any(c => c.Length != totalSamples))
      throw new ArgumentException("All channels must have the same sample count.");

    var numChannels = channels.Count;

    // Encode each channel; collect ADPCM bytes + coefficients.
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

    // ── Build HEAD chunk body (everything after "HEAD"+size). ──
    // Layout (all offsets relative to refBase = HEAD+8):
    //   [0x00] 3 sub-chunk refs (8 bytes each = 0x18)
    //   [0x18] sub-chunk 1: stream info (0x34 bytes, pad to 0x38)
    //   [...]  sub-chunk 2: track info (we keep one minimal track)
    //   [...]  sub-chunk 3: channel info (numChannels + per-channel ref + per-channel block)
    using var head = new MemoryStream();

    const int refTableSize = 0x18;
    var info1Off = refTableSize;                    // stream info right after the ref table
    const int info1Size = 0x40;                     // generous, zero-padded
    var info2Off = info1Off + info1Size;            // track info
    const int info2Size = 0x10;
    var info3Off = info2Off + info2Size;            // channel info

    // Channel-info sub-chunk: u8 numChannels, pad to 4, then numChannels ref pairs (8 bytes),
    // then per channel a 0x38 ADPCM-info block (coefs in first 0x20 bytes).
    var chanRefBase = info3Off + 4;
    var chanEntriesBase = chanRefBase + numChannels * 8;

    // Refs are filled below; first write zeroes for the whole HEAD body then patch.
    var headBodyLen = chanEntriesBase + numChannels * 0x38;
    headBodyLen = (headBodyLen + 0x1F) & ~0x1F;
    var headBody = new byte[headBodyLen];

    // Sub-chunk reference table (marker 0x01000000 + offset rel to refBase).
    WriteRef(headBody, 0x00, info1Off);
    WriteRef(headBody, 0x08, info2Off);
    WriteRef(headBody, 0x10, info3Off);

    // dataOffset is absolute in the file; computed after we know all chunk sizes.
    // Fill stream info except dataOffset, patched later.
    headBody[info1Off + 0] = 2;                                  // codec = DSP-ADPCM
    headBody[info1Off + 1] = (byte)(loop ? 1 : 0);
    headBody[info1Off + 2] = (byte)numChannels;
    headBody[info1Off + 3] = 0;
    BinaryPrimitives.WriteUInt16BigEndian(headBody.AsSpan(info1Off + 4), (ushort)sampleRate);
    BinaryPrimitives.WriteUInt32BigEndian(headBody.AsSpan(info1Off + 8), (uint)loopStart);
    BinaryPrimitives.WriteUInt32BigEndian(headBody.AsSpan(info1Off + 12), (uint)totalSamples);
    // info1Off+16 dataOffset patched later.
    BinaryPrimitives.WriteUInt32BigEndian(headBody.AsSpan(info1Off + 20), (uint)numBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(headBody.AsSpan(info1Off + 24), (uint)BlockSize);
    BinaryPrimitives.WriteUInt32BigEndian(headBody.AsSpan(info1Off + 28), (uint)SamplesPerBlock);
    BinaryPrimitives.WriteUInt32BigEndian(headBody.AsSpan(info1Off + 32), (uint)finalBlockSize);
    BinaryPrimitives.WriteUInt32BigEndian(headBody.AsSpan(info1Off + 36), (uint)finalBlockSamples);
    BinaryPrimitives.WriteUInt32BigEndian(headBody.AsSpan(info1Off + 40), (uint)finalBlockSizePadded);

    // Channel info sub-chunk.
    headBody[info3Off] = (byte)numChannels;
    for (var c = 0; c < numChannels; ++c) {
      var entryOff = chanEntriesBase + c * 0x38;
      WriteRef(headBody, chanRefBase + c * 8, entryOff);
      // entry: marker + coefOffset(rel refBase) pointing to the coef table (entryOff+8).
      var coefOff = entryOff + 8;
      WriteRef(headBody, entryOff, coefOff);
      for (var i = 0; i < 16; ++i)
        BinaryPrimitives.WriteInt16BigEndian(headBody.AsSpan(coefOff + i * 2), coefs[c][i]);
      // remaining gain/predictor/history fields are left zero (loop-less stream).
    }

    head.Write(headBody);
    var headBytes = head.ToArray();

    // ── Assemble file: RSTM(0x40) + ADPC(minimal) + DATA. ──
    var headChunkSize = 8 + headBytes.Length;
    headChunkSize = (headChunkSize + 0x1F) & ~0x1F;

    // Minimal ADPC chunk (history seek table) — header + zero body; the reader ignores it.
    const int adpcBodyLen = 0x20;
    var adpcChunkSize = 8 + adpcBodyLen;

    var headOffset = 0x40;
    var adpcOffset = headOffset + headChunkSize;
    var dataOffset = adpcOffset + adpcChunkSize;

    // DATA body: 0x20-byte header pad + interleaved blocks.
    const int dataHeaderPad = 0x20;
    var audioStart = dataOffset + 8 + dataHeaderPad;

    using var data = new MemoryStream();
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
        data.Write(buf);
      }
    }
    var audioBytes = data.ToArray();
    var dataBodyLen = dataHeaderPad + audioBytes.Length;
    var dataChunkSize = 8 + dataBodyLen;
    dataChunkSize = (dataChunkSize + 0x1F) & ~0x1F;

    // Patch absolute dataOffset into stream info (headBytes is the HEAD body, no chunk header).
    BinaryPrimitives.WriteUInt32BigEndian(headBytes.AsSpan(info1Off + 16), (uint)audioStart);

    var fileSize = dataOffset + dataChunkSize;
    var file = new byte[fileSize];

    // RSTM header.
    "RSTM"u8.CopyTo(file);
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), 0xFEFF);
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), 0x0100); // version 1.0
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), (uint)fileSize);
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(12), 0x40);  // header size
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(14), 2);     // chunk count (HEAD+DATA; ADPC ancillary)
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), (uint)headOffset);
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(20), (uint)headChunkSize);
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(24), (uint)adpcOffset);
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(28), (uint)adpcChunkSize);
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(32), (uint)dataOffset);
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(36), (uint)dataChunkSize);

    // HEAD chunk.
    "HEAD"u8.CopyTo(file.AsSpan(headOffset));
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headOffset + 4), (uint)headChunkSize);
    headBytes.CopyTo(file.AsSpan(headOffset + 8));

    // ADPC chunk (zeroed body).
    "ADPC"u8.CopyTo(file.AsSpan(adpcOffset));
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(adpcOffset + 4), (uint)adpcChunkSize);

    // DATA chunk.
    "DATA"u8.CopyTo(file.AsSpan(dataOffset));
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(dataOffset + 4), (uint)dataChunkSize);
    audioBytes.CopyTo(file.AsSpan(dataOffset + 8 + dataHeaderPad));

    return file;
  }

  private static void WriteRef(byte[] buf, int at, int relOffset) {
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at), 0x01000000);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 4), (uint)relOffset);
  }
}
