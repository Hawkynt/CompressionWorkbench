#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dar;

/// <summary>
/// DAR (Disk ARchive), the archive format of <c>dar</c>/libdar. Every slice file starts with a slice
/// header (<see cref="DarSliceHeader"/>) and ends with a one-byte trailer; the bytes between, glued
/// across all slices, hold a version header, the file data, the catalogue and, at the end, two
/// terminators that lead back to the version trailer and the catalogue (<see cref="DarArchive"/>).
///
/// <para>Reading: archive formats 8.1 to 11.3, single or multi-slice (the sibling
/// <c>basename.N.dar</c> files are picked up from the slice's directory), with or without tape
/// marks, uncompressed or gzip, bzip2, xz, zstd, lz4 or lzo compressed in streaming or block mode,
/// sparse files expanded, hard links resolved, every data and catalogue checksum checked, members
/// of any size streamed to disk.
/// Directories and regular files are extracted; symlinks, devices, pipes and sockets are listed
/// but not materialised. Encrypted archives, unknown compression and newer formats are refused:
/// listing then falls back to the raw slice (<c>FULL.dar</c>) and a <c>metadata.ini</c> naming the
/// reason, and never throws.</para>
///
/// <para>Writing: format 11.3, stored or compressed with gzip, bzip2, xz, zstd or lz4, in one slice
/// or cut at <see cref="SliceSizeOption"/> / <see cref="FirstSliceSizeOption"/> (dar's <c>-s</c> /
/// <c>-S</c>), with or without <see cref="TapeMarksOption"/>; file data is streamed, symlinks and
/// uid/gid/permissions/times are kept. dar 2.7.13 and 2.8.6 accept it (<c>dar -t</c>, also
/// sequentially) and restore the input byte for byte (see
/// <c>Compression.Tests/Dar/DarExternalConformanceTests.cs</c>).</para>
///
/// <para>Layout derived from dar's documentation and from archives written by dar 2.4.24 to 2.8.6:
/// <c>docs/DAR-ON-DISK.md</c>.</para>
/// </summary>
public sealed class DarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {
  public string Id => "Dar";
  public string DisplayName => "Disk ARchive (DAR)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".dar";
  public IReadOnlyList<string> Extensions => [".dar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // A four-byte magic of 123 is too weak to claim arbitrary files: detection is by extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("gzip", "gzip"),
    new("bzip2", "bzip2"),
    new("xz", "xz"),
    new("zstd", "zstd"),
    new("lz4", "lz4"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Disk ARchive (dar / libdar), formats 8.1-11.3: single and multi-slice, tape marks, " +
    "gzip/bzip2/xz/zstd/lz4/lzo streaming and block compression, sparse files and hard links; " +
    "writes dar-compatible format-11.3 archives, sliced or not, with or without tape marks.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var seekable = stream.CanSeek ? stream : Buffer(stream);
    try {
      var (archive, set, _) = TryOpen(seekable);
      if (archive == null) {
        var length = seekable.Length;
        return [
          new(0, "FULL.dar", length, length, "Stored", false, false, null, Kind: "Track"),
          new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"),
        ];
      }
      using (set)
        return archive.Entries.Select((e, i) => ToInfo(i, e)).ToList();
    } finally {
      if (!ReferenceEquals(seekable, stream))
        seekable.Dispose();
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var seekable = stream.CanSeek ? stream : Buffer(stream);
    try {
      var (archive, set, problem) = TryOpen(seekable);
      if (archive == null) {
        ExtractFallback(seekable, outputDir, files, problem);
        return;
      }
      using (set)
        ExtractEntries(archive, outputDir, files);
    } finally {
      if (!ReferenceEquals(seekable, stream))
        seekable.Dispose();
    }
  }

  /// <summary>Option key: size of every slice file in bytes (dar's <c>-s</c>); <c>k</c>, <c>M</c>,
  /// <c>G</c> and <c>T</c> suffixes count in 1024s. Empty or 0 writes one slice.</summary>
  public const string SliceSizeOption = "SliceSize";

  /// <summary>Option key: size of the first slice when it differs (dar's <c>-S</c>).</summary>
  public const string FirstSliceSizeOption = "FirstSliceSize";

  /// <summary>Option key: write tape marks, the escape layer dar's sequential reading needs (dar's
  /// default; <c>false</c> is <c>dar -at</c>).</summary>
  public const string TapeMarksOption = "TapeMarks";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new(SliceSizeOption, "Slice size", FormatOptionKind.String, "",
      Description: "Cut the archive into slice files of this size (dar -s), named basename.1.dar, basename.2.dar, ... "
                 + "The output must then be a file named basename.1.dar. Suffixes k, M, G, T count in 1024s; empty writes one slice.",
      IsOptimizationAxis: false),
    new(FirstSliceSizeOption, "First slice size", FormatOptionKind.String, "",
      Description: "A different size for the first slice (dar -S); needs a slice size.",
      DependsOn: null, IsOptimizationAxis: false),
    new(TapeMarksOption, "Tape marks", FormatOptionKind.Boolean, "false",
      Description: "Write the escape marks that let dar read the archive sequentially, from a pipe or tape "
                 + "(dar's default; off is dar -at).",
      IsOptimizationAxis: false),
    new(CompressionBlockSizeOption, "Compression block size", FormatOptionKind.String, "",
      Description: "Compress in independent blocks of this many clear bytes (dar -z algo:level:size); empty is "
                 + "streaming compression. xz and zstd files over 64 MiB always use 8 MiB blocks.",
      IsOptimizationAxis: false),
  ];

  /// <summary>Option key: dar's block-mode compression block size (<c>-z algo:level:size</c>).</summary>
  public const string CompressionBlockSizeOption = "CompressionBlockSize";

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var blockSize = ParseSize(options.GetString(CompressionBlockSizeOption), CompressionBlockSizeOption);
    if (blockSize > 64 << 20)
      throw new ArgumentException($"DAR option {CompressionBlockSizeOption} is limited to 64 MiB.");
    var settings = new DarWriter.Settings(
      DarWriter.AlgorithmFor(options.MethodName),
      options.Level,
      options.GetOptionBool(TapeMarksOption, false),
      ParseSize(options.GetString(SliceSizeOption), SliceSizeOption),
      ParseSize(options.GetString(FirstSliceSizeOption), FirstSliceSizeOption),
      (int)blockSize);
    if (settings.FirstSliceSize > 0 && settings.SliceSize == 0)
      throw new ArgumentException($"DAR option {FirstSliceSizeOption} needs {SliceSizeOption} as well.");
    var items = inputs.Select(ToItem).ToList();
    if (settings.SliceSize == 0) {
      DarWriter.Write(output, items, settings);
      return;
    }
    // Slices after the first go next to the first, which must be named the way dar names slices.
    var baseName = output is FileStream { Name: { Length: > 0 } name } && name.EndsWith(".1.dar", StringComparison.OrdinalIgnoreCase)
      ? name[..^".1.dar".Length]
      : throw new ArgumentException("A sliced DAR archive must be written to a file named basename.1.dar, so its other slices can go beside it.");
    DarWriter.Write(n => n == 1 ? output : File.Create($"{baseName}.{n.ToString(CultureInfo.InvariantCulture)}.dar"), items, settings);
  }

  /// <summary>Parses a dar-style size: digits with an optional k/M/G/T suffix in powers of 1024.</summary>
  internal static long ParseSize(string? text, string option) {
    if (string.IsNullOrWhiteSpace(text))
      return 0;
    var t = text.Trim();
    var shift = char.ToLowerInvariant(t[^1]) switch { 'k' => 10, 'm' => 20, 'g' => 30, 't' => 40, _ => 0 };
    if (shift > 0)
      t = t[..^1];
    if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > long.MaxValue >> shift)
      throw new ArgumentException($"DAR option {option} '{text}' is not a size.");
    return value << shift;
  }

  private static DarWriter.Item ToItem(ArchiveInputInfo input) {
    var meta = input.Metadata;
    var inMemory = input.InMemoryContent;
    FileSystemInfo? info = null;
    if (inMemory == null && !string.IsNullOrEmpty(input.FullPath)) {
      info = input.IsDirectory ? new DirectoryInfo(input.FullPath) : new FileInfo(input.FullPath);
      if (!info.Exists && info.LinkTarget == null)
        info = null;
    }
    var link = info?.LinkTarget;
    var kind = link != null ? DarWriter.ItemKind.Symlink : input.IsDirectory ? DarWriter.ItemKind.Directory : DarWriter.ItemKind.File;
    var now = DateTime.UtcNow;
    var modified = meta?.LastWriteTimeUtc?.UtcDateTime ?? info?.LastWriteTimeUtc ?? now;
    var accessed = meta?.LastAccessTimeUtc?.UtcDateTime ?? info?.LastAccessTimeUtc ?? modified;
    var changed = meta?.StatusChangeTimeUtc?.UtcDateTime ?? modified;
    ushort permissions = kind switch {
      DarWriter.ItemKind.Symlink => 0x1FF,
      DarWriter.ItemKind.Directory => 0x1ED,
      _ => 0x1A4,
    };
    if (meta?.UnixMode is { } mode)
      permissions = (ushort)(mode & 0xFFF);
    else if (info != null && link == null && !OperatingSystem.IsWindows())
      permissions = (ushort)((int)File.GetUnixFileMode(input.FullPath) & 0xFFF);
    var item = new DarWriter.Item(input.ArchiveName, kind, permissions, modified, accessed) {
      Changed = changed,
      Uid = meta?.UnixUserId ?? 0,
      Gid = meta?.UnixGroupId ?? 0,
      LinkTarget = link,
    };
    if (kind != DarWriter.ItemKind.File)
      return item;
    if (inMemory != null)
      return item with { Open = () => new MemoryStream(inMemory, writable: false), Length = inMemory.LongLength };
    var path = input.FullPath;
    return item with {
      Open = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan),
      Length = info is FileInfo file ? file.Length : 0,
    };
  }

  private static ArchiveEntryInfo ToInfo(int index, DarEntry e) => new(
    index,
    e.Path,
    (long)Math.Min(e.Size, long.MaxValue),
    e.HasData ? (long)Math.Min(e.StorageSize, long.MaxValue) : 0,
    e.HasData ? DarArchive.AlgorithmName(e.Algorithm) : "Stored",
    e.Kind == DarEntryKind.Directory,
    false,
    e.LastModified,
    Kind: KindName(e),
    IsSymlink: e.Kind == DarEntryKind.Symlink,
    LinkTarget: e.LinkTarget);

  private static string? KindName(DarEntry e) => e.Status switch {
    DarSavedStatus.Delta => "delta-patch",
    DarSavedStatus.NotSaved => "not-saved",
    DarSavedStatus.InodeOnly => "inode-only",
    DarSavedStatus.Fake => "placeholder",
    _ => e.Kind switch {
      DarEntryKind.CharDevice => "char-device",
      DarEntryKind.BlockDevice => "block-device",
      DarEntryKind.Fifo => "fifo",
      DarEntryKind.Socket => "socket",
      DarEntryKind.Door => "door",
      _ => null,
    },
  };

  private static void ExtractEntries(DarArchive archive, string outputDir, string[]? files) {
    Directory.CreateDirectory(outputDir);
    foreach (var entry in archive.Entries) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Path, files))
        continue;
      switch (entry.Kind) {
        case DarEntryKind.Directory:
          Directory.CreateDirectory(SafeDirectory(outputDir, entry.Path));
          continue;
        case DarEntryKind.File or DarEntryKind.Door when entry.HasData: {
          using (var target = CreateEntryFile(outputDir, entry.Path))
            archive.CopyData(entry, target);
          ApplyMetadata(Path.Combine(outputDir, entry.Path.Replace('/', Path.DirectorySeparatorChar)), entry);
          continue;
        }
        case DarEntryKind.File when entry.Status == DarSavedStatus.Delta:
          throw new NotSupportedException($"'{entry.Path}' is stored as a binary delta against another archive.");
        default:
          continue; // no data in this archive, or not a regular file
      }
    }
  }

  private static string SafeDirectory(string outputDir, string path) {
    var safe = path.Replace('\\', '/').TrimStart('/');
    if (safe.Contains(".."))
      safe = Path.GetFileName(safe);
    return Path.Combine(outputDir, safe);
  }

  private static void ApplyMetadata(string path, DarEntry entry) {
    try {
      if (!File.Exists(path))
        return;
      if (entry.LastModified is { } modified)
        File.SetLastWriteTimeUtc(path, modified);
      if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(path, (UnixFileMode)(entry.Permissions & 0x1FF));
    } catch (IOException) {
    } catch (UnauthorizedAccessException) {
    } catch (ArgumentOutOfRangeException) {
    }
  }

  private static (DarArchive? Archive, DarSliceSet? Set, string? Problem) TryOpen(Stream stream) {
    DarSliceSet? set = null;
    try {
      set = DarSliceSet.Open(stream);
      var archive = DarArchive.Read(set.Archive);
      return (archive, set, null);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException or IOException
                                  or OverflowException or ArgumentException or IndexOutOfRangeException) {
      set?.Dispose();
      return (null, null, e.Message);
    }
  }

  private static void ExtractFallback(Stream seekable, string outputDir, string[]? files, string? problem) {
    if (Wants(files, "FULL.dar")) {
      seekable.Position = 0;
      using var target = CreateEntryFile(outputDir, "FULL.dar");
      seekable.CopyTo(target);
    }
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(DarSliceSet.ReadHeader(seekable), problem)));
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static MemoryStream Buffer(Stream stream) {
    var ms = new MemoryStream();
    stream.CopyTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static string BuildMetadataIni(DarSliceHeader h, string? archiveProblem) {
    var sb = new StringBuilder();
    var inv = CultureInfo.InvariantCulture;
    sb.Append("[Dar]\n");
    sb.Append(inv, $"slice_magic_ok={(h.Magic == DarSliceHeader.SliceMagic ? 1 : 0)}\n");
    sb.Append(inv, $"magic=0x{h.Magic:X8}\n");
    if (h.InternalName.Length == DarSliceHeader.NameLength)
      sb.Append(inv, $"internal_name={Convert.ToHexString(h.InternalName)}\n");
    if (h.Flag != '\0')
      sb.Append(inv, $"header_flag={Printable(h.Flag)}\n");
    if (h.Extension != '\0')
      sb.Append(inv, $"extension_flag={Printable(h.Extension)}\n");
    if (h.Trailer is { } trailer)
      sb.Append(inv, $"trailer_flag={trailer}\n");
    sb.Append(inv, $"last_slice={h.IsLastSlice switch { true => "yes", false => "no", null => "unknown" }}\n");
    if (h.HeaderLength is { } headerLength)
      sb.Append(inv, $"slice_header_length={headerLength}\n");
    if (h.FirstSliceSize is { } firstSliceSize)
      sb.Append(inv, $"first_slice_size={firstSliceSize}\n");
    if (h.SliceSize is { } sliceSize)
      sb.Append(inv, $"slice_size={sliceSize}\n");
    if (h.DataName is { } dataName)
      sb.Append(inv, $"data_name={Convert.ToHexString(dataName)}\n");
    if (h.UnknownTlvTypes.Count > 0)
      sb.Append(inv, $"unknown_tlv_types={string.Join(',', h.UnknownTlvTypes)}\n");
    if (h.Problem is { } problem)
      sb.Append(inv, $"problem={problem}\n");
    sb.Append(inv, $"slice_header={(h.IsValid ? "ok" : "invalid")}\n");
    if (archiveProblem != null)
      sb.Append(inv, $"archive_problem={archiveProblem.Replace('\n', ' ')}\n");
    sb.Append("member_enumeration=unavailable\n");
    sb.Append("parse_status=partial\n");
    return sb.ToString();
  }

  private static string Printable(char c) => c is >= ' ' and < (char)0x7F ? c.ToString() : $"0x{(byte)c:X2}";
}
