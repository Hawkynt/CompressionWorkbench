#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Lif;

/// <summary>
/// Writer for HP LIF volumes — flat-directory layout with the directory at sector 2
/// (the conventional LIF starting location). Files are stored contiguously starting
/// after the directory; the writer enforces an upper bound of 14 files unless a
/// caller increases the directory size.
/// </summary>
public sealed class LifWriter {

  /// <summary>
  /// Builds a LIF image from the supplied files. <paramref name="volumeLabel"/> is
  /// truncated/padded to 6 ASCII characters; non-ASCII bytes are replaced with '?'.
  /// </summary>
  /// <param name="files">Files to embed; names are truncated/padded to 10 characters.</param>
  /// <param name="volumeLabel">Volume label; defaults to "CWB   ".</param>
  /// <param name="defaultFileType">LIF file type assigned to each file; default 0xE020 (BIN program).</param>
  /// <param name="dirSectors">Number of 256-byte sectors reserved for the directory; defaults to 1.</param>
  public static byte[] Build(
    IReadOnlyList<(string Name, byte[] Data)> files,
    string volumeLabel = "CWB",
    ushort defaultFileType = 0xE020,
    int dirSectors = 1) {

    if (dirSectors < 1) throw new ArgumentOutOfRangeException(nameof(dirSectors), "Need at least one directory sector.");
    var entriesPerSector = LifReader.SectorSize / 32;
    var maxFiles = dirSectors * entriesPerSector - 1; // leave one slot free for the 0xFF terminator
    if (files.Count > maxFiles)
      throw new ArgumentException($"LIF: too many files ({files.Count} > {maxFiles}); increase dirSectors.", nameof(files));

    const int dirStart = 2;
    var dataStart = dirStart + dirSectors;

    // Lay out file extents.
    var extents = new (int StartSec, int LenSec)[files.Count];
    var nextSec = dataStart;
    for (var i = 0; i < files.Count; i++) {
      var lenSec = (files[i].Data.Length + LifReader.SectorSize - 1) / LifReader.SectorSize;
      if (lenSec == 0) lenSec = 1;
      extents[i] = (nextSec, lenSec);
      nextSec += lenSec;
    }
    var totalSectors = nextSec;
    var image = new byte[totalSectors * LifReader.SectorSize];

    // ── Volume label sector ───────────────────────────────────────────────
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(0), LifReader.LifMagic);
    var labelBytes = Encoding.ASCII.GetBytes(SanitizeAscii(volumeLabel, 6));
    labelBytes.CopyTo(image.AsSpan(2));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8), (uint)dirStart);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(12), 0x1000); // system identifier (HP series 80)
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16), (uint)dirSectors);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(20), 0); // tracks
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(24), 0); // surfaces
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(28), 0); // sectors per track

    // BCD timestamp.
    var ts = DateTime.Now;
    var tsSpan = image.AsSpan(36, 6);
    tsSpan[0] = ToBcd((byte)(ts.Year % 100));
    tsSpan[1] = ToBcd((byte)ts.Month);
    tsSpan[2] = ToBcd((byte)ts.Day);
    tsSpan[3] = ToBcd((byte)ts.Hour);
    tsSpan[4] = ToBcd((byte)ts.Minute);
    tsSpan[5] = ToBcd((byte)ts.Second);

    // ── Directory entries ─────────────────────────────────────────────────
    var dirByteOffset = dirStart * LifReader.SectorSize;
    for (var i = 0; i < files.Count; i++) {
      var off = dirByteOffset + i * 32;
      var nameBytes = Encoding.ASCII.GetBytes(SanitizeAscii(files[i].Name, 10));
      nameBytes.CopyTo(image.AsSpan(off));
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 10), defaultFileType);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 12), (uint)extents[i].StartSec);
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(off + 16), (uint)extents[i].LenSec);
      tsSpan.CopyTo(image.AsSpan(off + 20));
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(off + 26), 0x8001); // volume number
      // bytes 28..32 implementation-specific; leave zero.
    }
    // Terminator entry.
    var terminatorOff = dirByteOffset + files.Count * 32;
    if (terminatorOff + 32 <= image.Length)
      image[terminatorOff] = 0xFF;

    // ── File data ─────────────────────────────────────────────────────────
    for (var i = 0; i < files.Count; i++) {
      var startByte = (long)extents[i].StartSec * LifReader.SectorSize;
      var data = files[i].Data;
      data.CopyTo(image.AsSpan((int)startByte));
    }

    return image;
  }

  private static string SanitizeAscii(string s, int width) {
    var chars = new char[width];
    for (var i = 0; i < width; i++) chars[i] = ' ';
    var max = Math.Min(s.Length, width);
    for (var i = 0; i < max; i++) {
      var c = s[i];
      chars[i] = c is >= (char)0x20 and < (char)0x7F ? c : '?';
    }
    return new string(chars);
  }

  private static byte ToBcd(byte v) => (byte)(((v / 10) << 4) | (v % 10));
}
