#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Axfs;

/// <summary>
/// AXFS — the "Advanced XIP File System" for embedded Linux (Numonyx, 2008): a read-only,
/// compressed, execute-in-place image. Magic <c>0x48A0E4CD</c> and the signature
/// "Advanced XIP FS" at offset 0.
/// </summary>
/// <remarks>
/// <para>
/// Read-only. Directories, regular files, symbolic links, modes and owners are read from
/// the byte tables, and file data from all three node kinds: XIP pages, byte-aligned
/// nodes and zlib cblocks. Device nodes, FIFOs and sockets are listed but have no bytes.
/// Only version 2.x images with zlib compression are accepted; anything else is refused
/// rather than guessed at.
/// </para>
/// <para>
/// Proof: images built by <c>mkfs.axfs</c> (jaredeh/axfs, AXFS 2.2.0) read back byte-identical
/// to the source tree it was given, with modes and link targets. Sourcing: rung 2. The GPL
/// tool and kernel driver were read to write <c>docs/AXFS-ON-DISK.md</c> and are used as a
/// black-box oracle; none of their code is used here. No writer: nothing outside this
/// package can read an AXFS image back on a current kernel.
/// </para>
/// </remarks>
public sealed class AxfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Axfs";
  public string DisplayName => "AXFS (Advanced XIP File System)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".axfs";
  public IReadOnlyList<string> Extensions => [".axfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x48, 0xA0, 0xE4, 0xCD, .. "Advanced XIP FS"u8], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zlib", "zlib")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "AXFS embedded read-only XIP filesystem image (XIP, byte-aligned and zlib nodes); read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AxfsReader(stream);
    var stamp = r.Timestamp == 0 ? (DateTime?)null : DateTime.UnixEpoch.AddSeconds(r.Timestamp);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Path, e.IsFile || e.IsSymlink ? (long)e.Size : 0, e.IsFile || e.IsSymlink ? (long)e.Size : 0,
      "zlib", e.IsDirectory, false, stamp,
      IsSymlink: e.IsSymlink,
      LinkTarget: e.IsSymlink ? System.Text.Encoding.UTF8.GetString(r.Extract(e)) : null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AxfsReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, e.Path.Replace('/', Path.DirectorySeparatorChar)));
        continue;
      }
      if (e.IsFile) WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new AxfsReader(archive);
    var e = r.Entries.FirstOrDefault(x => x.IsFile && x.Path == entryName.Replace('\\', '/').TrimStart('/'))
      ?? throw new FileNotFoundException($"AXFS image has no file '{entryName}'.", entryName);
    return r.Extract(e);
  }
}
