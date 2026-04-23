#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Mp3;

/// <summary>
/// Stream-level metadata extracted from an MP3 stream's first valid frame header
/// (and Xing/Info VBR header, if present). <see cref="DurationSamples"/> is
/// estimated from frame count when a VBR header is found, otherwise from byte size /
/// average bitrate; pass -1 when unknown.
/// </summary>
public sealed record Mp3StreamInfo(int SampleRate, int Channels, int Bitrate, long DurationSamples);

/// <summary>
/// Clean-room MP3 decoder. Ported from <c>minimp3</c>
/// (https://github.com/lieff/minimp3, commit 7b590fdcfa5a79c033e76eacc05d0c3e4c79f536,
/// public domain / CC0). Decodes MPEG-1 / MPEG-2 / MPEG-2.5 Layer III to interleaved
/// little-endian signed 16-bit PCM. Layer I and Layer II are detected and rejected
/// with <see cref="NotSupportedException"/>; free-format bitrates, ancillary data
/// and CRC verification are not implemented (CRC bytes are skipped if present).
/// </summary>
public static class Mp3Codec {

  private const int HdrSize = 4;

  /// <summary>
  /// Decodes an MP3 stream into raw interleaved little-endian PCM (signed 16-bit
  /// per channel) on <paramref name="output"/>. The output sample rate / channel
  /// count are set by the first decoded frame; readers are expected to track those
  /// separately (e.g. via <see cref="ReadStreamInfo"/>).
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    byte[] data;
    if (input is MemoryStream ms && ms.TryGetBuffer(out var seg)) {
      data = new byte[seg.Count];
      Array.Copy(seg.Array!, seg.Offset, data, 0, seg.Count);
    } else {
      using var tmp = new MemoryStream();
      input.CopyTo(tmp);
      data = tmp.ToArray();
    }

    var dec = new DecoderState();
    var pos = SkipId3v2(data, 0);

    // Guard is `≤` (not `<`) so a single 4-byte header at the very end of the
    // buffer still enters DecodeFrame — needed for Layer I/II rejection on
    // header-only inputs and for short-test-vector decode paths.
    while (pos <= data.Length - HdrSize) {
      var consumed = DecodeFrame(dec, data, pos, data.Length - pos, output);
      if (consumed <= 0) {
        // No frame found at remaining position — advance one byte and resync, or stop if nothing left.
        if (consumed == 0) break;
        pos += -consumed; // negative encodes byte advance for resync
      } else {
        pos += consumed;
      }
    }
  }

  /// <summary>Reads stream-level info (sample rate, channels, average bitrate, duration in samples).</summary>
  public static Mp3StreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);

    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    var pos = SkipId3v2(data, 0);
    pos = FindNextSync(data, pos);
    if (pos < 0 || pos + HdrSize > data.Length)
      throw new InvalidDataException("MP3 stream contains no valid frame syncword.");

    var hdr = Mp3FrameHeader.Parse(data.AsSpan(pos, HdrSize));
    var sampleRate = hdr.SampleRateHz;
    var channels = hdr.Channels;
    var bitrate = hdr.BitrateKbps;

    // Try to parse an Xing/Info VBR header in the first frame's side-info area.
    long durationSamples = -1;
    var xingFrames = TryReadXingFrameCount(data, pos, hdr);
    if (xingFrames > 0)
      durationSamples = (long)xingFrames * hdr.SamplesPerFrame;
    else if (bitrate > 0 && sampleRate > 0)
      durationSamples = (long)(data.Length - pos) * 8 * sampleRate / (bitrate * 1000);

    return new Mp3StreamInfo(sampleRate, channels, bitrate, durationSamples);
  }

  // -- ID3v2 / Xing parsing --------------------------------------------------

  private static int SkipId3v2(byte[] data, int pos) {
    if (pos + 10 > data.Length) return pos;
    if (data[pos] == (byte)'I' && data[pos + 1] == (byte)'D' && data[pos + 2] == (byte)'3') {
      // Synch-safe size: 4 bytes, each holds 7 bits.
      var size = (data[pos + 6] << 21) | (data[pos + 7] << 14) | (data[pos + 8] << 7) | data[pos + 9];
      return pos + 10 + size;
    }
    return pos;
  }

  private static int TryReadXingFrameCount(byte[] data, int framePos, Mp3FrameHeader hdr) {
    // Xing/Info tag offset: 4 (header) + 32 or 17 bytes of side info, depending on version/channels.
    int sideInfoSize = hdr.IsMpeg1 ? (hdr.IsMono ? 17 : 32) : (hdr.IsMono ? 9 : 17);
    var xingPos = framePos + 4 + sideInfoSize;
    if (xingPos + 8 > data.Length) return 0;
    var tag = (data[xingPos] << 24) | (data[xingPos + 1] << 16) | (data[xingPos + 2] << 8) | data[xingPos + 3];
    var isXing = tag == 0x58696E67; // "Xing"
    var isInfo = tag == 0x496E666F; // "Info"
    if (!isXing && !isInfo) return 0;

    var flags = (data[xingPos + 4] << 24) | (data[xingPos + 5] << 16) | (data[xingPos + 6] << 8) | data[xingPos + 7];
    if ((flags & 1) == 0) return 0; // no frame-count present
    if (xingPos + 12 > data.Length) return 0;
    return BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(xingPos + 8, 4));
  }

  private static int FindNextSync(byte[] data, int start) {
    for (var i = start; i < data.Length - 1; i++) {
      if (data[i] == 0xFF && (data[i + 1] & 0xE0) == 0xE0) {
        if (i + HdrSize > data.Length) return -1;
        try { _ = Mp3FrameHeader.Parse(data.AsSpan(i, HdrSize)); return i; }
        catch (InvalidDataException) { /* keep scanning */ }
      }
    }
    return -1;
  }

  // -- Decoder state ---------------------------------------------------------

  private sealed class DecoderState {
    public readonly float[] MdctOverlap0 = new float[9 * 32];
    public readonly float[] MdctOverlap1 = new float[9 * 32];
    public readonly float[] QmfState = new float[15 * 2 * 32];
    public readonly byte[] ReservBuf = new byte[Mp3Layer3.MaxBitReservoirBytes];
    public int Reserv;
  }

  private sealed class Scratch {
    public readonly byte[] MainData = new byte[Mp3Layer3.MaxBitReservoirBytes + Mp3Layer3.MaxL3FramePayloadBytes];
    public readonly Mp3Layer3.GrInfo[] GrInfo = {
      new Mp3Layer3.GrInfo(), new Mp3Layer3.GrInfo(), new Mp3Layer3.GrInfo(), new Mp3Layer3.GrInfo()
    };
    public readonly float[] Grbuf0 = new float[576];
    public readonly float[] Grbuf1 = new float[576];
    public readonly float[] Scf = new float[40];
    public readonly float[] Syn = new float[(18 + 15) * 2 * 32];
    public readonly byte[][] IstPos = { new byte[39], new byte[39] };
    public Mp3BitReader? Bs;
  }

  // Returns: positive = bytes consumed; 0 = stop; negative = -bytes-to-advance for resync.
  private static int DecodeFrame(DecoderState dec, byte[] data, int pos, int avail, Stream output) {
    var syncIdx = FindNextSync(data, pos);
    if (syncIdx < 0) return 0;
    if (syncIdx > pos) return -(syncIdx - pos); // skip junk

    var hdr = Mp3FrameHeader.Parse(data.AsSpan(syncIdx, HdrSize));
    if (hdr.Layer != 3) {
      throw new NotSupportedException(
        $"MPEG Layer {(hdr.Layer == 1 ? "I" : "II")} is not supported by this decoder " +
        "(only Layer III is implemented).");
    }

    var frameSize = hdr.FrameLengthBytes;
    if (frameSize == 0) {
      // Free-format — not supported, advance past header.
      return -1;
    }
    if (syncIdx + frameSize > data.Length) return 0;

    var scratch = new Scratch();
    var bsFrame = new Mp3BitReader(SliceBuffer(data, syncIdx + HdrSize, frameSize - HdrSize), frameSize - HdrSize);

    if (hdr.HasCrc) bsFrame.GetBits(16);

    var mainDataBegin = Mp3Layer3.ReadSideInfo(bsFrame, scratch.GrInfo, hdr);
    if (mainDataBegin < 0 || bsFrame.Pos > bsFrame.Limit) {
      ResetDecoder(dec);
      return frameSize;
    }

    var success = RestoreReservoir(dec, bsFrame, scratch, mainDataBegin);

    var samplesPerGranule = 576;
    var granules = hdr.IsMpeg1 ? 2 : 1;
    var pcm = new short[samplesPerGranule * hdr.Channels * granules];
    var pcmOff = 0;

    if (success) {
      for (var igr = 0; igr < granules; igr++, pcmOff += samplesPerGranule * hdr.Channels) {
        Array.Clear(scratch.Grbuf0, 0, scratch.Grbuf0.Length);
        Array.Clear(scratch.Grbuf1, 0, scratch.Grbuf1.Length);
        DecodeGranule(dec, scratch, hdr, igr * hdr.Channels, hdr.Channels);
        SynthOneGranule(dec, scratch, hdr.Channels, pcm, pcmOff);
      }
    }

    SaveReservoir(dec, scratch);

    // Write PCM as little-endian int16 interleaved.
    if (success) {
      var bytes = new byte[pcm.Length * 2];
      for (var i = 0; i < pcm.Length; i++) {
        bytes[i * 2] = (byte)(pcm[i] & 0xFF);
        bytes[i * 2 + 1] = (byte)((pcm[i] >> 8) & 0xFF);
      }
      output.Write(bytes, 0, bytes.Length);
    }

    return frameSize;
  }

  private static byte[] SliceBuffer(byte[] data, int offset, int count) {
    var buf = new byte[count + 8]; // +8 padding so the bit reader can pre-fetch 4 bytes safely
    Array.Copy(data, offset, buf, 0, count);
    return buf;
  }

  private static void ResetDecoder(DecoderState dec) {
    Array.Clear(dec.MdctOverlap0, 0, dec.MdctOverlap0.Length);
    Array.Clear(dec.MdctOverlap1, 0, dec.MdctOverlap1.Length);
    Array.Clear(dec.QmfState, 0, dec.QmfState.Length);
    Array.Clear(dec.ReservBuf, 0, dec.ReservBuf.Length);
    dec.Reserv = 0;
  }

  private static bool RestoreReservoir(DecoderState dec, Mp3BitReader bs, Scratch s, int mainDataBegin) {
    var frameBytes = (bs.Limit - bs.Pos) / 8;
    var bytesHave = Math.Min(dec.Reserv, mainDataBegin);
    Array.Copy(dec.ReservBuf, Math.Max(0, dec.Reserv - mainDataBegin), s.MainData, 0, bytesHave);
    Array.Copy(bs.Buf, bs.Pos / 8, s.MainData, bytesHave, frameBytes);
    s.Bs = new Mp3BitReader(PadCopy(s.MainData, bytesHave + frameBytes), bytesHave + frameBytes);
    return dec.Reserv >= mainDataBegin;
  }

  private static byte[] PadCopy(byte[] src, int len) {
    var buf = new byte[len + 8];
    Array.Copy(src, 0, buf, 0, len);
    return buf;
  }

  private static void SaveReservoir(DecoderState dec, Scratch s) {
    if (s.Bs == null) return;
    var pos = (s.Bs.Pos + 7) / 8;
    var remains = s.Bs.Limit / 8 - pos;
    if (remains > Mp3Layer3.MaxBitReservoirBytes) {
      pos += remains - Mp3Layer3.MaxBitReservoirBytes;
      remains = Mp3Layer3.MaxBitReservoirBytes;
    }
    if (remains > 0) Array.Copy(s.MainData, pos, dec.ReservBuf, 0, remains);
    dec.Reserv = remains;
  }

  private static void DecodeGranule(DecoderState dec, Scratch s, in Mp3FrameHeader hdr, int grBase, int nch) {
    var bs = s.Bs!;
    for (var ch = 0; ch < nch; ch++) {
      var gr = s.GrInfo[grBase + ch];
      var layer3GrLimit = bs.Pos + gr.Part23Length;
      Mp3Layer3.DecodeScalefactors(hdr, s.IstPos[ch], bs, gr, s.Scf, ch);
      Mp3Layer3.Huffman(ch == 0 ? s.Grbuf0 : s.Grbuf1, bs, gr, s.Scf, layer3GrLimit);
    }

    if (hdr.IsIntensityStereo) {
      // Stereo decorrelation operates on a flat 2*576 buffer (left + right contiguous).
      var combined = new float[1152];
      Array.Copy(s.Grbuf0, 0, combined, 0, 576);
      Array.Copy(s.Grbuf1, 0, combined, 576, 576);
      Mp3Layer3.IntensityStereo(combined, 0, s.IstPos[1], s.GrInfo[grBase], s.GrInfo[grBase + (nch > 1 ? 1 : 0)], hdr);
      Array.Copy(combined, 0, s.Grbuf0, 0, 576);
      Array.Copy(combined, 576, s.Grbuf1, 0, 576);
    } else if (hdr.IsMsStereo) {
      var combined = new float[1152];
      Array.Copy(s.Grbuf0, 0, combined, 0, 576);
      Array.Copy(s.Grbuf1, 0, combined, 576, 576);
      Mp3Layer3.MidSideStereo(combined, 0, 576);
      Array.Copy(combined, 0, s.Grbuf0, 0, 576);
      Array.Copy(combined, 576, s.Grbuf1, 0, 576);
    }

    var srMy = hdr.SampleRateIndex + (hdr.IsMpeg1 ? 0 : 3) + (hdr.IsMpeg25 ? 3 : 0);
    for (var ch = 0; ch < nch; ch++) {
      var gr = s.GrInfo[grBase + ch];
      var aaBands = 31;
      var nLongBands = (gr.MixedBlockFlag != 0 ? 2 : 0) << (srMy == 2 ? 1 : 0);

      var grbuf = ch == 0 ? s.Grbuf0 : s.Grbuf1;
      var overlap = ch == 0 ? dec.MdctOverlap0 : dec.MdctOverlap1;

      if (gr.NShortSfb != 0) {
        aaBands = nLongBands - 1;
        var scratchTmp = new float[576];
        Mp3Layer3.ReorderShort(grbuf, nLongBands * 18, scratchTmp, gr.Sfbtab, gr.NLongSfb);
      }

      Mp3Layer3.Antialias(grbuf, 0, aaBands);
      Mp3Layer3.ImdctGr(grbuf, 0, overlap, 0, gr.BlockType, nLongBands);
      Mp3Layer3.ChangeSign(grbuf, 0);
    }
  }

  private static void SynthOneGranule(DecoderState dec, Scratch s, int nch, short[] pcm, int pcmOff) {
    // Combine grbuf channels into one contiguous 2*576 buffer that the synthesis step expects.
    var combined = new float[1152];
    Array.Copy(s.Grbuf0, 0, combined, 0, 576);
    if (nch > 1) Array.Copy(s.Grbuf1, 0, combined, 576, 576);
    Mp3Synthesis.SynthGranule(dec.QmfState, combined, 18, nch, pcm, pcmOff, s.Syn);
  }
}
