#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Lfs;

/// <summary>
/// 4.4BSD log-structured file system as NetBSD keeps it (LFS version 2, 32-bit addresses):
/// superblock magic <c>0x070162</c> at 8 KiB.
/// </summary>
/// <remarks>
/// Read-only. Proof: a volume NetBSD 11.1's <c>newfs_lfs</c> created, filled through the
/// kernel (rewrites, a deleted file, a hard link, a file with indirect blocks) and checked
/// with <c>fsck_lfs</c> reads back byte-identical to what the kernel's own mount returned.
/// The last checkpoint is read; segments written after it are not rolled forward. 64-bit
/// LFS and byte-swapped volumes are refused. Sourcing: rung 1 — converted from NetBSD's
/// BSD-licensed LFS headers.
/// </remarks>
public sealed class LfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Lfs";
  public string DisplayName => "4.4BSD LFS (NetBSD)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".lfs";
  public IReadOnlyList<string> Extensions => [".lfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x62, 0x01, 0x07, 0x00, 0x02, 0x00, 0x00, 0x00], Offset: 8192, Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "4.4BSD log-structured file system (NetBSD LFS v2, 32-bit); read-only, last checkpoint.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.IsDirectory ? 0 : e.Size, e.IsDirectory ? 0 : e.Size,
      "Stored", e.IsDirectory, false, DateTime.UnixEpoch.AddSeconds(e.MTime),
      IsSymlink: e.IsSymlink, LinkTarget: e.IsSymlink ? System.Text.Encoding.UTF8.GetString(r.Extract(e)) : null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LfsReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.Path.Replace('/', Path.DirectorySeparatorChar)));
      else if (e.IsFile) WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new LfsReader(archive);
    var name = entryName.Replace('\\', '/').TrimStart('/');
    var e = r.Entries.FirstOrDefault(x => x.IsFile && x.Path == name)
      ?? throw new FileNotFoundException($"LFS volume has no file '{entryName}'.", entryName);
    return r.Extract(e);
  }
}
