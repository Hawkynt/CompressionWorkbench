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
/// <para>Reading: archive formats 9 to 11.3, single or multi-slice (the sibling
/// <c>basename.N.dar</c> files are picked up from the slice's directory), with or without tape
/// marks, uncompressed or gzip, bzip2, xz, zstd, lz4 or lzo compressed in streaming or block mode,
/// sparse files expanded, hard links resolved, every data and catalogue checksum checked.
/// Directories and regular files are extracted; symlinks, devices, pipes and sockets are listed
/// but not materialised. Encrypted archives, unknown compression and newer formats are refused:
/// listing then falls back to the raw slice (<c>FULL.dar</c>) and a <c>metadata.ini</c> naming the
/// reason, and never throws.</para>
///
/// <para>Writing: a single-slice format-11.3 archive without tape marks — what <c>dar -at</c>
/// produces — stored or compressed with gzip, bzip2, xz, zstd or lz4. <c>dar -t</c> accepts it and
/// <c>dar -x</c> restores the input byte for byte (dar 2.7.13; see
/// <c>Compression.Tests/Dar/DarExternalConformanceTests.cs</c>).</para>
///
/// <para>Layout derived from dar's documentation and from archives written by dar 2.7.13 and 2.8.6:
/// <c>docs/DAR-ON-DISK.md</c>.</para>
/// </summary>
public sealed class DarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
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
    "Disk ARchive (dar / libdar), formats 9-11.3: single and multi-slice, tape marks, " +
    "gzip/bzip2/xz/zstd/lz4/lzo streaming and block compression, sparse files and hard links; " +
    "writes dar-compatible format-11.3 single-slice archives.";

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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var algorithm = DarWriter.AlgorithmFor(options.MethodName);
    var items = new List<DarWriter.Item>(inputs.Count);
    foreach (var input in inputs) {
      var onDisk = input.InMemoryContent == null && (input.IsDirectory ? Directory.Exists(input.FullPath) : File.Exists(input.FullPath));
      var modified = onDisk ? File.GetLastWriteTimeUtc(input.FullPath) : DateTime.UtcNow;
      var accessed = onDisk ? File.GetLastAccessTimeUtc(input.FullPath) : modified;
      var permissions = input.IsDirectory ? (ushort)0x1ED : (ushort)0x1A4;
      if (onDisk && !OperatingSystem.IsWindows())
        permissions = (ushort)((int)File.GetUnixFileMode(input.FullPath) & 0xFFF);
      items.Add(new(input.ArchiveName, input.IsDirectory, input.IsDirectory ? [] : input.ReadContent(), permissions, modified, accessed));
    }
    DarWriter.Write(output, items, algorithm, options.Level);
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
          var data = archive.ReadData(entry);
          using (var target = CreateEntryFile(outputDir, entry.Path))
            target.Write(data);
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
