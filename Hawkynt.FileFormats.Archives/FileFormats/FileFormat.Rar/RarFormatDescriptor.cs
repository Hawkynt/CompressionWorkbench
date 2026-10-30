#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Rar;

/// <summary>
/// RAR archive reader/writer with selectable RAR 1.5, RAR4 and RAR5 creation targets.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rarlab.com/technote.htm</c> — RAR 5.0 archive format technote (RARLAB, official)</description></item>
///   <item><description>RAR 1.5-4.x technical notes / unrar source distribution — legacy container and decoder compatibility</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/RAR_(file_format)</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class RarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveLayoutMap, IFormatOptionsSchema {

  /// <inheritdoc />
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
  /// Gets the methods. The legacy rar4/rar5/rar15 names remain accepted as compatibility aliases.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("rar5", "RAR 5"), new("rar4", "RAR 4"), new("rar15", "RAR 1.5 (store)"), new("store", "Store")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "RAR archive with explicit writer compatibility targets. RAR 5 and RAR 4 use the managed " +
    "compression paths; RAR 1.5 creation currently emits fully compatible stored members with UNP_VER=15. " +
    "Pure RAR5 additions/removals use random-access block editors, while unsupported profiles rebuild.";

  /// <summary>Writer-generation constraint shown independently of the compression method.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: FormatOptionKeys.TargetCompatibility,
      DisplayName: "Target compatibility",
      Kind: FormatOptionKind.Enum,
      Default: nameof(RarCompatibility.Rar5),
      AllowedValues: [nameof(RarCompatibility.Rar5), nameof(RarCompatibility.Rar4), nameof(RarCompatibility.Rar1_5)],
      Description: "Select the oldest RAR generation that must read the archive. Rar1_5 currently writes stored, non-solid, unencrypted members; RAR 1.3 and older use a different RE~^ format and are not claimed here.",
      IsOptimizationAxis: false),
    new FormatOptionDescriptor(
      Key: FormatOptionKeys.VolumeSize,
      DisplayName: "Volume size",
      Kind: FormatOptionKind.String,
      Default: "",
      Description: "Split into volumes of at most this size (bytes, or with k/m/g, like rar -v). A target named name.partN.rar gets name.part2.rar, ...; any other name the older name.r00, name.r01, ... scheme. Empty writes one file.",
      IsOptimizationAxis: false),
  ];

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new RarReader(stream, password: password, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      MethodLabel(r, e), e.IsDirectory, e.IsEncrypted, e.ModifiedTime?.LocalDateTime,
      IsSymlink: e.IsSymlink, LinkTarget: e.IsSymlink ? e.LinkTarget : null)).ToList();
  }

  private static string MethodLabel(RarReader r, RarEntry e) {
    var m = e.CompressionMethod == 0 ? "Store" : $"m{e.CompressionMethod}";
    return r.IsRar5 ? $"RAR5 {m}" : $"RAR4 {m}";
  }

  /// <summary>
  /// Extracts entries, streaming each to its file; a file whose checksum fails is deleted
  /// and the failure thrown. Symbolic links are listed, not created; NTFS alternate streams
  /// are written only when asked for by name.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new RarReader(stream, password: password, leaveOpen: true);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.StreamName != null && files == null) continue;
      if (e.IsSymlink) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      string? path = null;
      try {
        using (var target = CreateEntryFile(outputDir, e.Name)) {
          path = target.Name;
          r.Extract(i, target);
        }
        if (e.ModifiedTime is { } mt)
          try { File.SetLastWriteTimeUtc(path, mt.UtcDateTime); } catch (IOException) { } catch (UnauthorizedAccessException) { }
      } catch {
        if (path != null) try { File.Delete(path); } catch (IOException) { }
        throw;
      }
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
    using var r = new RarReader(archive, leaveOpen: true, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (e.IsDirectory || e.IsSymlink) continue;
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
  /// Builds a RAR archive from <paramref name="inputs"/>. <see cref="FormatOptionKeys.TargetCompatibility"/>
  /// selects the container generation independently of compression level. Legacy MethodName values
  /// <c>rar5</c>, <c>rar4</c> and <c>rar15</c> remain compatibility aliases when the explicit option is absent.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => this.CreateCore(output, inputs.Select(ToStreaming).ToList(), options);

  /// <summary>Streams each input into the archive, so files of any size need bounded memory.</summary>
  public void CreateFromStreams(Stream target, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options)
    => this.CreateCore(target, inputs.ToList(), options);

  private static StreamingArchiveInput ToStreaming(ArchiveInputInfo i) {
    if (i.IsDirectory)
      return new StreamingArchiveInput(i.ArchiveName, 0, true, () => Stream.Null, i.Metadata);
    if (i.InMemoryContent is { } bytes)
      return new StreamingArchiveInput(i.ArchiveName, bytes.LongLength, false, () => new MemoryStream(bytes, writable: false), i.Metadata);
    var info = new FileInfo(i.FullPath);
    var meta = i.Metadata ?? new ArchiveEntryMetadata(info.CreationTimeUtc, info.LastAccessTimeUtc, info.LastWriteTimeUtc);
    return new StreamingArchiveInput(i.ArchiveName, info.Length, false, () => File.OpenRead(i.FullPath), meta);
  }

  /// <summary>
  /// Volume size from the <see cref="FormatOptionKeys.VolumeSize"/> option: bytes, or a number with a
  /// k/m/g suffix (powers of 1024), as rar's <c>-v</c> takes it; 0 or absent means one file.
  /// </summary>
  internal static long ParseVolumeSize(string? text) {
    if (string.IsNullOrWhiteSpace(text)) return 0;
    var t = text.Trim();
    var shift = char.ToLowerInvariant(t[^1]) switch { 'k' => 10, 'm' => 20, 'g' => 30, 't' => 40, 'b' => 0, _ => -1 };
    if (shift >= 0) t = t[..^1];
    else shift = 0;
    if (!long.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) || v > long.MaxValue >> shift)
      throw new ArgumentException($"RAR volume size '{text}' is not a size.");
    return v << shift;
  }

  /// <summary>
  /// The path the first volume is being written to, when <paramref name="output"/> is a file:
  /// the target behind an atomic-write temp name (<c>name.tmp.xxxxxxxx</c>) is what the
  /// other volumes are named after.
  /// </summary>
  private static string? OutputPath(Stream output) {
    if (output is not FileStream { Name: { Length: > 0 } name }) return null;
    var m = System.Text.RegularExpressions.Regex.Match(name, @"^(?<t>.+)\.tmp\.[0-9a-fA-F]{8}$");
    return m.Success ? m.Groups["t"].Value : name;
  }

  private void CreateCore(Stream output, IReadOnlyList<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    var target = ResolveCompatibility(options);
    var useStore = target == RarCompatibility.Rar1_5 || options.MethodName is "store" or "copy";
    var rarLevel = useStore ? 0 : options.Level switch {
      0 => 0, 1 => 1, 2 => 2, 3 or 4 => 3, 5 or 6 => 4, >= 7 => 5, _ => 3,
    };
    var volumeSize = ParseVolumeSize(options.GetString(FormatOptionKeys.VolumeSize));
    Func<int, Stream>? openVolume = null;
    var newNaming = false;
    if (volumeSize > 0) {
      var path = OutputPath(output) ?? throw new ArgumentException("A RAR volume set must be written to a file, so its other volumes can go beside it.");
      newNaming = System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"\.part[0-9]+\.rar$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      var names = RarVolumeNames.ForWriting(path, newNaming);
      openVolume = n => File.Create(names(n));
    }
    var solid = options.SolidSize == 0 && !useStore;
    if (target == RarCompatibility.Rar1_5 && (!string.IsNullOrEmpty(options.Password) || options.EncryptFilenames || !string.IsNullOrEmpty(options.EncryptionMethod)))
      throw new NotSupportedException("RAR 1.5 compatibility currently supports unencrypted archives only.");
    if (target != RarCompatibility.Rar5 && options.EncryptFilenames && !string.IsNullOrEmpty(options.Password))
      throw new NotSupportedException("Encrypted file names (rar -hp) are written for RAR 5 archives only.");

    static RarWriter.ArchiveTimes Times(StreamingArchiveInput i)
      => new(i.Metadata?.LastWriteTimeUtc, i.Metadata?.CreationTimeUtc, i.Metadata?.LastAccessTimeUtc);

    switch (target) {
      case RarCompatibility.Rar1_5:
      case RarCompatibility.Rar4: {
        var windowBits = options.DictSize > 0 ? Math.Clamp((int)Math.Log2(options.DictSize), 16, 22) : 22;
        using var w4 = new Rar4Writer(output, leaveOpen: true,
          method: target == RarCompatibility.Rar1_5 ? RarConstants.Rar4MethodStore : (byte)(0x30 + rarLevel),
          windowBits: target == RarCompatibility.Rar1_5 ? 16 : windowBits,
          solid: target == RarCompatibility.Rar4 && solid,
          password: target == RarCompatibility.Rar4 ? options.Password : null,
          targetCompatibility: target, volumeSize: volumeSize, openVolume: openVolume, newVolumeNaming: newNaming);
        foreach (var i in inputs) {
          if (i.IsDirectory) { w4.AddDirectory(i.Name, Times(i)); continue; }
          using var s = i.OpenStream();
          w4.AddFile(i.Name, s, i.Size, Times(i));
        }
        w4.Finish();
        break;
      }
      case RarCompatibility.Rar5: {
        var dictLog = options.DictSize > 0
          ? Math.Clamp((int)Math.Ceiling(Math.Log2(options.DictSize)), 17, 26)
          : rarLevel switch { <= 1 => 20, 2 => 22, 3 => 22, 4 => 24, _ => 25 };
        using var w = new RarWriter(output, leaveOpen: true, method: rarLevel, dictionarySizeLog: dictLog,
          solid: solid, password: options.Password, encryptHeaders: options.EncryptFilenames,
          volumeSize: volumeSize, openVolume: openVolume);
        foreach (var i in inputs) {
          if (i.IsDirectory) { w.AddDirectory(i.Name, Times(i)); continue; }
          using var s = i.OpenStream();
          w.AddFile(i.Name, s, i.Size, Times(i));
        }
        w.Finish();
        break;
      }
      default:
        throw new ArgumentOutOfRangeException(nameof(options), target, "Unknown RAR compatibility target.");
    }
  }

  private static RarCompatibility ResolveCompatibility(FormatCreateOptions options) {
    if (options.GetString(FormatOptionKeys.TargetCompatibility) is { } raw
        && Enum.TryParse<RarCompatibility>(raw, ignoreCase: true, out var parsed))
      return parsed;

    return options.MethodName?.ToLowerInvariant() switch {
      "rar15" or "rar1.5" or "rar1_5" => RarCompatibility.Rar1_5,
      "rar4" => RarCompatibility.Rar4,
      _ => RarCompatibility.Rar5,
    };
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
