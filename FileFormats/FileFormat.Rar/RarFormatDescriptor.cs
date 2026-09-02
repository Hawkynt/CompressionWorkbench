#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rar;

/// <summary>
/// RAR archive (RAR4 and RAR5 container framing).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rarlab.com/technote.htm</c> — RAR 5.0 archive format technote (RARLAB, official)</description></item>
///   <item><description>unrar source distribution (rarlab.com) — de-facto reference for RAR4 decoding</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/RAR_(file_format)</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class RarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "RAR defragmentation is not supported — solid blocks, recovery records, and per-archive signatures " +
      "would all need to be regenerated, which is not safe via a generic rebuild path.");
    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  /// <inheritdoc />
    /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => RarLayoutMap.Enumerate(archive);

  /// <summary>Gets the id.</summary>
  public string Id => "Rar";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "RAR";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: supported RAR5 mutations go directly through the random-access block
  // editors. Profiles those editors cannot preserve safely use verified rebuild.
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".rar";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".rar"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00], Confidence: 0.95),
    new([(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00], Confidence: 0.95)
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [
    new("rar5", "RAR 5"), new("rar4", "RAR 4"), new("store", "Store")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "RAR archive with solid compression and recovery records. Pure RAR5 additions " +
    "append FILE blocks before ENDARC without copying existing packed data; supported " +
    "non-solid removals shift only the physical tail after removed blocks. Encrypted, " +
    "recovery/quick-open, solid-dependent, RAR4, directory-add, and same-name update " +
    "cases fall back to verified rebuild.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new RarReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      $"Method {e.CompressionMethod}", e.IsDirectory, e.IsEncrypted, e.ModifiedTime?.DateTime)).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new RarReader(stream, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }

  /// <summary>
  /// Opens a single RAR entry as a bounded read-only <see cref="Stream"/>.
  /// The reader's per-entry extractor returns the fully-decompressed bytes;
  /// they are wrapped in a <see cref="BoundedEntryStream"/> sized to the
  /// entry's uncompressed size so the universal per-entry isolation contract
  /// holds even though RAR's decoder produces a byte[].
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new RarReader(archive, leaveOpen: true, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(i);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction — routed through the
  /// bounded <see cref="OpenEntry"/> so the per-entry isolation contract holds
  /// uniformly.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Builds a RAR archive from <paramref name="inputs"/>. Selects RAR4 or RAR5
  /// based on <c>options.MethodName</c> and resolves dictionary / level from
  /// <c>options.DictSize</c> / <c>options.Level</c>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var useRar4 = options.MethodName == "rar4";
    var useStore = options.MethodName is "store" or "copy";
    var rarLevel = useStore ? 0 : options.Level switch {
      0 => 0, 1 => 1, 2 => 2, 3 or 4 => 3, 5 or 6 => 4, >= 7 => 5, _ => 3,
    };

    if (useRar4) {
      var windowBits = options.DictSize > 0
        ? Math.Clamp((int)Math.Log2(options.DictSize), 15, 22) : 20;
      var rar4Method = (byte)(0x30 + rarLevel);
      var w4 = new Rar4Writer(output, method: rar4Method, windowBits: windowBits,
        solid: options.SolidSize == 0, password: options.Password);
      foreach (var i in inputs) {
        if (i.IsDirectory) continue;
        w4.AddFile(i.ArchiveName, i.ReadContent());
      }
      w4.Finish();
    } else {
      var dictLog = options.DictSize > 0
        ? Math.Clamp((int)Math.Log2(options.DictSize), 17, 28) : 17;
      var w = new RarWriter(output, method: rarLevel, dictionarySizeLog: dictLog,
        solid: options.SolidSize == 0, password: options.Password,
        encryptHeaders: options.EncryptFilenames);
      foreach (var i in inputs) {
        if (i.IsDirectory) continue;
        w.AddFile(i.ArchiveName, i.ReadContent());
      }
      w.Finish();
    }
  }

  /// <summary>
  /// Appends new files directly to supported RAR5 archives. The in-place adder
  /// validates the complete block profile and all name collisions before its
  /// first write, so an unsupported profile can safely fall back without taking
  /// a whole-archive transaction snapshot. Same-name updates deliberately take
  /// the rebuild path because remove+add is a two-step transaction.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var hasDirectory = false;
    var newFiles = new List<(string Name, byte[] Data, DateTimeOffset? ModifiedTime)>();
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (input.IsDirectory) { hasDirectory = true; continue; }
      newFiles.Add((input.ArchiveName, input.ReadContent(), null));
    }

    if (!hasDirectory && newFiles.Count > 0) {
      try {
        archive.Position = 0;
        RarInPlaceAdder.Add(archive, newFiles);
        return;
      } catch (NotSupportedException) {
        if (archive.CanSeek)
          archive.Position = 0;
      }
    }

    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
        var dest = Path.Combine(tmpDir, input.ArchiveName.Replace('/', Path.DirectorySeparatorChar));
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        File.WriteAllBytes(dest, input.ReadContent());
      }
    });
  }

  /// <summary>
  /// Removes non-solid RAR5 FILE blocks directly. The remover performs all format,
  /// recovery/quick-open, encryption, ENDARC and solid-chain checks before its
  /// first byte move, so unsupported archives can fall back without cloning the
  /// entire container first. Cost is proportional to block headers plus the tail
  /// physically shifted after removed blocks.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    try {
      archive.Position = 0;
      RarInPlaceRemover.Remove(archive, entryNames);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    var skip = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(file);
      }
    });
  }
}
