#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux3;

/// <summary>
/// Native metadata descriptor for the linux-tux3 research filesystem.
/// </summary>
/// <remarks>
/// The descriptor recognises native linux-tux3 disk-format revisions, parses the packed big-endian
/// superblock, and uses the native inode tree, allocation-bitmap data tree, and replayable allocation
/// log records to prove free/allocated block runs. Active structural tree-log records deliberately
/// disable the in-volume allocation map until structural replay is implemented.
///
/// <para>Create/Modify/Defragment capabilities remain withheld. Allocation-tree parsing is enough
/// to make layout and Wipe useful inside the declared volume, but moving or deleting native objects
/// additionally requires directory/inode traversal and transactional metadata/log writing.</para>
/// </remarks>
public sealed class Tux3FormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, ISyntheticEntryNames, IFilesystemExtentMap, IArchiveShrinkable {

  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.tux3", "metadata.ini", "superblock.bin" };

  /// <inheritdoc />
  public IReadOnlySet<string> SyntheticEntryNames => SyntheticNames;

  /// <inheritdoc />
  public string Id => "Tux3";

  /// <inheritdoc />
  public string DisplayName => "TUX3";

  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;

  /// <inheritdoc />
  public string DefaultExtension => ".tux3";

  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".tux3"];

  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Tux3Reader.Magic, Offset: Tux3Reader.SuperblockOffset, Confidence: 0.99),
    new(Tux3Reader.Legacy2012Magic, Offset: Tux3Reader.SuperblockOffset, Confidence: 0.95),
  ];

  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <inheritdoc />
  public string? TarCompressionFormatId => null;

  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc />
  public string Description =>
    "TUX3 version-tree research filesystem — native big-endian superblock, allocation-tree and journal parsing; " +
    "fail-closed in-volume free-space layout/wipe plus external-tail shrink; native mutation not yet implemented.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new Tux3Reader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", entry.IsDirectory, false, null)).ToList();
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new Tux3Reader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files)) continue;

      var target = Path.Combine(outputDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      reader.ExtractTo(entry, output);
    }
  }

  /// <summary>
  /// Enumerates only byte ranges proven by native metadata. When the allocation bitmap and active
  /// allocation-only journal records are trustworthy, allocated runs stay metadata-reserved and
  /// unallocated runs are exposed as Free. Otherwise the declared volume remains reserved wholesale.
  /// Bytes physically appended beyond the declared TUX3 volume are always outside the filesystem.
  /// Invalid, unrepresentable, or truncated volume metadata reserves the complete physical image.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      return [];

    var imageLength = image.Length;
    try {
      using var reader = new Tux3Reader(image);
      if (!TryGetDeclaredVolumeLength(reader, out var volumeLength))
        return ReserveWholeImage(imageLength);

      // A volume declaring more blocks than the image physically holds is truncated. Its allocation
      // map describes blocks that are not there, so nothing inside it is provable and the complete
      // physical image stays reserved. An exactly-sized volume is healthy and keeps the map path.
      if (volumeLength > imageLength)
        return ReserveWholeImage(imageLength);

      var extents = new List<DefragBlockInfo>();
      if (reader.AllocationMapValid && reader.AllocationRuns.Count > 0) {
        var blockSize = 1UL << reader.BlockBits;
        foreach (var run in reader.AllocationRuns) {
          if (run.BlockCount == 0 || run.StartBlock > (ulong)long.MaxValue / blockSize ||
              run.BlockCount > (ulong)long.MaxValue / blockSize)
            return ReserveWholeImage(imageLength);

          var offset = run.StartBlock * blockSize;
          var length = run.BlockCount * blockSize;
          if (offset > (ulong)volumeLength || length > (ulong)volumeLength - offset)
            return ReserveWholeImage(imageLength);

          extents.Add(new DefragBlockInfo(
            (long)offset,
            (long)length,
            run.IsAllocated ? DefragBlockKind.MetadataReserved : DefragBlockKind.Free,
            run.IsAllocated
              ? "TUX3 allocated blocks (ownership unresolved)"
              : "TUX3 free blocks (bitmap + journal)"));
        }
      } else if (volumeLength > 0) {
        extents.Add(new DefragBlockInfo(
          0,
          volumeLength,
          DefragBlockKind.MetadataReserved,
          "TUX3 volume (allocation map unresolved)"));
      }

      if (volumeLength < imageLength)
        extents.Add(new DefragBlockInfo(
          volumeLength,
          imageLength - volumeLength,
          DefragBlockKind.Free,
          "Trailing bytes outside TUX3 volume"));

      return extents;
    } catch (InvalidDataException) {
      return ReserveWholeImage(imageLength);
    } catch (IOException) {
      return ReserveWholeImage(imageLength);
    }
  }

  /// <summary>
  /// Removes only bytes beyond the volume size declared by <c>volblocks * blocksize</c>.
  /// Malformed, truncated, or arithmetically invalid images are copied through unchanged.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("TUX3 shrink requires a readable, seekable input stream.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("TUX3 shrink requires a writable, seekable output stream.", nameof(output));
    if (ReferenceEquals(input, output))
      throw new ArgumentException("TUX3 shrink requires distinct input and output streams.", nameof(output));

    var copyLength = input.Length;
    try {
      using var reader = new Tux3Reader(input);
      if (TryGetDeclaredVolumeLength(reader, out var volumeLength) && volumeLength <= input.Length)
        copyLength = volumeLength;
    } catch (InvalidDataException) {
      // Total operation: malformed input is copied through unchanged.
    } catch (IOException) {
      // Total operation: an unreadable metadata surface is copied through unchanged.
    }

    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    CopyPrefix(input, output, copyLength);
  }

  private static bool TryGetDeclaredVolumeLength(Tux3Reader reader, out long length) {
    length = 0;
    // The reference format reserves a 4 KiB superblock area as the maximum block size.
    // Treat larger shifts as corrupt rather than trusting them for destructive maintenance.
    if (!reader.ValidSuperblock || reader.VolBlocks == 0 || reader.BlockBits > 12)
      return false;

    var blockSize = 1UL << reader.BlockBits;
    if (reader.VolBlocks > (ulong)long.MaxValue / blockSize)
      return false;

    var declared = reader.VolBlocks * blockSize;
    if (declared < Tux3Reader.SuperblockOffset + Tux3Reader.DiskSuperSize)
      return false;

    length = (long)declared;
    return true;
  }

  private static IEnumerable<DefragBlockInfo> ReserveWholeImage(long imageLength)
    => imageLength == 0
      ? []
      : [new DefragBlockInfo(0, imageLength, DefragBlockKind.MetadataReserved, "TUX3 image (volume boundary unresolved)")];

  private static void CopyPrefix(Stream input, Stream output, long count) {
    var buffer = new byte[128 * 1024];
    while (count > 0) {
      var requested = (int)Math.Min(buffer.Length, count);
      var read = input.Read(buffer, 0, requested);
      if (read <= 0)
        throw new EndOfStreamException("TUX3 input ended before the expected image boundary.");
      output.Write(buffer, 0, read);
      count -= read;
    }
  }
}
