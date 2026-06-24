#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileSystem.Fat;

namespace FileSystem.FatPlus;

/// <summary>
/// Genuine in-place add for FAT+ images — the FAT+ counterpart of
/// <see cref="FileSystem.Fat.FatModifier"/>. FAT+ is a backward-compatible
/// FAT32 extension whose on-disk layout (boot sector, FATs, FSInfo, root
/// directory as a cluster chain, data clusters) is byte-for-byte standard
/// FAT32; the <em>only</em> difference is the directory entry's file-size
/// encoding, where the low 6 bits of <c>DIR_NTRes</c> (offset 12) carry
/// bits 32..37 of a 38-bit size.
/// <para>
/// This adder therefore delegates the heavy lifting (free-cluster allocation,
/// FAT-chain linking in every FAT copy, directory-slot insertion, FSInfo
/// free-count maintenance) to <see cref="FatModifier"/>, then patches the
/// just-written short-name dirent so the FAT+ size encoding is correct:
/// it always emits the long name through VFAT slots so the 8.3 alias is
/// upper-case and carries <em>no</em> NT case bits — which is essential
/// because those case bits (0x08 / 0x10) live in the very low-6-bit region
/// FAT+ repurposes for size, and would otherwise be mis-read as size bits.
/// </para>
/// <para>
/// Structural cases <see cref="FatModifier"/> cannot handle (a full root
/// directory, insufficient free clusters) throw so the descriptor can fall
/// back to the verified <see cref="FatPlusWriter"/> rebuild. Files whose
/// declared size needs more than 31 bits of payload (&gt; 2 GiB) cannot be
/// carried by the in-place byte[] path and also fall back.
/// </para>
/// </summary>
public static class FatPlusInPlaceAdder {

  /// <summary>
  /// Adds (or replaces by name) <paramref name="name"/> to the in-memory FAT+
  /// image, genuinely in place. The data clusters, FAT links and directory
  /// entry are written via <see cref="FatModifier"/>; the short-name dirent is
  /// then patched with the FAT+ extended-size encoding. The volume's
  /// <c>"FAT+    "</c> OEM signature (primary + backup boot sector) is
  /// preserved/repaired so detection still recognises the image as FAT+.
  /// </summary>
  /// <param name="extendedSize">Declared file size to encode (defaults to
  /// <c>data.Length</c>). Values &gt; 4 GiB exercise the NTRes high-6-bit
  /// encoding; the cluster chain only carries <paramref name="data"/>'s bytes.</param>
  /// <param name="modTime">Optional modification timestamp for the new entry.</param>
  public static void AddFile(byte[] image, string name, byte[] data, long extendedSize = -1, DateTime? modTime = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var declared = extendedSize < 0 ? data.Length : extendedSize;
    if (declared < 0 || declared >= 1L << 38)
      throw new ArgumentOutOfRangeException(nameof(extendedSize),
        "FAT+ extended size must fit in 38 bits (0 .. 256 GiB − 1).");

    // forceLfn: emit a VFAT long-name slot set for EVERY name so the 8.3 alias
    // is always upper-case (no NT case bits) — keeping the low 6 bits of NTRes
    // exclusively for the FAT+ size, which FatModifier zeroes by default.
    FatModifier.AddFile(image, name, data, modTime, forceLfn: true);

    // Patch the just-added entry's FAT+ size encoding. Locate it by name so we
    // don't depend on slot ordering. For files that fit in 32 bits the high-6
    // bits are 0 and this is a no-op write (FatModifier already wrote the low
    // 32 bits = data.Length); for declared sizes > 4 GiB the chain carries only
    // data.Length bytes but the dirent reports the larger declared size.
    PatchExtendedSize(image, name, declared);

    // Preserve / repair the FAT+ marks: FatModifier never touches the OEM bytes,
    // but a defensive re-stamp keeps the contract explicit and also fixes the
    // backup boot sector if a prior writer missed it.
    FatPlusReader.OemSignature.CopyTo(image.AsSpan(3));
    FatPlusWriter.PatchBackupOem(image);
  }

  /// <summary>
  /// Removes <paramref name="name"/> from the FAT+ image in place (delegates to
  /// <see cref="FatRemover"/> — the FAT chain, dirent and data wipe are identical
  /// to standard FAT; the FAT+ size bits live in the dirent which is zeroed) and
  /// re-stamps the FAT+ OEM signature so detection survives.
  /// </summary>
  public static void RemoveFile(byte[] image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    FatRemover.Remove(image, name);
    FatPlusReader.OemSignature.CopyTo(image.AsSpan(3));
    FatPlusWriter.PatchBackupOem(image);
  }

  // ── Dirent FAT+ size patch ───────────────────────────────────────────────

  private static void PatchExtendedSize(byte[] image, string name, long declared) {
    var off = FindShortEntryOffset(image, name);
    if (off < 0)
      throw new InvalidOperationException(
        $"FAT+ in-place add: could not locate the dirent for '{name}' after insertion.");
    var sizeLo = (uint)(declared & 0xFFFFFFFFu);
    var sizeHi = (byte)((declared >> 32) & 0x3F);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 28), sizeLo);
    // Preserve the top 2 bits of NTRes (reserved for NT); replace the low 6.
    image[off + 12] = (byte)((image[off + 12] & 0xC0) | sizeHi);
  }

  /// <summary>
  /// Walks the FAT32 root directory cluster chain and returns the absolute
  /// image offset of the short-name dirent whose assembled name (LFN preferred,
  /// else 8.3) matches <paramref name="name"/> case-insensitively, or -1.
  /// </summary>
  private static int FindShortEntryOffset(byte[] image, string name) {
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var sectorsPerCluster = image[13] == 0 ? 1 : image[13];
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    var fatCount = image[16] == 0 ? 2 : image[16];
    var fatSize = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36));
    var firstDataSector = reservedSectors + fatCount * fatSize; // FAT32: no fixed root region
    var clusterSize = sectorsPerCluster * bytesPerSector;
    var rootCluster = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(44));
    var fatStart = reservedSectors * bytesPerSector;

    var lfnParts = new SortedDictionary<int, string>();
    var cluster = rootCluster;
    var seen = new HashSet<int>();
    while (cluster >= 2 && cluster < 0x0FFFFFF8 && seen.Add(cluster)) {
      var clusterOff = (firstDataSector + (cluster - 2) * sectorsPerCluster) * bytesPerSector;
      for (var s = 0; s + 32 <= clusterSize && clusterOff + s + 32 <= image.Length; s += 32) {
        var off = clusterOff + s;
        var first = image[off];
        if (first == 0x00) return -1;
        if (first == 0xE5) { lfnParts.Clear(); continue; }
        var attr = image[off + 11];
        if ((attr & 0x3F) == 0x0F) {
          var seq = first & 0x3F;
          var sb = new StringBuilder();
          AppendLfn(image, off + 1, 5, sb);
          AppendLfn(image, off + 14, 6, sb);
          AppendLfn(image, off + 28, 2, sb);
          lfnParts[seq] = sb.ToString();
          continue;
        }
        if ((attr & 0x08) != 0) { lfnParts.Clear(); continue; } // volume label

        string candidate;
        if (lfnParts.Count > 0) {
          var sb = new StringBuilder();
          foreach (var p in lfnParts.Values) sb.Append(p);
          candidate = sb.ToString().TrimEnd('\0', '\xFFFF');
        } else {
          candidate = DecodeShortName(image.AsSpan(off, 11));
        }
        if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase))
          return off;
        lfnParts.Clear();
      }

      cluster = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(fatStart + cluster * 4)) & 0x0FFFFFFF;
    }
    return -1;
  }

  private static void AppendLfn(byte[] image, int offset, int count, StringBuilder sb) {
    for (var j = 0; j < count; ++j) {
      var charOff = offset + j * 2;
      if (charOff + 2 > image.Length) break;
      var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(charOff));
      if (c == 0 || c == 0xFFFF) break;
      sb.Append(c);
    }
  }

  private static string DecodeShortName(ReadOnlySpan<byte> entry) {
    var baseName = Encoding.ASCII.GetString(entry[..8]).TrimEnd(' ');
    var ext = Encoding.ASCII.GetString(entry[8..11]).TrimEnd(' ');
    return ext.Length == 0 ? baseName : $"{baseName}.{ext}";
  }
}
