using Codec.Aac;

namespace Compression.Tests.Codecs.Aac;

/// <summary>
/// Builders for hand-crafted, spec-valid AAC-LC ADTS frames used by the decode
/// tests. All frames use ONLY_LONG windows at the given sample-rate index.
/// </summary>
internal static class AacTestFrames {

  /// <summary>Public MSB-first bit writer for SBR payload tests.</summary>
  public sealed class SbrBitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bitsFilled;

    public void Write(uint value, int count) {
      for (var i = count - 1; i >= 0; --i) {
        var bit = (int)((value >> i) & 1);
        this._cur = (this._cur << 1) | bit;
        if (++this._bitsFilled == 8) {
          this._bytes.Add((byte)this._cur);
          this._cur = 0;
          this._bitsFilled = 0;
        }
      }
    }

    public byte[] ToArray() {
      if (this._bitsFilled > 0)
        this._bytes.Add((byte)(this._cur << (8 - this._bitsFilled)));
      return [.. this._bytes];
    }

    public int BitsWritten => this._bytes.Count * 8 + this._bitsFilled;
  }

  /// <summary>MSB-first bit writer mirroring <see cref="AacBitReader"/>'s order.</summary>
  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _cur;
    private int _bitsFilled;

    public void Write(uint value, int count) {
      for (var i = count - 1; i >= 0; --i) {
        var bit = (int)((value >> i) & 1);
        this._cur = (this._cur << 1) | bit;
        if (++this._bitsFilled == 8) {
          this._bytes.Add((byte)this._cur);
          this._cur = 0;
          this._bitsFilled = 0;
        }
      }
    }

    public byte[] ToArray() {
      if (this._bitsFilled > 0)
        this._bytes.Add((byte)(this._cur << (8 - this._bitsFilled)));
      return [.. this._bytes];
    }
  }

  /// <summary>
  /// Wraps a raw_data_block payload in a 7-byte ADTS header (profile=1 LC),
  /// computing frame_length from the payload size.
  /// </summary>
  private static byte[] WrapAdts(byte[] payload, int sampleRateIndex, int channelConfig) {
    var frameLength = AacAdtsReader.ShortHeaderLength + payload.Length;
    var header = AacAdtsReader.BuildHeader(
      profile: 1, sampleRateIndex: sampleRateIndex, channelConfig: channelConfig,
      frameLength: frameLength);
    var frame = new byte[header.Length + payload.Length];
    header.CopyTo(frame, 0);
    payload.CopyTo(frame, header.Length);
    return frame;
  }

  // ── individual_channel_stream pieces ────────────────────────────────────────

  // Silent ICS: ONLY_LONG, sine window, max_sfb = 0 -> no sections, no scale
  // factors, no spectral data.
  private static void WriteSilentIcs(BitWriter w) {
    w.Write(0, 8);   // global_gain
    w.Write(0, 1);   // ics_reserved_bit
    w.Write(0, 2);   // window_sequence = ONLY_LONG
    w.Write(0, 1);   // window_shape = sine
    w.Write(0, 6);   // max_sfb = 0
    w.Write(0, 1);   // predictor_data_present = 0
    // section_data: max_sfb == 0 -> nothing
    // scale_factor_data: nothing
    w.Write(0, 1);   // pulse_data_present
    w.Write(0, 1);   // tns_data_present
    w.Write(0, 1);   // gain_control_data_present
    // spectral_data: nothing
  }

  /// <summary>A single-element silence frame (SCE for mono, CPE for stereo).</summary>
  public static byte[] SilenceFrame(int channelConfig, int sampleRateIndex) {
    var w = new BitWriter();
    if (channelConfig == 1) {
      w.Write((uint)AacElementType.Sce, 3);
      w.Write(0, 4); // element_instance_tag
      WriteSilentIcs(w);
    } else {
      w.Write((uint)AacElementType.Cpe, 3);
      w.Write(0, 4); // element_instance_tag
      w.Write(0, 1); // common_window = 0 (each channel carries its own ICS)
      WriteSilentIcs(w); // left
      WriteSilentIcs(w); // right
    }
    w.Write((uint)AacElementType.End, 3);
    return WrapAdts(w.ToArray(), sampleRateIndex, channelConfig);
  }

  /// <summary>
  /// A mono frame coding exactly one non-zero quantised coefficient in sfb 0 using
  /// the escape codebook (cb 11). The first two coefficients of sfb 0 are the pair
  /// (1, 0); the value 1 is below the escape threshold so no escape sequence is
  /// emitted, just the codeword plus one sign bit.
  /// </summary>
  public static byte[] SingleCoefficientFrame(int sampleRateIndex) {
    const int cb = AacHuffmanTables.EscapeHcb; // 11
    // Codeword for the pair (1,0): unsigned base 17 -> index = 1*17 + 0 = 17.
    const int pairIndex = 1 * 17 + 0;
    var code = AacHuffmanTables.SpectralCodes[cb - 1][pairIndex];
    var bits = AacHuffmanTables.SpectralBits[cb - 1][pairIndex];

    var w = new BitWriter();
    w.Write((uint)AacElementType.Sce, 3);
    w.Write(0, 4); // element_instance_tag
    w.Write(120, 8); // global_gain (scale factor baseline)
    w.Write(0, 1);   // ics_reserved_bit
    w.Write(0, 2);   // window_sequence = ONLY_LONG
    w.Write(0, 1);   // window_shape = sine
    w.Write(1, 6);   // max_sfb = 1 (code only sfb 0)
    w.Write(0, 1);   // predictor_data_present

    // section_data: one section, codebook 11, length 1 sfb (5-bit length field).
    w.Write((uint)cb, 4);
    w.Write(1, 5);   // sect_len_incr = 1

    // scale_factor_data: one delta for sfb 0. HCB_SF index 60 == delta 0 has the
    // shortest code; use it so the band gain equals 2^((120-100)/4) = 32.
    var sfCode = AacHuffmanTables.ScaleFactorCodes[60];
    var sfBits = AacHuffmanTables.ScaleFactorBits[60];
    w.Write(sfCode, sfBits);

    w.Write(0, 1);   // pulse_data_present
    w.Write(0, 1);   // tns_data_present
    w.Write(0, 1);   // gain_control_data_present

    // spectral_data: sfb 0 spans bins [0,4) at this rate -> two cb-11 pairs.
    // Pair 0 = (1,0): codeword + sign bit for the leading 1.
    w.Write(code, bits);
    w.Write(0, 1);   // sign bit for the value 1 (0 = positive)
    // (value 0 carries no sign bit)
    // Pair 1 = (0,0): index 0.
    var zeroCode = AacHuffmanTables.SpectralCodes[cb - 1][0];
    var zeroBits = AacHuffmanTables.SpectralBits[cb - 1][0];
    w.Write(zeroCode, zeroBits);

    w.Write((uint)AacElementType.End, 3);
    return WrapAdts(w.ToArray(), sampleRateIndex, channelConfig: 1);
  }

  // ── SBR (HE-AAC) payloads ────────────────────────────────────────────────────

  /// <summary>
  /// Writes an SBR extension payload (positioned after the 4-bit extension type):
  /// a header (bs_amp_res, the given start/stop/xover, no header extras), then a
  /// FIXFIX single-channel-element grid with one envelope and all-zero envelope /
  /// noise data. Built for the 22050-core configuration (n[0]=3, nQ=1).
  /// </summary>
  public static void WriteSbrHeaderAndFixFixSce(SbrBitWriter w, int startFreq, int stopFreq, int xover) {
    // bs_header_flag = 1, then the header.
    w.Write(1, 1);
    w.Write(0, 1);                 // bs_amp_res
    w.Write((uint)startFreq, 4);   // bs_start_freq
    w.Write((uint)stopFreq, 4);    // bs_stop_freq
    w.Write((uint)xover, 3);       // bs_xover_band
    w.Write(0, 2);                 // bs_reserved
    w.Write(0, 1);                 // bs_header_extra_1 = 0 (freq_scale=2, alter=1, noise=2)
    w.Write(0, 1);                 // bs_header_extra_2 = 0

    // sbr_single_channel_element.
    w.Write(0, 1);                 // bs_data_extra = 0
    // sbr_grid: FIXFIX, bs_num_env = 1<<0 = 1.
    w.Write(0, 2);                 // bs_frame_class = FIXFIX
    w.Write(0, 2);                 // bs_num_env_bits -> 1 envelope
    w.Write(0, 1);                 // bs_freq_res[1] = 0 (low res, n[0]=3)
    // dtdf: bs_num_env(1) + bs_num_noise(1).
    w.Write(0, 1);                 // bs_df_env[0]   = 0 -> envelope codes from scratch
    w.Write(0, 1);                 // bs_df_noise[0] = 0
    // invf: nQ (=1 for this config) * 2 bits.
    w.Write(0, 2);                 // bs_invf_mode[0][0]
    // envelope (df_env=0, non-coupling, amp_res=0 -> 7-bit start, n[0]-1 freq deltas):
    w.Write(0, 7);                 // bs_env_start_value
    WriteZeroFEnv15Delta(w);       // band 1 (freq huffman, zero delta)
    WriteZeroFEnv15Delta(w);       // band 2
    // noise (df_noise=0 -> 5-bit start, nQ-1=0 deltas):
    w.Write(0, 5);                 // bs_noise_start_value
    // add_harmonic flag.
    w.Write(0, 1);                 // bs_add_harmonic_flag = 0
  }

  // The shortest F_HUFFMAN_ENV_1_5DB codeword (index 60 == zero delta).
  private static void WriteZeroFEnv15Delta(SbrBitWriter w) {
    var code = AacSbrTables.FEnv15Codes[60];
    var bits = AacSbrTables.FEnv15Bits[60];
    w.Write(code, bits);
  }

  /// <summary>
  /// A mono (or stereo) LC silence frame followed by a FIL element carrying an
  /// EXT_SBR_DATA payload. The LC core decodes to silence; the SBR header flips the
  /// decoder's SBR-detected flag (and so the reported effective rate doubles).
  /// </summary>
  public static byte[] SilenceFrameWithSbr(int channelConfig, int sampleRateIndex) {
    var w = new BitWriter();
    if (channelConfig == 1) {
      w.Write((uint)AacElementType.Sce, 3);
      w.Write(0, 4);
      WriteSilentIcs(w);
    } else {
      w.Write((uint)AacElementType.Cpe, 3);
      w.Write(0, 4);
      w.Write(0, 1);
      WriteSilentIcs(w);
      WriteSilentIcs(w);
    }

    // FIL element carrying EXT_SBR_DATA. Build the SBR bits, byte-pad, and wrap in
    // a fill element whose 4-bit count gives the payload byte length.
    var sbrW = new SbrBitWriter();
    sbrW.Write(13, 4); // extension_type = EXT_SBR_DATA
    WriteSbrHeaderAndFixFixSce(sbrW, startFreq: 5, stopFreq: 0, xover: 3);
    var sbrBytes = sbrW.ToArray();

    w.Write((uint)AacElementType.Fil, 3);
    if (sbrBytes.Length < 15) {
      w.Write((uint)sbrBytes.Length, 4); // count
    } else {
      w.Write(15, 4);
      w.Write((uint)(sbrBytes.Length - 15 + 1), 8); // esc_count
    }
    foreach (var b in sbrBytes)
      w.Write(b, 8);

    w.Write((uint)AacElementType.End, 3);
    return WrapAdts(w.ToArray(), sampleRateIndex, channelConfig);
  }
}
