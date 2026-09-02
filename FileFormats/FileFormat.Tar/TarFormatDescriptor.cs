#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Tar;

/// <summary>
/// Unix tape archive (tar) — 512-byte header blocks; ustar/GNU/pax variants; container only, no compression.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html</c> — POSIX pax — defines the ustar header and pax extended headers</description></item>
///   <item><description><c>https://www.gnu.org/software/tar/manual/</c> — GNU tar manual — GNU extensions (long names, sparse files)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Tar_(computing)</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class TarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatValidator, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty, IArchiveShrinkable, IFormatOptionsSchema {

  /// <inheritdoc />
  /// <summary>
  /// Gets the options schema.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("BlockingFactor", "Blocking factor", FormatOptionKind.Integer, "20",
      AllowedValues: ["1", "10", "20"],
      Description: "Output is padded to N × 512-byte blocks. 20 = classic 10 KiB record."),
    new("Format", "Header format", FormatOptionKind.Enum, "ustar",
      AllowedValues: ["ustar", "gnu", "pax"],
      Description: "TAR header format. ustar = POSIX, gnu = GNU extensions for long names, pax = extended headers."),
  ];

  /// <summary>Rebuild-based defrag: extracts then re-creates the TAR archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the TAR archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new TarReader(stream);
        var list = new List<(string, byte[])>();
        while (r.GetNextEntry() is { } e) {
          if (e.IsDirectory) { r.Skip(); continue; }
          using var es = r.GetEntryStream();
          var data = new byte[e.Size];
          es.ReadExactly(data);
          list.Add((e.Name, data));
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new TarWriter(ms);
        foreach (var (n, d) in files)
          w.AddEntry(new TarEntry { Name = n, Size = d.Length }, d);
        w.Finish();
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => TarLayoutMap.Enumerate(archive);

  /// <summary>
  /// Zeros every dead byte in the TAR archive: intra-block padding after each
  /// entry's data (the 512-byte alignment slack), and any junk trailing the
  /// two-block end-of-archive marker. Header blocks, file data and the
  /// terminator are live and preserved, so every entry still extracts
  /// byte-identically. Cluster-tip wiping is N/A — the layout map already
  /// classifies per-entry alignment padding as Free.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = TarLayoutMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <summary>
  /// TAR has no fixed media geometry, so the only canonical size is the
  /// archive's minimal terminated length: every live entry plus the two-block
  /// zero terminator, rounded up to the configured blocking factor.
  /// </summary>
  public IReadOnlyList<long> CanonicalSizes => [0];

  /// <summary>
  /// Drops any bytes trailing the end-of-archive marker (left behind by
  /// truncating writers, tape padding, or external concatenation) by walking
  /// the entries and re-terminating at the minimal length. File data is copied
  /// through byte-identically — no header is rewritten, so the output is the
  /// exact prefix of the input up to and including the terminator (padded to
  /// the blocking factor). When the input already has no trailing junk the
  /// output is byte-identical to the input.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    input.Position = 0;

    // The end-of-archive marker is the only MetadataReserved tile the layout
    // map emits whose name starts with "End". Its offset+length is the first
    // byte past all live content; everything beyond is trailing junk.
    long liveEnd = -1;
    foreach (var b in TarLayoutMap.Enumerate(input)) {
      if (b.Kind == DefragBlockKind.MetadataReserved &&
          b.FileName is { } n && n.StartsWith("End", StringComparison.Ordinal)) {
        liveEnd = b.Offset + b.Length;
        break;
      }
    }

    // No terminator found (degenerate / un-terminated archive): keep every byte.
    if (liveEnd < 0) liveEnd = input.Length;

    // Pad the live prefix up to a 512-byte block boundary — the minimal valid
    // TAR alignment. The on-disk blocking factor is unrecoverable from the byte
    // stream, so we never re-impose a larger record (which would grow a tight
    // archive); excess zero-block padding beyond the two-block terminator is
    // legitimately reclaimable. Every well-formed TAR is already 512-aligned, so
    // a tight archive's kept length equals its full length (idempotent).
    var record = (long)TarConstants.BlockSize;
    var padded = (liveEnd + record - 1) / record * record;
    var keep = Math.Min(padded, input.Length);

    input.Position = 0;
    var buf = new byte[64 * 1024];
    var remaining = keep;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      var read = input.Read(buf, 0, chunk);
      if (read == 0) break;
      output.Write(buf, 0, read);
      remaining -= read;
    }
    // If the live prefix is shorter than the padded length (input was already
    // truncated below the blocking boundary) top up with zero blocks.
    if (remaining > 0) {
      var zero = new byte[(int)Math.Min(buf.Length, remaining)];
      while (remaining > 0) {
        var chunk = (int)Math.Min(zero.Length, remaining);
        output.Write(zero, 0, chunk);
        remaining -= chunk;
      }
    }
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Tar";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "TAR";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing TAR archive.
  /// Uses <see cref="TarModifier"/> for true random-access I/O — Add is
  /// O(touched bytes) (append before terminator); Remove is O(image-size-after-target)
  /// because TAR has no central directory and trailing entries must be shifted.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      TarModifier.RemoveFile(archive, name, wipeData: true);
      TarModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes named entries from an existing TAR archive. Uses
  /// <see cref="TarModifier"/> for in-place compaction.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      TarModifier.RemoveFile(archive, name, wipeData: true);
  }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".tar";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".tar"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x75, 0x73, 0x74, 0x61, 0x72], Offset: 257, Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tar", "TAR")];
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
  public string Description => "Unix tape archive, no compression, container only";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TarReader(stream);
    var entries = new List<ArchiveEntryInfo>();
    var i = 0;
    while (r.GetNextEntry() is { } e) {
      entries.Add(new(i++, e.Name, e.Size, e.Size, "tar", e.IsDirectory, false, e.ModifiedTime.DateTime));
      r.Skip();
    }
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TarReader(stream);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.Name, files)) { r.Skip(); continue; }
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); r.Skip(); continue; }
      // Stream the entry straight to disk: an entry past the array limit cannot
      // be materialised, and there is no reason to buffer the smaller ones either.
      using (var target = CreateEntryFile(outputDir, e.Name))
        r.CopyEntryDataTo(target);
    }
  }

  /// <summary>
  /// Opens a single TAR entry as a read-only <see cref="Stream"/> bounded
  /// to its data size. TAR is positional — each entry's data starts at a
  /// known offset followed by 512-byte block padding. The
  /// <see cref="BoundedEntryStream"/> wrapper guarantees the padding and
  /// next entry's header bytes are physically unreachable through the
  /// returned view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new TarReader(archive, leaveOpen: true);
    while (r.GetNextEntry() is { } e) {
      if (e.IsDirectory) { r.Skip(); continue; }
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) {
        r.Skip();
        continue;
      }
      var es = r.GetEntryStream();
      return new BoundedEntryStream(es, e.Size, leaveOpen: false);
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
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var blockingFactor = options.GetOptionInt("BlockingFactor", 20);
    var formatName = options.GetOption("Format", "ustar").ToLowerInvariant();
    var headerFormat = formatName switch {
      "gnu" => TarHeaderFormat.Gnu,
      "pax" => TarHeaderFormat.Pax,
      _ => TarHeaderFormat.Ustar,
    };
    var w = new TarWriter(output, leaveOpen: false, format: headerFormat, blockingFactor: blockingFactor);
    foreach (var i in inputs) {
      if (i.IsDirectory) {
        w.AddEntry(new TarEntry { Name = i.ArchiveName, Size = 0, TypeFlag = (byte)'5' }, []);
      } else {
        // ReadContent() transparently handles both on-disk inputs and the
        // in-memory variant fed by the small-image ConvertArchive pipeline.
        var data = i.ReadContent();
        w.AddEntry(new TarEntry { Name = i.ArchiveName, Size = data.Length }, data);
      }
    }
    w.Finish();
  }

  /// <summary>
  /// Large-file-safe streaming variant of <see cref="Create"/>. TAR encodes
  /// each entry's size in its header before any payload byte, so the
  /// pre-known <see cref="StreamingArchiveInput.Size"/> lets the writer emit
  /// the header and then copy the payload in 64 KB chunks via
  /// <see cref="TarWriter.AddStreamingEntry"/> — peak memory is bounded by the
  /// copy buffer regardless of entry size. Output is byte-identical to
  /// <see cref="Create"/> for the same inputs.
  /// </summary>
  public void CreateFromStreams(Stream target, IEnumerable<StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);
    var blockingFactor = options.GetOptionInt("BlockingFactor", 20);
    var formatName = options.GetOption("Format", "ustar").ToLowerInvariant();
    var headerFormat = formatName switch {
      "gnu" => TarHeaderFormat.Gnu,
      "pax" => TarHeaderFormat.Pax,
      _ => TarHeaderFormat.Ustar,
    };
    var w = new TarWriter(target, leaveOpen: false, format: headerFormat, blockingFactor: blockingFactor);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddEntry(new TarEntry { Name = input.Name, Size = 0, TypeFlag = (byte)'5' }, []);
      } else {
        using var src = input.OpenStream();
        w.AddStreamingEntry(new TarEntry { Name = input.Name }, input.Size, src);
      }
    }
    w.Finish();
  }

  // ── IFormatValidator ─────────────────────────────────────────────

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < TarConstants.BlockSize) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "TAR_TOO_SHORT",
        "File too short for TAR header (need 512 bytes minimum)"));
      return new() { IsValid = false, Confidence = 0.3, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Header, Issues = issues };
    }
    // Verify ustar magic at offset 257
    if (header.Length > 262) {
      var magic = System.Text.Encoding.ASCII.GetString(header.Slice(257, 5));
      if (magic != TarConstants.UstarMagic) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Warning, "TAR_NO_USTAR",
          $"No ustar magic at offset 257 (got '{magic}')"));
      }
    }
    // Verify header checksum
    var storedChecksum = ParseOctal(header.Slice(148, 8));
    if (storedChecksum >= 0) {
      // Compute checksum: sum of all bytes with checksum field treated as spaces (0x20)
      var computed = 0;
      for (var i = 0; i < TarConstants.BlockSize; ++i) {
        computed += (i >= 148 && i < 156) ? (byte)' ' : header[i];
      }
      if (computed != storedChecksum) {
        issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, "TAR_BAD_CHECKSUM",
          $"Header checksum mismatch: stored={storedChecksum}, computed={computed}"));
        return new() { IsValid = false, Confidence = 0.4, Health = FormatHealth.Damaged,
          Level = ValidationLevel.Header, Issues = issues };
      }
    }
    // Check type flag is known
    var typeFlag = header[156];
    if (typeFlag != 0 && typeFlag != (byte)'0' && typeFlag != (byte)'1' && typeFlag != (byte)'2' &&
        typeFlag != (byte)'3' && typeFlag != (byte)'4' && typeFlag != (byte)'5' && typeFlag != (byte)'6' &&
        typeFlag != (byte)'7' && typeFlag != (byte)'L' && typeFlag != (byte)'K' &&
        typeFlag != (byte)'x' && typeFlag != (byte)'g' && typeFlag != (byte)'M' && typeFlag != (byte)'S') {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Info, "TAR_UNUSUAL_TYPEFLAG",
        $"Unusual type flag: '{(char)typeFlag}' (0x{typeFlag:X2})", 156));
    }
    if (fileSize % TarConstants.BlockSize != 0) {
      issues.Add(new(ValidationLevel.Header, IssueSeverity.Info, "TAR_UNALIGNED",
        $"File size {fileSize} is not a multiple of 512 bytes"));
    }
    var confidence = issues.Any(i => i.Severity == IssueSeverity.Warning) ? 0.80 :
      issues.Any(i => i.Severity == IssueSeverity.Info) ? 0.88 : 0.92;
    var health = issues.Any(i => i.Severity >= IssueSeverity.Warning) ? FormatHealth.Degraded : FormatHealth.Good;
    return new() { IsValid = true, Confidence = confidence, Health = health,
      Level = ValidationLevel.Header, Issues = issues };
  }

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
  public ValidationResult ValidateStructure(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var r = new TarReader(stream);
      var entryCount = 0;
      while (r.GetNextEntry() is { } e) {
        ++entryCount;
        r.Skip();
      }
      return new() { IsValid = true, Confidence = 0.93, Health = FormatHealth.Good,
        Level = ValidationLevel.Structure, Issues = issues,
        ValidEntries = entryCount, TotalEntries = entryCount };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Structure, IssueSeverity.Error, "TAR_STRUCTURE_FAILED",
        $"TAR structure parse failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.6, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Structure, Issues = issues };
    }
  }

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
  public ValidationResult ValidateIntegrity(Stream stream) {
    var issues = new List<ValidationIssue>();
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var r = new TarReader(stream);
      var validEntries = 0;
      var totalEntries = 0;
      while (r.GetNextEntry() is { } e) {
        ++totalEntries;
        try {
          if (e.IsDirectory) { r.Skip(); ++validEntries; continue; }
          using var es = r.GetEntryStream();
          // Read all data to verify no truncation
          var remaining = e.Size;
          var buf = new byte[8192];
          while (remaining > 0) {
            var toRead = (int)Math.Min(buf.Length, remaining);
            var read = es.Read(buf, 0, toRead);
            if (read == 0) {
              issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "TAR_ENTRY_TRUNCATED",
                $"Entry '{e.Name}': premature end of data (expected {e.Size} bytes)"));
              break;
            }
            remaining -= read;
          }
          if (remaining == 0) ++validEntries;
        } catch (Exception ex) {
          issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "TAR_ENTRY_FAILED",
            $"Entry '{e.Name}': {ex.Message}"));
        }
      }
      if (validEntries == totalEntries && issues.Count == 0) {
        return new() { IsValid = true, Confidence = 0.97, Health = FormatHealth.Perfect,
          Level = ValidationLevel.Integrity, Issues = issues,
          ValidEntries = validEntries, TotalEntries = totalEntries };
      }
      var health = validEntries == 0 ? FormatHealth.Damaged
        : validEntries < totalEntries ? FormatHealth.Degraded : FormatHealth.Good;
      return new() { IsValid = validEntries > 0, Confidence = validEntries > 0 ? 0.88 : 0.5,
        Health = health, Level = ValidationLevel.Integrity, Issues = issues,
        ValidEntries = validEntries, TotalEntries = totalEntries };
    } catch (Exception ex) {
      issues.Add(new(ValidationLevel.Integrity, IssueSeverity.Error, "TAR_INTEGRITY_FAILED",
        $"Integrity check failed: {ex.Message}"));
      return new() { IsValid = false, Confidence = 0.5, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Integrity, Issues = issues };
    }
  }

  private static long ParseOctal(ReadOnlySpan<byte> data) {
    long result = 0;
    foreach (var b in data) {
      if (b == 0 || b == (byte)' ') break;
      if (b < (byte)'0' || b > (byte)'7') return -1;
      result = (result << 3) | (long)(b - (byte)'0');
    }
    return result;
  }
}
