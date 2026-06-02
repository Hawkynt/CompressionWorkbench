#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zip;

public sealed class ZipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IFormatValidator, IArchiveModifiable, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty, IFormatOptionsSchema {

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Method", "Compression method", FormatOptionKind.Enum, "deflate",
      AllowedValues: ["deflate", "store", "deflate64", "bzip2", "lzma", "zstd"]),
    new("Level", "Compression level", FormatOptionKind.Integer, "5",
      AllowedValues: ["0", "1", "3", "5", "7", "9"],
      Description: "Compression level. 0 = fastest, 9 = max."),
    new("Password", "Password", FormatOptionKind.String, "",
      Description: "Optional ZipCrypto / AES password."),
    new("EncryptionMethod", "Encryption method", FormatOptionKind.Enum, "none",
      AllowedValues: ["none", "zipcrypto", "aes-128", "aes-192", "aes-256"]),
  ];

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => ZipLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag: extracts every entry then re-creates the archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts every entry then re-creates the archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ZipReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }

  public string Id => "Zip";
  public string DisplayName => "ZIP";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories |
    FormatCapabilities.SupportsOptimize;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ZIP archive. Uses
  /// <see cref="ZipModifier"/> for true O(touched bytes) random-access I/O —
  /// only the central directory, the EOCD, and the appended file's local
  /// file header + compressed data are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ZipModifier.RemoveFile(archive, name, wipeData: true);
      ZipModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes named entries from an existing ZIP archive. Uses
  /// <see cref="ZipModifier"/> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ZipModifier.RemoveFile(archive, name, wipeData: true);
  }

  public string DefaultExtension => ".zip";
  public IReadOnlyList<string> Extensions => [".zip", ".zipx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'P', (byte)'K', 0x03, 0x04], Confidence: 0.95),
    new([(byte)'P', (byte)'K', 0x05, 0x06], Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("deflate", "Deflate", SupportsOptimize: true),
    new("store", "Store"), new("deflate64", "Deflate64"),
    new("bzip2", "BZip2"), new("lzma", "LZMA"), new("zstd", "Zstandard"), new("ppmd", "PPMd")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Universal archive with multiple compression methods";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZipReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  /// <summary>
  /// Opens a single ZIP entry as a read-only <see cref="Stream"/> bounded
  /// to its uncompressed size. The DEFLATE / store / etc. decoder runs
  /// against the entry's local-header bytes, the result is wrapped in a
  /// <see cref="BoundedEntryStream"/> sized to <c>UncompressedSize</c> so
  /// the next entry's bytes — which immediately follow in the source —
  /// can never bleed into the returned view even if the underlying decoder
  /// over-reads by a chunk boundary.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new ZipReader(archive, leaveOpen: true, password: password);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      // ZipReader.ExtractEntry already produces exactly UncompressedSize
      // bytes (CRC-checked) — wrapping in a bounded view is belt-and-braces
      // so callers can rely on the universal contract.
      var bytes = r.ExtractEntry(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>
  /// Native in-memory single-entry extraction. Routed through the bounded
  /// <see cref="OpenEntry"/> so the per-entry isolation contract holds
  /// uniformly across descriptors.
  /// </summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Builds a ZIP archive from <paramref name="inputs"/>. Honors all of
  /// <see cref="FormatCreateOptions"/>: method, level, dict-size, threads,
  /// password, encryption mode, and incompressibility hints.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // Three-tier fallback: FormatSpecific (schema) → direct property → hardcoded default.
    // The direct properties remain authoritative when set (legacy tests), but
    // when they're null/zero the schema knobs from FormatSpecific take over.
    var methodName = options.MethodName ?? options.GetOption("Method", "deflate");
    var passwordFromSchema = options.GetOption("Password", "");
    var password = !string.IsNullOrEmpty(options.Password) ? options.Password
      : !string.IsNullOrEmpty(passwordFromSchema) ? passwordFromSchema : null;
    var encryptionName = options.EncryptionMethod ?? options.GetOption("EncryptionMethod", "none");
    // Resolve explicit Level: direct property wins; otherwise fall back to schema
    // (sentinel -1 means "use the method default"). Hardcoded default stays in
    // ZipOptionsResolver.ResolveMethod.
    var explicitLevel = options.Level ?? (options.GetOptionInt("Level", -1) is var lv && lv >= 0 ? lv : (int?)null);

    var (zipMethod, level) = ZipOptionsResolver.ResolveMethod(methodName, options.Optimize);
    if (explicitLevel.HasValue)
      level = ZipOptionsResolver.ResolveDeflateLevel(explicitLevel.Value, options.Optimize);
    var encryption = ZipOptionsResolver.ResolveEncryption(encryptionName);

    if (options.Threads > 1 && inputs.Count(i => !i.IsDirectory) > 1) {
      ParallelZipCreator.CreateZipParallel(output, inputs, password, zipMethod, level,
        options.IncompressiblePaths, options.Threads, encryption);
      return;
    }

    var w = new ZipWriter(output, leaveOpen: true, compressionLevel: level,
      password: password, encryptionMethod: encryption);

    if (options.DictSize > 0 && zipMethod == ZipCompressionMethod.Lzma)
      w.LzmaDictionarySize = ZipOptionsResolver.NormalizeDictSize(
        ZipOptionsResolver.ResolveLzmaDictSize(options.DictSize, options.Optimize));
    w.LzmaLevel = ZipOptionsResolver.ResolveLzmaLevel(options.Level, options.Optimize);
    if (options.WordSize.HasValue && zipMethod == ZipCompressionMethod.Ppmd)
      w.PpmdOrder = Math.Clamp(options.WordSize.Value, 2, 16);
    if (options.DictSize > 0 && zipMethod == ZipCompressionMethod.Ppmd)
      w.PpmdMemorySizeMB = Math.Clamp((int)(options.DictSize / (1024 * 1024)), 1, 256);
    if (options.DictSize > 0 && zipMethod == ZipCompressionMethod.BZip2)
      w.Bzip2BlockSize = ZipOptionsResolver.ResolveBzip2BlockSize(options.DictSize);

    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.ArchiveName); continue; }
      // ReadContent() transparently handles both on-disk inputs and the
      // in-memory variant fed by the small-image ConvertArchive pipeline.
      var data = i.ReadContent();
      var entryMethod = options.IncompressiblePaths != null && options.IncompressiblePaths.Contains(i.FullPath)
        ? ZipCompressionMethod.Store
        : zipMethod;
      w.AddEntry(i.ArchiveName, data, entryMethod);
    }
    w.Finish();
  }

  // ── IFormatValidator ─────────────────────────────────────────────

  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < 30) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "ZIP_TOO_SHORT",
        "File too short for local file header (need 30 bytes minimum)"));
      return new() { IsValid = false, Confidence = 0.3, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    var sig = BitConverter.ToUInt32(header);
    if (sig != ZipConstants.LocalFileHeaderSignature && sig != ZipConstants.EndOfCentralDirectorySignature) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "ZIP_BAD_SIGNATURE",
        $"Invalid ZIP signature: 0x{sig:X8}", 0));
      return new() { IsValid = false, Confidence = 0.2, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    if (sig == ZipConstants.LocalFileHeaderSignature) {
      var versionNeeded = BitConverter.ToUInt16(header[4..]);
      if (versionNeeded > 63) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "ZIP_HIGH_VERSION",
          $"Version needed to extract is unusually high: {versionNeeded / 10}.{versionNeeded % 10}", 4));
      }
      var method = BitConverter.ToUInt16(header[8..]);
      if (method != 0 && method != 8 && method != 9 && method != 12 && method != 14 &&
          method != 93 && method != 98 && method != 99) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "ZIP_UNKNOWN_METHOD",
          $"Unknown compression method: {method}", 8));
      }
      var fnLen = BitConverter.ToUInt16(header[26..]);
      var exLen = BitConverter.ToUInt16(header[28..]);
      if (30 + fnLen + exLen > fileSize) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "ZIP_HEADER_OVERFLOW",
          "Local file header extends beyond file size"));
        return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Header, Issues = issues };
      }
    }
    if (fileSize < 22) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "ZIP_NO_EOCD",
        "File too short to contain end of central directory record (min 22 bytes)"));
    }
    var confidence = issues.Any(i => i.Severity == IssueSeverity.Warning) ? 0.80 : 0.90;
    var health = issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
    return new() { IsValid = true, Confidence = confidence, Health = health,
      Level = ValidationLevel.Header, Issues = issues };
  }

  public ValidationResult ValidateStructure(Stream stream) {
    var issues = new List<ValidationIssue>();
    int entryCount;
    try {
      var (cdOffset, cdSize, cdCount, _) = ZipEndOfCentralDirectory.Read(stream);
      entryCount = cdCount;
      if (cdOffset < 0 || cdOffset > stream.Length) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_CD_OFFSET_OOB",
          $"Central directory offset {cdOffset} is outside file bounds"));
        return new() { IsValid = false, Confidence = 0.6, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Structure, Issues = issues };
      }
      if (cdOffset + cdSize > stream.Length) {
        issues.Add(new(ValidationLevel.Structure, IssueSeverity.Warning, "ZIP_CD_TRUNCATED",
          $"Central directory extends beyond file (offset={cdOffset}, size={cdSize}, fileLen={stream.Length})"));
      }
      // Walk central directory entries
      stream.Position = cdOffset;
      var reader = new BinaryReader(stream, System.Text.Encoding.Latin1, leaveOpen: true);
      var validEntries = 0;
      for (var i = 0; i < cdCount; ++i) {
        if (stream.Position + 46 > stream.Length) {
          issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_CD_ENTRY_TRUNCATED",
            $"Central directory entry {i} truncated", stream.Position));
          break;
        }
        var entrySig = reader.ReadUInt32();
        if (entrySig != ZipConstants.CentralDirectorySignature) {
          issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_CD_BAD_SIG",
            $"Central directory entry {i}: bad signature 0x{entrySig:X8}", stream.Position - 4));
          break;
        }
        // Skip past the fixed fields to get name/extra/comment lengths
        stream.Position += 24; // skip to fnLen (offset 46-4-24 = fields after sig)
        var fnLen = reader.ReadUInt16();
        var exLen = reader.ReadUInt16();
        var cmtLen = reader.ReadUInt16();
        stream.Position += 12; // skip diskStart(2)+internalAttr(2)+externalAttr(4)+localHeaderOffset(4)
        stream.Position += fnLen + exLen + cmtLen;
        ++validEntries;
      }
      var confidence = issues.Count == 0 ? 0.92 : 0.75;
      var health = issues.Any(i => i.Severity >= IssueSeverity.Error) ? FormatHealth.Damaged
        : issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
      return new() { IsValid = health != FormatHealth.Damaged, Confidence = confidence, Health = health,
        Level = ValidationLevel.Structure, Issues = issues,
        ValidEntries = validEntries, TotalEntries = entryCount };
    } catch (InvalidDataException ex) {
      issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "ZIP_STRUCTURE_FAILED",
        $"Structure parse failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Structure, Issues = issues };
    }
  }

  /// <summary>
  /// Zeros all dead bytes in the ZIP archive: gaps between local file entries,
  /// orphan data left after <see cref="ZipModifier.RemoveFile"/>, and any
  /// padding regions not covered by the layout map.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = ZipLayoutMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  public ValidationResult ValidateIntegrity(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      var r = new ZipReader(stream);
      var validEntries = 0;
      var totalEntries = r.Entries.Count;
      foreach (var e in r.Entries) {
        if (e.IsDirectory) { ++validEntries; continue; }
        try {
          _ = r.ExtractEntry(e);
          ++validEntries;
        } catch (Exception ex) {
          issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "ZIP_ENTRY_EXTRACT_FAILED",
            $"Entry '{e.FileName}': {ex.Message}"));
        }
      }
      if (validEntries == totalEntries && issues.Count == 0) {
        return new() { IsValid = true, Confidence = 0.99, Health = FormatHealth.Perfect,
          Level = ValidationLevel.Integrity, Issues = issues,
          ValidEntries = validEntries, TotalEntries = totalEntries };
      }
      var health = validEntries == 0 ? FormatHealth.Damaged
        : validEntries < totalEntries ? FormatHealth.Degraded : FormatHealth.Good;
      return new() { IsValid = validEntries > 0, Confidence = validEntries > 0 ? 0.90 : 0.5,
        Health = health, Level = ValidationLevel.Integrity, Issues = issues,
        ValidEntries = validEntries, TotalEntries = totalEntries };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "ZIP_INTEGRITY_FAILED",
        $"Integrity check failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Integrity, Issues = issues };
    }
  }
}
