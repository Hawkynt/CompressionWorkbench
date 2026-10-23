#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Svr4Bfs;

/// <summary>
/// Read/write descriptor for the SVR4 / UnixWare Boot File System — the flat,
/// contiguous-file volume UnixWare boots from (<c>/stand</c>). Magic <c>0x1BADFACE</c>
/// little-endian at offset 0.
/// </summary>
/// <remarks>
/// <para>
/// Layout, field by field, with the measurement behind each value:
/// <c>docs/SVR4-BFS-ON-DISK.md</c>. Written from the Linux <c>bfs_fs.h</c> structure
/// definitions (constants only) and black-box observation of <c>mkfs.bfs</c>
/// (util-linux 2.39.3) and the Linux 6.8 <c>bfs</c> driver.
/// </para>
/// <para>
/// Create lays files out contiguously behind the root directory; Add/Remove edit the
/// volume in place through <see cref="Svr4BfsModifier"/> and refuse — image unchanged —
/// a name longer than 14 bytes, a path with a directory in it, a file with no contiguous
/// free run, or a volume with an interrupted compaction. Proof: the Linux kernel driver
/// mounts what Create and Add write and reads every file back byte-exact, and our reader
/// reads volumes made by <c>mkfs.bfs</c> and written by that driver.
/// </para>
/// </remarks>
public sealed class Svr4BfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IFormatOptionsSchema {

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: "",
      Description: "BFS volume name stored in s_volume (max 6 ASCII chars).", IsOptimizationAxis: false),
  ];

  public string Id => "Svr4Bfs";
  public string DisplayName => "SVR4 Boot File System (BFS)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.SupportsMultipleEntries;

  // ".bfs" belongs to BeFS; the format has no extension of its own, so it is found by magic.
  public string DefaultExtension => ".svbfs";
  public IReadOnlyList<string> Extensions => [".svbfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xCE, 0xFA, 0xAD, 0x1B], Offset: 0, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "SVR4 / UnixWare Boot File System (/stand) — flat root directory, 14-byte names, " +
    "contiguous files; in-place R/W verified against the Linux bfs kernel driver.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Svr4BfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Svr4BfsReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
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
    var r = new Svr4BfsReader(archive);
    var e = r.Entries.FirstOrDefault(x => x.Name == entryName)
      ?? throw new FileNotFoundException($"SVR4 BFS volume has no file '{entryName}'.", entryName);
    return r.Extract(e);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var w = new Svr4BfsWriter();
    if (options.HasOption("VolumeLabel")) w.VolumeLabel = options.GetOption("VolumeLabel", "");
    // A flat format takes a tree the way the package's other flat volumes do: files by
    // their leaf name, directories dropped. Two leaves with one name, or a leaf over
    // 14 bytes, are refused rather than renamed.
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddFile(Path.GetFileName(input.ArchiveName.Replace('\\', '/').TrimEnd('/')), input.ReadContent(), input.Metadata);
    }
    w.WriteTo(output);
  }

  /// <summary>Adds or replaces root-level files in place; all inputs land or none do.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    Transact(archive, work => {
      foreach (var input in inputs) {
        if (input.IsDirectory)
          throw new NotSupportedException($"SVR4 BFS has no subdirectories; '{input.ArchiveName}' cannot be stored.");
        Svr4BfsModifier.AddFile(work, input.ArchiveName, input.ReadContent(), input.Metadata);
      }
    });
  }

  /// <summary>Removes the named files in place, zeroing their blocks; unknown names are ignored.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    Transact(archive, work => {
      foreach (var name in entryNames) Svr4BfsModifier.RemoveFile(work, name);
    });
  }

  /// <summary>
  /// Runs a multi-file edit on a copy and writes it back only when every step succeeded,
  /// so a refusal partway leaves the caller's image byte-identical.
  /// </summary>
  private static void Transact(Stream archive, Action<Stream> edit) {
    if (!archive.CanSeek || !archive.CanWrite)
      throw new NotSupportedException("SVR4 BFS edits need a seekable, writable image.");
    var original = new byte[archive.Length];
    archive.Position = 0;
    archive.ReadExactly(original);
    using var work = new MemoryStream();
    work.Write(original);
    edit(work);
    var edited = work.GetBuffer().AsSpan(0, (int)work.Length);
    // Write back only the blocks that changed.
    for (var off = 0; off < edited.Length; off += Svr4BfsLayout.BlockSize) {
      var n = Math.Min(Svr4BfsLayout.BlockSize, edited.Length - off);
      if (edited.Slice(off, n).SequenceEqual(original.AsSpan(off, n))) continue;
      archive.Position = off;
      archive.Write(edited.Slice(off, n));
    }
    archive.Flush();
  }
}
