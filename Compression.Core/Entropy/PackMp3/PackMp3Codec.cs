namespace Compression.Core.Entropy.PackMp3;

/// <summary>
/// packMP3 v1.0g (Matthias Stirner): lossless recompression of MPEG-1 Layer III files into the
/// PMP format, and back. WinZip stores exactly this stream as ZIP method 94.
/// </summary>
/// <remarks>
/// Converted from <c>packmp3.cpp</c> (packMP3 v1.0g, LGPL-3.0, see
/// <c>THIRD-PARTY-NOTICE.packMP3.txt</c>): frame parsing and rebuilding, the frame analysis, the
/// PMP header, and the context-modelled coding of side information and main data. Behaviour is
/// kept as packMP3 has it, quirks included, because the format is whatever packMP3 writes.
/// </remarks>
public static class PackMp3Codec {
  /// <summary>The PMP signature, "MS".</summary>
  public static ReadOnlySpan<byte> Magic => "MS"u8;

  /// <summary>packMP3's format version byte (1.0).</summary>
  public const byte Version = 10;

  /// <summary>Compresses an MP3 file to a PMP stream.</summary>
  /// <exception cref="NotSupportedException">The file uses something packMP3 does not support (it is not MPEG-1 Layer III, has mixed blocks, CRC errors, ...).</exception>
  /// <exception cref="InvalidDataException">The file is not usable MP3 data.</exception>
  public static byte[] Compress(ReadOnlySpan<byte> mp3) {
    var s = new Session();
    try {
      s.ReadMp3(mp3.ToArray());
      s.AnalyzeFrames();
      return s.CompressMp3();
    } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or NullReferenceException or OverflowException) {
      throw new InvalidDataException("packMP3: unusable MP3 data.", e);
    }
  }

  /// <summary>
  /// Compresses an MP3 file when packMP3 can carry it and the result decompresses to the very same
  /// bytes; returns false for anything else (other audio, unsupported MP3 features, non-MP3 data).
  /// </summary>
  public static bool TryCompress(ReadOnlySpan<byte> mp3, out byte[] pmp) {
    pmp = [];
    try {
      var candidate = Compress(mp3);
      if (!Decompress(candidate).AsSpan().SequenceEqual(mp3)) return false;
      pmp = candidate;
      return true;
    } catch (Exception e) when (e is NotSupportedException or InvalidDataException) {
      return false;
    }
  }

  /// <summary>Decompresses a PMP stream back to the original MP3 file.</summary>
  /// <exception cref="InvalidDataException">The stream is corrupt or not PMP.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> pmp) {
    var s = new Session();
    try {
      s.UncompressPmp(pmp.ToArray());
      return s.WriteMp3();
    } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or NullReferenceException or OverflowException or DivideByZeroException) {
      throw new InvalidDataException("packMP3: corrupt stream.", e);
    }
  }

  // ── constants ────────────────────────────────────────────────────────

  private const int Mp3Mono = 3;
  private const int Mp3Stereo = 0;
  private const int Mp3JointStereo = 1;
  private const int LongBlock = 0;
  private const int StartBlock = 1;
  private const int ShortBlock = 2;
  private const int StopBlock = 3;
  private const int PaddingMaxRun = 255;
  private const int SwitchingMaxRun = 31;
  private const int GgContextSize = 16;
  private const int AuxDataStep = 255;
  private const int StuffingStep = 15;
  private const int MaxBadFirst = 255;
  private const int GarbageTolerance = 64 * 1024;
  private const int FirstFrameArea = 1024 * 1024;

  private static readonly int[,] SlengthTable = {
    { 0, 0 }, { 0, 1 }, { 0, 2 }, { 0, 3 }, { 3, 0 }, { 1, 1 }, { 1, 2 }, { 1, 3 },
    { 2, 1 }, { 2, 2 }, { 2, 3 }, { 3, 1 }, { 3, 2 }, { 3, 3 }, { 4, 2 }, { 4, 3 },
  };
  private static readonly int[] ScfBounds = [6, 11, 16, 21];
  private static readonly int[] ScfWidth = [6, 5, 5, 5];
  private static readonly int[] ScfLhBoundsShort = [6, 12];
  private static readonly int[] ScfLhWidthShort = [6, 6];

  private static int FrameSizeOf(int mpeg, int layer, int samples, int bits) => PackMp3Tables.FrameSize[((mpeg * 4 + layer) * 4 + samples) * 16 + bits];
  private static int Mp3FrameSize(int samples, int bits) => FrameSizeOf(3, 1, samples, bits);
  private static int BandBound(int samples, int band) => PackMp3Tables.BandwidthBoundsMpeg1[samples * 23 + band];
  private static int BandBoundShort(int samples, int band) => PackMp3Tables.BandwidthBoundsShortMpeg1[samples * 14 + band];

  // ── data model ───────────────────────────────────────────────────────

  private sealed class Granule {
    public int N;
    public int Share;
    public int MainDataBit;
    public int BigValPairs;
    public int GlobalGain;
    public int SLength;
    public int WindowSwitching;
    public int Preemphasis;
    public int Coarse;
    public int SelectHtabB;
    public readonly int[] RegionTable = new int[3];
    public int Region0Size;
    public int Region1Size;
    public readonly int[] RegionBound = new int[3];
    public int SvBound;
    public int BlockType;
    public int MixedFlag;
    public readonly int[] SbGain = new int[3];
    public Granule? Next;
    public Granule? Prev;
  }

  private sealed class Frame {
    public int N;
    public int Mpeg, Layer, Protection, Bits, Samples, Padding, Privbit, Channels, StereoMs, StereoInt, Copyright, Original, Emphasis;
    public int BitReservoir;
    public int Padbits;
    public Granule[][] Granules = [];
    public int FrameSize, FixedSize, MainSize, AuxSize, MainIndex, MainBits, NChannels;
    public Frame? Next;
    public Frame? Prev;
  }

  private sealed class Session {
    private Frame? _first, _last;
    private byte[] _mainData = [];
    private int _mainDataSize;
    private byte[] _before = [], _after = [];
    private List<byte> _unmute = [];
    private int _badFirst;
    private readonly byte[][] _ggContext = [[], []];

    private int _nframes, _nchannels;

    private int iMpeg = -1, iLayer = -1, iSamplerate = -1, iBitrate = -1, iProtection = -1, iPadding = -1, iPrivbit = -1,
      iChannels = -1, iStereoMs = -1, iStereoInt = -1, iCopyright = -1, iOriginal = -1, iEmphasis = -1, iPadbits = -1,
      iBitRes = -1, iShare = -1, iSblocks = -1, iMixed = -1, iPreemphasis = -1, iCoarse = -1, iSbgain = -1, iAuxH = -1, iSbDiff = -1;

    // mp3_append_frame keeps these between calls
    private readonly Granule?[] _lastGranule = new Granule?[2];
    private int _appendN;

    // ── MP3 side ───────────────────────────────────────────────────────

    public void ReadMp3(byte[] mp3) {
      var size = mp3.Length;
      if (size <= 0) throw new InvalidDataException("packMP3: empty input.");
      var seekSize = Math.Min(size, FirstFrameArea);
      var pos = SeekFirstFrame(mp3, seekSize);
      if (pos == -1) throw new InvalidDataException("packMP3: no MPEG audio data recognised.");
      var mainDataBegin = pos;
      if (seekSize - pos >= 2) {
        var type = (mp3[pos + 1] & 0x1F) >> 1;
        if (type != 0xD) throw new NotSupportedException("packMP3: only MPEG-1 Layer III is supported.");
      }

      var data = new List<byte>(size);
      int mpeg = -1, layer = -1, samples = -1, channels = -1;
      var incompleteLastFrame = false;
      while (pos < size) {
        var frame = ReadFrame(mp3, pos, size - pos);
        if (frame == null) break;
        if (frame.FrameSize > size - pos) {
          incompleteLastFrame = true;
          break;
        }
        if (frame.Mpeg != mpeg || frame.Layer != layer || frame.Samples != samples || frame.Channels != channels) {
          if (mpeg == -1) {
            mpeg = frame.Mpeg;
            layer = frame.Layer;
            samples = frame.Samples;
            channels = frame.Channels;
          } else
            break;
        }
        if (this._last != null) {
          if (frame.AuxSize < 0) {
            if (this._last.N > MaxBadFirst - 2) break;
            this._badFirst = this._last.N + 2;
          } else if (this._last.AuxSize < frame.BitReservoir) {
            if (this._last.N > MaxBadFirst - 1) break;
            this._badFirst = this._last.N + 1;
          } else if (frame.N == -1) {
            if (this._last.N > MaxBadFirst - 2) break;
            this._badFirst = this._last.N + 2;
          }
          this._last.AuxSize -= frame.BitReservoir;
        } else if (frame.AuxSize < 0 || frame.N == -1)
          this._badFirst = 1;
        frame.MainIndex = data.Count - frame.BitReservoir;
        data.AddRange(mp3.AsSpan(pos + frame.FixedSize, frame.FrameSize - frame.FixedSize));
        this.AppendFrame(frame);
        if (frame.MainIndex < 0) this._badFirst = frame.N + 1;
        pos += frame.FrameSize;
      }

      this._mainDataSize = data.Count;
      this._mainData = [.. data];
      if (this._last == null || this._last.N + 1 - this._badFirst < 5)
        throw new InvalidDataException("packMP3: corrupted file, compression not possible.");

      var mainDataEnd = pos;
      if (!incompleteLastFrame && mainDataEnd < size) {
        if (mainDataEnd + Id3Size(mp3.AsSpan(mainDataEnd), size - mainDataEnd) != size && size - mainDataEnd > GarbageTolerance)
          throw new InvalidDataException("packMP3: synching failure.");
      }
      this._after = mp3.AsSpan(mainDataEnd).ToArray();
      this._before = mp3.AsSpan(0, mainDataBegin).ToArray();
    }

    /// <summary>One physical frame, or null where the audio data ends (mp3_read_frame).</summary>
    private static Frame? ReadFrame(byte[] data, int at, int maxSize) {
      if (maxSize < 21) return null;
      if (data[at] != 0xFF || (data[at + 1] & 0xFA) != 0xFA) return null;
      var f = new Frame {
        Mpeg = (data[at + 1] >> 3) & 3,
        Layer = (data[at + 1] >> 1) & 3,
        Protection = (data[at + 1] & 1) ^ 1,
        Bits = (data[at + 2] >> 4) & 0xF,
        Samples = (data[at + 2] >> 2) & 3,
        Padding = (data[at + 2] >> 1) & 1,
        Privbit = data[at + 2] & 1,
        Channels = (data[at + 3] >> 6) & 3,
        StereoMs = (data[at + 3] >> 5) & 1,
        StereoInt = (data[at + 3] >> 4) & 1,
        Copyright = (data[at + 3] >> 3) & 1,
        Original = (data[at + 3] >> 2) & 1,
        Emphasis = data[at + 3] & 3,
      };
      if (f.Bits == 0 || f.Bits == 0xF || f.Samples == 3) return null;
      f.NChannels = f.Channels == Mp3Mono ? 1 : 2;
      var nch = f.NChannels;
      f.Granules = new Granule[nch][];
      for (var ch = 0; ch < nch; ++ch) f.Granules[ch] = [new Granule(), new Granule()];
      f.FrameSize = Mp3FrameSize(f.Samples, f.Bits) + (f.Padding != 0 ? 1 : 0);

      var nsb = nch == 1 ? 17 : 32;
      f.FixedSize = 4 + nsb + (f.Protection != 0 ? 2 : 0);
      if (f.FixedSize > maxSize) return null;
      int side;
      if (f.Protection == 1) {
        side = at + 6;
        var crc = (data[at + 4] << 8) | data[at + 5];
        if (crc != Crc(data.AsSpan(at, 4), data.AsSpan(side, nsb)))
          throw new NotSupportedException("packMP3: CRC checksum mismatch.");
      } else
        side = at + 4;

      var r = new PmpBitReader(data[side..(side + nsb)], nsb);
      f.BitReservoir = r.ReadBits(9);
      f.Padbits = r.ReadBits(nch == 1 ? 5 : 3);
      for (var ch = 0; ch < nch; ++ch) {
        f.Granules[ch][0].Share = r.ReadBits(4);
        f.Granules[ch][1].Share = 0;
      }
      for (var gr = 0; gr < 2; ++gr) {
        for (var ch = 0; ch < nch; ++ch) {
          var g = f.Granules[ch][gr];
          g.MainDataBit = r.ReadBits(12);
          g.BigValPairs = r.ReadBits(9);
          g.GlobalGain = r.ReadBits(8);
          g.SLength = r.ReadBits(4);
          g.WindowSwitching = r.ReadBits(1);
          if (g.WindowSwitching == 0) {
            g.RegionTable[0] = r.ReadBits(5);
            g.RegionTable[1] = r.ReadBits(5);
            g.RegionTable[2] = r.ReadBits(5);
            g.Region0Size = r.ReadBits(4);
            g.Region1Size = r.ReadBits(3);
            if (g.Region0Size + g.Region1Size > 20) throw new InvalidDataException("packMP3: region size out of bounds.");
            g.RegionBound[0] = BandBound(f.Samples, g.Region0Size + 1);
            g.RegionBound[1] = BandBound(f.Samples, g.Region0Size + g.Region1Size + 2);
            g.RegionBound[2] = (short)(g.BigValPairs << 1);
            if (g.RegionBound[0] > g.RegionBound[2]) {
              g.RegionBound[0] = g.RegionBound[2];
              g.RegionBound[1] = g.RegionBound[2];
            } else if (g.RegionBound[1] > g.RegionBound[2])
              g.RegionBound[1] = g.RegionBound[2];
            g.BlockType = LongBlock;
            g.MixedFlag = 0;
            g.SbGain[0] = g.SbGain[1] = g.SbGain[2] = 0;
          } else {
            g.BlockType = r.ReadBits(2);
            g.MixedFlag = r.ReadBits(1);
            g.RegionTable[0] = r.ReadBits(5);
            g.RegionTable[1] = r.ReadBits(5);
            g.SbGain[0] = r.ReadBits(3);
            g.SbGain[1] = r.ReadBits(3);
            g.SbGain[2] = r.ReadBits(3);
            if (g.BlockType != ShortBlock) {
              g.Region0Size = 8;
              g.Region1Size = 0;
              g.RegionBound[0] = BandBound(f.Samples, 8);
            } else {
              g.Region0Size = 9;
              g.Region1Size = 0;
              g.RegionBound[0] = BandBoundShort(f.Samples, 9 / 3) * 3;
            }
            g.RegionBound[1] = g.BigValPairs << 1;
            if (g.RegionBound[0] > g.RegionBound[1]) g.RegionBound[0] = g.RegionBound[1];
            g.RegionBound[2] = g.RegionBound[1];
            g.RegionTable[2] = 0;
          }
          g.Preemphasis = r.ReadBits(1);
          g.Coarse = r.ReadBits(1);
          g.SelectHtabB = r.ReadBits(1);
          g.SvBound = 0;
          if (g.MainDataBit == 0 && (g.BigValPairs != 0 || g.SLength != 0)) f.N = -1;
        }
      }

      f.MainBits = 0;
      for (var gr = 0; gr < 2; ++gr)
        for (var ch = 0; ch < nch; ++ch) f.MainBits += f.Granules[ch][gr].MainDataBit;
      f.MainSize = (f.MainBits + 7) >> 3;
      f.AuxSize = f.FrameSize - f.FixedSize + f.BitReservoir - f.MainSize;
      return f;
    }

    private Frame BuildFrame() {
      var f = new Frame {
        Mpeg = this.iMpeg, Layer = this.iLayer, Protection = this.iProtection, Bits = this.iBitrate, Samples = this.iSamplerate,
        Padding = this.iPadding, Privbit = this.iPrivbit, Channels = this.iChannels, StereoMs = this.iStereoMs,
        StereoInt = this.iStereoInt, Copyright = this.iCopyright, Original = this.iOriginal, Emphasis = this.iEmphasis,
        BitReservoir = 0, Padbits = this.iPadbits,
      };
      if (f.Channels == Mp3Mono) {
        f.FixedSize = f.Protection != 0 ? 4 + 2 + 17 : 4 + 17;
        f.NChannels = 1;
      } else {
        f.FixedSize = f.Protection != 0 ? 4 + 2 + 32 : 4 + 32;
        f.NChannels = 2;
      }
      f.Granules = new Granule[f.NChannels][];
      for (var ch = 0; ch < f.NChannels; ++ch) {
        f.Granules[ch] = [new Granule(), new Granule()];
        for (var gr = 0; gr < 2; ++gr) {
          var g = f.Granules[ch][gr];
          g.Share = gr == 0 ? this.iShare : 0;
          g.WindowSwitching = this.iSblocks;
          g.MixedFlag = this.iMixed;
          g.BlockType = LongBlock;
          g.SbGain[0] = g.SbGain[1] = g.SbGain[2] = this.iSbgain;
          g.Preemphasis = this.iPreemphasis;
          g.Coarse = this.iCoarse;
        }
      }
      return f;
    }

    private void AppendFrame(Frame f) {
      if (this._last == null) {
        this._first = f;
        f.Prev = null;
        this._lastGranule[0] = this._lastGranule[1] = null;
        this._appendN = 0;
      } else {
        this._last.Next = f;
        f.Prev = this._last;
      }
      this._last = f;
      for (var ch = 0; ch < f.NChannels; ++ch) {
        f.Granules[ch][0].N = this._appendN << 1;
        f.Granules[ch][1].N = f.Granules[ch][0].N | 1;
        f.Granules[ch][0].Next = f.Granules[ch][1];
        f.Granules[ch][1].Prev = f.Granules[ch][0];
        f.Granules[ch][0].Prev = this._lastGranule[ch];
        f.Granules[ch][1].Next = null;
        if (this._lastGranule[ch] != null) this._lastGranule[ch]!.Next = f.Granules[ch][0];
        this._lastGranule[ch] = f.Granules[ch][1];
      }
      f.N = this._appendN++;
      f.Next = null;
    }

    private void MuteFrame(Frame f) {
      var nch = f.NChannels;
      if (this._unmute.Count == 0) this._unmute.Add(0);
      var b0 = ((f.Privbit & 1) << 7) | ((f.Original & 1) << 6) | ((f.Copyright & 1) << 5) | ((f.BitReservoir >> 8) & 1);
      this._unmute.Add((byte)b0);
      this._unmute.Add((byte)f.BitReservoir);
      for (var ch = 0; ch < nch; ++ch)
        for (var gr = 0; gr < 2; ++gr) {
          var g = f.Granules[ch][gr];
          this._unmute.Add((byte)(g.MainDataBit >> 4));
          this._unmute.Add((byte)(((g.MainDataBit << 4) & 0xF0) | (g.SLength & 0x0F)));
          this._unmute.Add((byte)(g.BigValPairs >> 1));
          this._unmute.Add((byte)((g.BigValPairs << 7) & 0x80));
        }
      this._unmute[0]++;

      if (f.Prev != null && this.iBitRes != 0) {
        f.BitReservoir += f.Prev.AuxSize;
        if (f.BitReservoir >= 512) {
          f.Prev.AuxSize = f.BitReservoir - 511;
          f.BitReservoir = 511;
        } else
          f.Prev.AuxSize = 0;
      } else
        f.BitReservoir = 0;
      f.AuxSize = f.FrameSize - f.FixedSize + f.BitReservoir;
      if (f.Next != null) f.AuxSize -= f.Next.BitReservoir;
      f.MainSize = 0;
      f.MainBits = 0;
      for (var ch = 0; ch < nch; ++ch)
        for (var gr = 0; gr < 2; ++gr) {
          var g = f.Granules[ch][gr];
          g.MainDataBit = 0;
          g.BigValPairs = 0;
          g.SLength = 0;
          g.RegionBound[0] = g.RegionBound[1] = g.RegionBound[2] = 0;
        }
    }

    private void UnmuteFrame(Frame f) {
      var nch = this._nchannels;
      var ums = 2 + nch * 2 * 4;
      var p = 1 + this._badFirst * ums;
      var u = this._unmute;
      f.Privbit = (u[p] >> 7) & 1;
      f.Original = (u[p] >> 6) & 1;
      f.Copyright = (u[p] >> 5) & 1;
      f.BitReservoir = (u[p] << 8) & 0xFFFF;
      ++p;
      f.BitReservoir |= u[p++];
      f.BitReservoir = (short)f.BitReservoir;
      for (var ch = 0; ch < nch; ++ch)
        for (var gr = 0; gr < 2; ++gr) {
          var g = f.Granules[ch][gr];
          g.MainDataBit = (short)((u[p++] << 4) | (u[p] >> 4));
          g.SLength = u[p++] & 0x0F;
          g.BigValPairs = (short)((u[p++] << 1) | (u[p++] >> 7));
        }
    }

    private byte[] BuildFixed(Frame f) {
      var fixedPart = new byte[f.FixedSize];
      var nch = f.NChannels;
      var nsb = nch == 1 ? 17 : 32;
      var side = f.Protection != 0 ? 6 : 4;
      fixedPart[0] = 0xFF;
      fixedPart[1] = (byte)(0xFA | (f.Mpeg << 3) | (f.Layer << 1) | (f.Protection ^ 1));
      fixedPart[2] = (byte)((f.Bits << 4) | (f.Samples << 2) | (f.Padding << 1) | f.Privbit);
      fixedPart[3] = (byte)((f.Channels << 6) | (f.StereoMs << 5) | (f.StereoInt << 4) | (f.Copyright << 3) | (f.Original << 2) | f.Emphasis);

      var w = new PmpBitWriter();
      void Put(int value, int bits) => w.WriteBits(value & ((1 << bits) - 1), bits);
      Put(f.BitReservoir, 9);
      Put(f.Padbits, nch == 1 ? 5 : 3);
      for (var ch = 0; ch < nch; ++ch) Put(f.Granules[ch][0].Share, 4);
      for (var gr = 0; gr < 2; ++gr)
        for (var ch = 0; ch < nch; ++ch) {
          var g = f.Granules[ch][gr];
          Put(g.MainDataBit, 12);
          Put(g.BigValPairs, 9);
          Put(g.GlobalGain, 8);
          Put(g.SLength, 4);
          Put(g.WindowSwitching, 1);
          if (g.WindowSwitching == 0) {
            Put(g.RegionTable[0], 5);
            Put(g.RegionTable[1], 5);
            Put(g.RegionTable[2], 5);
            Put(g.Region0Size, 4);
            Put(g.Region1Size, 3);
          } else {
            Put(g.BlockType, 2);
            Put(g.MixedFlag, 1);
            Put(g.RegionTable[0], 5);
            Put(g.RegionTable[1], 5);
            Put(g.SbGain[0], 3);
            Put(g.SbGain[1], 3);
            Put(g.SbGain[2], 3);
          }
          Put(g.Preemphasis, 1);
          Put(g.Coarse, 1);
          Put(g.SelectHtabB, 1);
        }
      var sideInfo = w.Finish();
      sideInfo.AsSpan(0, Math.Min(nsb, sideInfo.Length)).CopyTo(fixedPart.AsSpan(side));
      if (f.Protection == 1) {
        var crc = Crc(fixedPart.AsSpan(0, 4), fixedPart.AsSpan(side, nsb));
        fixedPart[4] = (byte)(crc >> 8);
        fixedPart[5] = (byte)crc;
      }
      return fixedPart;
    }

    private static int SeekFirstFrame(byte[] data, int size) {
      int mpeg = -1, layer = -1, samples = -1, channels = -1, protection = -1;
      var pos0 = Id3Size(data, size);
      var pos1 = pos0 + GarbageTolerance;
      if (pos1 > size - 4) pos1 = size - 4;
      while (true) {
        for (; pos0 < pos1; ++pos0)
          if (data[pos0] == 0xFF && (data[pos0 + 1] & 0xE0) == 0xE0) break;
        if (pos0 == pos1) return -1;
        int n, pos, frameSize = 0;
        for (pos = pos0, n = 0; n < 5 && pos < size - 4; ++n, pos += frameSize) {
          if (data[pos] != 0xFF || (data[pos + 1] & 0xE0) != 0xE0) break;
          if (n == 0) {
            mpeg = (data[pos + 1] >> 3) & 3;
            layer = (data[pos + 1] >> 1) & 3;
            protection = data[pos + 1] & 1;
            samples = (data[pos + 2] >> 2) & 3;
            channels = (data[pos + 3] >> 6) & 3;
          } else if (mpeg != ((data[pos + 1] >> 3) & 3) || layer != ((data[pos + 1] >> 1) & 3) || protection != (data[pos + 1] & 1) ||
                     samples != ((data[pos + 2] >> 2) & 3) || channels != ((data[pos + 3] >> 6) & 3))
            break;
          var bits = (data[pos + 2] >> 4) & 0xF;
          var padding = (data[pos + 2] >> 1) & 1;
          if (mpeg == 1 || layer == 0 || bits == 0 || bits == 0xF || samples == 3) break;
          frameSize = FrameSizeOf(mpeg, layer, samples, bits);
          if (padding != 0) frameSize += layer == 3 ? 4 : 1;
        }
        if (n >= 5) break;
        ++pos0;
      }
      return pos0;
    }

    private static int Id3Size(ReadOnlySpan<byte> tag, int maxSize) {
      if (maxSize < 10) return 0;
      int size;
      if (tag.StartsWith("ID3"u8)) {
        size = 10;
        var unsynchronized = ((tag[5] >> 7) & 1) == 1;
        size += ((tag[5] >> 4) & 1) == 1 ? 10 : 0;
        size += tag[9] + (tag[8] << 7) + (tag[7] << 14) + (tag[6] << 21);
        if (size > maxSize) return 0;
        if (unsynchronized)
          for (var p = 0; p < size - 1; ++p)
            if (tag[p] == 0xFF && tag[p + 1] == 0x00) {
              ++p;
              ++size;
              if (size > maxSize) return 0;
            }
      } else if (tag.StartsWith("TAG"u8)) {
        size = 128;
        if (size > maxSize) return 0;
      } else if (tag.StartsWith("LYRICS"u8) && maxSize >= 148) {
        if (tag.StartsWith("LYRICSBEGIN"u8) && tag.Slice(maxSize - 128, 3).SequenceEqual("TAG"u8) && tag.Slice(maxSize - 128 - 9, 6).SequenceEqual("LYRICS"u8))
          size = maxSize;
        else
          return 0;
      } else if (tag.StartsWith("APETAGEX"u8))
        size = maxSize;
      else
        return 0;
      return size;
    }

    private static int Crc(ReadOnlySpan<byte> header, ReadOnlySpan<byte> side) {
      ushort crc = 0xFFFF;
      crc = (ushort)((crc << 8) ^ PackMp3Tables.Crc16[(crc >> 8) ^ header[2]]);
      crc = (ushort)((crc << 8) ^ PackMp3Tables.Crc16[(crc >> 8) ^ header[3]]);
      foreach (var b in side) crc = (ushort)((crc << 8) ^ PackMp3Tables.Crc16[(crc >> 8) ^ b]);
      return crc;
    }

    public void AnalyzeFrames() {
      var first = this._first!;
      this.iMpeg = first.Mpeg;
      this.iLayer = first.Layer;
      this.iSamplerate = first.Samples;
      this.iBitrate = first.Bits;
      this.iProtection = first.Protection;
      this.iPadding = first.Padding;
      this.iPrivbit = first.Privbit;
      this.iChannels = first.Channels;
      this.iStereoMs = first.StereoMs;
      this.iStereoInt = first.StereoInt;
      this.iCopyright = first.Copyright;
      this.iOriginal = first.Original;
      this.iEmphasis = first.Emphasis;
      this.iPadbits = first.Padbits;
      this.iBitRes = first.BitReservoir > 0 ? 1 : 0;
      bool aux0 = false, aux1 = false;
      if (first.AuxSize > 0) {
        this.iAuxH = 1;
        aux0 = true;
      } else
        this.iAuxH = 0;
      var g0 = first.Granules[0][0];
      this.iShare = g0.Share != 0 ? 1 : 0;
      this.iSblocks = g0.WindowSwitching != 0 ? 1 : 0;
      this.iMixed = g0.MixedFlag != 0 ? 1 : 0;
      this.iPreemphasis = g0.Preemphasis;
      this.iCoarse = g0.Coarse;
      this.iSbDiff = 0;
      this.iSbgain = 0;

      for (var f = this._first; f != null; f = f.Next) {
        if (this.iPrivbit != -1 && this.iPrivbit != f.Privbit) {
          if (f.N == 1) {
            if (this._badFirst == 0) this._badFirst = 1;
            this.iPrivbit = f.Privbit;
          } else
            this.iPrivbit = -1;
        }
        if (this.iCopyright != -1 && this.iCopyright != f.Copyright) {
          if (f.N == 1) {
            if (this._badFirst == 0) this._badFirst = 1;
            this.iCopyright = f.Copyright;
          } else
            this.iCopyright = -1;
        }
        if (this.iOriginal != -1 && this.iOriginal != f.Original) {
          if (f.N == 1) {
            if (this._badFirst == 0) this._badFirst = 1;
            this.iOriginal = f.Original;
          } else
            this.iOriginal = -1;
        }
        if (this.iProtection != -1 && this.iProtection != f.Protection) this.iProtection = -1;
        if (this.iBitrate != -1 && this.iBitrate != f.Bits) this.iBitrate = -1;
        if (this.iPadding != -1 && this.iPadding != f.Padding) this.iPadding = -1;
        if (this.iStereoMs != -1 && this.iStereoMs != f.StereoMs) this.iStereoMs = -1;
        if (this.iStereoInt != -1 && this.iStereoInt != f.StereoInt) this.iStereoInt = -1;
        if (this.iEmphasis != -1 && this.iEmphasis != f.Emphasis) this.iEmphasis = -1;
        if (this.iPadbits != -1 && this.iPadbits != f.Padbits) this.iPadbits = -1;
        if (this.iBitRes != 1 && f.BitReservoir > 0) this.iBitRes = 1;
        if (this.iAuxH != -1) {
          if (f.AuxSize > 0) {
            if (!aux0) aux1 = true;
          } else {
            if (aux1) this.iAuxH = -1;
            if (aux0) aux0 = false;
          }
        }
        for (var ch = 0; ch < f.NChannels; ++ch)
          for (var gr = 0; gr < 2; ++gr) {
            var g = f.Granules[ch][gr];
            var sbxGain = g.SbGain[0] != 0 || g.SbGain[1] != 0 || g.SbGain[2] != 0;
            if (this.iShare != 1 && g.Share != 0) this.iShare = 1;
            if (this.iSblocks != 1 && g.WindowSwitching != 0) this.iSblocks = 1;
            if (this.iMixed != 1 && g.MixedFlag != 0) this.iMixed = 1;
            if (this.iPreemphasis != -1 && this.iPreemphasis != g.Preemphasis) this.iPreemphasis = -1;
            if (this.iCoarse != -1 && this.iCoarse != g.Coarse) this.iCoarse = -1;
            if (this.iSbgain != -1 && sbxGain) this.iSbgain = g.BlockType == ShortBlock ? 1 : -1;
            if (this.iSbDiff != 1 && ch == 1 && g.BlockType != f.Granules[0][gr].BlockType) this.iSbDiff = 1;
          }
      }
      if (this.iAuxH != -1 && aux1) this.iAuxH = 1;
      this._nframes = this._last!.N + 1;
      this._nchannels = this.iChannels == Mp3Mono ? 1 : 2;
    }

    public byte[] CompressMp3() {
      if (this.iProtection == -1) throw new NotSupportedException("packMP3: inconsistent use of checksums.");
      if (this.iStereoInt == -1) throw new NotSupportedException("packMP3: inconsistent use of intensity stereo.");
      if (this.iEmphasis == -1) throw new NotSupportedException("packMP3: inconsistent use of emphasis.");
      if (this.iMixed != 0) throw new NotSupportedException("packMP3: mixed blocks are not supported.");
      if (this.iPrivbit == -1) this.iPrivbit = 0;
      if (this.iCopyright == -1) this.iCopyright = 0;
      if (this.iOriginal == -1) this.iOriginal = 1;
      if (this.iPadbits != 0) this.iPadbits = 0;

      var output = new MemoryStream();
      output.Write(Magic);
      output.WriteByte(Version);
      this.WriteHeader(output);

      if (this._badFirst > 0) {
        for (var f = this._first; this._badFirst > 0; f = f!.Next, --this._badFirst) this.MuteFrame(f!);
        output.Write([.. this._unmute]);
      }

      var enc = new PmpArithmeticCoder(output);
      if (this._before.Length > 0 || this._after.Length > 0) this.EncodeId3(enc);
      if (this.iPadding != 0) this.CodePadding(enc, true);
      if (this.iSblocks != 0) this.CodeBlockTypes(enc, true);
      this.CodeGlobalGain(enc, true);
      this.BuildContext();
      this.CodeSlength(enc, true);
      this.CodeRegionData(enc, true);
      if (this.iShare != 0) this.CodeSharing(enc, true);
      if (this.iPreemphasis != 0) this.CodeBinaryRuns(enc, true, g => g.Preemphasis, (g, v) => g.Preemphasis = v);
      if (this.iCoarse != 0) this.CodeBinaryRuns(enc, true, g => g.Coarse, (g, v) => g.Coarse = v);
      if (this.iSbgain != 0) this.CodeSubblockGain(enc, true);
      if (this.iStereoMs != 0) this.CodeStereoMs(enc, true);
      this.EncodeMainData(enc);
      enc.Finish();
      return output.ToArray();
    }

    private void WriteHeader(MemoryStream output) {
      var h0 = (this.iSamplerate << 6) | (this.iChannels << 4) | (this.iBitrate != -1 ? this.iBitrate : 0);
      var h1 = ((this.iPadding == 0 ? 0 : 1) << 7) | ((this.iStereoMs == 0 ? 0 : 1) << 6) | ((this.iStereoInt == 0 ? 0 : 1) << 5) |
               ((this.iSblocks == 0 ? 0 : 1) << 4) | ((this.iSbgain == 0 ? 0 : 1) << 3) | ((this.iShare == 0 ? 0 : 1) << 2) |
               ((this.iPreemphasis == 0 ? 0 : 1) << 1) | (this.iCoarse == 0 ? 0 : 1);
      var h2 = (this.iProtection << 7) | (this.iOriginal << 6) | (this.iCopyright << 5) | (this.iPrivbit << 4) | (this.iEmphasis << 2) |
               ((this._before.Length > 0 ? 1 : 0) << 1) | (this._after.Length > 0 ? 1 : 0);
      var h3 = ((this.iBitRes == 0 ? 0 : 1) << 7) | ((this.iSbDiff == 0 ? 0 : 1) << 6) | ((this._badFirst > 0 ? 1 : 0) << 5);
      output.Write([(byte)h0, (byte)h1, (byte)h2, (byte)h3]);
      output.Write([(byte)(this._nframes >> 24), (byte)(this._nframes >> 16), (byte)(this._nframes >> 8), (byte)this._nframes]);
    }

    public void UncompressPmp(byte[] pmp) {
      if (pmp.Length < 11 || !pmp.AsSpan(0, 2).SequenceEqual(Magic)) throw new InvalidDataException("packMP3: not a PMP stream.");
      if (pmp[2] != Version) throw new InvalidDataException($"packMP3: incompatible version {pmp[2] / 10}.{pmp[2] % 10}.");
      var h = pmp.AsSpan(3, 4);
      this.iSamplerate = (h[0] >> 6) & 3;
      this.iChannels = (h[0] >> 4) & 3;
      this.iBitrate = h[0] & 0xF;
      if (this.iBitrate == 0) this.iBitrate = -1;
      this.iPadding = -((h[1] >> 7) & 1);
      this.iStereoMs = -((h[1] >> 6) & 1);
      this.iStereoInt = (h[1] >> 5) & 1;
      this.iSblocks = (h[1] >> 4) & 1;
      this.iSbgain = -((h[1] >> 3) & 1);
      this.iShare = -((h[1] >> 2) & 1);
      this.iPreemphasis = -((h[1] >> 1) & 1);
      this.iCoarse = -(h[1] & 1);
      this.iProtection = (h[2] >> 7) & 1;
      this.iOriginal = (h[2] >> 6) & 1;
      this.iCopyright = (h[2] >> 5) & 1;
      this.iPrivbit = (h[2] >> 4) & 1;
      this.iEmphasis = (h[2] >> 2) & 3;
      var hasBefore = ((h[2] >> 1) & 1) != 0;
      var hasAfter = (h[2] & 1) != 0;
      this.iBitRes = -((h[3] >> 7) & 1);
      this.iSbDiff = (h[3] >> 6) & 1;
      this._badFirst = (h[3] >> 5) & 1;
      this.iMpeg = 3;
      this.iLayer = 1;
      this.iPadbits = 0;
      this.iMixed = 0;
      this.iAuxH = -2;
      if (this.iBitrate == 0xF || this.iSamplerate == 3) throw new InvalidDataException("packMP3: not a proper PMP file.");
      this._nframes = (pmp[7] << 24) | (pmp[8] << 16) | (pmp[9] << 8) | pmp[10];
      if (this._nframes is < 0 or > 1 << 24) throw new InvalidDataException("packMP3: bad frame count.");
      this._nchannels = this.iChannels == Mp3Mono ? 1 : 2;
      for (var n = 0; n < this._nframes; ++n) this.AppendFrame(this.BuildFrame());
      if (this._first == null) throw new InvalidDataException("packMP3: no frames.");

      var pos = 11;
      if (this._badFirst > 0) {
        if (pos >= pmp.Length) throw new InvalidDataException("packMP3: unexpected end of data.");
        int n = pmp[pos++];
        var size = n * (2 + this._nchannels * 2 * 4);
        if (pos + size > pmp.Length) throw new InvalidDataException("packMP3: unexpected end of data.");
        this._unmute = [(byte)n, .. pmp.AsSpan(pos, size)];
        pos += size;
        this._badFirst = 0;
      }

      var dec = new PmpArithmeticCoder(pmp, pos);
      if (hasBefore || hasAfter) this.DecodeId3(dec, hasBefore, hasAfter);
      if (this.iPadding != 0) this.CodePadding(dec, false);
      if (this.iSblocks != 0) this.CodeBlockTypes(dec, false);
      this.CodeGlobalGain(dec, false);
      this.BuildContext();
      this.CodeSlength(dec, false);
      this.CodeRegionData(dec, false);
      if (this.iShare != 0) this.CodeSharing(dec, false);
      if (this.iPreemphasis != 0) this.CodeBinaryRuns(dec, false, g => g.Preemphasis, (g, v) => g.Preemphasis = v);
      if (this.iCoarse != 0) this.CodeBinaryRuns(dec, false, g => g.Coarse, (g, v) => g.Coarse = v);
      if (this.iSbgain != 0) this.CodeSubblockGain(dec, false);
      if (this.iStereoMs != 0) this.CodeStereoMs(dec, false);
      this.DecodeMainData(dec);

      if (this._unmute.Count > 0)
        for (var f = this._first; this._badFirst < this._unmute[0]; f = f!.Next, ++this._badFirst) this.UnmuteFrame(f!);
    }

    public byte[] WriteMp3() {
      var output = new MemoryStream();
      output.Write(this._before);
      var at = 0;
      for (var f = this._first; f != null; f = f.Next) {
        output.Write(this.BuildFixed(f));
        var length = f.FrameSize - f.FixedSize;
        if (length < 0 || at + length > this._mainDataSize) throw new InvalidDataException("packMP3: frame data out of bounds.");
        output.Write(this._mainData, at, length);
        at += length;
      }
      output.Write(this._after);
      return output.ToArray();
    }

    // ── PMP side information ───────────────────────────────────────────

    private void EncodeId3(PmpArithmeticCoder enc) {
      var model = new PmpModelS(256 + 1, 256, 0);
      if (this._before.Length > 0) {
        foreach (var b in this._before) model.Encode(enc, b);
        model.Encode(enc, 256);
      }
      if (this._after.Length > 0) {
        foreach (var b in this._after) model.Encode(enc, b);
        model.Encode(enc, 256);
      }
    }

    private void DecodeId3(PmpArithmeticCoder dec, bool before, bool after) {
      var model = new PmpModelS(256 + 1, 256, 0);
      byte[] Chunk() {
        var bytes = new List<byte>();
        for (int c; (c = model.Decode(dec)) != 256;) {
          bytes.Add((byte)c);
          if (bytes.Count > 1 << 28) throw new InvalidDataException("packMP3: tag data too large.");
        }
        return [.. bytes];
      }
      if (before) this._before = Chunk();
      if (after) this._after = Chunk();
    }

    private void CodePadding(PmpArithmeticCoder coder, bool encode) {
      var model = new PmpModelS(PaddingMaxRun + 1, PaddingMaxRun + 1, 1);
      if (encode) {
        int run = 0, bit = 0;
        for (var f = this._first; f != null; f = f.Next) {
          if (f.Padding != bit) {
            bit ^= 1;
            EncodeRun(coder, model, ref run, PaddingMaxRun, true);
            run = 0;
          }
          ++run;
        }
        if (run > 0) EncodeRun(coder, model, ref run, PaddingMaxRun, false);
      } else {
        var run = model.Decode(coder);
        model.ShiftContext(run);
        var bit = 0;
        for (var f = this._first; f != null; f = f.Next) {
          while (run == 0) {
            bit ^= 1;
            run = model.Decode(coder);
            model.ShiftContext(run);
          }
          --run;
          f.Padding = bit;
        }
      }
    }

    /// <summary>Encodes a run as packMP3 does: maximal pieces separated by zero-length runs, then the rest.</summary>
    private static void EncodeRun(PmpArithmeticCoder coder, PmpModelS model, ref int run, int maxRun, bool shiftAfter) {
      while (run >= maxRun + 1) {
        model.Encode(coder, maxRun);
        model.ShiftContext(maxRun);
        model.Encode(coder, 0);
        model.ShiftContext(0);
        run -= maxRun;
      }
      model.Encode(coder, run);
      if (shiftAfter) model.ShiftContext(run);
    }

    private void CodeBlockTypes(PmpArithmeticCoder coder, bool encode) {
      var sw = new PmpModelS(SwitchingMaxRun + 1, SwitchingMaxRun + 1, 1);
      var bt = new PmpModelS(4, 4, 1);
      for (var ch = 0; ch < this._nchannels; ++ch) {
        if (this.iSbDiff == 0 && ch == 1) break;
        sw.ShiftContext(0);
        if (encode) {
          int run = 0, bit = 0;
          for (var g = this._first!.Granules[ch][0]; g != null; g = g.Next) {
            if (g.WindowSwitching != bit) {
              bit ^= 1;
              EncodeRun(coder, sw, ref run, SwitchingMaxRun, true);
              run = 0;
            }
            ++run;
          }
          if (run > 0) EncodeRun(coder, sw, ref run, SwitchingMaxRun, true);
        } else {
          var run = sw.Decode(coder);
          sw.ShiftContext(run);
          var bit = 0;
          for (var g = this._first!.Granules[ch][0]; g != null; g = g.Next) {
            while (run == 0) {
              bit ^= 1;
              run = sw.Decode(coder);
              sw.ShiftContext(run);
            }
            --run;
            g.WindowSwitching = bit;
          }
        }
        var c = StopBlock;
        for (var g = this._first!.Granules[ch][0]; g != null; g = g.Next) {
          if (g.WindowSwitching != 0) {
            int ctx;
            if (c == StopBlock) ctx = StartBlock;
            else if (g.Next != null) ctx = g.Next.WindowSwitching != 0 ? ShortBlock : StopBlock;
            else ctx = StopBlock;
            bt.ShiftContext(ctx);
            if (encode) {
              c = g.BlockType;
              bt.Encode(coder, c);
            } else {
              c = bt.Decode(coder);
              g.BlockType = c;
            }
          } else if (!encode)
            g.BlockType = LongBlock;
        }
      }
      if (!encode && this.iSbDiff == 0 && this._nchannels == 2) {
        var g1 = this._first!.Granules[1][0];
        for (var g0 = this._first.Granules[0][0]; g0 != null; g0 = g0.Next) {
          g1!.WindowSwitching = g0.WindowSwitching;
          g1.BlockType = g0.BlockType;
          g1 = g1.Next;
        }
      }
    }

    private void CodeGlobalGain(PmpArithmeticCoder coder, bool encode) {
      var model = new PmpModelS(256, 0, 0);
      var last = 0;
      for (var g0 = this._first!.Granules[0][0]; g0 != null; g0 = g0.Next) {
        if (encode) {
          model.Encode(coder, (g0.GlobalGain - last) & 0xFF);
          last = g0.GlobalGain;
        } else {
          last = (model.Decode(coder) + last) & 0xFF;
          g0.GlobalGain = last;
        }
      }
      if (this._nchannels == 2) {
        var g0 = this._first.Granules[0][0];
        for (var g1 = this._first.Granules[1][0]; g1 != null; g1 = g1.Next) {
          if (encode)
            model.Encode(coder, (g1.GlobalGain - g0!.GlobalGain) & 0xFF);
          else
            g1.GlobalGain = (model.Decode(coder) + g0!.GlobalGain) & 0xFF;
          g0 = g0.Next;
        }
      }
    }

    private void BuildContext() {
      var ngr = this._nframes * 2;
      this._ggContext[0] = new byte[ngr];
      this._ggContext[1] = new byte[ngr];
      var counts = new int[2, 256];
      var i = 0;
      for (var g = this._first!.Granules[0][0]; g != null; g = g.Next, ++i) {
        this._ggContext[0][i] = (byte)g.GlobalGain;
        ++counts[0, (byte)g.GlobalGain];
      }
      for (var k = 1; k < 256; ++k) counts[0, k] += counts[0, k - 1];
      if (this._nchannels == 2) {
        i = 0;
        for (var g = this._first.Granules[1][0]; g != null; g = g.Next, ++i) {
          this._ggContext[1][i] = (byte)g.GlobalGain;
          ++counts[1, (byte)g.GlobalGain];
        }
        for (var k = 1; k < 256; ++k) counts[1, k] += counts[1, k - 1];
      }
      for (var ch = 0; ch < this._nchannels; ++ch) {
        int lbound = 0, inc0 = 0;
        for (var k = 0; k < 256 - GgContextSize; ++k) {
          var inc1 = counts[ch, k + GgContextSize] - counts[ch, k];
          if (inc1 >= inc0) {
            inc0 = inc1;
            lbound = k;
          }
        }
        var ctx = this._ggContext[ch];
        for (var k = 0; k < ngr; ++k) {
          if (ctx[k] >= lbound) {
            ctx[k] = (byte)(ctx[k] - lbound);
            if (ctx[k] >= GgContextSize) ctx[k] = GgContextSize - 1;
          } else
            ctx[k] = 0;
        }
      }
    }

    private void CodeSlength(PmpArithmeticCoder coder, bool encode) {
      var model = new PmpModelS(16, Math.Max(GgContextSize, 16), 2);
      for (var ch = 0; ch < this._nchannels; ++ch) {
        model.Flush(1);
        var gg = this._ggContext[ch];
        var c = 0;
        var i = 0;
        for (var g = this._first!.Granules[ch][0]; g != null; g = g.Next) {
          model.Shift(gg[i++], c);
          if (encode) {
            c = g.SLength;
            model.Encode(coder, c);
          } else {
            c = model.Decode(coder);
            g.SLength = c;
          }
        }
      }
    }

    private void CodeStereoMs(PmpArithmeticCoder coder, bool encode) {
      var model = new PmpModelB(16, 1);
      var ctx = 0;
      for (var f = this._first; f != null; f = f.Next) {
        model.ShiftContext(ctx);
        if (encode) model.Encode(coder, f.StereoMs);
        else f.StereoMs = model.Decode(coder);
        ctx = ((ctx << 1) | f.StereoMs) & 0xF;
      }
    }

    private void CodeRegionData(PmpArithmeticCoder coder, bool encode) {
      var samples = this.iSamplerate;
      var t0 = new PmpModelS(32, Math.Max(GgContextSize, 32), 2);
      var t1 = new PmpModelS(32, 32, 2);
      var t2 = new PmpModelS(32, 32, 2);
      var ts = new PmpModelB(16, 1);
      var s0 = new PmpModelS(16, 22, 2);
      var s1 = new PmpModelS(8, 22, 2);
      var bv = new PmpModelS(576 / 2 + 1, 1 + 1, 1);
      for (var ch = 0; ch < this._nchannels; ++ch) {
        s0.Flush(1);
        s1.Flush(1);
        bv.Flush(1);
        var gg = this._ggContext[ch];
        var ggAt = 0;
        int ctxSv = 0, tR0 = 0, tR1, sR0 = 0, sR2;
        for (var g = this._first!.Granules[ch][0]; g != null; g = g.Next) {
          if (encode) {
            if (g.BigValPairs > 576 / 2) throw new InvalidDataException("packMP3: big value pairs out of bounds.");
            if (g.WindowSwitching != 0) {
              bv.ShiftContext(g.BlockType == ShortBlock ? 1 : 0);
              bv.Encode(coder, g.BigValPairs);
              sR0 = 0;
            } else {
              sR2 = PackMp3Tables.BandwidthConv[samples * 577 + (g.BigValPairs << 1)];
              bv.ShiftContext(0);
              bv.Encode(coder, g.BigValPairs);
              s0.Shift(sR0, sR2);
              sR0 = g.Region0Size;
              s0.Encode(coder, sR0);
              s1.Shift(sR0, sR2);
              s1.Encode(coder, g.Region1Size);
              ++sR0;
            }
          } else if (g.WindowSwitching == 0) {
            bv.ShiftContext(0);
            g.BigValPairs = bv.Decode(coder);
            sR2 = PackMp3Tables.BandwidthConv[samples * 577 + (g.BigValPairs << 1)];
            s0.Shift(sR0, sR2);
            sR0 = s0.Decode(coder);
            g.Region0Size = sR0;
            s1.Shift(sR0, sR2);
            g.Region1Size = s1.Decode(coder);
            g.RegionBound[0] = BandBound(samples, sR0 + 1);
            g.RegionBound[1] = BandBound(samples, sR0 + g.Region1Size + 2);
            g.RegionBound[2] = g.BigValPairs << 1;
            if (g.RegionBound[0] > g.RegionBound[2]) {
              g.RegionBound[0] = g.RegionBound[2];
              g.RegionBound[1] = g.RegionBound[2];
            } else if (g.RegionBound[1] > g.RegionBound[2])
              g.RegionBound[1] = g.RegionBound[2];
            ++sR0;
          } else if (g.BlockType != ShortBlock) {
            g.Region0Size = 8;
            g.Region1Size = 0;
            g.RegionBound[0] = BandBound(samples, 8);
            bv.ShiftContext(0);
            g.BigValPairs = bv.Decode(coder);
            g.RegionBound[1] = g.BigValPairs << 1;
            if (g.RegionBound[0] > g.RegionBound[1]) g.RegionBound[0] = g.RegionBound[1];
            g.RegionBound[2] = g.RegionBound[1];
            sR0 = 0;
          } else {
            g.Region0Size = 9;
            g.Region1Size = 0;
            g.RegionBound[0] = BandBoundShort(samples, 9 / 3) * 3;
            bv.ShiftContext(1);
            g.BigValPairs = bv.Decode(coder);
            g.RegionBound[1] = g.BigValPairs << 1;
            if (g.RegionBound[0] > g.RegionBound[1]) g.RegionBound[0] = g.RegionBound[1];
            g.RegionBound[2] = g.RegionBound[1];
            sR0 = 0;
          }

          t0.Shift(gg[ggAt], tR0);
          if (encode) {
            tR0 = g.RegionTable[0];
            t0.Encode(coder, tR0);
          } else
            g.RegionTable[0] = tR0 = t0.Decode(coder);
          t1.Shift(tR0, sR0);
          if (encode) {
            tR1 = g.RegionTable[1];
            t1.Encode(coder, tR1);
          } else
            g.RegionTable[1] = tR1 = t1.Decode(coder);
          if (g.WindowSwitching == 0) {
            t2.Shift(tR0, tR1);
            if (encode) t2.Encode(coder, g.RegionTable[2]);
            else g.RegionTable[2] = t2.Decode(coder);
          } else if (!encode)
            g.RegionTable[2] = 0;
          ts.ShiftContext(ctxSv);
          int tSv;
          if (encode) {
            tSv = g.SelectHtabB;
            ts.Encode(coder, tSv);
          } else
            g.SelectHtabB = tSv = ts.Decode(coder);
          ctxSv = ((ctxSv << 1) | tSv) & 0xF;
          ++ggAt;
        }
      }
    }

    private void CodeSharing(PmpArithmeticCoder coder, bool encode) {
      var model = new PmpModelS(16, 16, 3);
      for (var ch = 0; ch < this._nchannels; ++ch) {
        model.Flush(1);
        var c = 0;
        for (var g = this._first!.Granules[ch][0]; g != null; g = g.Next!.Next) {
          model.Shift(c, g.SLength, g.Next!.SLength);
          if (encode) {
            c = g.Share;
            model.Encode(coder, c);
          } else {
            c = model.Decode(coder);
            g.Share = c;
          }
        }
      }
    }

    /// <summary>Preemphasis and coarse scalefactors: one bit per granule, last four bits as context.</summary>
    private void CodeBinaryRuns(PmpArithmeticCoder coder, bool encode, Func<Granule, int> get, Action<Granule, int> set) {
      var model = new PmpModelB(16, 1);
      for (var ch = 0; ch < this._nchannels; ++ch) {
        model.Flush(1);
        var ctx = 0;
        for (var g = this._first!.Granules[ch][0]; g != null; g = g.Next) {
          model.ShiftContext(ctx);
          int c;
          if (encode) {
            c = get(g);
            model.Encode(coder, c);
          } else {
            c = model.Decode(coder);
            set(g, c);
          }
          ctx = ((ctx << 1) | c) & 0xF;
        }
      }
    }

    private void CodeSubblockGain(PmpArithmeticCoder coder, bool encode) {
      var model = new PmpModelS(8, 8, 1);
      model.ShiftContext(0);
      for (var ch = 0; ch < this._nchannels; ++ch)
        for (var g = this._first!.Granules[ch][0]; g != null; g = g.Next) {
          if (g.WindowSwitching != 0) {
            for (var sb = 0; sb < 3; ++sb) {
              if (encode) model.Encode(coder, g.SbGain[sb]);
              else g.SbGain[sb] = model.Decode(coder);
              model.ShiftContext(g.SbGain[sb]);
            }
          } else if (!encode)
            g.SbGain[0] = g.SbGain[1] = g.SbGain[2] = 0;
        }
    }

    // ── main data ──────────────────────────────────────────────────────

    /// <summary>The statistical models and context arrays both directions of the main data share.</summary>
    private sealed class MainModels {
      public readonly PmpModelS[,,] Scf = new PmpModelS[2, 4, 10];
      public readonly PmpModelS?[,,] Abv = new PmpModelS?[2, 8, 32];
      public readonly PmpModelB[,,] Asv = new PmpModelB[2, 8, 2];
      public readonly PmpModelB[,] Sgn = new PmpModelB[2, 8];
      public readonly PmpModelS?[,] Len = new PmpModelS?[2, 14];
      public readonly PmpModelB Res = new(13 + 1, 2);
      public readonly PmpModelS Svb = new(576 / 4 + 1 + 1, 576 / 4 + 1 + 1, 1);
      public readonly PmpModelS Bvf = new(576 / 2 + 1, 0, 0);
      public readonly PmpModelS Nst = new(StuffingStep + 1, StuffingStep + 1, 1);
      public readonly PmpModelB Bst = new(16, 1);
      public readonly PmpModelB Pad = new(256, 1);
      public readonly PmpModelB Pap = new(0, 0);
      public readonly PmpModelS Aux = new(AuxDataStep + 1, AuxDataStep + 1, 1);
      public readonly PmpModelS Btr = new(16, 16, 1);

      public readonly byte[][] ScfC = [new byte[21], new byte[21]];
      public readonly byte[][] ScfLong = [new byte[21], new byte[21]];
      public readonly byte[][] ScfShort = [new byte[21], new byte[21]];
      // coefficient arrays carry one extra slot in front (index -1) and behind, as in packMP3
      public readonly byte[][] Abs = [new byte[580], new byte[580]];
      public readonly byte[][] Sgn2 = [new byte[580], new byte[580]];
      public readonly byte[][] Len2 = [new byte[580], new byte[580]];
      public readonly ushort[][] Lbt = [new ushort[576], new ushort[576]];
      public readonly byte[][] AbsLong = [new byte[580], new byte[580]];
      public readonly byte[][] AbsShort = [new byte[580], new byte[580]];
      public readonly byte[][] SgnLong = [new byte[580], new byte[580]];
      public readonly byte[][] SgnShort = [new byte[580], new byte[580]];
      public readonly byte[][] LenLong = [new byte[580], new byte[580]];
      public readonly byte[][] LenShort = [new byte[580], new byte[580]];

      public MainModels(int nchannels, bool jointCoding) {
        for (var ch = 0; ch < nchannels; ++ch) {
          for (var g = 0; g < 10; ++g) {
            this.Scf[ch, 0, g] = new(2, 16, 2);
            this.Scf[ch, 1, g] = new(4, 16, 2);
            this.Scf[ch, 2, g] = new(8, 16, 2);
            this.Scf[ch, 3, g] = new(16, 16, 2);
          }
          for (var flags = 0; flags < (jointCoding && ch != 0 ? 8 : 2); ++flags) {
            int[] sizes = [0, 2, 3, 3, 0, 4, 4, 6, 6, 6, 8, 8, 8, 16, 0, 16];
            for (var t = 1; t < 16; ++t)
              if (sizes[t] != 0) this.Abv[ch, flags, t] = new(sizes[t], 16, 2);
            PmpModelS m16 = new(15 + 1, 16, 2), m24 = new(15 + 1, 16, 2);
            for (var t = 16; t < 24; ++t) this.Abv[ch, flags, t] = m16;
            for (var t = 24; t < 32; ++t) this.Abv[ch, flags, t] = m24;
            this.Asv[ch, flags, 0] = new(16, 2);
            this.Asv[ch, flags, 1] = new(16, 2);
            this.Sgn[ch, flags] = new(16, 2);
          }
          for (var i = 1; i <= 13; ++i) this.Len[ch, i] = new(i + 1, 13 + 1, 1);
        }
      }

      /// <summary>Context histories after a granule (identical in both directions).</summary>
      public void UpdateContexts(int ch, bool shortBlock, bool jointCoding, int p) {
        var abs = this.Abs[ch];
        var sgn = this.Sgn2[ch];
        var len = this.Len2[ch];
        if (p < 578) Array.Clear(abs, p + 1, 578 - p);
        if (jointCoding && ch != 0) return;
        var hAbs = shortBlock ? this.AbsShort[ch] : this.AbsLong[ch];
        var hSgn = shortBlock ? this.SgnShort[ch] : this.SgnLong[ch];
        var hLen = shortBlock ? this.LenShort[ch] : this.LenLong[ch];
        for (var i = 578 - 1; i >= 0; --i) {
          hAbs[i + 1] = (byte)((3 * abs[i + 1] + abs[i] + abs[i + 2] + 2 * hAbs[i + 1] + 4) / 7);
          if (abs[i + 1] > 0) {
            hSgn[i + 1] = (byte)(((hSgn[i + 1] << 1) | (sgn[i + 1] & 1)) & 0xF);
            if (abs[i + 1] == 15) hLen[i + 1] = (byte)((2 * len[i + 1] + hLen[i + 1] + 2) / 3);
            else hLen[i + 1] >>= 1;
          } else {
            hSgn[i + 1] = (byte)((hSgn[i + 1] << 1) & 0xF);
            hLen[i + 1] >>= 1;
          }
        }
        if (!jointCoding) return;
        hAbs = shortBlock ? this.AbsShort[1] : this.AbsLong[1];
        hSgn = shortBlock ? this.SgnShort[1] : this.SgnLong[1];
        hLen = shortBlock ? this.LenShort[1] : this.LenLong[1];
        if (p < 578) {
          Array.Clear(hAbs, p + 1, 578 - p);
          Array.Fill(hSgn, (byte)2, p + 1, 578 - p);
          Array.Clear(hLen, p + 1, 578 - p);
        }
        for (var i = p - 1; i >= 0; --i) {
          hAbs[i + 1] = abs[i + 1];
          if (abs[i + 1] > 0) {
            hSgn[i + 1] = sgn[i + 1];
            hLen[i + 1] = abs[i + 1] == 15 ? len[i + 1] : (byte)0;
          } else {
            hSgn[i + 1] = 2;
            hLen[i + 1] = 0;
          }
        }
      }
    }

    // LAME ancillary-data predictor state (static in packMP3)
    private readonly byte[] _pred = new byte[2048];
    private readonly byte[] _lameStr = new byte[4 + 16];
    private int _lameStrLength = 4, _lameBit;
    private bool _altPred;

    /// <summary>Returns null when the prediction matches <paramref name="reference"/>, the prediction otherwise.</summary>
    private byte[]? PredictLameAncillary(int nbits, byte[]? reference) {
      if (nbits < 0) {
        this._lameBit = 0;
        this._lameStrLength = 4;
        "LAME"u8.CopyTo(this._lameStr);
        this._altPred = false;
        return this._pred;
      }
      var offset = nbits % 8;
      var nbytes = nbits / 8;
      if (!this._altPred) {
        var i = 0;
        for (; nbits >= 8 && i < 4; nbits -= 8, ++i) this._pred[i] = this._lameStr[i];
        if (nbits >= 32)
          for (; nbits >= 8 && i < this._lameStrLength; nbits -= 8, ++i) this._pred[i] = this._lameStr[i];
        for (; nbits > 0; nbits -= 8, ++i) this._pred[i] = this._lameBit == 0 ? (byte)0x55 : (byte)0xAA;
        this._lameBit ^= offset % 2;
      }
      if (reference != null) {
        if (this._pred.AsSpan(0, nbytes).SequenceEqual(reference.AsSpan(0, nbytes)) && reference[nbytes] == this._pred[nbytes] >> (8 - offset))
          return null;
        if (nbytes >= 8 && reference.AsSpan(0, 4).SequenceEqual(this._lameStr.AsSpan(0, 4))) {
          int i;
          for (i = 4; i < 4 + 16 && i < nbytes; ++i) {
            if (reference[i] == 0x55) {
              this._lameBit = 0;
              break;
            }
            if (reference[i] == 0xAA) {
              this._lameBit = 1;
              break;
            }
            this._lameStr[i] = reference[i];
          }
          this._lameStrLength = i;
        } else if (nbytes == 0 && offset > 1) {
          if ((0x00 ^ reference[0]) >> (8 - offset) == 0) {
            Array.Fill(this._pred, (byte)0x00);
            this._altPred = true;
          } else if ((0xFF ^ reference[0]) >> (8 - offset) == 0) {
            Array.Fill(this._pred, (byte)0xFF);
            this._altPred = true;
          } else
            this._altPred = false;
        } else if (nbytes >= 1) {
          if (reference[0] == 0x00) {
            Array.Fill(this._pred, (byte)0x00);
            this._altPred = true;
          } else if (reference[0] == 0xFF) {
            Array.Fill(this._pred, (byte)0xFF);
            this._altPred = true;
          } else
            this._altPred = false;
        }
      }
      return this._pred;
    }

    private void EncodeMainData(PmpArithmeticCoder enc) {
      var padAndAux = new byte[2048 + 1];
      var joint = this.iChannels is Mp3Stereo or Mp3JointStereo;
      var huffman = new PmpBitReader(this._mainData, this._mainDataSize);
      this.PredictLameAncillary(-1, null);
      var m = new MainModels(this._nchannels, joint);
      var ctxSvb = new int[2];
      int ctxNst = 0, ctxBst = 0, ctxAux = 0;
      var shared = new int[2, 4];
      var flags = 0;

      for (var frame = this._first; frame != null; frame = frame.Next) {
        var bitp = 0;
        for (var gr = 0; gr < 2; ++gr) {
          for (var ch = 0; ch < this._nchannels; ++ch) {
            var g = frame.Granules[ch][gr];
            var sbl = g.BlockType == ShortBlock ? 1 : 0;
            var lmaxp = g.MainDataBit;
            var abs = m.Abs[ch];
            var sgn = m.Sgn2[ch];
            var len = m.Len2[ch];
            var lbt = m.Lbt[ch];
            flags = !joint || ch == 0 ? sbl : sbl | (frame.StereoMs << 1) | ((flags ^ sbl) << 2);
            if (gr == 0)
              for (var k = 0; k < 4; ++k) shared[ch, k] = (g.Share >> (3 - k)) & 1;
            huffman.ResetCounter();
            var bitc = 0;
            var badCoding = false;

            // scalefactors
            if (sbl == 0) {
              var scf = m.ScfC[ch];
              var prev = m.ScfLong[ch];
              var ctx = 0;
              for (int grp = 0, p = 0; grp < 4; ++grp) {
                var sl = SlengthTable[g.SLength, grp < 2 ? 0 : 1];
                if ((gr & shared[ch, grp]) != 0) {
                  Array.Copy(prev, p, scf, p, ScfWidth[grp]);
                  p = ScfBounds[grp];
                  ctx = scf[p - 1];
                } else if (sl == 0) {
                  Array.Clear(scf, p, ScfWidth[grp]);
                  p = ScfBounds[grp];
                  ctx = 0;
                } else {
                  var model = m.Scf[ch, sl - 1, shared[ch, grp] != 0 ? grp | 4 : grp];
                  for (; p < ScfBounds[grp]; ++p) {
                    scf[p] = (byte)huffman.ReadBits(sl);
                    model.Shift(ctx, prev[p]);
                    model.Encode(enc, scf[p]);
                    ctx = scf[p];
                  }
                }
              }
              (m.ScfC[ch], m.ScfLong[ch]) = (m.ScfLong[ch], m.ScfC[ch]);
            } else {
              for (var i = 0; i < 3; ++i) {
                var scf = m.ScfC[ch];
                var prev = m.ScfShort[ch];
                var ctx = 0;
                for (int grp = 0, p = 0; grp < 2; ++grp) {
                  var sl = SlengthTable[g.SLength, grp];
                  if (sl == 0) {
                    Array.Clear(scf, p, ScfLhWidthShort[grp]);
                    p = ScfLhBoundsShort[grp];
                  } else {
                    var model = m.Scf[ch, sl - 1, grp | 8];
                    for (; p < ScfLhBoundsShort[grp]; ++p) {
                      scf[p] = (byte)huffman.ReadBits(sl);
                      model.Shift(ctx, prev[p]);
                      model.Encode(enc, scf[p]);
                      ctx = scf[p];
                    }
                  }
                }
                (m.ScfC[ch], m.ScfShort[ch]) = (m.ScfShort[ch], m.ScfC[ch]);
              }
            }
            if (huffman.Count > lmaxp) throw new InvalidDataException($"packMP3: Huffman decoding error (in frame #{frame.N}).");

            // big values
            int pos = 0;
            for (var r = 0; r < 3 && !badCoding; ++r) {
              if (g.RegionTable[r] == 0) {
                pos = g.RegionBound[r];
                continue;
              }
              var code = PmpHuffman.BigValues(g.RegionTable[r])
                ?? throw new InvalidDataException($"packMP3: bad Huffman table ({g.RegionTable[r]}) used (in frame #{frame.N}).");
              var tree = PmpHuffman.TreeOf(code);
              var linbits = PmpHuffman.Linbits(g.RegionTable[r]);
              for (; pos < g.RegionBound[r]; bitc = huffman.Count) {
                var (v0, v1) = huffman.DecodePair(tree);
                abs[pos + 1] = (byte)v0;
                abs[pos + 2] = (byte)v1;
                for (var i = 0; i < 2; ++i, ++pos)
                  if (abs[pos + 1] > 0) {
                    if (linbits > 0 && abs[pos + 1] == 15) {
                      lbt[pos] = (ushort)huffman.ReadBits(linbits);
                      len[pos + 1] = (byte)BitLength(lbt[pos]);
                    }
                    sgn[pos + 1] = (byte)huffman.ReadBit();
                  }
                if (huffman.Count > lmaxp) {
                  badCoding = true;
                  --pos;
                  abs[pos + 1] = sgn[pos + 1] = len[pos + 1] = 0;
                  --pos;
                  abs[pos + 1] = sgn[pos + 1] = len[pos + 1] = 0;
                  huffman.Rewind(huffman.Count - bitc);
                  break;
                }
              }
            }

            // small values
            if (!badCoding) {
              var tree = PmpHuffman.TreeOf(g.SelectHtabB != 0 ? PmpHuffman.QuadB : PmpHuffman.QuadA);
              for (bitc = huffman.Count; pos < 576; bitc = huffman.Count) {
                if (bitc == lmaxp) break;
                var quad = huffman.DecodeQuadruple(tree);
                for (var k = 0; k < 4; ++k) abs[pos + 1 + k] = (byte)((quad >> (3 - k)) & 1);
                for (var i = 0; i < 4; ++i, ++pos)
                  if (abs[pos + 1] > 0) sgn[pos + 1] = (byte)huffman.ReadBit();
                if (huffman.Count > lmaxp) {
                  badCoding = true;
                  for (var k = 0; k < 4; ++k) {
                    --pos;
                    abs[pos + 1] = sgn[pos + 1] = 0;
                  }
                  huffman.Rewind(huffman.Count - bitc);
                  break;
                }
              }
            }

            // sv bound
            g.SvBound = pos;
            m.Svb.ShiftContext(sbl == 0 ? ctxSvb[ch] : 144 + 1);
            if (pos >= g.RegionBound[2]) {
              var c = g.SvBound >> 2;
              m.Svb.Encode(enc, c);
              if (sbl == 0) ctxSvb[ch] = c;
            } else {
              m.Svb.Encode(enc, 144 + 1);
              m.Bvf.Encode(enc, (g.RegionBound[2] - g.SvBound) >> 1);
              g.RegionBound[2] = pos;
              for (var i = 1; i >= 0; --i) {
                if (pos < g.RegionBound[i]) g.RegionBound[i] = pos;
                else break;
              }
            }

            this.CodeCoefficients(enc, null, m, g, ch, flags, sbl, ref pos);
            m.UpdateContexts(ch, sbl != 0, joint, g.SvBound);

            // stuffing bits
            var n = lmaxp - bitc;
            if (n == 0) {
              m.Nst.ShiftContext(ctxNst);
              m.Nst.Encode(enc, 0);
              ctxNst = 0;
            } else {
              for (; n >= StuffingStep; n -= StuffingStep) {
                m.Nst.ShiftContext(ctxNst);
                m.Nst.Encode(enc, StuffingStep);
                ctxNst = StuffingStep;
              }
              m.Nst.ShiftContext(ctxNst);
              m.Nst.Encode(enc, n);
              ctxNst = n;
              for (; bitc < lmaxp; ++bitc) {
                m.Bst.ShiftContext(ctxBst);
                var c = huffman.ReadBit();
                m.Bst.Encode(enc, c);
                ctxBst = ((ctxBst << 1) | c) & 0xF;
              }
            }
            bitp += lmaxp;
          }
        }

        if (this.iBitrate == -1) {
          m.Btr.ShiftContext(PackMp3Tables.BitratePrediction[this.iSamplerate * 2049 + frame.MainSize]);
          m.Btr.Encode(enc, frame.Bits);
        }

        if (frame != this._last && this.iBitRes != 0) {
          var n = frame.AuxSize + frame.Next!.BitReservoir < 511 ? frame.AuxSize : 511 - frame.Next.BitReservoir;
          for (; n >= AuxDataStep; n -= AuxDataStep) {
            m.Aux.ShiftContext(ctxAux);
            m.Aux.Encode(enc, AuxDataStep);
            ctxAux = AuxDataStep;
          }
          m.Aux.ShiftContext(ctxAux);
          m.Aux.Encode(enc, n);
          ctxAux = n;
        }

        var bits = (frame.MainSize + frame.AuxSize) * 8 - bitp;
        if (bits < 0 || bits > 2048 * 8) throw new InvalidDataException("packMP3: inconsistent frame data sizes.");
        int at = 0, rest;
        for (rest = bits; rest >= 8; rest -= 8) padAndAux[at++] = (byte)huffman.ReadBits(8);
        padAndAux[at] = (byte)huffman.ReadBits(rest);
        if (this.PredictLameAncillary(bits, padAndAux) == null)
          m.Pap.Encode(enc, 1);
        else {
          m.Pap.Encode(enc, 0);
          huffman.Rewind(bits);
          var ctxPad = 0xFF;
          for (; bits > 0; --bits) {
            m.Pad.ShiftContext(ctxPad);
            var c = huffman.ReadBit();
            m.Pad.Encode(enc, c);
            ctxPad = ((ctxPad << 1) | c) & 0xFF;
          }
        }
      }
    }

    /// <summary>Codes small and big values from the sv bound downwards (both directions).</summary>
    private void CodeCoefficients(PmpArithmeticCoder? enc, PmpArithmeticCoder? dec, MainModels m, Granule g, int ch, int flags, int sbl, ref int pos) {
      var abs = m.Abs[ch];
      var sgn = m.Sgn2[ch];
      var len = m.Len2[ch];
      var lbt = m.Lbt[ch];
      var hAbs = sbl == 0 ? m.AbsLong[ch] : m.AbsShort[ch];
      var hSgn = sbl == 0 ? m.SgnLong[ch] : m.SgnShort[ch];
      var hLen = sbl == 0 ? m.LenLong[ch] : m.LenShort[ch];
      int ctxAbs = 0, ctxPat = 0;
      var rlb = g.RegionBound[2];
      var sgc = m.Sgn[ch, flags];
      var asc = m.Asv[ch, flags, g.SelectHtabB];
      int p;
      for (p = g.SvBound - 1; p >= rlb; --p) {
        asc.Shift(ctxPat, hAbs[p + 1]);
        if (enc != null) asc.Encode(enc, abs[p + 1]);
        else abs[p + 1] = (byte)asc.Decode(dec!);
        ctxPat = ((ctxPat << 1) | abs[p + 1]) & 0xF;
        ctxAbs = (2 * abs[p + 1] + ctxAbs + 2) / 3;
        if (abs[p + 1] == 1) {
          sgc.Shift(hAbs[p + 1], hSgn[p + 1]);
          if (enc != null) sgc.Encode(enc, sgn[p + 1]);
          else sgn[p + 1] = (byte)sgc.Decode(dec!);
        }
      }
      for (var r = 2; r >= 0; --r) {
        rlb = r == 0 ? 0 : g.RegionBound[r - 1];
        if (p < rlb) continue;
        if (g.RegionTable[r] == 0) {
          Array.Clear(abs, rlb + 1, p - rlb);
          p = rlb;
          ctxAbs = 0;
          continue;
        }
        var linbits = PmpHuffman.Linbits(g.RegionTable[r]);
        var abc = m.Abv[ch, flags, g.RegionTable[r]]
          ?? throw new InvalidDataException("packMP3: bad Huffman table.");
        var lnc = m.Len[ch, linbits];
        for (; p >= rlb; --p) {
          abc.Shift(ctxAbs, hAbs[p + 1]);
          if (enc != null) abc.Encode(enc, abs[p + 1]);
          else abs[p + 1] = (byte)abc.Decode(dec!);
          ctxAbs = (2 * abs[p + 1] + ctxAbs + 2) / 3;
          if (abs[p + 1] > 0) {
            sgc.Shift(hAbs[p + 1], hSgn[p + 1]);
            if (enc != null) sgc.Encode(enc, sgn[p + 1]);
            else sgn[p + 1] = (byte)sgc.Decode(dec!);
            if (linbits > 0 && abs[p + 1] == 15) {
              lnc!.ShiftContext(hLen[p + 1]);
              if (enc != null) {
                lnc.Encode(enc, len[p + 1]);
                for (var i = len[p + 1] - 2; i >= 0; --i) {
                  m.Res.Shift(len[p + 1], i);
                  m.Res.Encode(enc, (lbt[p] >> i) & 1);
                }
              } else {
                len[p + 1] = (byte)lnc.Decode(dec!);
                if (len[p + 1] > 0) {
                  lbt[p] = 1;
                  for (var i = len[p + 1] - 2; i >= 0; --i) {
                    m.Res.Shift(len[p + 1], i);
                    lbt[p] = (ushort)((lbt[p] << 1) | m.Res.Decode(dec!));
                  }
                } else
                  lbt[p] = 0;
              }
            }
          }
        }
      }
      pos = p;
    }

    private static int BitLength(int v) => 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

    private void DecodeMainData(PmpArithmeticCoder dec) {
      var padAndAux = new byte[2048 + 1];
      var joint = this.iChannels is Mp3Stereo or Mp3JointStereo;
      var huffman = new PmpBitWriter();
      this.PredictLameAncillary(-1, null);
      var m = new MainModels(this._nchannels, joint);
      var ctxSvb = new int[2];
      int ctxNst = 0, ctxBst = 0, ctxAux = 0;
      var shared = new int[2, 4];
      var flags = 0;
      var bitres = 0;

      for (var frame = this._first; frame != null; frame = frame.Next) {
        var bitp = 0;
        frame.MainIndex = huffman.BytePosition;
        for (var gr = 0; gr < 2; ++gr) {
          for (var ch = 0; ch < this._nchannels; ++ch) {
            var g = frame.Granules[ch][gr];
            var sbl = g.BlockType == ShortBlock ? 1 : 0;
            var abs = m.Abs[ch];
            var sgn = m.Sgn2[ch];
            var lbt = m.Lbt[ch];
            flags = !joint || ch == 0 ? sbl : sbl | (frame.StereoMs << 1) | ((flags ^ sbl) << 2);
            if (gr == 0)
              for (var k = 0; k < 4; ++k) shared[ch, k] = (g.Share >> (3 - k)) & 1;
            huffman.ResetCounter();
            frame.BitReservoir = bitres;

            if (sbl == 0) {
              var scf = m.ScfC[ch];
              var prev = m.ScfLong[ch];
              var ctx = 0;
              for (int grp = 0, p = 0; grp < 4; ++grp) {
                var sl = SlengthTable[g.SLength, grp < 2 ? 0 : 1];
                if ((gr & shared[ch, grp]) != 0) {
                  Array.Copy(prev, p, scf, p, ScfWidth[grp]);
                  p = ScfBounds[grp];
                  ctx = scf[p - 1];
                } else if (sl == 0) {
                  Array.Clear(scf, p, ScfWidth[grp]);
                  p = ScfBounds[grp];
                  ctx = 0;
                } else {
                  var model = m.Scf[ch, sl - 1, shared[ch, grp] != 0 ? grp | 4 : grp];
                  for (; p < ScfBounds[grp]; ++p) {
                    model.Shift(ctx, prev[p]);
                    scf[p] = (byte)model.Decode(dec);
                    huffman.WriteBits(scf[p], sl);
                    ctx = scf[p];
                  }
                }
              }
              (m.ScfC[ch], m.ScfLong[ch]) = (m.ScfLong[ch], m.ScfC[ch]);
            } else {
              for (var i = 0; i < 3; ++i) {
                var scf = m.ScfC[ch];
                var prev = m.ScfShort[ch];
                var ctx = 0;
                for (int grp = 0, p = 0; grp < 2; ++grp) {
                  var sl = SlengthTable[g.SLength, grp];
                  if (sl == 0) {
                    Array.Clear(scf, p, ScfLhWidthShort[grp]);
                    p = ScfLhBoundsShort[grp];
                  } else {
                    var model = m.Scf[ch, sl - 1, grp | 8];
                    for (; p < ScfLhBoundsShort[grp]; ++p) {
                      model.Shift(ctx, prev[p]);
                      scf[p] = (byte)model.Decode(dec);
                      huffman.WriteBits(scf[p], sl);
                      ctx = scf[p];
                    }
                  }
                }
                (m.ScfC[ch], m.ScfShort[ch]) = (m.ScfShort[ch], m.ScfC[ch]);
              }
            }

            // sv bound
            m.Svb.ShiftContext(sbl == 0 ? ctxSvb[ch] : 144 + 1);
            var c = m.Svb.Decode(dec);
            if (c <= 144) {
              g.SvBound = c << 2;
              if (g.RegionBound[2] % 4 == 2) g.SvBound += 2;
              if (sbl == 0) ctxSvb[ch] = c;
            } else {
              c = m.Bvf.Decode(dec);
              g.SvBound = g.RegionBound[2] - (c << 1);
              g.RegionBound[2] = g.SvBound;
              for (var i = 1; i >= 0; --i) {
                if (g.SvBound < g.RegionBound[i]) g.RegionBound[i] = g.SvBound;
                else break;
              }
            }
            if (g.SvBound is < 0 or > 576) throw new InvalidDataException("packMP3: bad coefficient bound.");

            var pos = 0;
            this.CodeCoefficients(null, dec, m, g, ch, flags, sbl, ref pos);

            // Huffman re-encoding: big values
            int p2 = 0;
            for (var r = 0; r < 3; ++r) {
              if (g.RegionTable[r] == 0) {
                p2 = g.RegionBound[r];
                continue;
              }
              var code = PmpHuffman.BigValues(g.RegionTable[r]) ?? throw new InvalidDataException("packMP3: bad Huffman table.");
              var linbits = PmpHuffman.Linbits(g.RegionTable[r]);
              while (p2 < g.RegionBound[r]) {
                int v0 = abs[p2 + 1], v1 = abs[p2 + 2];
                if (v0 >= code.Width || v1 >= code.Width) throw new InvalidDataException("packMP3: value out of table range.");
                var index = v0 * code.Width + v1;
                huffman.WriteBits(code.Codes[index], code.Lengths[index]);
                for (var i = 0; i < 2; ++i, ++p2)
                  if (abs[p2 + 1] > 0) {
                    if (linbits > 0 && abs[p2 + 1] == 15) huffman.WriteBits(lbt[p2], linbits);
                    huffman.WriteBit(sgn[p2 + 1]);
                  }
              }
            }
            // small values
            var quad = g.SelectHtabB != 0 ? PmpHuffman.QuadB : PmpHuffman.QuadA;
            while (p2 < g.SvBound) {
              var bitsQ = 0;
              for (var i = 0; i < 4; ++i) bitsQ = (bitsQ << 1) | (abs[p2 + 1 + i] != 0 ? 1 : 0);
              huffman.WriteBits(quad.Codes[bitsQ], quad.Lengths[bitsQ]);
              for (var i = 0; i < 4; ++i, ++p2)
                if (abs[p2 + 1] != 0) huffman.WriteBit(sgn[p2 + 1]);
            }
            m.UpdateContexts(ch, sbl != 0, joint, p2);

            // stuffing bits
            int n;
            for (n = 0, c = StuffingStep; c == StuffingStep; n += c) {
              m.Nst.ShiftContext(ctxNst);
              c = m.Nst.Decode(dec);
              ctxNst = c;
              if (n > 4095) throw new InvalidDataException("packMP3: too many stuffing bits.");
            }
            for (; n > 0; --n) {
              m.Bst.ShiftContext(ctxBst);
              c = m.Bst.Decode(dec);
              huffman.WriteBit(c);
              ctxBst = ((ctxBst << 1) | c) & 0xF;
            }
            g.MainDataBit = huffman.Count;
            bitp += g.MainDataBit;
          }
        }
        frame.MainBits = bitp;
        frame.MainSize = (bitp + 7) / 8;
        if (frame.MainSize > 2048) throw new InvalidDataException("packMP3: frame main data too large.");

        if (this.iBitrate == -1) {
          m.Btr.ShiftContext(PackMp3Tables.BitratePrediction[this.iSamplerate * 2049 + frame.MainSize]);
          frame.Bits = m.Btr.Decode(dec);
        }
        frame.FrameSize = Mp3FrameSize(this.iSamplerate, frame.Bits) + (frame.Padding != 0 ? 1 : 0);

        if (this.iBitRes != 0) {
          bitres = frame.FrameSize - frame.FixedSize + frame.BitReservoir - frame.MainSize;
          if (frame != this._last) {
            var n = bitres < 511 ? 0 : bitres - 511;
            for (var c = AuxDataStep; c == AuxDataStep; n += c) {
              m.Aux.ShiftContext(ctxAux);
              c = m.Aux.Decode(dec);
              ctxAux = c;
              if (n > 1 << 16) throw new InvalidDataException("packMP3: too much ancillary data.");
            }
            frame.AuxSize = n;
          } else
            frame.AuxSize = bitres;
          bitres -= frame.AuxSize;
        } else {
          bitres = 0;
          frame.AuxSize = frame.FrameSize - frame.FixedSize + frame.BitReservoir - frame.MainSize;
        }

        var bits = (frame.MainSize + frame.AuxSize) * 8 - bitp;
        if (bits < 0 || bits > 2048 * 8) throw new InvalidDataException("packMP3: inconsistent frame data sizes.");
        if (m.Pap.Decode(dec) == 1) {
          var pred = this.PredictLameAncillary(bits, null)!;
          var at = 0;
          for (; bits >= 8; bits -= 8) huffman.WriteBits(pred[at++], 8);
          if (bits > 0) huffman.WriteBits(pred[at] >> (8 - bits), bits);
        } else {
          Array.Clear(padAndAux);
          var at = 0;
          var ctxPad = 0xFF;
          for (var i = 1; i <= bits; ++i) {
            m.Pad.ShiftContext(ctxPad);
            var c = m.Pad.Decode(dec);
            huffman.WriteBit(c);
            ctxPad = ((ctxPad << 1) | c) & 0xFF;
            padAndAux[at] = (byte)((padAndAux[at] << 1) | c);
            if (i % 8 == 0) padAndAux[++at] = 0;
          }
          if (this.PredictLameAncillary(bits, padAndAux) == null)
            throw new InvalidDataException("packMP3: ancillary prediction error.");
        }
      }
      this._mainData = huffman.Finish();
      this._mainDataSize = this._mainData.Length;
    }
  }
}
