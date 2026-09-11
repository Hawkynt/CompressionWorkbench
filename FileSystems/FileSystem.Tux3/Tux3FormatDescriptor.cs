#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux3;

/// <summary>
/// Native-superblock descriptor for the linux-tux3 research filesystem.
/// </summary>
/// <remarks>
/// The descriptor recognises real linux-tux3 disk-format revisions and parses the packed,
/// big-endian <c>struct disksuper</c> at byte 4096. Native tree traversal and mutation are not
/// implemented, so Create/Modify/Defragment capabilities are intentionally withheld rather than
/// routing files through a private side-table that no TUX3 implementation understands.
///
/// <para>The on-disk <c>volblocks</c> field does, however, define the native volume boundary.
/// Bytes after that boundary are outside the TUX3 volume and can therefore be reported as free,
/// wiped, and removed by shrink without interpreting or modifying any native tree. Everything
/// inside the declared volume remains fail-closed as metadata-reserved until allocation-tree
/// traversal exists.</para>
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
    "TUX3 version-tree research filesystem — native big-endian superblock detection/metadata, " +
    "fail-closed layout mapping, external-padding wipe/shrink; native tree traversal and writing not yet implemented.";

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
  /// Enumerates the provable byte layout without guessing at undecoded TUX3 allocation state.
  /// The declared native volume is reserved wholesale; only trailing bytes outside it are free.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      return [];

    try {
      using var reader = new Tux3Reader(image);
      if (!TryGetDeclaredVolumeLength(reader, out var volumeLength))
        return [];

      var imageLength = image.Length;
      if (volumeLength >= imageLength)
        return imageLength == 0
          ? []
          : [new DefragBlockInfo(0, imageLength, DefragBlockKind.MetadataReserved, "TUX3 volume (allocation map unresolved)")];

      return [
        new DefragBlockInfo(0, volumeLength, DefragBlockKind.MetadataReserved, "TUX3 volume (allocation map unresolved)"),
        new DefragBlockInfo(volumeLength, imageLength - volumeLength, DefragBlockKind.Free, "Trailing bytes outside TUX3 volume"),
      ];
    } catch (InvalidDataException) {
      return [];
    } catch (IOException) {
      return [];
    }
  }

  /// <summary>
  /// Removes only bytes beyond the volume size declared by <c>volblocks &lt;&lt; blockbits</c>.
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
    if (!reader.ValidSuperblock || reader.VolBlocks == 0 || reader.BlockBits >= 63)
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
