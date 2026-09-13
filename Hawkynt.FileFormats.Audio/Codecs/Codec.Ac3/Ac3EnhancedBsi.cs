#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// E-AC-3 (ATSC A/52 Annex E §E.1.2) bit-stream information parser. Where <see cref="Ac3FrameHeader"/>
/// only decodes the leading fixed fields needed for sync / framing, this walks the <em>full</em>
/// E-AC-3 BSI — channel map (dependent substreams), the mixing-metadata and informational-metadata
/// blocks (parse-skip), the converter-sync flag, the AC-3-compatibility frame-size code and the
/// additional-bit-stream-information block — so that the supplied <see cref="Ac3BitReader"/> is left
/// positioned at the first bit of the audio frame header. Field order and widths are ported from the
/// FFmpeg reference (<c>ac3_parser.c</c> / <c>eac3dec.c</c>) and the A/52 Annex E syntax.
/// </summary>
internal static class Ac3EnhancedBsi {

  /// <summary>The parsed-out BSI state needed downstream by the audio-frame-header decoder.</summary>
  internal readonly record struct Info(
    int StreamType, int SubstreamId, int Acmod, bool LfeOn, int Bsid, int NumBlocks,
    int FsCod, int FsCod2);

  /// <summary>
  /// Consumes the entire E-AC-3 BSI from <paramref name="r"/> (positioned at the 0x0B77 sync word),
  /// leaving it at the first audio-frame-header bit. Returns the decoded BSI state. Throws
  /// <see cref="InvalidDataException"/> on a malformed / over-read stream.
  /// </summary>
  internal static Info Parse(Ac3BitReader r) {
    r.SkipBits(16);                              // syncword (0x0B77)
    var strmtyp = (int)r.ReadBits(2);
    var substreamid = (int)r.ReadBits(3);
    r.SkipBits(11);                              // frmsiz
    var fscod = (int)r.ReadBits(2);
    int numblkscod, fscod2 = -1;
    if (fscod == 3) {
      fscod2 = (int)r.ReadBits(2);
      numblkscod = 3;
    } else {
      numblkscod = (int)r.ReadBits(2);
    }
    var acmod = (int)r.ReadBits(3);
    var lfeon = r.ReadFlag();
    var bsid = (int)r.ReadBits(5);

    var numBlocks = numblkscod switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 };

    // dialnorm + compr, once per program (twice for 1+1 dual mono).
    var sets = acmod == 0 ? 2 : 1;
    for (var i = 0; i < sets; ++i) {
      r.SkipBits(5);                             // dialnorm
      if (r.ReadFlag())                          // compre
        r.SkipBits(8);                           // compr
    }

    // chanmap (dependent substreams only).
    if (strmtyp == 1 && r.ReadFlag())            // chanmape
      r.SkipBits(16);                            // chanmap

    // Mixing metadata.
    if (r.ReadFlag())                            // mixmdate
      SkipMixingMetadata(r, strmtyp, acmod, lfeon, numBlocks);

    // Informational metadata.
    if (r.ReadFlag())                            // infomdate
      SkipInfoMetadata(r, acmod, fscod);

    // Converter synchronization flag (independent substreams of a converted stream, < 6 blocks).
    if (strmtyp == 0 && numBlocks != 6)
      r.SkipBits(1);                             // convsync

    // AC-3 compatibility frame-size code (strmtyp 2 only).
    if (strmtyp == 2) {
      var blkid = numBlocks == 6 || r.ReadFlag();
      if (blkid)
        r.SkipBits(6);                           // frmsizecod
    }

    // Additional bit-stream information.
    if (r.ReadFlag()) {                          // addbsie
      var addbsil = (int)r.ReadBits(6);
      r.SkipBits((addbsil + 1) * 8);             // addbsi
    }

    return new Info(strmtyp, substreamid, acmod, lfeon, bsid, numBlocks, fscod, fscod2);
  }

  // A/52 Annex E §E.1.2.2 mixing-metadata (mixmdate). Every field is parse-skipped; only the bit
  // position matters, so we advance exactly the right number of bits.
  private static void SkipMixingMetadata(Ac3BitReader r, int strmtyp, int acmod, bool lfeon, int numBlocks) {
    if (acmod > 2)
      r.SkipBits(2);                             // dmixmod
    if ((acmod & 0x1) != 0 && acmod > 2)
      r.SkipBits(3 + 3);                         // ltrtcmixlev + lorocmixlev
    if ((acmod & 0x4) != 0)
      r.SkipBits(3 + 3);                         // ltrtsurmixlev + lorosurmixlev
    if (lfeon && r.ReadFlag())                   // lfemixlevcode
      r.SkipBits(5);                             // lfemixlevcod

    if (strmtyp == 0) {
      if (r.ReadFlag()) r.SkipBits(6);           // pgmscle → pgmscl
      if (acmod == 0 && r.ReadFlag()) r.SkipBits(6);  // pgmscl2e → pgmscl2
      if (r.ReadFlag()) r.SkipBits(6);           // extpgmscle → extpgmscl
      var mixdef = (int)r.ReadBits(2);
      switch (mixdef) {
        case 1: r.SkipBits(5); break;            // premixcmpsel + drcsrc + premixcmpscl
        case 2: r.SkipBits(12); break;           // mixdata
        case 3: {
          var mixdeflen = (int)r.ReadBits(5);
          r.SkipBits((mixdeflen + 2) * 8);       // mixdata
          break;
        }
      }
      if (acmod < 2) {                           // mono / dual-mono panning
        if (r.ReadFlag()) r.SkipBits(8 + 6);     // paninfoe → panmean + paninfo
        if (acmod == 0 && r.ReadFlag())
          r.SkipBits(8 + 6);                     // paninfo2e → panmean2 + paninfo2
      }
    }

    // Per-frame / per-block mixing-config info (A/52 Annex E Table E1.20). With a single block the
    // frame carries one 5-bit blkmixcfginfo[0]; with multiple blocks each block is individually
    // gated by a blkmixcfginfoe[blk] flag before its 5-bit field.
    if (r.ReadFlag()) {                          // frmmixcfginfoe
      if (numBlocks == 1) {
        r.SkipBits(5);                           // blkmixcfginfo[0]
      } else {
        for (var blk = 0; blk < numBlocks; ++blk)
          if (r.ReadFlag())                      // blkmixcfginfoe[blk]
            r.SkipBits(5);                        // blkmixcfginfo[blk]
      }
    }
  }

  // A/52 Annex E §E.1.2.3 informational-metadata (infomdate). Parse-skip only.
  private static void SkipInfoMetadata(Ac3BitReader r, int acmod, int fscod) {
    r.SkipBits(3);                               // bsmod
    r.SkipBits(1);                               // copyrightb
    r.SkipBits(1);                               // origbs
    if (acmod == 2)
      r.SkipBits(2 + 2);                         // dsurmod + dheadphonmod
    if (acmod >= 6)
      r.SkipBits(2);                             // dsurexmod
    if (r.ReadFlag())                            // audprodie
      r.SkipBits(5 + 2 + 1);                     // mixlevel + roomtyp + adconvtyp
    if (acmod == 0 && r.ReadFlag())              // audprodi2e
      r.SkipBits(5 + 2 + 1);                     // mixlevel2 + roomtyp2 + adconvtyp2
    if (fscod < 3)
      r.SkipBits(1);                             // sourcefscod
  }
}
