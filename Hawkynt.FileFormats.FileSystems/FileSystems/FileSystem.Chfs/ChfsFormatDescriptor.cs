#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Chfs;

/// <summary>
/// CHFS — NetBSD's Chip File System for raw NOR and NAND flash. An image starts with the
/// erase-counter header magic "RDES" (<c>0x53454452</c>).
/// </summary>
/// <remarks>
/// Read-only. Proof: images NetBSD 11.1's <c>makefs -t chfs</c> built — NAND with the default
/// geometry and NOR with 512-byte pages and 16 KiB eraseblocks — read back byte-identical to
/// the tree they were built from. The only flash NetBSD attaches under emulation is
/// <c>nandemulator(4)</c>, and <c>mount_chfs</c> on it blocks on a kernel lock in NetBSD 11.1
/// and 10.1, so nothing outside this package reads a CHFS image back and no writer is offered
/// (routes tried: <c>docs/CHFS-ON-DISK.md</c>). Logs that remove names (written only by a
/// mounted file system) are refused.
/// Sourcing: rung 1 — converted from NetBSD's BSD-licensed CHFS headers.
/// </remarks>
public sealed class ChfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Chfs";
  public string DisplayName => "CHFS (NetBSD Chip File System)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".chfs";
  public IReadOnlyList<string> Extensions => [".chfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("RDES"u8.ToArray(), Offset: 0, Confidence: 0.55)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "NetBSD CHFS flash image (NOR or NAND eraseblocks); read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ChfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.IsFile || e.IsSymlink ? e.Data.Length : 0,
      e.IsFile || e.IsSymlink ? e.Data.Length : 0, "Stored", e.IsDirectory, false, DateTime.UnixEpoch.AddSeconds(e.MTime),
      IsSymlink: e.IsSymlink, LinkTarget: e.IsSymlink ? System.Text.Encoding.UTF8.GetString(e.Data) : null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ChfsReader(stream);
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
    var name = entryName.Replace('\\', '/').TrimStart('/');
    return (new ChfsReader(archive).Entries.FirstOrDefault(e => e.IsFile && e.Path == name)
      ?? throw new FileNotFoundException($"CHFS image has no file '{entryName}'.", entryName)).Data;
  }
}
