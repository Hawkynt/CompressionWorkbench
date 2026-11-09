#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using FileFormat.Cdi;

namespace FileFormat.CloneCd;

/// <summary>
/// Writes a CloneCD image set of a single-session disc with one Mode 1 data track: the <c>.ccd</c>
/// control file, the raw <c>.img</c> (2352-byte sectors with sync, BCD address, EDC and ECC from
/// ECMA-130) and the <c>.sub</c> subchannel (96 bytes per sector: P, Q, then R–W, each 12 bytes).
/// Layout in <c>docs/CLONECD-ON-DISK.md</c>.
/// </summary>
public static class CloneCdWriter {
  public const int UserDataSize = 2048;
  public const int RawSectorSize = 2352;
  public const int SubchannelSize = 96;

  /// <summary>Frames before LBA 0: the 2-second pregap of track 1.</summary>
  private const int PregapFrames = 150;

  /// <summary>Sectors at the end of the last track whose P channel announces the lead-out.</summary>
  private const int LeadOutPauseSectors = 149;

  /// <summary>The three files of a CloneCD image.</summary>
  public sealed record ImageSet(byte[] Ccd, byte[] Img, byte[] Sub);

  /// <summary>Builds the image set of a data disc whose Mode 1 user data is <paramref name="iso"/>.</summary>
  public static ImageSet FromIso(ReadOnlySpan<byte> iso) {
    if (iso.Length == 0 || iso.Length % UserDataSize != 0)
      throw new InvalidDataException($"CloneCD: a Mode 1 track needs whole {UserDataSize}-byte sectors; got {iso.Length} bytes.");
    var sectors = iso.Length / UserDataSize;
    var img = new byte[(long)sectors * RawSectorSize];
    for (var lba = 0; lba < sectors; ++lba) {
      var raw = img.AsSpan(lba * RawSectorSize, RawSectorSize);
      raw[0] = 0x00;
      raw[1..11].Fill(0xFF);
      raw[11] = 0x00;
      WriteMsf(raw[12..], lba + PregapFrames);
      raw[15] = 1;
      iso.Slice(lba * UserDataSize, UserDataSize).CopyTo(raw[16..]);
      CdiCdSectorIntegrity.RegenerateMode1(raw);
    }
    var sub = Subchannel(sectors);
    return new ImageSet(Encoding.ASCII.GetBytes(Ccd(sectors)), img, sub);
  }

  /// <summary>The P and Q channels of track 1, a data track starting at LBA 0 and running for <paramref name="sectors"/> sectors; R–W stay zero.</summary>
  public static byte[] Subchannel(int sectors) {
    var sub = new byte[(long)sectors * SubchannelSize];
    for (var lba = 0; lba < sectors; ++lba) {
      // P marks pauses. As on pressed discs, it is still set on the first sector after the pregap and is set
      // again for the last 149 sectors before the lead-out.
      if (lba == 0 || lba >= sectors - LeadOutPauseSectors)
        sub.AsSpan(lba * SubchannelSize, 12).Fill(0xFF);

      // Q, mode 1 (position): control 4 (data track) / ADR 1, track 1, index 1, time in the track, zero, absolute time.
      var q = sub.AsSpan(lba * SubchannelSize + 12, 12);
      q[0] = 0x41;
      q[1] = 0x01;
      q[2] = 0x01;
      WriteMsf(q[3..], lba);
      q[6] = 0;
      WriteMsf(q[7..], lba + PregapFrames);
      var crc = (ushort)~Crc16(q[..10]);
      q[10] = (byte)(crc >> 8);
      q[11] = (byte)crc;
    }
    return sub;
  }

  /// <summary>The control file: a TOC of A0 (first track), A1 (last track), A2 (lead-out) and track 1.</summary>
  private static string Ccd(int sectors) {
    var sb = new StringBuilder();
    sb.Append("[CloneCD]\r\nVersion=3\r\n");
    sb.Append("[Disc]\r\nTocEntries=4\r\nSessions=1\r\nDataTracksScrambled=0\r\nCDTextLength=0\r\n");
    sb.Append("[Session 1]\r\nPreGapMode=1\r\nPreGapSubC=0\r\n");
    var leadOut = sectors + PregapFrames;
    (int Point, int Min, int Sec, int Frame)[] entries = [
      (0xA0, 1, 0, 0),
      (0xA1, 1, 0, 0),
      (0xA2, leadOut / 4500, leadOut / 75 % 60, leadOut % 75),
      (0x01, 0, PregapFrames / 75, 0),
    ];
    for (var i = 0; i < entries.Length; ++i) {
      var (point, min, sec, frame) = entries[i];
      sb.Append(CultureInfo.InvariantCulture, $"[Entry {i}]\r\nSession=1\r\nPoint=0x{point:x2}\r\nADR=0x01\r\nControl=0x04\r\nTrackNo=0\r\n");
      sb.Append("AMin=0\r\nASec=0\r\nAFrame=0\r\nALBA=-150\r\nZero=0\r\n");
      sb.Append(CultureInfo.InvariantCulture, $"PMin={min}\r\nPSec={sec}\r\nPFrame={frame}\r\nPLBA={(min * 60 + sec) * 75 + frame - PregapFrames}\r\n");
    }
    sb.Append("[TRACK 1]\r\nMODE=1\r\nINDEX 1=0\r\n");
    return sb.ToString();
  }

  private static void WriteMsf(Span<byte> msf, int frames) {
    static byte Bcd(int v) => (byte)((v / 10) << 4 | v % 10);
    msf[0] = Bcd(frames / 4500);
    msf[1] = Bcd(frames / 75 % 60);
    msf[2] = Bcd(frames % 75);
  }

  /// <summary>CRC-16/CCITT (polynomial 0x1021, initial value 0), as the Q channel uses it before inversion.</summary>
  public static ushort Crc16(ReadOnlySpan<byte> data) {
    var crc = 0;
    foreach (var b in data) {
      crc ^= b << 8;
      for (var i = 0; i < 8; ++i) crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
    }
    return (ushort)crc;
  }
}
