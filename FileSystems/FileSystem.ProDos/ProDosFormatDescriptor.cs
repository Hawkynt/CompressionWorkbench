#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ProDos;

public sealed class ProDosFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveModifiable {

  // We cap at the 800 KB Mac-format floppy — the largest canonical size we emit.
  public long? MaxTotalArchiveSize => ProDosWriter.Disk800KTotalBlocks * 512L;
  public string AcceptedInputsDescription =>
    "Apple ProDOS block-ordered disk image (.po) or 2mg-wrapped ProDOS image.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>Canonical ProDOS image sizes (5.25" floppy = 143 360, 800 KB floppy = 819 200).</summary>
  public IReadOnlyList<long> CanonicalSizes => [
    ProDosWriter.FloppyTotalBlocks * 512L,
    ProDosWriter.Disk800KTotalBlocks * 512L,
  ];

  public string Id => "ProDos";
  public string DisplayName => "ProDOS";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ProDos image.
  /// Uses <c>ProDosModifier</c> for true O(touched bytes) random-access
  /// I/O — only the volume header, the directory chain, the bitmap, and
  /// the file's index + data blocks are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ProDosModifier.RemoveFile(archive, name, wipeData: true);
      ProDosModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing ProDos image. Uses
  /// <c>ProDosModifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ProDosModifier.RemoveFile(archive, name, wipeData: true);
  }


  public string DefaultExtension => ".po";
  public IReadOnlyList<string> Extensions => [".po", ".2mg"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // .2mg files begin with "2IMG" at offset 0. Raw .po files have no magic — detection
  // falls back to extension plus a valid volume-directory parse.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("2IMG"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple II / Apple IIgs ProDOS filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new ProDosReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FullPath, e.Size, e.Size, "Stored", e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ProDosReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += new FileInfo(i.FullPath).Length;
    if (this.MaxTotalArchiveSize is long cap && total > cap)
      throw new InvalidOperationException(
        $"ProDOS: combined input size {total} bytes exceeds 800 KB disk capacity ({cap} bytes).");

    var w = new ProDosWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    // Use the smaller floppy size by default; auto-promote to 800 KB when the 140 KB floppy
    // would not fit. This keeps round-trip tests tiny while still allowing larger corpora.
    var floppyCap = (ProDosWriter.FloppyTotalBlocks - 10) * 512L;  // rough free-space cap
    var totalBlocks = total > floppyCap ? ProDosWriter.Disk800KTotalBlocks : ProDosWriter.FloppyTotalBlocks;
    output.Write(w.Build(totalBlocks: totalBlocks));
  }
}
