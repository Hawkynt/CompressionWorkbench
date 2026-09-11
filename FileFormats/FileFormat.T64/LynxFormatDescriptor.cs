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
    IArchiveShrinkable,
    IArchiveLayoutMap,
    IWipeEmpty,
    IArchivePurgeable,
    IFormatOptionsSchema {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Lynx";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Commodore Lynx archive";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".lnx";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".lnx"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// Canonical Lynx BASIC preambles contain the text "USE LYNX..." with LYNX at offset 0x3C.
  /// Keeping the offset avoids colliding with Atari Lynx cartridge ROMs, whose LYNX magic is at 0.
  /// The parser itself also accepts non-canonical BASIC preamble lengths when opened explicitly.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("LYNX"u8.ToArray(), Offset: 0x3C, Confidence: 0.92),
  ];

  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "Will Corley/Ultimate Lynx Commodore archive: 254-byte sector payload blocks, PRG/SEQ/USR/DEL " +
    "creation, REL read/remove support, genuine in-place add/replace/remove, exact layout/wipe, " +
    "directory/trailer shrink, and purge. No compression or checksum exists in the format.";

  /// <summary>
  /// Gets the options schema.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("FileType", "Default Commodore file type", FormatOptionKind.Enum, "P",
      ["P", "S", "U", "D"], "File type used for fresh generic inputs: PRG, SEQ, USR or DEL."),
    new("Signature", "24-byte Lynx signature", FormatOptionKind.String, LynxWriter.DefaultSignature,
      Description: "Exactly 24 printable ASCII characters containing LYNX."),
  ];

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = Open(stream);
    foreach (var entry in reader.Entries) {
      if (files is not null && files.Length > 0 && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Performs the open entry operation.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var reader = Open(archive);
    var entry = reader.Entries.FirstOrDefault(candidate =>
      string.Equals(candidate.Name, entryName, StringComparison.OrdinalIgnoreCase));
    var data = entry is null ? [] : reader.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  /// <summary>
  /// Performs the extract entry to memory operation.
  /// </summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var entry = this.OpenEntry(archive, entryName, password);
    using var memory = new MemoryStream();
    entry.CopyTo(memory);
    return memory.ToArray();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
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
  /// Removes all live entries in one pass while preserving the input archive's BASIC preamble
  /// and Lynx signature. The empty directory is emitted at its minimum one-block allocation.
  /// </summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("Lynx purge requires a readable, writable, seekable stream.", nameof(archive));

    var reader = Open(archive);
    var directory = LynxWriter.BuildDirectory([], 1, reader.BasicHeader, reader.Signature);
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(directory);
    archive.SetLength(directory.Length);
    archive.Flush();
    archive.Position = 0;
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

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException("Lynx has an implicit sequential layout; only ConsolidateAtStart is meaningful.");
    this.Defragment(archive);
  }

  /// <summary>
  /// Rebuilds the directory at its smallest whole-254-byte allocation and copies the complete
  /// existing data area byte-for-byte. This preserves REL side sectors and file-type metadata,
  /// while reclaiming directory blocks left behind after removals and any trailing transport data.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (ReferenceEquals(input, output))
      throw new ArgumentException("Lynx shrink requires distinct input and output streams.", nameof(output));
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("Lynx shrink input must be readable and seekable.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("Lynx shrink output must be writable and seekable.", nameof(output));

    var reader = Open(input);
    var directory = LynxWriter.BuildDirectory(
      reader.Entries.Select(LynxWriter.FromEntry).ToArray(),
      1,
      reader.BasicHeader,
      reader.Signature);

    output.Position = 0;
    output.SetLength(0);
    output.Write(directory);

    input.Position = reader.DataStart;
    var remaining = reader.LogicalDataEnd - reader.DataStart;
    var buffer = new byte[64 * 1024];
    while (remaining > 0) {
      var count = (int)Math.Min(buffer.Length, remaining);
      input.ReadExactly(buffer.AsSpan(0, count));
      output.Write(buffer.AsSpan(0, count));
      remaining -= count;
    }

    output.SetLength(output.Position);
    output.Flush();
    output.Position = 0;
  }

  /// <summary>
  /// Enumerates the exact byte layout: live directory text, directory padding, REL side sectors,
  /// logical file bytes, per-file block padding, and any trailer beyond the archive allocation.
  /// Explicit free extents make generic forensic wiping safe for this format.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    var reader = Open(archive);

    if (reader.DirectoryContentEnd > 0)
      yield return new DefragBlockInfo(0, reader.DirectoryContentEnd, DefragBlockKind.MetadataReserved, "Lynx directory");
    if (reader.DirectoryContentEnd < reader.DataStart)
      yield return new DefragBlockInfo(
        reader.DirectoryContentEnd,
        reader.DataStart - reader.DirectoryContentEnd,
        DefragBlockKind.Free,
        "Directory padding");

    foreach (var entry in reader.Entries) {
      if (entry.DataOffset > entry.AllocationOffset)
        yield return new DefragBlockInfo(
          entry.AllocationOffset,
          entry.DataOffset - entry.AllocationOffset,
          DefragBlockKind.MetadataReserved,
          $"{entry.Name} REL side sectors");

      if (entry.Length > 0)
        yield return new DefragBlockInfo(entry.DataOffset, entry.Length, DefragBlockKind.Used, entry.Name);

      var allocationEnd = checked(entry.AllocationOffset + (long)entry.ArchiveBlocks * LynxReader.BlockSize);
      var payloadEnd = checked(entry.DataOffset + entry.Length);
      if (payloadEnd < allocationEnd)
        yield return new DefragBlockInfo(
          payloadEnd,
          allocationEnd - payloadEnd,
          DefragBlockKind.Free,
          $"{entry.Name} block padding");
    }

    if (reader.LogicalDataEnd < archive.Length)
      yield return new DefragBlockInfo(
        reader.LogicalDataEnd,
        archive.Length - reader.LogicalDataEnd,
        DefragBlockKind.Free,
        "Trailing transport data");
  }

  /// <summary>
  /// Zeros only byte ranges that the Lynx layout proves unused: directory padding, payload block
  /// padding and trailing transport data. Live file bytes and REL side-sector metadata are retained.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Lynx wipe requires a readable, writable, seekable stream.", nameof(image));

    var imageSize = image.Length;
    var layout = this.EnumerateLayout(image).ToArray();
    return UnusedSpaceWiper.WipeDeclaredFree(
      image,
      layout,
      imageSize,
      wipeClusterTips: false,
      fileSizeLookup: null);
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
