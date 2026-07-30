#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ods1;

/// <summary>
/// Read+R/W descriptor for DEC VAX/VMS ODS-1 (Files-11 Level 1) volumes.
/// Signature "DECFILE11A" at file offset 0x3F0 (= LBN 1 + 0x1F0).
/// Reader covers single-extent retrieval pointers; writer emits a fresh
/// Files-11 L1 disk image (home block + index file + bitmap + user-file
/// headers + contiguous extents); modifier mutates existing images in-place
/// via <see cref="Ods1Modifier"/> (Add allocates a free header slot + a
/// contiguous BITMAP run, Remove zeros the header slot + frees its BITMAP
/// bits + zero-fills its data extent; both recompute the home-block additive
/// checksums). Self-round-trip gated; no Linux fsck for ODS-1 exists.
///
/// References:
/// <list type="bullet">
///   <item><description>DEC "Files-11 On-Disk Structure Specification" — the canonical ODS-1/ODS-2 spec (archived at Bitsavers)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Files-11</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Ods1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Sole tunable the Files-11 L1 writer honours: the 12-character home-block
  /// volume name (hm1$t_volname). The rest of the Stage-1 geometry is fixed.
  /// An empty label falls back to the writer default ("CWBVOL").
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 12),
  ];

  public string Id => "Ods1";
  public string DisplayName => "ODS-1 (VAX/VMS Files-11 L1)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ods1";
  public IReadOnlyList<string> Extensions => [".ods1", ".vms"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "DECFILE11A" at file offset 0x200 + 0x1F0 = 0x3F0
    new([(byte)'D', (byte)'E', (byte)'C', (byte)'F', (byte)'I', (byte)'L', (byte)'E', (byte)'1', (byte)'1', (byte)'A'], Offset: 0x3F0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DEC ODS-1 (RSX-11/VAX-VMS Files-11 Level 1) volume — read + R/W create + in-place " +
    "Add/Remove (Stage 1: single-extent retrieval pointers, ASCII filenames, ≤ 9.3 chars, " +
    "64-slot INDEXF window, home-block additive checksums recomputed on every mutation).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Ods1Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new Ods1Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
  }

  /// <summary>
  /// Builds a fresh ODS-1 disk image. Inputs are stored in the root with
  /// 9.3 ASCII filenames (longer names are truncated by
  /// <see cref="Ods1Writer.SplitName"/>); directory inputs are skipped
  /// (ODS-1 Stage-1 has no subdirectory support).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = new List<(string Name, Compression.Core.DiskImage.FilePayload Payload)>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var info = input;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap it at what an array can hold.
      files.Add((Path.GetFileName(info.ArchiveName), info.InMemoryContent is { } bytes
        ? Compression.Core.DiskImage.FilePayload.FromBytes(bytes)
        : Compression.Core.DiskImage.FilePayload.FromStream(
            new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath))));
    }
    var volumeName = options?.GetOption("VolumeLabel", "") ?? "";
    if (string.IsNullOrEmpty(volumeName)) volumeName = "CWBVOL";
    Ods1Writer.WriteTo(output, files, volumeName);
  }

  /// <summary>
  /// Adds files to an existing ODS-1 image via <see cref="Ods1Modifier.AddFile"/>.
  /// Each input gets a free header slot in the 64-slot INDEXF window plus a
  /// contiguous BITMAP run for its data extent. Directory inputs are skipped
  /// (Stage-1 has no subdirectory support). Throws
  /// <see cref="NotSupportedException"/> when INDEXF or BITMAP is exhausted.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var leaf = Path.GetFileName(input.ArchiveName);
      // Replace-by-name: drop any prior entry first so an update overwrites in place
      // rather than leaving a duplicate directory record.
      Ods1Modifier.RemoveFile(archive, leaf);
      Ods1Modifier.AddFile(archive, leaf, input.ReadContent());
    }
  }

  /// <summary>
  /// Removes the named entries from an existing ODS-1 image via
  /// <see cref="Ods1Modifier.RemoveFile"/>. Each removal frees the file's
  /// BITMAP bits, zero-fills its data extent (no forensic recovery), and
  /// zero-fills its file-header slot. Unknown names are silently skipped.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      Ods1Modifier.RemoveFile(archive, name);
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new Ods1Reader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }
}
