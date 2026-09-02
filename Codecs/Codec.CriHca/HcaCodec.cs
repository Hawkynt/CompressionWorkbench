#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace Codec.CriHca;

/// <summary>
/// CRI HCA (High Compression Audio) decoder — a faithful, decode-only port of FFmpeg's
/// <c>libavcodec/hcadec.c</c> together with its <c>hca_data.h</c> tables (cross-checked
/// against CRI's reference <c>clHCA</c> / vgmstream / VGAudio). HCA is the modern CRI
/// Middleware game-audio codec: an MDCT transform coder carrying base bands, optional
/// intensity-coupled stereo bands, and high-frequency-reconstruction (HFR) groups.
/// <para>The container is a CRC-checked header (<c>"HCA\0"</c>, optionally masked with
/// <c>0x7F</c> per byte when the stream is keyed) whose chunks declare channel/rate/
/// frame counts (<c>fmt</c>), the compression parameters (<c>comp</c> / <c>dec</c>),
/// the ATH curve (<c>ath</c>), the cipher (<c>ciph</c>) and optional <c>vbr</c>,
/// <c>loop</c>, <c>rva</c>, <c>comm</c> metadata. Frames are fixed-size, each carrying
/// 8 sub-frames of 128 samples per channel (1024 samples/frame/channel) and prefixed
/// with a CRC-16 over the frame body.</para>
/// <para>Cipher type 0 (none) and type 1 (keyless static table) are supported; type 56
/// (56-bit keyed) decryption is recognised but not performed — callers fall back to a
/// header-only view. MS-stereo streams (a rare v3.0 feature) are likewise recognised
/// but not decoded, matching the reference libraries.</para>
/// </summary>
public sealed class HcaCodec {

  /// <summary>Samples produced per frame per channel (8 sub-frames × 128).</summary>
  public const int SamplesPerFrame = 1024;

  /// <summary>Samples in one sub-frame / spectral coefficients per channel.</summary>
  public const int SamplesPerSubframe = 128;

  private const int SubframesPerFrame = 8;
  private const uint Mask = 0x7F7F7F7Fu;

  // Channel coupling type per the r01..r09 channel config (ffmpeg `chan_type` /
  // clHCA STEREO_PRIMARY=1, STEREO_SECONDARY=2, DISCRETE=0).
  private const int TypeDiscrete = 0;
  private const int TypeStereoPrimary = 1;
  private const int TypeStereoSecondary = 2;

  /// <summary>Parsed HCA header fields and decode parameters.</summary>
  public sealed class HcaHeader {
    /// <summary>
    /// Provides the version value.
    /// </summary>
public int Version;
    /// <summary>
    /// Provides the header size value.
    /// </summary>
public int HeaderSize;
    /// <summary>
    /// Provides the channels value.
    /// </summary>
public int Channels;
    /// <summary>
    /// Provides the sample rate value.
    /// </summary>
public int SampleRate;
    /// <summary>
    /// Provides the frame count value.
    /// </summary>
public int FrameCount;
    /// <summary>
    /// Provides the encoder delay value.
    /// </summary>
public int EncoderDelay;
    /// <summary>
    /// Provides the encoder padding value.
    /// </summary>
public int EncoderPadding;
    /// <summary>
    /// Provides the frame size value.
    /// </summary>
public int FrameSize;
    /// <summary>
    /// Provides the min resolution value.
    /// </summary>
public int MinResolution;
    /// <summary>
    /// Provides the max resolution value.
    /// </summary>
public int MaxResolution;
    /// <summary>
    /// Provides the track count value.
    /// </summary>
public int TrackCount;
    /// <summary>
    /// Provides the channel config value.
    /// </summary>
public int ChannelConfig;
    /// <summary>
    /// Provides the total band count value.
    /// </summary>
public int TotalBandCount;
    /// <summary>
    /// Provides the base band count value.
    /// </summary>
public int BaseBandCount;
    /// <summary>
    /// Provides the stereo band count value.
    /// </summary>
public int StereoBandCount;
    /// <summary>
    /// Provides the bands per hfr group value.
    /// </summary>
public int BandsPerHfrGroup;
    /// <summary>
    /// Provides the ms stereo value.
    /// </summary>
public int MsStereo;
    /// <summary>
    /// Provides the ath type value.
    /// </summary>
public int AthType;
    /// <summary>
    /// Provides the cipher type value.
    /// </summary>
public int CipherType;
    /// <summary>
    /// Provides the hfr group count value.
    /// </summary>
public int HfrGroupCount;
    /// <summary>
    /// Provides the has loop value.
    /// </summary>
public bool HasLoop;
    /// <summary>
    /// Provides the loop start frame value.
    /// </summary>
public int LoopStartFrame;
    /// <summary>
    /// Provides the loop end frame value.
    /// </summary>
public int LoopEndFrame;
    /// <summary>
    /// Provides the rva volume value.
    /// </summary>
public float RvaVolume = 1.0f;
    /// <summary>
    /// Provides the comment value.
    /// </summary>
public string Comment = "";

    /// <summary>Total decoded samples (frames × 1024), before delay/padding trimming.</summary>
    public long TotalSamples => (long)this.FrameCount * SamplesPerFrame;

    /// <summary>True for the 56-bit keyed cipher, which this decoder cannot decrypt.</summary>
    public bool IsKeyedCipher => this.CipherType == 56;

    /// <summary>True for MS-stereo streams, which the reference libraries do not decode.</summary>
    public bool IsMsStereo => this.MsStereo != 0;
  }

  private sealed class Channel {
    public int Type;
    public int CodedCount;
    public readonly int[] ScaleFactors = new int[SamplesPerSubframe];
    public readonly int[] Resolution = new int[SamplesPerSubframe];
    public readonly int[] Intensity = new int[SubframesPerFrame];
    public readonly int[] HfrScale = new int[SamplesPerSubframe];
    public readonly double[] Gain = new double[SamplesPerSubframe];
    public readonly double[] ImdctIn = new double[SamplesPerSubframe];
    public readonly double[] ImdctOut = new double[SamplesPerSubframe];
    public readonly HcaImdct Imdct = new();
  }

  private readonly HcaHeader _header;
  private readonly byte[] _cipherTable;
  private readonly byte[] _ath = new byte[SamplesPerSubframe];
  private readonly Channel[] _channels;

  private HcaCodec(HcaHeader header, byte[] cipherTable) {
    this._header = header;
    this._cipherTable = cipherTable;

    AthInit(this._ath, header.AthType, header.SampleRate);

    this._channels = new Channel[header.Channels];
    var channelTypes = AssignChannelTypes(header);
    for (var i = 0; i < header.Channels; i++) {
      var ch = new Channel { Type = channelTypes[i] };
      ch.CodedCount = ch.Type != TypeStereoSecondary
        ? header.BaseBandCount + header.StereoBandCount
        : header.BaseBandCount;
      this._channels[i] = ch;
    }
  }

  // ── Public surface ──────────────────────────────────────────────────────

  /// <summary>True when <paramref name="data"/> starts with a (possibly masked) HCA magic.</summary>
  public static bool LooksLikeHca(ReadOnlySpan<byte> data) {
    if (data.Length < 8)
      return false;
    var magic = BinaryPrimitives.ReadUInt32BigEndian(data) & Mask;
    return magic == 0x48434100u; // "HCA\0"
  }

  /// <summary>
  /// Parses and validates the HCA header (all chunks). Throws <see cref="InvalidDataException"/>
  /// for malformed headers (bad magic, CRC failure, unsupported version, illegal bands).
  /// </summary>
  public static (HcaHeader Header, byte[] CipherTable) ReadHeader(ReadOnlySpan<byte> data) {
    if (!LooksLikeHca(data))
      throw new InvalidDataException("Not an HCA stream (missing HCA\\0 magic).");

    var headerSize = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
    if (headerSize < 8 || headerSize > data.Length)
      throw new InvalidDataException("HCA header size out of range.");

    if (Crc16(data[..headerSize]) != 0)
      throw new InvalidDataException("HCA header CRC mismatch.");

    var h = new HcaHeader { HeaderSize = headerSize };
    var br = new HcaBitReader(data[..headerSize].ToArray(), 0, headerSize);

    // base header
    br.Skip(32);
    h.Version = br.GetBits(16);
    br.Skip(16); // header size (already read)
    h.AthType = h.Version < 0x200 ? 1 : 0;

    if (!ChunkMatches(br, 0x666D7400)) // "fmt\0"
      throw new InvalidDataException("HCA missing fmt chunk.");
    br.Skip(32);
    h.Channels = br.GetBits(8);
    h.SampleRate = br.GetBits(24);
    h.FrameCount = br.GetBits(32);
    h.EncoderDelay = br.GetBits(16);
    h.EncoderPadding = br.GetBits(16);
    if (h.Channels is < 1 or > 16)
      throw new InvalidDataException($"HCA channel count {h.Channels} out of range.");
    if (h.SampleRate <= 0)
      throw new InvalidDataException("HCA sample rate must be positive.");

    if (ChunkMatches(br, 0x636F6D70)) { // "comp"
      br.Skip(32);
      h.FrameSize = br.GetBits(16);
      h.MinResolution = br.GetBits(8);
      h.MaxResolution = br.GetBits(8);
      h.TrackCount = br.GetBits(8);
      h.ChannelConfig = br.GetBits(8);
      h.TotalBandCount = br.GetBits(8);
      h.BaseBandCount = br.GetBits(8);
      h.StereoBandCount = br.GetBits(8);
      h.BandsPerHfrGroup = br.GetBits(8);
      h.MsStereo = br.GetBits(8);
      br.Skip(8); // reserved
    } else if (ChunkMatches(br, 0x64656300)) { // "dec\0"
      br.Skip(32);
      h.FrameSize = br.GetBits(16);
      h.MinResolution = br.GetBits(8);
      h.MaxResolution = br.GetBits(8);
      h.TotalBandCount = br.GetBits(8) + 1;
      h.BaseBandCount = br.GetBits(8) + 1;
      h.TrackCount = br.GetBits(4);
      h.ChannelConfig = br.GetBits(4);
      var stereoType = br.GetBits(8);
      if (stereoType == 0)
        h.BaseBandCount = h.TotalBandCount;
      h.StereoBandCount = h.TotalBandCount - h.BaseBandCount;
      h.BandsPerHfrGroup = 0;
    } else
      throw new InvalidDataException("HCA missing comp/dec chunk.");

    if (ChunkMatches(br, 0x76627200)) { // "vbr\0"
      br.Skip(32);
      br.Skip(16 + 16);
    }
    if (ChunkMatches(br, 0x61746800)) { // "ath\0"
      br.Skip(32);
      h.AthType = br.GetBits(16);
    }
    if (ChunkMatches(br, 0x6C6F6F70)) { // "loop"
      br.Skip(32);
      h.LoopStartFrame = br.GetBits(32);
      h.LoopEndFrame = br.GetBits(32);
      br.Skip(16 + 16);
      h.HasLoop = true;
    }
    if (ChunkMatches(br, 0x63697068)) { // "ciph"
      br.Skip(32);
      h.CipherType = br.GetBits(16);
      if (h.CipherType is not (0 or 1 or 56))
        throw new InvalidDataException($"HCA unknown cipher type {h.CipherType}.");
    }
    if (ChunkMatches(br, 0x72766100)) { // "rva\0"
      br.Skip(32);
      var raw = (uint)br.GetBits(32);
      h.RvaVolume = BitConverter.UInt32BitsToSingle(raw);
    }
    if (ChunkMatches(br, 0x636F6D6D)) { // "comm"
      br.Skip(32);
      var len = br.GetBits(8);
      var sb = new StringBuilder(len);
      for (var i = 0; i < len; i++)
        sb.Append((char)br.GetBits(8));
      h.Comment = sb.ToString();
    }

    // Band-count sanity (matches the reference validations).
    if (h.TotalBandCount > SamplesPerSubframe ||
        h.BaseBandCount > SamplesPerSubframe ||
        h.StereoBandCount > SamplesPerSubframe ||
        h.BaseBandCount + h.StereoBandCount > SamplesPerSubframe ||
        h.BandsPerHfrGroup > SamplesPerSubframe ||
        h.TotalBandCount < h.BaseBandCount)
      throw new InvalidDataException("HCA band configuration out of range.");

    if (h.TrackCount == 0)
      h.TrackCount = 1;
    if (h.TrackCount > h.Channels)
      throw new InvalidDataException("HCA track count exceeds channel count.");

    h.HfrGroupCount = CeilDiv(h.TotalBandCount - h.BaseBandCount - h.StereoBandCount, h.BandsPerHfrGroup);
    if (h.BaseBandCount + h.StereoBandCount + h.HfrGroupCount > SamplesPerSubframe)
      throw new InvalidDataException("HCA HFR group configuration out of range.");

    var cipher = CipherInit(h.CipherType);
    return (h, cipher);
  }

  /// <summary>
  /// Decodes the full HCA <paramref name="file"/> to interleaved 16-bit PCM. Throws
  /// <see cref="NotSupportedException"/> for keyed (type 56) or MS-stereo streams so the
  /// container layer can degrade to a header-only view.
  /// </summary>
  public static (short[] InterleavedPcm, int Channels, int SampleRate, HcaHeader Header) Decode(ReadOnlySpan<byte> file) {
    var (header, cipher) = ReadHeader(file);
    if (header.IsKeyedCipher)
      throw new NotSupportedException("Keyed (56-bit) HCA streams are not supported.");
    if (header.IsMsStereo)
      throw new NotSupportedException("MS-stereo HCA streams are not supported.");

    var codec = new HcaCodec(header, cipher);
    var channels = header.Channels;
    var frameCount = header.FrameCount;
    var totalSamples = (long)frameCount * SamplesPerFrame;

    var pcm = new short[totalSamples * channels];
    var subframe = new double[SamplesPerSubframe];
    var frameBuf = new byte[header.FrameSize];

    var dataStart = header.HeaderSize;
    for (var frame = 0; frame < frameCount; frame++) {
      var off = dataStart + frame * header.FrameSize;
      if (off + header.FrameSize > file.Length)
        break;

      file.Slice(off, header.FrameSize).CopyTo(frameBuf);
      if (header.CipherType != 0)
        for (var n = 0; n < frameBuf.Length; n++)
          frameBuf[n] = cipher[frameBuf[n]];

      // CRC-16 over the whole frame must be zero (last two bytes are the checksum).
      if (Crc16(frameBuf) != 0)
        throw new InvalidDataException($"HCA frame {frame} CRC mismatch.");

      codec.DecodeFrame(frameBuf, subframe, pcm, frame * SamplesPerFrame, channels);
    }

    return (pcm, channels, header.SampleRate, header);
  }

  // ── Frame decode (ffmpeg decode_frame) ──────────────────────────────────

  private void DecodeFrame(byte[] frame, double[] subframeOut, short[] pcm, long sampleBase, int channels) {
    var br = new HcaBitReader(frame, 0, this._header.FrameSize);
    if (br.GetBits(16) != 0xFFFF)
      throw new InvalidDataException("HCA frame sync (0xFFFF) missing.");

    var packedNoiseLevel = (br.GetBits(9) << 8) - br.GetBits(7);

    foreach (var ch in this._channels)
      this.Unpack(ch, br, packedNoiseLevel);

    for (var sub = 0; sub < SubframesPerFrame; sub++) {
      foreach (var ch in this._channels)
        this.DequantizeCoefficients(ch, br);

      foreach (var ch in this._channels)
        this.ReconstructHfr(ch);

      for (var c = 0; c < this._channels.Length - 1; c++)
        ApplyIntensityStereo(this._channels[c], this._channels[c + 1], sub,
          this._header.TotalBandCount - this._header.BaseBandCount, this._header.BaseBandCount, this._header.StereoBandCount);

      for (var c = 0; c < this._channels.Length; c++) {
        var ch = this._channels[c];
        ch.Imdct.RunImdct(ch.ImdctIn, ch.ImdctOut);
        var baseIndex = (sampleBase + sub * SamplesPerSubframe) * channels + c;
        for (var i = 0; i < SamplesPerSubframe; i++)
          pcm[baseIndex + (long)i * channels] = FloatToS16(ch.ImdctOut[i]);
      }
    }
  }

  // ffmpeg unpack(): scalefactors (delta-coded), intensity / HFR scales, resolution and gain.
  private void Unpack(Channel ch, HcaBitReader br, int packedNoiseLevel) {
    var deltaBits = br.GetBits(3);

    if (deltaBits > 5) {
      for (var i = 0; i < ch.CodedCount; i++)
        ch.ScaleFactors[i] = br.GetBits(6);
    } else if (deltaBits > 0) {
      var factor = br.GetBits(6);
      var maxValue = (1 << deltaBits) - 1;
      var halfMax = maxValue >> 1;
      ch.ScaleFactors[0] = factor;
      for (var i = 1; i < ch.CodedCount; i++) {
        var delta = br.GetBits(deltaBits);
        if (delta == maxValue)
          factor = br.GetBits(6);
        else
          factor += delta - halfMax;
        factor = ClipUIntP2(factor, 6);
        ch.ScaleFactors[i] = factor;
      }
    } else {
      Array.Clear(ch.ScaleFactors, 0, ch.ScaleFactors.Length);
    }

    if (ch.Type == TypeStereoSecondary) {
      ch.Intensity[0] = br.GetBits(4);
      if (ch.Intensity[0] < 15)
        for (var i = 1; i < SubframesPerFrame; i++)
          ch.Intensity[i] = br.GetBits(4);
    } else {
      // HFR scales sit just past the base+stereo bands (ffmpeg `hfr_scale` pointer).
      var hfrBase = this._header.BaseBandCount + this._header.StereoBandCount;
      for (var i = 0; i < this._header.HfrGroupCount; i++)
        ch.HfrScale[hfrBase + i] = br.GetBits(6);
    }

    for (var i = 0; i < ch.CodedCount; i++) {
      var scale = ch.ScaleFactors[i];
      if (scale != 0) {
        scale = this._ath[i] + ((packedNoiseLevel + i) >> 8) - ((scale * 5) >> 1) + 2;
        scale = HcaTables.ScaleTable[Clip(scale, 0, 58)];
      }
      ch.Resolution[i] = scale;
    }
    Array.Clear(ch.Resolution, ch.CodedCount, ch.Resolution.Length - ch.CodedCount);

    for (var i = 0; i < ch.CodedCount; i++)
      ch.Gain[i] = (double)HcaTables.DequantizerScaling[ch.ScaleFactors[i]] * HcaTables.QuantStepSize[ch.Resolution[i]];
  }

  // ffmpeg dequantize_coefficients(): read spectral mantissas into ImdctIn.
  private void DequantizeCoefficients(Channel ch, HcaBitReader br) {
    var outp = ch.ImdctIn;
    for (var i = 0; i < ch.CodedCount; i++) {
      var resolution = ch.Resolution[i];
      var nbBits = HcaTables.MaxBits[resolution];
      var value = br.GetBitsZ(nbBits);
      double factor;

      if (resolution > 7) {
        // Sign-magnitude (lowest bit = sign); zero consumes one less bit.
        value = (1 - ((value & 1) << 1)) * (value >> 1);
        if (value == 0)
          br.Skip(-1);
        factor = value;
      } else {
        var index = (resolution << 4) + value;
        br.Skip(HcaTables.QuantSpectrumBits[index] - nbBits);
        factor = HcaTables.QuantSpectrumValue[index];
      }
      outp[i] = ch.Gain[i] * factor;
    }
    Array.Clear(outp, ch.CodedCount, outp.Length - ch.CodedCount);
  }

  // ffmpeg reconstruct_hfr(): rebuild high bands from lower bands via the scale-conversion table.
  private void ReconstructHfr(Channel ch) {
    if (ch.Type == TypeStereoSecondary || this._header.BandsPerHfrGroup == 0)
      return;

    var startBand = this._header.BaseBandCount + this._header.StereoBandCount;
    var groupCount = this._header.HfrGroupCount;
    var bandsPerGroup = this._header.BandsPerHfrGroup;
    var total = this._header.TotalBandCount;

    var k = startBand;
    var l = startBand - 1;
    for (var i = 0; i < groupCount; i++) {
      for (var j = 0; j < bandsPerGroup && k < total && l >= 0; j++, k++, l--) {
        var idx = HcaTables.ScaleConvBias + ClipIntP2(ch.HfrScale[startBand + i] - ch.ScaleFactors[l], 6);
        ch.ImdctIn[k] = HcaTables.ScaleConversion[idx] * ch.ImdctIn[l];
      }
    }
    ch.ImdctIn[SamplesPerSubframe - 1] = 0;
  }

  // ffmpeg apply_intensity_stereo(): couple the secondary channel's bands from the primary.
  private static void ApplyIntensityStereo(Channel ch1, Channel ch2, int index,
      int bandCount, int baseBandCount, int stereoBandCount) {
    if (ch1.Type != TypeStereoPrimary || stereoBandCount == 0)
      return;

    var ratioL = HcaTables.IntensityRatio[ch2.Intensity[index]];
    var ratioR = ratioL - 2.0f;
    for (var i = 0; i < bandCount; i++) {
      ch2.ImdctIn[baseBandCount + i] = ch1.ImdctIn[baseBandCount + i] * ratioR;
      ch1.ImdctIn[baseBandCount + i] *= ratioL;
    }
  }

  // ── Channel typing (ffmpeg init_hca `r` table / clHCA channel_types) ─────

  private static int[] AssignChannelTypes(HcaHeader h) {
    var r = new int[h.Channels];
    var channelsPerTrack = h.Channels / h.TrackCount;
    if (h.StereoBandCount > 0 && channelsPerTrack > 1) {
      for (var t = 0; t < h.TrackCount; t++) {
        var x = t * channelsPerTrack;
        switch (channelsPerTrack) {
          case 2:
          case 3:
            r[x + 0] = TypeStereoPrimary; r[x + 1] = TypeStereoSecondary;
            break;
          case 4:
            r[x + 0] = TypeStereoPrimary; r[x + 1] = TypeStereoSecondary;
            if (h.ChannelConfig == 0) { r[x + 2] = TypeStereoPrimary; r[x + 3] = TypeStereoSecondary; }
            break;
          case 5:
            r[x + 0] = TypeStereoPrimary; r[x + 1] = TypeStereoSecondary;
            if (h.ChannelConfig <= 2) { r[x + 3] = TypeStereoPrimary; r[x + 4] = TypeStereoSecondary; }
            break;
          case 6:
          case 7:
            r[x + 0] = TypeStereoPrimary; r[x + 1] = TypeStereoSecondary;
            r[x + 4] = TypeStereoPrimary; r[x + 5] = TypeStereoSecondary;
            break;
          case 8:
            r[x + 0] = TypeStereoPrimary; r[x + 1] = TypeStereoSecondary;
            r[x + 4] = TypeStereoPrimary; r[x + 5] = TypeStereoSecondary;
            r[x + 6] = TypeStereoPrimary; r[x + 7] = TypeStereoSecondary;
            break;
        }
      }
    }
    return r;
  }

  // ── Cipher (vgmstream/clHCA cipher_init) ─────────────────────────────────

  /// <summary>
  /// Builds the 256-byte cipher substitution table. Type 0 is the identity; type 1 is
  /// the keyless static table (multiplicative generator, <c>mul=13, add=11</c>). Type 56
  /// (keyed) returns the identity here — the decoder treats keyed streams as unsupported.
  /// </summary>
  public static byte[] CipherInit(int type) {
    var table = new byte[256];
    switch (type) {
      case 1:
        var v = 0;
        for (var i = 1; i < 255; i++) {
          v = (v * 13 + 11) & 0xFF;
          if (v is 0 or 0xFF)
            v = (v * 13 + 11) & 0xFF;
          table[i] = (byte)v;
        }
        table[0] = 0;
        table[0xFF] = 0xFF;
        break;
      default: // type 0 (none) and type 56 (unsupported → identity placeholder)
        for (var i = 0; i < 256; i++)
          table[i] = (byte)i;
        break;
    }
    return table;
  }

  // ── ATH curve (ffmpeg ath_init) ──────────────────────────────────────────

  private static void AthInit(byte[] ath, int type, int sampleRate) {
    switch (type) {
      case 0:
        Array.Clear(ath, 0, ath.Length);
        break;
      case 1:
        var acc = 0u;
        for (var i = 0; i < SamplesPerSubframe; i++) {
          acc += (uint)sampleRate;
          var index = acc >> 13;
          if (index >= 654) {
            for (var j = i; j < SamplesPerSubframe; j++)
              ath[j] = 0xFF;
            return;
          }
          ath[i] = HcaTables.AthBaseCurve[index];
        }
        break;
      default:
        throw new InvalidDataException($"HCA unsupported ATH type {type}.");
    }
  }

  // ── CRC-16 (poly 0x8005, MSB-first; AV_CRC_16_ANSI) ──────────────────────

  private static readonly ushort[] Crc16Table = BuildCrc16Table();

  private static ushort[] BuildCrc16Table() {
    var table = new ushort[256];
    for (var n = 0; n < 256; n++) {
      var crc = (ushort)(n << 8);
      for (var k = 0; k < 8; k++)
        crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
      table[n] = crc;
    }
    return table;
  }

  /// <summary>HCA's CRC-16 (IBM/ANSI, poly 0x8005, no reflection); a valid block sums to 0.</summary>
  public static ushort Crc16(ReadOnlySpan<byte> data) {
    ushort sum = 0;
    foreach (var b in data)
      sum = (ushort)((sum << 8) ^ Crc16Table[(byte)((sum >> 8) ^ b)]);
    return sum;
  }

  // ── Small helpers ────────────────────────────────────────────────────────

  private static bool ChunkMatches(HcaBitReader br, uint tag) {
    // Peek 32 bits (masked) without consuming; the reader has no peek, so read+rewind.
    var bits = (uint)br.GetBits(32);
    br.Skip(-32);
    return (bits & Mask) == tag;
  }

  private static int CeilDiv(int a, int b) => b > 0 ? a / b + (a % b != 0 ? 1 : 0) : 0;

  private static int Clip(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

  private static int ClipUIntP2(int v, int bits) {
    var max = (1 << bits) - 1;
    return v < 0 ? 0 : v > max ? max : v;
  }

  private static int ClipIntP2(int v, int bits) {
    var max = (1 << bits) - 1;
    var min = -(1 << bits);
    return v < min ? min : v > max ? max : v;
  }

  private static short FloatToS16(double sample) {
    var v = (int)Math.Round(sample * 32768.0);
    return (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v);
  }
}
