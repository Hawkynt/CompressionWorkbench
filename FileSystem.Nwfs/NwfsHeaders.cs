#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nwfs;

/// <summary>
/// Best-effort detector for the Novell NetWare 386 (NWFS386) filesystem —
/// a.k.a. the Traditional NetWare File System. NWFS was the *only* filesystem
/// in NetWare 2.x/3.x/4.x and the default for the SYS: volume in NetWare 5/6.
/// Its on-disk format was never publicly documented by Novell; everything we
/// know comes from reverse-engineering. Read-only — we cannot validate
/// contents without an authoritative spec.
///
/// Detected signatures (per the zhmu/nwfs reverse-engineering project):
/// <list type="bullet">
///   <item><description><c>"HOTFIX00"</c> — 8 ASCII bytes at byte offset <c>0x4000</c> (16384) within the partition (sector 32 with 512 B sectors). Marks the start of the Hot Fix area.</description></item>
///   <item><description><c>"MIRROR00"</c> — 8 ASCII bytes adjacent to HOTFIX. Marks the Mirror header.</description></item>
///   <item><description><c>"NetWare Volumes"</c> — 16 ASCII bytes (NUL-padded) in the volume area following the redirection sectors. Marks the Volume area.</description></item>
/// </list>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/zhmu/nwfs</c> — primary RE source for NWFS286/NWFS386</description></item>
///   <item><description><c>https://github.com/jeffmerkey/netware-file-system</c> — secondary reference (former Novell engineers' release)</description></item>
///   <item><description>NWFS386 partitions: HOTFIX_OFFSET = 0x4000, sector 32 (assumes 512 B sectors)</description></item>
/// </list>
/// </summary>
public sealed class NwfsHeaders {
  /// <summary>HOTFIX header signature — 8 ASCII bytes at offset 0x4000 within the partition.</summary>
  public static readonly byte[] HotfixMagic = "HOTFIX00"u8.ToArray();

  /// <summary>MIRROR header signature — 8 ASCII bytes at the Mirror sector.</summary>
  public static readonly byte[] MirrorMagic = "MIRROR00"u8.ToArray();

  /// <summary>Volume area signature — 16 ASCII bytes (NUL-padded) at the start of the Volume area.</summary>
  public static readonly byte[] VolumesMagic = "NetWare Volumes\0"u8.ToArray();

  /// <summary>HOTFIX_OFFSET — Hot Fix area lives at this byte offset from the start of the partition.</summary>
  public const long HotfixOffset = 0x4000; // 16384

  /// <summary>Sector 32 with 512 B sectors equals HotfixOffset.</summary>
  public const int HotfixSector = 32;

  /// <summary>Volume area span (4 mirrored copies of 16 KB).</summary>
  public const int VolumeAreaSize = 4 * 16384;

  /// <summary>Bytes captured for <c>volume_header.bin</c> — first 4 KB containing HOTFIX/MIRROR.</summary>
  public const int HeaderCaptureSize = 4096;

  /// <summary>True iff the HOTFIX00 signature was found at <see cref="HotfixOffset"/>.</summary>
  public bool HotfixFound { get; private init; }

  /// <summary>True iff the MIRROR00 signature was found anywhere in the first 64 KB.</summary>
  public bool MirrorFound { get; private init; }

  /// <summary>True iff the "NetWare Volumes" signature was found anywhere in the first 64 KB.</summary>
  public bool VolumesFound { get; private init; }

  /// <summary>Byte offset where HOTFIX was located (or -1).</summary>
  public long HotfixFoundOffset { get; private init; } = -1;

  /// <summary>Byte offset where MIRROR was located (or -1).</summary>
  public long MirrorFoundOffset { get; private init; } = -1;

  /// <summary>Byte offset where "NetWare Volumes" was located (or -1).</summary>
  public long VolumesFoundOffset { get; private init; } = -1;

  /// <summary>Raw 4 KB capture starting at HotfixOffset (when present), else empty.</summary>
  public byte[] HeaderRaw { get; private init; } = [];

  /// <summary>True iff at least one of the three RE'd signatures was located.</summary>
  public bool AnyValid => this.HotfixFound || this.MirrorFound || this.VolumesFound;

  /// <summary>
  /// Best-effort scan. Looks for HOTFIX00 at offset 0x4000 first (canonical),
  /// then scans the first 64 KB for MIRROR00 and "NetWare Volumes". Never
  /// throws.
  /// </summary>
  public static NwfsHeaders TryParse(ReadOnlySpan<byte> image) {
    var hotfixFound = false;
    var hotfixOff = -1L;
    if (image.Length >= HotfixOffset + HotfixMagic.Length) {
      if (image.Slice((int)HotfixOffset, HotfixMagic.Length).SequenceEqual(HotfixMagic)) {
        hotfixFound = true;
        hotfixOff = HotfixOffset;
      }
    }

    // Free-form scan for MIRROR00 + "NetWare Volumes" in the first 64 KB
    // (sector-aligned to keep the work small). NWFS sectors are 512 B and the
    // signatures always live at sector boundaries on real images.
    var scanLimit = Math.Min(image.Length, 64 * 1024);
    var mirrorOff = -1L;
    var volumesOff = -1L;

    for (var i = 0; i + 16 <= scanLimit; i += 512) {
      var s = image.Slice(i, Math.Min(16, scanLimit - i));
      if (mirrorOff < 0 && s.Length >= MirrorMagic.Length &&
          s[..MirrorMagic.Length].SequenceEqual(MirrorMagic))
        mirrorOff = i;
      if (volumesOff < 0 && s.Length >= VolumesMagic.Length &&
          s[..VolumesMagic.Length].SequenceEqual(VolumesMagic))
        volumesOff = i;
      if (mirrorOff >= 0 && volumesOff >= 0) break;
    }

    // Capture 4 KB starting at HOTFIX (or the first found signature) for the
    // user to inspect raw. If none matched, no capture.
    byte[] raw = [];
    if (hotfixFound) {
      raw = SafeCapture(image, hotfixOff, HeaderCaptureSize);
    } else if (mirrorOff >= 0) {
      raw = SafeCapture(image, mirrorOff, HeaderCaptureSize);
    } else if (volumesOff >= 0) {
      raw = SafeCapture(image, volumesOff, HeaderCaptureSize);
    }

    return new NwfsHeaders {
      HotfixFound = hotfixFound,
      HotfixFoundOffset = hotfixOff,
      MirrorFound = mirrorOff >= 0,
      MirrorFoundOffset = mirrorOff,
      VolumesFound = volumesOff >= 0,
      VolumesFoundOffset = volumesOff,
      HeaderRaw = raw,
    };
  }

  private static byte[] SafeCapture(ReadOnlySpan<byte> image, long offset, int requested) {
    if (offset < 0 || offset >= image.Length) return [];
    var avail = (int)Math.Min(requested, image.Length - offset);
    if (avail <= 0) return [];
    var buf = new byte[requested];
    image.Slice((int)offset, avail).CopyTo(buf);
    return buf;
  }

  // Helpers retained for symmetry with other FileSystem.* parsers — currently
  // unused because the NWFS volume header layout (after the magic) is not
  // documented. Future work could parse e.g. the partition-size field at
  // offset 20/24 of the hotfix header per the zhmu doc.
  internal static long ReadI64(ReadOnlySpan<byte> image, int off) =>
    off + 8 <= image.Length
      ? BinaryPrimitives.ReadInt64LittleEndian(image.Slice(off, 8))
      : 0;

  internal static string AsciiTrim(ReadOnlySpan<byte> data) {
    var nul = data.IndexOf((byte)0);
    if (nul < 0) nul = data.Length;
    return nul == 0 ? "" : Encoding.ASCII.GetString(data[..nul]);
  }
}
