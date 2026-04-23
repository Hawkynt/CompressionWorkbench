#pragma warning disable CS1591

namespace Codec.Mp3;

/// <summary>
/// MPEG Layer III decoder internals: side info, scalefactors, Huffman big_values /
/// count1, reorder (short blocks), antialias butterflies, IMDCT (long/short/start/stop),
/// MS + intensity stereo decorrelation. One instance per decode loop; granules hold
/// side-info for one audio-data slot (2 granules × channels for MPEG-1, 1 granule ×
/// channels for MPEG-2/2.5 LSF). Port of minimp3's L3_* functions.
/// </summary>
internal sealed class Mp3Layer3 {

  /// <summary>Per-granule side info (matches minimp3's <c>L3_gr_info_t</c>).</summary>
  public sealed class GrInfo {
    public byte[] Sfbtab = Array.Empty<byte>();
    public ushort Part23Length;
    public ushort BigValues;
    public ushort ScalefacCompress;
    public byte GlobalGain;
    public byte BlockType;
    public byte MixedBlockFlag;
    public byte NLongSfb;
    public byte NShortSfb;
    public readonly byte[] TableSelect = new byte[3];
    public readonly byte[] RegionCount = new byte[3];
    public readonly byte[] SubblockGain = new byte[3];
    public byte Preflag;
    public byte ScalefacScale;
    public byte Count1Table;
    public byte Scfsi;
  }

  public const int MaxBitReservoirBytes = 511;
  public const int MaxL3FramePayloadBytes = 2304;
  public const int ShortBlockType = 2;
  public const int StopBlockType = 3;
  public const int BitsDequantizerOut = -1;
  public const int MaxScf = 255 + BitsDequantizerOut * 4 - 210; // 40
  public const int MaxScfi = (MaxScf + 3) & ~3;                 // 40

  /// <summary>
  /// Reads side info for all granules of the current frame. Returns main_data_begin
  /// (bytes backreference into the bit reservoir) or -1 on invalid side info.
  /// </summary>
  public static int ReadSideInfo(Mp3BitReader bs, GrInfo[] gr, in Mp3FrameHeader hdr) {
    // Sample-rate index for sfbtabs: 0/1/2 (MPEG-1) then 3/4/5 (MPEG-2) then 6/7/8 (MPEG-2.5) — 8 entries,
    // the minimp3 lookup does "sr_idx -= (sr_idx != 0)" to skip the hole between MPEG-1 idx 0 and MPEG-2 idx 3.
    var srIdx = hdr.SampleRateIndex + (hdr.IsMpeg1 ? 0 : 3) + (hdr.IsMpeg25 ? 3 : 0);
    if (srIdx != 0) srIdx -= 1;
    if (srIdx > 7) srIdx = 7;

    var grCount = hdr.IsMono ? 1 : 2;
    int mainDataBegin;
    uint scfsi = 0;

    if (hdr.IsMpeg1) {
      grCount *= 2;
      mainDataBegin = (int)bs.GetBits(9);
      scfsi = bs.GetBits(7 + grCount);
    } else {
      mainDataBegin = (int)bs.GetBits(8 + grCount) >> grCount;
    }

    var part23Sum = 0;
    var grIdx = 0;
    do {
      if (hdr.IsMono) scfsi <<= 4;

      var g = gr[grIdx++];
      g.Part23Length = (ushort)bs.GetBits(12);
      part23Sum += g.Part23Length;
      g.BigValues = (ushort)bs.GetBits(9);
      if (g.BigValues > 288) return -1;

      g.GlobalGain = (byte)bs.GetBits(8);
      g.ScalefacCompress = (ushort)bs.GetBits(hdr.IsMpeg1 ? 4 : 9);
      g.Sfbtab = Mp3Scalefactor.ScfLong[srIdx];
      g.NLongSfb = 22;
      g.NShortSfb = 0;

      uint tables;
      if (bs.GetBits(1) != 0) {
        g.BlockType = (byte)bs.GetBits(2);
        if (g.BlockType == 0) return -1;
        g.MixedBlockFlag = (byte)bs.GetBits(1);
        g.RegionCount[0] = 7;
        g.RegionCount[1] = 255;
        if (g.BlockType == ShortBlockType) {
          scfsi &= 0x0F0F;
          if (g.MixedBlockFlag == 0) {
            g.RegionCount[0] = 8;
            g.Sfbtab = Mp3Scalefactor.ScfShort[srIdx];
            g.NLongSfb = 0;
            g.NShortSfb = 39;
          } else {
            g.Sfbtab = Mp3Scalefactor.ScfMixed[srIdx];
            g.NLongSfb = (byte)(hdr.IsMpeg1 ? 8 : 6);
            g.NShortSfb = 30;
          }
        }
        tables = bs.GetBits(10) << 5;
        g.SubblockGain[0] = (byte)bs.GetBits(3);
        g.SubblockGain[1] = (byte)bs.GetBits(3);
        g.SubblockGain[2] = (byte)bs.GetBits(3);
      } else {
        g.BlockType = 0;
        g.MixedBlockFlag = 0;
        tables = bs.GetBits(15);
        g.RegionCount[0] = (byte)bs.GetBits(4);
        g.RegionCount[1] = (byte)bs.GetBits(3);
        g.RegionCount[2] = 255;
      }
      g.TableSelect[0] = (byte)(tables >> 10);
      g.TableSelect[1] = (byte)((tables >> 5) & 31);
      g.TableSelect[2] = (byte)(tables & 31);
      g.Preflag = (byte)(hdr.IsMpeg1 ? (int)bs.GetBits(1) : (g.ScalefacCompress >= 500 ? 1 : 0));
      g.ScalefacScale = (byte)bs.GetBits(1);
      g.Count1Table = (byte)bs.GetBits(1);
      g.Scfsi = (byte)((scfsi >> 12) & 15);
      scfsi <<= 4;
    } while (--grCount > 0);

    if (part23Sum + bs.Pos > bs.Limit + mainDataBegin * 8) return -1;
    return mainDataBegin;
  }

  private static void ReadScalefactors(byte[] scf, int scfOff, byte[] istPos, int istOff,
                                       byte[] scfSize, byte[] scfCount, Mp3BitReader bitbuf, int scfsi) {
    for (var i = 0; i < 4 && scfCount[i] != 0; i++, scfsi *= 2) {
      int cnt = scfCount[i];
      if ((scfsi & 8) != 0) {
        Array.Copy(istPos, istOff, scf, scfOff, cnt);
      } else {
        int bits = scfSize[i];
        if (bits == 0) {
          Array.Clear(scf, scfOff, cnt);
          Array.Clear(istPos, istOff, cnt);
        } else {
          var maxScf = scfsi < 0 ? (1 << bits) - 1 : -1;
          for (var k = 0; k < cnt; k++) {
            var s = (int)bitbuf.GetBits(bits);
            istPos[istOff + k] = (byte)(s == maxScf ? 0xFF /*-1 as byte*/ : s);
            scf[scfOff + k] = (byte)s;
          }
        }
      }
      istOff += cnt;
      scfOff += cnt;
    }
    scf[scfOff + 0] = scf[scfOff + 1] = scf[scfOff + 2] = 0;
  }

  /// <summary>Decodes per-band dequantizer-ready scalefactors for one granule/channel into <paramref name="scf"/>.</summary>
  public static void DecodeScalefactors(in Mp3FrameHeader hdr, byte[] istPos, Mp3BitReader bs,
                                         GrInfo gr, float[] scf, int ch) {
    var partIndex = (gr.NShortSfb != 0 ? 1 : 0) + (gr.NLongSfb == 0 ? 1 : 0);
    var scfPartition = Mp3Scalefactor.ScfPartitions[partIndex];
    var scfSize = new byte[4];
    var iscf = new byte[40];
    var scfShift = gr.ScalefacScale + 1;
    int scfsi = gr.Scfsi;
    int partOffset = 0;

    if (hdr.IsMpeg1) {
      var part = Mp3Scalefactor.ScfcDecode[gr.ScalefacCompress];
      scfSize[0] = scfSize[1] = (byte)(part >> 2);
      scfSize[2] = scfSize[3] = (byte)(part & 3);
    } else {
      var ist = (hdr.IsIntensityStereo && ch != 0) ? 1 : 0;
      var sfc = gr.ScalefacCompress >> ist;
      var k = ist * 3 * 4;
      for (; sfc >= 0; k += 4) {
        var modprod = 1;
        for (var i = 3; i >= 0; i--) {
          scfSize[i] = (byte)(sfc / modprod % Mp3Scalefactor.Mod[k + i]);
          modprod *= Mp3Scalefactor.Mod[k + i];
        }
        sfc -= modprod;
      }
      partOffset = k - 4;   // minimp3's "scf_partition += k" after the loop exits with k over-incremented by 4
      scfsi = -16;
    }

    // scfPartition is indexed from partOffset onwards: build a virtual span by shifting indices manually.
    // We pass a segment via a helper that reads partitions[partOffset..partOffset+4].
    var scfCount = new byte[4];
    for (var i = 0; i < 4; i++) scfCount[i] = scfPartition[partOffset + i];

    ReadScalefactors(iscf, 0, istPos, 0, scfSize, scfCount, bs, scfsi);

    if (gr.NShortSfb != 0) {
      var sh = 3 - scfShift;
      for (var i = 0; i < gr.NShortSfb; i += 3) {
        iscf[gr.NLongSfb + i + 0] = (byte)(iscf[gr.NLongSfb + i + 0] + (gr.SubblockGain[0] << sh));
        iscf[gr.NLongSfb + i + 1] = (byte)(iscf[gr.NLongSfb + i + 1] + (gr.SubblockGain[1] << sh));
        iscf[gr.NLongSfb + i + 2] = (byte)(iscf[gr.NLongSfb + i + 2] + (gr.SubblockGain[2] << sh));
      }
    } else if (gr.Preflag != 0) {
      for (var i = 0; i < 10; i++) iscf[11 + i] = (byte)(iscf[11 + i] + Mp3Scalefactor.Preamp[i]);
    }

    var gainExp = gr.GlobalGain + BitsDequantizerOut * 4 - 210 - (hdr.IsMsStereo ? 2 : 0);
    var gain = Mp3Scalefactor.LdexpQ2(1 << (MaxScfi / 4), MaxScfi - gainExp);
    int totalBands = gr.NLongSfb + gr.NShortSfb;
    for (var i = 0; i < totalBands; i++)
      scf[i] = Mp3Scalefactor.LdexpQ2(gain, iscf[i] << scfShift);
  }

  // -- Huffman big_values/count1 decode -------------------------------------

  private static readonly float[] _Pow43 = {
    // negatives (offset +16 gives 0..15)
    0f, -1f, -2.519842f, -4.326749f, -6.349604f, -8.549880f, -10.902724f, -13.390518f,
    -16.000000f, -18.720754f, -21.544347f, -24.463781f, -27.473142f, -30.567351f, -33.741992f, -36.993181f,
    // positives (values 0..128)
    0f, 1f, 2.519842f, 4.326749f, 6.349604f, 8.549880f, 10.902724f, 13.390518f,
    16.000000f, 18.720754f, 21.544347f, 24.463781f, 27.473142f, 30.567351f, 33.741992f, 36.993181f,
    40.317474f, 43.711787f, 47.173345f, 50.699631f, 54.288352f, 57.937408f, 61.644865f, 65.408941f,
    69.227979f, 73.100443f, 77.024898f, 81.000000f, 85.024491f, 89.097188f, 93.216975f, 97.382800f,
    101.593667f, 105.848633f, 110.146801f, 114.487321f, 118.869381f, 123.292209f, 127.755065f, 132.257246f,
    136.798076f, 141.376907f, 145.993119f, 150.646117f, 155.335327f, 160.060199f, 164.820202f, 169.614826f,
    174.443577f, 179.305980f, 184.201575f, 189.129918f, 194.090580f, 199.083145f, 204.107210f, 209.162385f,
    214.248292f, 219.364564f, 224.510845f, 229.686789f, 234.892058f, 240.126328f, 245.389280f, 250.680604f,
    256.000000f, 261.347174f, 266.721841f, 272.123723f, 277.552547f, 283.008049f, 288.489971f, 293.998060f,
    299.532071f, 305.091761f, 310.676898f, 316.287249f, 321.922592f, 327.582707f, 333.267377f, 338.976394f,
    344.709550f, 350.466646f, 356.247482f, 362.051866f, 367.879608f, 373.730522f, 379.604427f, 385.501143f,
    391.420496f, 397.362314f, 403.326427f, 409.312672f, 415.320884f, 421.350905f, 427.402579f, 433.475750f,
    439.570269f, 445.685987f, 451.822757f, 457.980436f, 464.158883f, 470.357960f, 476.577530f, 482.817459f,
    489.077615f, 495.357868f, 501.658090f, 507.978156f, 514.317941f, 520.677324f, 527.056184f, 533.454404f,
    539.871867f, 546.308458f, 552.764065f, 559.238575f, 565.731879f, 572.243870f, 578.774440f, 585.323483f,
    591.890898f, 598.476581f, 605.080431f, 611.702349f, 618.342238f, 625.000000f, 631.675540f, 638.368763f, 645.079578f
  };

  private static float Pow43(int x) {
    if (x < 129) return _Pow43[16 + x];
    var mult = 256;
    if (x < 1024) { mult = 16; x <<= 3; }
    var sign = 2 * x & 64;
    var frac = (float)((x & 63) - sign) / ((x & ~63) + sign);
    return _Pow43[16 + ((x + sign) >> 6)] * (1f + frac * (4f / 3 + frac * (2f / 9))) * mult;
  }

  /// <summary>Huffman decoder for one granule/channel's 576 samples — big_values + count1 + padding zeros.</summary>
  public static void Huffman(float[] dst, Mp3BitReader bs, GrInfo gr, float[] scf, int layer3GrLimit) {
    var tabs = Mp3HuffmanTables.Tabs;
    var tabIndex = Mp3HuffmanTables.TabIndex;
    var linbits = Mp3HuffmanTables.LinBits;

    var one = 0.0f;
    var ireg = 0;
    int bigValCnt = gr.BigValues;
    var sfb = gr.Sfbtab;
    var sfbPos = 0;
    var bsNextPtr = bs.Pos / 8;
    var bsCache = ((uint)bs.Buf[bsNextPtr] << 24) | ((uint)bs.Buf[bsNextPtr + 1] << 16)
                | ((uint)bs.Buf[bsNextPtr + 2] << 8) | bs.Buf[bsNextPtr + 3];
    bsCache <<= bs.Pos & 7;
    bsNextPtr += 4;
    int bsSh = (bs.Pos & 7) - 8;
    var dstPos = 0;
    var scfPos = 0;

    while (bigValCnt > 0) {
      int tabNum = gr.TableSelect[ireg];
      int sfbCnt = gr.RegionCount[ireg++];
      var codebookOff = tabIndex[tabNum];
      var lb = linbits[tabNum];
      if (lb != 0) {
        int np;
        do {
          np = sfb[sfbPos++] / 2;
          var pairsToDecode = Math.Min(bigValCnt, np);
          one = scf[scfPos++];
          do {
            var w = 5;
            int leaf = tabs[codebookOff + (int)(bsCache >> (32 - w))];
            while (leaf < 0) {
              bsCache <<= w; bsSh += w;
              w = leaf & 7;
              leaf = tabs[codebookOff + (int)(bsCache >> (32 - w)) - (leaf >> 3)];
            }
            bsCache <<= (leaf >> 8); bsSh += (leaf >> 8);

            for (var j = 0; j < 2; j++, dstPos++, leaf >>= 4) {
              var lsb = leaf & 0x0F;
              if (lsb == 15) {
                lsb += (int)(bsCache >> (32 - lb));
                bsCache <<= lb; bsSh += lb;
                while (bsSh >= 0) { bsCache |= (uint)bs.Buf[bsNextPtr++] << bsSh; bsSh -= 8; }
                dst[dstPos] = one * Pow43(lsb) * ((int)bsCache < 0 ? -1f : 1f);
              } else {
                dst[dstPos] = _Pow43[16 + lsb - 16 * (int)(bsCache >> 31)] * one;
              }
              if (lsb != 0) { bsCache <<= 1; bsSh += 1; }
            }
            while (bsSh >= 0) { bsCache |= (uint)bs.Buf[bsNextPtr++] << bsSh; bsSh -= 8; }
          } while (--pairsToDecode > 0);
        } while ((bigValCnt -= np) > 0 && --sfbCnt >= 0);
      } else {
        int np;
        do {
          np = sfb[sfbPos++] / 2;
          var pairsToDecode = Math.Min(bigValCnt, np);
          one = scf[scfPos++];
          do {
            var w = 5;
            int leaf = tabs[codebookOff + (int)(bsCache >> (32 - w))];
            while (leaf < 0) {
              bsCache <<= w; bsSh += w;
              w = leaf & 7;
              leaf = tabs[codebookOff + (int)(bsCache >> (32 - w)) - (leaf >> 3)];
            }
            bsCache <<= (leaf >> 8); bsSh += (leaf >> 8);

            for (var j = 0; j < 2; j++, dstPos++, leaf >>= 4) {
              var lsb = leaf & 0x0F;
              dst[dstPos] = _Pow43[16 + lsb - 16 * (int)(bsCache >> 31)] * one;
              if (lsb != 0) { bsCache <<= 1; bsSh += 1; }
            }
            while (bsSh >= 0) { bsCache |= (uint)bs.Buf[bsNextPtr++] << bsSh; bsSh -= 8; }
          } while (--pairsToDecode > 0);
        } while ((bigValCnt -= np) > 0 && --sfbCnt >= 0);
      }
    }

    // count1 region
    var np2 = 1 - bigValCnt;
    for (;; dstPos += 4) {
      var codebookCount1 = gr.Count1Table != 0 ? Mp3HuffmanTables.Tab33 : Mp3HuffmanTables.Tab32;
      int leaf = codebookCount1[(int)(bsCache >> (32 - 4))];
      if ((leaf & 8) == 0)
        leaf = codebookCount1[(leaf >> 3) + (int)(bsCache << 4 >> (32 - (leaf & 3)))];
      var consume = leaf & 7;
      bsCache <<= consume; bsSh += consume;
      var bspos = (bsNextPtr - 4) * 8 - 24 + bsSh;
      if (bspos > layer3GrLimit) break;

      // Reload scf
      if (--np2 == 0) {
        np2 = sfb[sfbPos++] / 2;
        if (np2 == 0) break;
        one = scf[scfPos++];
      }
      if ((leaf & (128 >> 0)) != 0) { dst[dstPos + 0] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
      if ((leaf & (128 >> 1)) != 0) { dst[dstPos + 1] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
      if (--np2 == 0) {
        np2 = sfb[sfbPos++] / 2;
        if (np2 == 0) break;
        one = scf[scfPos++];
      }
      if ((leaf & (128 >> 2)) != 0) { dst[dstPos + 2] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
      if ((leaf & (128 >> 3)) != 0) { dst[dstPos + 3] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
      while (bsSh >= 0) { bsCache |= (uint)bs.Buf[bsNextPtr++] << bsSh; bsSh -= 8; }
    }

    bs.Pos = layer3GrLimit;
  }

  // -- Stereo processing -----------------------------------------------------

  public static void MidSideStereo(float[] grbuf, int leftOff, int n) {
    var rightOff = leftOff + 576;
    for (var i = 0; i < n; i++) {
      var a = grbuf[leftOff + i];
      var b = grbuf[rightOff + i];
      grbuf[leftOff + i] = a + b;
      grbuf[rightOff + i] = a - b;
    }
  }

  private static void IntensityStereoBand(float[] grbuf, int leftOff, int n, float kl, float kr) {
    for (var i = 0; i < n; i++) {
      grbuf[leftOff + i + 576] = grbuf[leftOff + i] * kr;
      grbuf[leftOff + i] = grbuf[leftOff + i] * kl;
    }
  }

  private static void StereoTopBand(float[] grbuf, int rightOff, byte[] sfb, int nbands, int[] maxBand) {
    maxBand[0] = maxBand[1] = maxBand[2] = -1;
    for (var i = 0; i < nbands; i++) {
      for (var k = 0; k < sfb[i]; k += 2) {
        if (grbuf[rightOff + k] != 0 || grbuf[rightOff + k + 1] != 0) {
          maxBand[i % 3] = i;
          break;
        }
      }
      rightOff += sfb[i];
    }
  }

  private static void StereoProcess(float[] grbuf, int leftOff, byte[] istPos, byte[] sfb, in Mp3FrameHeader hdr,
                                     int[] maxBand, int mpeg2Sh) {
    var maxPos = hdr.IsMpeg1 ? 7u : 64u;
    for (uint i = 0; sfb[i] != 0; i++) {
      var ipos = (uint)(sbyte)istPos[i]; // interpret -1 sentinel as large unsigned
      if ((int)i > maxBand[i % 3] && ipos < maxPos) {
        var s = hdr.IsMsStereo ? 1.41421356f : 1f;
        float kl, kr;
        if (hdr.IsMpeg1) {
          kl = Mp3Scalefactor.IntensityPanMpeg1[2 * ipos];
          kr = Mp3Scalefactor.IntensityPanMpeg1[2 * ipos + 1];
        } else {
          kl = 1f;
          kr = Mp3Scalefactor.LdexpQ2(1f, (int)((ipos + 1) >> 1 << mpeg2Sh));
          if ((ipos & 1) != 0) { kl = kr; kr = 1f; }
        }
        IntensityStereoBand(grbuf, leftOff, sfb[i], kl * s, kr * s);
      } else if (hdr.IsMsStereo) {
        MidSideStereo(grbuf, leftOff, sfb[i]);
      }
      leftOff += sfb[i];
    }
  }

  public static void IntensityStereo(float[] grbuf, int leftOff, byte[] istPos, GrInfo gr, GrInfo grNext, in Mp3FrameHeader hdr) {
    var maxBand = new int[3];
    int nSfb = gr.NLongSfb + gr.NShortSfb;
    var maxBlocks = gr.NShortSfb != 0 ? 3 : 1;

    StereoTopBand(grbuf, leftOff + 576, gr.Sfbtab, nSfb, maxBand);
    if (gr.NLongSfb != 0) {
      var max = Math.Max(Math.Max(maxBand[0], maxBand[1]), maxBand[2]);
      maxBand[0] = maxBand[1] = maxBand[2] = max;
    }
    for (var i = 0; i < maxBlocks; i++) {
      var defaultPos = hdr.IsMpeg1 ? 3 : 0;
      var itop = nSfb - maxBlocks + i;
      var prev = itop - maxBlocks;
      istPos[itop] = (byte)(maxBand[i] >= prev ? defaultPos : istPos[prev]);
    }
    StereoProcess(grbuf, leftOff, istPos, gr.Sfbtab, hdr, maxBand, grNext.ScalefacCompress & 1);
  }

  // -- Reorder, antialias, IMDCT --------------------------------------------

  /// <summary>Reorders short-block coefficients into <paramref name="grbuf"/> from <paramref name="startOff"/>.</summary>
  public static void ReorderShort(float[] grbuf, int startOff, float[] scratch, byte[] sfb, int sfbOff) {
    var dst = 0;
    var src = startOff;
    for (var i = sfbOff; sfb[i] != 0; i += 3) {
      int len = sfb[i];
      for (var k = 0; k < len; k++) {
        scratch[dst++] = grbuf[src + k + 0 * len];
        scratch[dst++] = grbuf[src + k + 1 * len];
        scratch[dst++] = grbuf[src + k + 2 * len];
      }
      src += 2 * len;
    }
    Array.Copy(scratch, 0, grbuf, startOff, dst);
  }

  private static readonly float[][] _Aa = {
    new[] { 0.85749293f, 0.88174200f, 0.94962865f, 0.98331459f, 0.99551782f, 0.99916056f, 0.99989920f, 0.99999316f },
    new[] { 0.51449576f, 0.47173197f, 0.31337745f, 0.18191320f, 0.09457419f, 0.04096558f, 0.01419856f, 0.00369997f }
  };

  public static void Antialias(float[] grbuf, int off, int nbands) {
    for (; nbands > 0; nbands--, off += 18) {
      for (var i = 0; i < 8; i++) {
        var u = grbuf[off + 18 + i];
        var d = grbuf[off + 17 - i];
        grbuf[off + 18 + i] = u * _Aa[0][i] - d * _Aa[1][i];
        grbuf[off + 17 - i] = u * _Aa[1][i] + d * _Aa[0][i];
      }
    }
  }

  // IMDCT — dct3_9 + imdct36 for long blocks, imdct12 x3 for short blocks, then windowing/overlap-add.
  private static void Dct3_9(float[] y, int off) {
    float s0 = y[off + 0], s2 = y[off + 2], s4 = y[off + 4], s6 = y[off + 6], s8 = y[off + 8];
    var t0 = s0 + s6 * 0.5f;
    s0 -= s6;
    var t4 = (s4 + s2) * 0.93969262f;
    var t2 = (s8 + s2) * 0.76604444f;
    s6 = (s4 - s8) * 0.17364818f;
    s4 += s8 - s2;

    var s2b = s0 - s4 * 0.5f;
    y[off + 4] = s4 + s0;
    s8 = t0 - t2 + s6;
    s0 = t0 - t4 + t2;
    s4 = t0 + t4 - s6;

    float s1 = y[off + 1], s3 = y[off + 3], s5 = y[off + 5], s7 = y[off + 7];
    s3 *= 0.86602540f;
    t0 = (s5 + s1) * 0.98480775f;
    t4 = (s5 - s7) * 0.34202014f;
    t2 = (s1 + s7) * 0.64278761f;
    s1 = (s1 - s5 - s7) * 0.86602540f;

    s5 = t0 - s3 - t2;
    s7 = t4 - s3 - t0;
    s3 = t4 + s3 - t2;

    y[off + 0] = s4 - s7;
    y[off + 1] = s2b + s1;
    y[off + 2] = s0 - s3;
    y[off + 3] = s8 + s5;
    y[off + 5] = s8 - s5;
    y[off + 6] = s0 + s3;
    y[off + 7] = s2b - s1;
    y[off + 8] = s4 + s7;
  }

  private static readonly float[] _Twid9 = {
    0.73727734f, 0.79335334f, 0.84339145f, 0.88701083f, 0.92387953f, 0.95371695f, 0.97629601f, 0.99144486f, 0.99904822f,
    0.67559021f, 0.60876143f, 0.53729961f, 0.46174861f, 0.38268343f, 0.30070580f, 0.21643961f, 0.13052619f, 0.04361938f
  };

  private static readonly float[] _Twid3 = {
    0.79335334f, 0.92387953f, 0.99144486f, 0.60876143f, 0.38268343f, 0.13052619f
  };

  private static readonly float[][] _MdctWindow = {
    new[] { 0.99904822f, 0.99144486f, 0.97629601f, 0.95371695f, 0.92387953f, 0.88701083f, 0.84339145f, 0.79335334f, 0.73727734f,
            0.04361938f, 0.13052619f, 0.21643961f, 0.30070580f, 0.38268343f, 0.46174861f, 0.53729961f, 0.60876143f, 0.67559021f },
    new[] { 1f, 1f, 1f, 1f, 1f, 1f, 0.99144486f, 0.92387953f, 0.79335334f,
            0f, 0f, 0f, 0f, 0f, 0f, 0.13052619f, 0.38268343f, 0.60876143f }
  };

  private static void Imdct36(float[] grbuf, int gOff, float[] overlap, int oOff, float[] window, int nbands) {
    for (var j = 0; j < nbands; j++, gOff += 18, oOff += 9) {
      var co = new float[9];
      var si = new float[9];
      co[0] = -grbuf[gOff + 0];
      si[0] = grbuf[gOff + 17];
      for (var i = 0; i < 4; i++) {
        si[8 - 2 * i] = grbuf[gOff + 4 * i + 1] - grbuf[gOff + 4 * i + 2];
        co[1 + 2 * i] = grbuf[gOff + 4 * i + 1] + grbuf[gOff + 4 * i + 2];
        si[7 - 2 * i] = grbuf[gOff + 4 * i + 4] - grbuf[gOff + 4 * i + 3];
        co[2 + 2 * i] = -(grbuf[gOff + 4 * i + 3] + grbuf[gOff + 4 * i + 4]);
      }
      Dct3_9(co, 0);
      Dct3_9(si, 0);

      si[1] = -si[1]; si[3] = -si[3]; si[5] = -si[5]; si[7] = -si[7];

      for (var i = 0; i < 9; i++) {
        var ovl = overlap[oOff + i];
        var sum = co[i] * _Twid9[9 + i] + si[i] * _Twid9[0 + i];
        overlap[oOff + i] = co[i] * _Twid9[0 + i] - si[i] * _Twid9[9 + i];
        grbuf[gOff + i] = ovl * window[0 + i] - sum * window[9 + i];
        grbuf[gOff + 17 - i] = ovl * window[9 + i] + sum * window[0 + i];
      }
    }
  }

  private static void Idct3(float x0, float x1, float x2, float[] dst, int off) {
    var m1 = x1 * 0.86602540f;
    var a1 = x0 - x2 * 0.5f;
    dst[off + 1] = x0 + x2;
    dst[off + 0] = a1 + m1;
    dst[off + 2] = a1 - m1;
  }

  private static void Imdct12(float[] x, int xOff, float[] dst, int dOff, float[] overlap, int oOff) {
    var co = new float[3];
    var si = new float[3];
    Idct3(-x[xOff + 0], x[xOff + 6] + x[xOff + 3], x[xOff + 12] + x[xOff + 9], co, 0);
    Idct3(x[xOff + 15], x[xOff + 12] - x[xOff + 9], x[xOff + 6] - x[xOff + 3], si, 0);
    si[1] = -si[1];
    for (var i = 0; i < 3; i++) {
      var ovl = overlap[oOff + i];
      var sum = co[i] * _Twid3[3 + i] + si[i] * _Twid3[0 + i];
      overlap[oOff + i] = co[i] * _Twid3[0 + i] - si[i] * _Twid3[3 + i];
      dst[dOff + i] = ovl * _Twid3[2 - i] - sum * _Twid3[5 - i];
      dst[dOff + 5 - i] = ovl * _Twid3[5 - i] + sum * _Twid3[2 - i];
    }
  }

  private static void ImdctShort(float[] grbuf, int gOff, float[] overlap, int oOff, int nbands) {
    var tmp = new float[18];
    for (; nbands > 0; nbands--, oOff += 9, gOff += 18) {
      Array.Copy(grbuf, gOff, tmp, 0, 18);
      Array.Copy(overlap, oOff, grbuf, gOff, 6);
      Imdct12(tmp, 0, grbuf, gOff + 6, overlap, oOff + 6);
      Imdct12(tmp, 1, grbuf, gOff + 12, overlap, oOff + 6);
      Imdct12(tmp, 2, overlap, oOff, overlap, oOff + 6);
    }
  }

  public static void ChangeSign(float[] grbuf, int off) {
    for (var b = 0; b < 32; b += 2, off += 36) {
      for (var i = 1; i < 18; i += 2)
        grbuf[off + 18 + i] = -grbuf[off + 18 + i];
    }
  }

  public static void ImdctGr(float[] grbuf, int gOff, float[] overlap, int oOff, int blockType, int nLongBands) {
    if (nLongBands != 0) {
      Imdct36(grbuf, gOff, overlap, oOff, _MdctWindow[0], nLongBands);
      gOff += 18 * nLongBands;
      oOff += 9 * nLongBands;
    }
    if (blockType == ShortBlockType)
      ImdctShort(grbuf, gOff, overlap, oOff, 32 - nLongBands);
    else
      Imdct36(grbuf, gOff, overlap, oOff, _MdctWindow[blockType == StopBlockType ? 1 : 0], 32 - nLongBands);
  }
}
