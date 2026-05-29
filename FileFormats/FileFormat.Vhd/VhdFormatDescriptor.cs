#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vhd;

public sealed class VhdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IFilesystemExtentMap, IPartitionEditable {
  public string Id => "Vhd";
  public string DisplayName => "VHD";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vhd";
  public IReadOnlyList<string> Extensions => [".vhd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("conectix"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft VHD virtual hard disk";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (TryOpenVhdStream(stream) is { } vhdStream) {
      using (vhdStream) {
        var inner = InnerFsDetector.Detect(vhdStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vhdStream.Position = 0;
            return ops.List(vhdStream, password);
          } catch {
            // fall through to raw listing
          }
        }
      }
    }

    // Fallback: raw disk listing
    var r = new VhdReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null
    )).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fatImage = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    var w = new VhdWriter();
    w.SetDiskData(fatImage);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (TryOpenVhdStream(stream) is { } vhdStream) {
      using (vhdStream) {
        var inner = InnerFsDetector.Detect(vhdStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vhdStream.Position = 0;
            ops.Extract(vhdStream, outputDir, password, files);
            return;
          } catch {
            // fall through to raw extraction
          }
        }
      }
    }

    // Fallback: raw disk extraction
    var r = new VhdReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  // ── IArchiveLayoutMap ───────────────────────────────────────────────

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => VhdLayoutMap.Enumerate(archive);

  // ── IFilesystemExtentMap ────────────────────────────────────────────

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    if (TryOpenVhdStream(image) is { } vhdStream) {
      using (vhdStream) {
        var inner = InnerFsDetector.Detect(vhdStream);
        if (inner is IFilesystemExtentMap extentMap) {
          vhdStream.Position = 0;
          // Materialise to avoid use-after-dispose
          return extentMap.EnumerateExtents(vhdStream).ToList();
        }
      }
    }

    // Fallback: emit the container layout
    return VhdLayoutMap.Enumerate(image);
  }

  // ── IArchiveModifiable (inner-FS-aware) ────────────────────────────

  /// <inheritdoc />
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (TryDelegateModifiable(archive, out var vhdStream, out var modifiable) && vhdStream is not null && modifiable is not null) {
      using (vhdStream) {
        try {
          vhdStream.Position = 0;
          modifiable.Add(vhdStream, inputs);
          vhdStream.Flush();
          return;
        } catch {
          // fall through to rebuild
        }
      }
    }

    ModifyRebuilder.Add(archive, inputs, ReadDiskEntries, BuildImage);
  }

  /// <inheritdoc />
  public void Remove(Stream archive, string[] entryNames) {
    if (TryDelegateModifiable(archive, out var vhdStream, out var modifiable) && vhdStream is not null && modifiable is not null) {
      using (vhdStream) {
        try {
          vhdStream.Position = 0;
          modifiable.Remove(vhdStream, entryNames);
          vhdStream.Flush();
          return;
        } catch {
          // fall through to rebuild
        }
      }
    }

    ModifyRebuilder.Remove(archive, entryNames, ReadDiskEntries, BuildImage);
  }

  // ── IArchiveDefragmentable (inner-FS-aware) ────────────────────────

  /// <inheritdoc />
  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <inheritdoc />
  public void Defragment(Stream archive, DefragOptions options) {
    if (TryOpenVhdStream(archive) is { } vhdStream) {
      using (vhdStream) {
        var inner = InnerFsDetector.Detect(vhdStream);
        if (inner is IArchiveDefragmentable defrag) {
          try {
            vhdStream.Position = 0;
            defrag.Defragment(vhdStream, options);
            vhdStream.Flush();
            return;
          } catch {
            // fall through to rebuild
          }
        }
      }
    }

    DefragRebuilder.Rebuild(archive, options, ReadDiskEntries, BuildImage);
  }

  // ── IPartitionEditable ─────────────────────────────────────────────

  /// <inheritdoc />
  /// <remarks>
  /// Returns a <see cref="VhdStream"/> bound to <paramref name="image"/>. For
  /// fixed VHDs this is a direct pass-through view over bytes
  /// [0 .. fileLength-512), so partition-table edits land at the same
  /// byte offsets a real disk would expose. Dynamic VHDs are also supported
  /// (writes allocate new BAT blocks on demand) but partition-editing on a
  /// freshly-created dynamic VHD requires the virtual size to be large
  /// enough to hold any new partitions.
  /// </remarks>
  public Stream OpenGuestDiskStream(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite)
      throw new NotSupportedException("Partition editing requires a writable VHD stream.");
    return new VhdStream(image, leaveOpen: true);
  }

  // ── Private helpers ────────────────────────────────────────────────

  /// <summary>
  /// Tries to open a <see cref="VhdStream"/> for a VHD (fixed or dynamic).
  /// Returns <c>null</c> if the stream is not a valid VHD (too small, bad magic).
  /// The caller owns the returned stream and must dispose it.
  /// </summary>
  private static VhdStream? TryOpenVhdStream(Stream stream) {
    try {
      if (stream.Length < 512) return null;

      // Check the footer at EOF
      stream.Position = stream.Length - 512;
      Span<byte> magic = stackalloc byte[8];
      stream.ReadExactly(magic);
      if (!"conectix"u8.SequenceEqual(magic)) {
        // Could be a dynamic VHD with footer copy at offset 0
        stream.Position = 0;
        stream.ReadExactly(magic);
        if (!"conectix"u8.SequenceEqual(magic))
          return null;
      }

      stream.Position = 0;
      return new VhdStream(stream, leaveOpen: true);
    } catch {
      stream.Position = 0;
      return null;
    }
  }

  /// <summary>
  /// Tries to open the inner FS as an <see cref="IArchiveModifiable"/> via <see cref="VhdStream"/>.
  /// </summary>
  private static bool TryDelegateModifiable(Stream archive, out VhdStream? vhdStream, out IArchiveModifiable? modifiable) {
    vhdStream = null;
    modifiable = null;
    var vs = TryOpenVhdStream(archive);
    if (vs == null) return false;

    var inner = InnerFsDetector.Detect(vs);
    if (inner is IArchiveModifiable mod) {
      vhdStream = vs;
      modifiable = mod;
      return true;
    }

    vs.Dispose();
    return false;
  }

  // ── Rebuild-path delegates (fallback) ──────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadDiskEntries(Stream stream) {
    var r = new VhdReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      yield return (e.Name, r.Extract(e));
    }
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    // VHD wraps a single raw disk image; the first file is used as the disk data
    var diskData = files.Count > 0 ? files[0].Data : [];
    var w = new VhdWriter();
    w.SetDiskData(diskData);
    return w.Build();
  }
}
