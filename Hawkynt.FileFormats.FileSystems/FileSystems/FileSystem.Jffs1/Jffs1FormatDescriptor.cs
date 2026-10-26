#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Jffs1;

/// <summary>
/// JFFS version 1 — Axis Communications' Journalling Flash File System (Linux 2.4, removed
/// in 2.6). Magic "1984" in either byte order at offset 0, followed by the root inode 1.
/// </summary>
/// <remarks>
/// Read-only. Proof: images <c>mkfs.jffs</c> (mtd-utils, as kept in uClinux/DSLinux) builds
/// in both byte orders read back byte-identical to the tree it was given, with modes and
/// link targets. Logs of a running file system — deleted, renamed or overwritten nodes — are
/// refused: the kernel driver that defined their replay is gone, so a replay could not be
/// checked. Sourcing: rung 2 — the GPL tool was read for <c>docs/JFFS1-ON-DISK.md</c> and
/// used as a black-box oracle. No writer: nothing can read JFFS v1 back to prove one.
/// </remarks>
public sealed class Jffs1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Jffs1";
  public string DisplayName => "JFFS (version 1)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".jffs";
  public IReadOnlyList<string> Extensions => [".jffs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x31, 0x39, 0x38, 0x34, 0x01, 0x00, 0x00, 0x00], Offset: 0, Confidence: 0.60),
    new([0x34, 0x38, 0x39, 0x31, 0x00, 0x00, 0x00, 0x01], Offset: 0, Confidence: 0.60),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "JFFS v1 flash image (mkfs.jffs layout, either byte order); read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Jffs1Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.IsFile || e.IsSymlink ? e.Data.Length : 0,
      e.IsFile || e.IsSymlink ? e.Data.Length : 0, "Stored", e.IsDirectory, false, DateTime.UnixEpoch.AddSeconds(e.MTime),
      IsSymlink: e.IsSymlink, LinkTarget: e.IsSymlink ? System.Text.Encoding.UTF8.GetString(e.Data) : null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Jffs1Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.Path.Replace('/', Path.DirectorySeparatorChar)));
      else if (e.IsFile) WriteFile(outputDir, e.Path, e.Data);
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new Jffs1Reader(archive);
    var name = entryName.Replace('\\', '/').TrimStart('/');
    return (r.Entries.FirstOrDefault(e => e.IsFile && e.Path == name)
      ?? throw new FileNotFoundException($"JFFS image has no file '{entryName}'.", entryName)).Data;
  }
}
