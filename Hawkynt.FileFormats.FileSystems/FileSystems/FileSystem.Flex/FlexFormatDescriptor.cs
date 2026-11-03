#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Flex;

/// <summary>
/// FLEX disk images (Technical Systems Consultants' FLEX for the 6800/6809, 1976-): raw
/// <c>.dsk</c> sector dumps with 256-byte linked sectors, a System Information Record on
/// track 0 sector 3 and a directory chain from track 0 sector 5.
/// </summary>
/// <remarks>
/// <para>
/// Written from TSC's own manuals — the <i>6809 FLEX Adaptation Guide</i> (disk format,
/// NEWDISK, SIR layout) and the <i>FLEX Advanced Programmer's Guide</i> (directory entry,
/// data sector, deleted entries, random files); see <c>docs/FLEX-ON-DISK.md</c>.
/// </para>
/// <para>
/// R/W in place: Add takes sectors from the head of the free chain and a free or deleted
/// directory slot (growing the directory chain when full); Remove marks the entry deleted,
/// zeroes the file's sectors and appends them to the free chain. Edits run on a copy and
/// are written back only when every step succeeded. FLEX keeps no byte length, so file
/// contents are whole 252-byte records (padded with zeros on write). Proof: the real TSC
/// FLEX 9 distribution disk reads byte-identical to what <c>flexfloppy</c> (laurent-fr/flextools)
/// extracts, and <c>flexfloppy</c> lists and extracts what we write and edit, while we read
/// what it formats and adds.
/// </para>
/// </remarks>
public sealed class FlexFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IFormatOptionsSchema {

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: "",
      Description: "SIR volume name (11 characters).", IsOptimizationAxis: false),
    new FormatOptionDescriptor(Key: "Tracks", DisplayName: "Tracks", Kind: FormatOptionKind.Integer, Default: "0",
      Description: "Tracks on the disk (0 = smallest standard geometry that fits: 35×10, 40×18, 77×15, 80×36, 255×255).", IsOptimizationAxis: false),
    new FormatOptionDescriptor(Key: "SectorsPerTrack", DisplayName: "Sectors per track", Kind: FormatOptionKind.Integer, Default: "0",
      Description: "256-byte sectors per track (0 = with Tracks, the standard geometry).", IsOptimizationAxis: false),
  ];

  public string Id => "Flex";
  public string DisplayName => "FLEX disk (6800/6809)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".flx";
  public IReadOnlyList<string> Extensions => [".flx", ".dsk"];
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// FLEX has no magic number. What every FLEX disk shares: the SIR (track 0 sector 3)
  /// starts with 16 zero bytes, and the first directory sector (track 0 sector 5) links to
  /// track 0 sector 6 and has twelve zero bytes after its link and record number.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures { get; } = [BuildSignature()];

  private static MagicSignature BuildSignature() {
    var bytes = new byte[0x410 - 0x200];
    var mask = new byte[bytes.Length];
    for (var i = 0; i < 16; ++i) mask[i] = 0xFF;            // SIR bytes 0-15 zero
    bytes[0x201] = 0x06;                                       // dir sector link: track 0, sector 6
    for (var i = 0x200; i < 0x210; ++i) mask[i] = 0xFF;      // link 00 06, then 14 zero bytes
    return new(bytes, Offset: 0x200, Confidence: 0.40, Mask: mask);
  }

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "FLEX (TSC 6800/6809) disk image: linked 256-byte sectors, SIR and directory on track 0; " +
    "in-place R/W checked against flexfloppy and the TSC FLEX 9 distribution disk.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var d = new FlexDisk(stream);
    return d.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullName, e.Size, e.Size, "Stored", false, false, e.Created)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var d = new FlexDisk(stream);
    foreach (var e in d.Entries) {
      if (files != null && !MatchesFilter(e.FullName, files)) continue;
      WriteFile(outputDir, e.FullName, d.Extract(e));
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
    var d = new FlexDisk(archive);
    var e = d.Entries.FirstOrDefault(x => string.Equals(x.FullName, entryName, StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"FLEX disk has no file '{entryName}'.", entryName);
    return d.Extract(e);
  }

  /// <summary>Formats a disk and writes the files by leaf name (FLEX has one flat directory).</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var files = inputs.Where(i => !i.IsDirectory)
      .Select(i => (Name: Path.GetFileName(i.ArchiveName.Replace('\\', '/').TrimEnd('/')), Data: i.ReadContent(), When: i.Metadata?.LastWriteTimeUtc?.UtcDateTime))
      .ToList();
    foreach (var f in files) FlexWriter.EncodeName(f.Name);   // refuse before formatting
    var sectors = files.Sum(f => (long)Math.Max(1, (f.Data.Length + FlexDisk.DataBytes - 1) / FlexDisk.DataBytes));

    var tracks = options.GetOptionInt("Tracks", 0);
    var spt = options.GetOptionInt("SectorsPerTrack", 0);
    if (tracks == 0 || spt == 0) (tracks, spt) = FlexWriter.ChooseGeometry(sectors, files.Count);
    var img = FlexWriter.Format(tracks, spt, options.GetOption("VolumeLabel", ""));
    foreach (var f in files) {
      if (files.Count(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase)) > 1)
        throw new NotSupportedException($"FLEX cannot hold two files named '{f.Name}'.");
      FlexWriter.AddFile(img, f.Name, f.Data, f.When);
    }
    output.Write(img);
  }

  /// <summary>Adds or replaces files in place; all inputs land or none do.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Transact(archive, img => {
      foreach (var input in inputs) {
        if (input.IsDirectory || input.ArchiveName.Contains('/') || input.ArchiveName.Contains('\\'))
          throw new NotSupportedException($"FLEX has no directories; '{input.ArchiveName}' cannot be stored.");
        FlexWriter.AddFile(img, input.ArchiveName, input.ReadContent(), input.Metadata?.LastWriteTimeUtc?.UtcDateTime);
      }
    });
  }

  /// <summary>Deletes the named files in place; unknown names are ignored.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    Transact(archive, img => {
      foreach (var n in entryNames) FlexWriter.RemoveFile(img, n);
    });
  }

  private static void Transact(Stream archive, Action<byte[]> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanSeek || !archive.CanWrite) throw new NotSupportedException("FLEX edits need a seekable, writable image.");
    var original = new byte[archive.Length];
    archive.Position = 0;
    archive.ReadExactly(original);
    var img = (byte[])original.Clone();
    edit(img);
    for (var off = 0; off < img.Length; off += FlexDisk.SectorSize) {
      var n = Math.Min(FlexDisk.SectorSize, img.Length - off);
      if (img.AsSpan(off, n).SequenceEqual(original.AsSpan(off, n))) continue;
      archive.Position = off;
      archive.Write(img, off, n);
    }
    archive.Flush();
  }
}
