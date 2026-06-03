#pragma warning disable CS1591

namespace Compression.Tests.Codecs.Mp3;

/// <summary>
/// Hand-builds minimal valid MPEG-1 Layer I / Layer II frames for decoder tests. There
/// is no Layer I/II encoder in the tree, so these frames are crafted bit-by-bit from
/// ISO/IEC 11172-3 frame syntax: a 4-byte header followed by an MSB-first payload of
/// bit-allocation codes, scale-factor selection info, scale factors and quantized
/// samples. "Silence" frames set every allocation to zero (no scalefactors / samples
/// follow), which the decoder must turn into pure digital silence.
/// </summary>
internal static class Mp3SyntheticFrames {

  // MPEG-1 Layer II, 128 kbps, 48 kHz, mono, no CRC → 384-byte frame, alloc table
  // g_alloc_L2M1 (27 subbands; band-group widths 4/4/3/2 bits).
  private const int MonoFrameBytes = 384;
  // MPEG-1 Layer II, 192 kbps, 48 kHz, stereo, no CRC → 576-byte frame.
  private const int StereoFrameBytes = 576;
  // MPEG-1 Layer I, 256 kbps, 48 kHz, mono, no CRC.
  // 384 samples * 256000 / 48000 / 8 = 256 bytes (already slot-aligned).
  private const int Layer1MonoFrameBytes = 256; // 256 kbps (bitrate index 8)

  /// <summary>Layer II mono frame with all bit allocations zero → decodes to silence.</summary>
  public static byte[] BuildLayerIIMonoSilenceFrame() {
    var frame = new byte[MonoFrameBytes];
    // Header FF FD 84 C0: MPEG-1, Layer II, no CRC, 128 kbps, 48 kHz, mono.
    frame[0] = 0xFF; frame[1] = 0xFD; frame[2] = 0x84; frame[3] = 0xC0;
    // 27 bands, mono → 27 allocation codes; all zero bits ⇒ ba=0 for every band.
    // Leaving the payload all-zero already encodes that, so nothing else to write.
    return frame;
  }

  /// <summary>Layer II stereo frame with all bit allocations zero → decodes to silence.</summary>
  public static byte[] BuildLayerIIStereoSilenceFrame() {
    var frame = new byte[StereoFrameBytes];
    // Header FF FD A4 00: MPEG-1, Layer II, no CRC, 192 kbps, 48 kHz, stereo.
    frame[0] = 0xFF; frame[1] = 0xFD; frame[2] = 0xA4; frame[3] = 0x00;
    return frame;
  }

  /// <summary>Layer I mono frame with all bit allocations zero → decodes to silence.</summary>
  public static byte[] BuildLayerIMonoSilenceFrame() {
    var frame = new byte[Layer1MonoFrameBytes];
    // Header FF FF 84 C0: MPEG-1, Layer I, no CRC, 256 kbps, 48 kHz, mono.
    // byte2 = bitrate idx 1000 (256k) + samplerate 01 (48k) = 1000 0100 = 0x84.
    frame[0] = 0xFF; frame[1] = 0xFF; frame[2] = 0x84; frame[3] = 0xC0;
    // Layer I reads 4-bit allocation per band; all-zero payload ⇒ every band ba=0.
    return frame;
  }

  /// <summary>
  /// Layer II mono frame with subband 0 carrying a small (3-level) quantization class
  /// and a non-zero scale factor + sample, so the decoder produces non-zero PCM.
  /// </summary>
  public static byte[] BuildLayerIIMonoOneActiveSubbandFrame() {
    var frame = new byte[MonoFrameBytes];
    frame[0] = 0xFF; frame[1] = 0xFD; frame[2] = 0x84; frame[3] = 0xC0;

    var bw = new BitWriter(frame, 4 * 8); // start writing after the 4-byte header

    // --- Bit allocation, 27 bands, group widths 4/4/4 (first 3 bands), then 4,3,2... ---
    // g_alloc_L2M1 rows: {0,4,3},{16,4,8},{32,3,12},{40,2,7}.
    // Band 0 uses code-tab offset 0, width 4. Code value 1 → bitalloc table entry 17
    // (grouped 3-level class). All other bands: code 0 → ba=0.
    // Band 0 (width 4): write 1 → ba = g_bitalloc_code_tab[0 + 1] = 17.
    bw.Write(1, 4);
    // Bands 1..2 (width 4): zero.
    bw.Write(0, 4);
    bw.Write(0, 4);
    // Bands 3..10 (width 4): zero.
    for (var i = 0; i < 8; i++) bw.Write(0, 4);
    // Bands 11..22 (width 3): zero.
    for (var i = 0; i < 12; i++) bw.Write(0, 3);
    // Bands 23..26 (width 2): zero.
    for (var i = 0; i < 4; i++) bw.Write(0, 2);

    // --- scfcod for band 0 (the only allocated band): 2 bits. Use 0 → 3 scale factors. ---
    bw.Write(0, 2);

    // --- 3 scale factors for band 0 (6 bits each). Index 10 → moderate gain. ---
    bw.Write(10, 6);
    bw.Write(10, 6);
    bw.Write(10, 6);

    // --- Samples for band 0: ba=17 ⇒ grouped 3-level, mod=3, read 5 bits per triplet. ---
    // 4 groups, group_size 3 ⇒ one 5-bit code per group encodes 3 samples. Use a code
    // whose decoded levels are non-zero (code 13 → levels 1,1,1 around the centre).
    for (var g = 0; g < 4; g++) bw.Write(13, 5);

    return frame;
  }

  /// <summary>Minimal MSB-first bit writer over a fixed byte buffer.</summary>
  private sealed class BitWriter {
    private readonly byte[] _buf;
    private int _bitPos;

    public BitWriter(byte[] buf, int startBit) {
      this._buf = buf;
      this._bitPos = startBit;
    }

    public void Write(int value, int bits) {
      for (var i = bits - 1; i >= 0; i--) {
        var bit = (value >> i) & 1;
        if (bit != 0)
          this._buf[this._bitPos >> 3] |= (byte)(0x80 >> (this._bitPos & 7));
        this._bitPos++;
      }
    }
  }
}
