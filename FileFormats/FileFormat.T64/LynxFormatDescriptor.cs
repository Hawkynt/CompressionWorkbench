#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lynx;

/// <summary>
/// Commodore 64 Lynx/LNX archive. The format stores a textual PETSCII-ish directory and
/// uncompressed file extents in 254-byte blocks mirroring a 1541 sector with its two link
/// bytes removed.
/// </summary>
public sealed class LynxFormatDescriptor :
    IFormatDescriptor,
    IArchiveFormatOperations,
    IArchiveCreatable,
    IArchiveModifiable,
    IArchiveDefragmentable,
    IArchiveLayoutMap,
    IFormatOptionsSchema {

  public string Id => "Lynx";
  public string DisplayName => "Commodore Lynx archive";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lnx";
  public IReadOnlyList<string> Extensions => [".lnx"];
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// Canonical Lynx BASIC preambles contain the text "USE LYNX..." with LYNX at offset 0x3C.
  /// Keeping the offset avoids colliding with Atari Lynx cartridge ROMs, whose LYNX magic is at 0.
  /// The parser itself also accepts non-canonical BASIC preamble lengths when opened explicitly.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("LYNX"u8.ToArray(), Offset: 0x3C, Confidence: 0.92),
  ];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Will Corley/Ultimate Lynx Commodore archive: 254-byte sector payload blocks, PRG/SEQ/USR/DEL " +
    "creation, REL read/remove support, and genuine in-place add/replace/remove by shifting only " +
    "the affected directory/data block ranges. No compression or checksum exists in the format.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("FileType", "Default Commodore file type", FormatOptionKind.Enum, "P",
      ["P", "S", "U", "D"], "File type used for fresh generic inputs: PRG, SEQ, USR or DEL."),
    new("Signature", "24-byte Lynx signature", FormatOptionKind.String, LynxWriter.DefaultSignature,
      Description: "Exactly 24 printable ASCII characters containing LYNX."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = Open(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index,
      entry.Name,
      entry.Length,
      entry.Length,
      $"Stored/{entry.FileType}",
      false,
      false,
      null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = Open(stream);
    foreach (var entry in reader.Entries) {
      if (files is not null && files.Length > 0 && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var reader = Open(archive);
    var entry = reader.Entries.FirstOrDefault(candidate =>
      string.Equals(candidate.Name, entryName, StringComparison.OrdinalIgnoreCase));
    var data = entry is null ? [] : reader.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var entry = this.OpenEntry(archive, entryName, password);
    using var memory = new MemoryStream();
    entry.CopyTo(memory);
    return memory.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    RejectEncryption(options);
    if (options.MethodName is { Length: > 0 } method
        && !method.Equals("stored", StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException($"Lynx supports only stored data, not '{method}'.");

    var fileTypeText = options.GetOption("FileType", "P");
    if (fileTypeText.Length != 1)
      throw new ArgumentException("Lynx FileType must be one of P, S, U or D.", nameof(options));
    var signature = options.GetOption("Signature", LynxWriter.DefaultSignature);
    var files = FlatFiles(inputs).ToList();
    LynxWriter.WriteArchive(output, files, fileTypeText[0], signature);
  }

  /// <summary>
  /// Adds new PRG files or directly replaces same-name non-REL entries. The modifier rewrites
  /// the directory metadata in place, grows it by whole 254-byte blocks only when needed, and
  /// shifts only the affected data tail. Existing unaffected payload bytes are not re-encoded.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FlatFiles(inputs))
      LynxInPlaceModifier.AddOrReplace(archive, name, data, 'P');
  }

  /// <summary>
  /// Removes entries by closing their allocated block range and truncating the shifted tail.
  /// REL side-sector blocks are removed together with their data blocks.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      LynxInPlaceModifier.Remove(archive, name);
  }

  /// <summary>
  /// Lynx data extents are inherently contiguous and ordered by the directory. Defragmentation
  /// therefore consists of validating that layout and dropping transport/trailing padding after
  /// the last allocated archive block; intrinsic per-block padding is part of the format.
  /// </summary>
  public void Defragment(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanSeek || !archive.CanWrite)
      throw new ArgumentException("Lynx defragmentation requires a writable, seekable stream.", nameof(archive));
    var reader = Open(archive);
    if (archive.Length > reader.LogicalDataEnd)
      archive.SetLength(reader.LogicalDataEnd);
    archive.Flush();
    archive.Position = 0;
  }

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException("Lynx has an implicit sequential layout; only ConsolidateAtStart is meaningful.");
    this.Defragment(archive);
  }

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    var reader = Open(archive);
    yield return new DefragBlockInfo(0, reader.DataStart, DefragBlockKind.MetadataReserved, "Lynx directory");
    foreach (var entry in reader.Entries) {
      var allocated = checked((long)entry.ArchiveBlocks * LynxReader.BlockSize);
      if (allocated > 0)
        yield return new DefragBlockInfo(entry.AllocationOffset, allocated, DefragBlockKind.Used, entry.Name);
    }
    if (reader.LogicalDataEnd < archive.Length)
      yield return new DefragBlockInfo(reader.LogicalDataEnd, archive.Length - reader.LogicalDataEnd, DefragBlockKind.Free);
  }

  private static LynxReader Open(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    return new LynxReader(stream);
  }

  private static void RejectEncryption(FormatCreateOptions options) {
    if (!string.IsNullOrEmpty(options.Password) || options.EncryptFilenames || !string.IsNullOrEmpty(options.EncryptionMethod))
      throw new NotSupportedException("The Lynx archive format has no encryption facility.");
  }
}
