#pragma warning disable CS1591
namespace FileSystem.Gemdos;

/// <summary>
/// In-place modifier for Atari ST GEMDOS disk images. GEMDOS is FAT12 with
/// a single byte difference at offset 0: the m68k <c>BRA.S</c> opcode
/// <c>0x60</c> instead of the x86 <c>JMP</c> opcode <c>0xEB</c>. The on-disk
/// FAT chains, root directory layout, dirent format and data-cluster region
/// are byte-identical to plain FAT12.
///
/// <para>
/// All mutation work is delegated to <see cref="FileSystem.Fat.FatRemover"/>
/// (for <see cref="RemoveFiles"/>) and to a re-pack via
/// <see cref="FileSystem.Fat.FatWriter"/> seeded from the existing files
/// (for <see cref="AddFiles"/>). The single-byte jump signature is patched
/// to <c>0xEB</c> before the FAT codepath runs and restored to <c>0x60</c>
/// before the result is written back.
/// </para>
/// </summary>
public static class GemdosInPlaceModifier {

  /// <summary>
  /// Adds — or replaces by name — files in an existing GEMDOS image. The
  /// image is re-packed from its existing files plus the new ones; the
  /// outer sector count is preserved.
  /// </summary>
  public static void AddFiles(Stream archive, IReadOnlyList<(string Name, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    // Snapshot existing files via the GEMDOS reader (which patches the jump
    // byte internally). We re-pack into a fresh FAT12 image, then patch the
    // 0x60 jump byte back to match the GEMDOS-specific signature.
    using var snap = new MemoryStream(image, writable: false);
    var reader = new FileSystem.Fat.FatReader(PatchedFatView(image));
    var combined = new FileSystem.Fat.FatWriter();
    foreach (var e in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(e.Name, reader.Extract(e));
    var byName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in reader.Entries.Where(e => !e.IsDirectory))
      byName[e.Name] = reader.Extract(e);
    foreach (var (name, data) in inputs) {
      ArgumentNullException.ThrowIfNull(name);
      ArgumentNullException.ThrowIfNull(data);
      byName[name] = data;
    }

    var fresh = new FileSystem.Fat.FatWriter();
    foreach (var (name, data) in byName)
      fresh.AddFile(name, data);

    var totalSectors = image.Length / 512;
    if (totalSectors <= 0) totalSectors = 1440;
    var rebuilt = fresh.Build(totalSectors: totalSectors);

    // Patch jump byte back to GEMDOS signature.
    if (rebuilt.Length > 0)
      rebuilt[0] = GemdosBpb.GemdosJump;

    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Removes the named entries from an existing GEMDOS image. All bytes
  /// of the entry's data clusters, cluster-tip slack, directory entry and
  /// FAT chain entries are zeroed via <see cref="FileSystem.Fat.FatRemover"/>.
  /// The 0x60 jump byte is preserved in the resulting image.
  /// </summary>
  public static void RemoveFiles(Stream archive, IReadOnlyList<string> names) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(names);

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    if (image.Length == 0) return;

    var originalJump = image[0];
    // FatRemover validates the BPB by reading bps from offset 0x0B but does
    // not check the jump byte; nevertheless patch to MS-DOS jump for any
    // downstream tooling that does, then patch back.
    image[0] = 0xEB;
    foreach (var name in names) {
      ArgumentNullException.ThrowIfNull(name);
      try {
        FileSystem.Fat.FatRemover.Remove(image, name);
      } catch (FileNotFoundException) {
        // Tolerate missing names — mirrors FAT descriptor's loop semantics.
      }
    }
    image[0] = originalJump != 0 ? originalJump : GemdosBpb.GemdosJump;

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  /// <summary>
  /// Returns a read-only stream over an in-memory copy of the GEMDOS image
  /// with the jump byte patched from <c>0x60</c> to <c>0xEB</c> so
  /// <see cref="FileSystem.Fat.FatReader"/> accepts the boot sector.
  /// </summary>
  private static Stream PatchedFatView(byte[] image) {
    var copy = new byte[image.Length];
    Buffer.BlockCopy(image, 0, copy, 0, image.Length);
    if (copy.Length > 0 && copy[0] == GemdosBpb.GemdosJump)
      copy[0] = 0xEB;
    return new MemoryStream(copy, writable: false);
  }
}
