#pragma warning disable CS1591
namespace FileSystem.Gemdos;

/// <summary>
/// Reads GEMDOS (Atari ST FAT12) images. The on-disk layout is exactly FAT12
/// except for the jump byte at offset 0 (0x60 BRA.S vs MS-DOS's 0xEB). This
/// reader patches the jump byte to 0xEB in an in-memory copy and then defers
/// to <see cref="FileSystem.Fat.FatReader"/> for all parsing — same FAT chains,
/// same root directory, same 8.3 dirent layout.
/// </summary>
public sealed class GemdosReader : System.IDisposable {

  private readonly FileSystem.Fat.FatReader _inner;

    /// <summary>
  /// Initializes a new instance of <see cref="GemdosReader"/>.
  /// </summary>
public GemdosReader(System.IO.Stream stream) {
    System.ArgumentNullException.ThrowIfNull(stream);
    using var ms = new System.IO.MemoryStream();
    stream.CopyTo(ms);
    var buf = ms.ToArray();
    // Patch the GEMDOS jump byte to MS-DOS so FatReader accepts the boot sector.
    // The remaining BPB layout is identical so there's nothing else to fix.
    if (buf.Length > 0 && buf[0] == GemdosBpb.GemdosJump)
      buf[0] = 0xEB;
    _inner = new FileSystem.Fat.FatReader(new System.IO.MemoryStream(buf, writable: false));
  }

  /// <summary>All entries (files + directories) in the volume, recursively.</summary>
  public System.Collections.Generic.IReadOnlyList<FileSystem.Fat.FatEntry> Entries => _inner.Entries;

  /// <summary>Reads the bytes of a file entry.</summary>
  public byte[] Extract(FileSystem.Fat.FatEntry entry) => _inner.Extract(entry);

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() => _inner.Dispose();
}
