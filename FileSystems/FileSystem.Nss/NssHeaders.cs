#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Nss;

/// <summary>
/// Best-effort detector for NSS (Novell Storage Services) — the
/// pool-based, object-aware filesystem that replaced NWFS386 as the
/// default for NetWare 5+ and Open Enterprise Server. Successor to
/// NWFS, predecessor to (still-Novell) NSS-on-Linux in OES.
///
/// HONEST DISCLAIMER: NSS's on-disk format was never officially
/// documented by Novell. The only public structural information comes
/// from third-party RE notes, Novell support/marketing materials, and
/// utility output (nsscon / nss /poolverify). We can identify NSS-shaped
/// images by scanning for known ASCII signatures Novell embedded in
/// pool / volume / superblock metadata, but we cannot validate the
/// object tree without the proprietary spec.
///
/// Detected signatures (free-form scan within the first 1 MB):
/// <list type="bullet">
///   <item><description><c>"NSS Pool"</c> — appears in the pool descriptor block</description></item>
///   <item><description><c>"NSSVolume"</c> — appears in volume descriptors</description></item>
///   <item><description><c>"SuperBlk"</c> — appears in NSS object-store superblock copies (typically four mirrored copies)</description></item>
///   <item><description><c>"Novell"</c> + <c>"NetWare"</c> — corroborating brand strings used as low-weight confirmation; never alone</description></item>
/// </list>
///
/// References:
/// <list type="bullet">
///   <item><description>Novell (now OpenText) "NSS File System Administration Guide" — operational docs, no on-disk layout</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Novell_Storage_Services</c> — overview, object/pool model</description></item>
///   <item><description>NetWare 6.5 NSS Storage Management Services docs — pool/volume terminology</description></item>
///   <item><description>RE notes around NSS partition type 0x69 (MBR) on legacy NetWare disks</description></item>
/// </list>
/// </summary>
public sealed class NssHeaders {
  /// <summary>"NSS Pool" — 8 ASCII bytes. Pool descriptor identifier.</summary>
  public static readonly byte[] NssPoolMagic = "NSS Pool"u8.ToArray();

  /// <summary>"NSSVolume" — 9 ASCII bytes. Volume descriptor identifier.</summary>
  public static readonly byte[] NssVolumeMagic = "NSSVolume"u8.ToArray();

  /// <summary>"SuperBlk" — 8 ASCII bytes. Mirrored superblock identifier (typically 4 copies).</summary>
  public static readonly byte[] NssSuperblockMagic = "SuperBlk"u8.ToArray();

  /// <summary>"Novell" — corroborating brand string (low weight, never alone).</summary>
  public static readonly byte[] NovellMagic = "Novell"u8.ToArray();

  /// <summary>"NetWare" — corroborating brand string (low weight, never alone).</summary>
  public static readonly byte[] NetWareMagic = "NetWare"u8.ToArray();

  /// <summary>Bytes captured for <c>volume_header.bin</c> when any anchor is found.</summary>
  public const int HeaderCaptureSize = 4096;

  /// <summary>Scan limit — the first 1 MB of the image. Larger pools may exist but signatures cluster near the start.</summary>
  public const int ScanLimit = 1024 * 1024;

  /// <summary>
  /// Gets a value indicating whether pool found.
  /// </summary>
public bool PoolFound { get; private init; }
  /// <summary>
  /// Gets or sets the pool found offset.
  /// </summary>
public long PoolFoundOffset { get; private init; } = -1;

  /// <summary>
  /// Gets a value indicating whether volume found.
  /// </summary>
public bool VolumeFound { get; private init; }
  /// <summary>
  /// Gets or sets the volume found offset.
  /// </summary>
public long VolumeFoundOffset { get; private init; } = -1;

  /// <summary>
  /// Gets a value indicating whether superblock found.
  /// </summary>
public bool SuperblockFound { get; private init; }
  /// <summary>
  /// Gets or sets the superblock found offset.
  /// </summary>
public long SuperblockFoundOffset { get; private init; } = -1;

  /// <summary>
  /// Gets a value indicating whether novell found.
  /// </summary>
public bool NovellFound { get; private init; }
  /// <summary>
  /// Gets a value indicating whether net ware found.
  /// </summary>
public bool NetWareFound { get; private init; }

  /// <summary>
  /// Gets or sets the header raw.
  /// </summary>
public byte[] HeaderRaw { get; private init; } = [];

  /// <summary>True iff at least one primary NSS signature (Pool/Volume/SuperBlk) was located.</summary>
  public bool AnyValid => this.PoolFound || this.VolumeFound || this.SuperblockFound;

  /// <summary>
  /// Free-form scan for NSS anchors. Never throws. Bounds itself to the
  /// first 1 MB — pool/volume descriptors live near the start of the
  /// partition. We hop 512 B at a time (NSS uses 4 KB blocks but the
  /// strings can land at any aligned offset, and 512 keeps the work tiny).
  /// </summary>
  public static NssHeaders TryParse(ReadOnlySpan<byte> image) {
    var limit = Math.Min(image.Length, ScanLimit);

    var poolOff = -1L;
    var volOff = -1L;
    var sbOff = -1L;
    var novellFound = false;
    var netwareFound = false;

    for (var i = 0; i + 16 <= limit; i += 512) {
      var window = image.Slice(i, Math.Min(64, limit - i));
      if (poolOff < 0 && window.Length >= NssPoolMagic.Length &&
          window[..NssPoolMagic.Length].SequenceEqual(NssPoolMagic))
        poolOff = i;
      if (volOff < 0 && window.Length >= NssVolumeMagic.Length &&
          window[..NssVolumeMagic.Length].SequenceEqual(NssVolumeMagic))
        volOff = i;
      if (sbOff < 0 && window.Length >= NssSuperblockMagic.Length &&
          window[..NssSuperblockMagic.Length].SequenceEqual(NssSuperblockMagic))
        sbOff = i;

      if (!novellFound && window.Length >= NovellMagic.Length &&
          window[..NovellMagic.Length].SequenceEqual(NovellMagic))
        novellFound = true;
      if (!netwareFound && window.Length >= NetWareMagic.Length &&
          window[..NetWareMagic.Length].SequenceEqual(NetWareMagic))
        netwareFound = true;

      if (poolOff >= 0 && volOff >= 0 && sbOff >= 0 && novellFound && netwareFound) break;
    }

    // Capture 4 KB from the highest-priority signature for diagnostics.
    byte[] raw = [];
    var captureFrom = poolOff;
    if (captureFrom < 0) captureFrom = sbOff;
    if (captureFrom < 0) captureFrom = volOff;
    if (captureFrom >= 0)
      raw = SafeCapture(image, captureFrom, HeaderCaptureSize);

    return new NssHeaders {
      PoolFound = poolOff >= 0,
      PoolFoundOffset = poolOff,
      VolumeFound = volOff >= 0,
      VolumeFoundOffset = volOff,
      SuperblockFound = sbOff >= 0,
      SuperblockFoundOffset = sbOff,
      NovellFound = novellFound,
      NetWareFound = netwareFound,
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

  /// <summary>
  /// Tries to read an ASCII volume name string near the volume anchor.
  /// NSS volume descriptors carry a length-prefixed name shortly after
  /// the "NSSVolume" string; without an authoritative spec we use a
  /// printable-ASCII heuristic.
  /// </summary>
  internal static string TryReadVolumeNameNear(ReadOnlySpan<byte> image, long anchor) {
    if (anchor < 0 || anchor + 64 > image.Length) return "";
    var window = image.Slice((int)anchor, Math.Min(256, image.Length - (int)anchor));
    // Skip the magic itself.
    var start = NssVolumeMagic.Length;
    if (start >= window.Length) return "";
    // Find the first printable run ≥ 2 chars after the magic.
    var sb = new StringBuilder();
    for (var i = start; i < window.Length; i++) {
      var c = window[i];
      if (c is >= 0x20 and < 0x7F) {
        sb.Append((char)c);
      } else if (sb.Length >= 2) {
        break;
      } else {
        sb.Clear();
      }
    }
    return sb.ToString();
  }
}
