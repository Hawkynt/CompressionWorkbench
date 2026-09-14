#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Nwfs;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nwfs386;

/// <summary>
/// Compatibility descriptor for the Novell NetWare 386 Traditional File System.
/// The actual reader/writer lives in <c>FileSystem.Nwfs</c>; this descriptor keeps
/// the historical <c>Nwfs386</c> id/extensions while sharing that implementation.
/// </summary>
/// <remarks>
/// <para>
/// Fresh images and rewrites intentionally target the conservative profile the
/// shared writer can prove: one Traditional NetWare volume, one segment, DOS
/// namespace only, without compression, suballocation, migration, auditing or
/// special directory records. Unsupported legacy profiles remain readable where
/// the shared reader understands them, but mutation fails before the source is
/// touched.
/// </para>
/// <para>
/// Novell documents MBR partition type <c>0x65</c> as a Traditional NetWare
/// partition. The detailed on-disk structures are reconstructed from public
/// reverse-engineering/reference implementations; no third-party implementation
/// code is copied here.
/// </para>
/// </remarks>
public sealed class Nwfs386FormatDescriptor :
    IFormatDescriptor,
    IArchiveFormatOperations,
    IArchiveInMemoryExtract,
    IArchiveCreatable,
    IArchiveModifiable,
    IArchiveDefragmentable,
    IArchiveShrinkable,
    IFormatOptionsSchema,
    ILayoutOptimizable {

  private const long MaxManagedImageBytes = 512L * 1024 * 1024;
  private const int DirectoryEntryBytes = 128;
  private const uint NoBlock = 0xFFFFFFFF;
  private const uint DirIdAvailable = 0xFFFFFFFF;
  private const uint DirIdGrantOrDeleted = 0xFFFFFFFE;
  private const uint DirIdVolumeInfo = 0xFFFFFFFD;
  private const uint HighestSpecialDirectoryId = 0xFFFFFF00;
  private const uint AttributeDirectory = 0x10;
  private const uint AttributeArchive = 0x20;
  private const byte FlagDeleted3 = 0x01;
  private const byte FlagSubdirectory = 0x04;
  private const byte FlagPrimaryNamespace = 0x10;
  private const byte FlagDeleted4 = 0x20;

  private static readonly int[] SupportedBlockSizes =
    [4096, 8192, 16384, 32768, 65536];

  public string Id => "Nwfs386";
  public string DisplayName => "NWFS386 (Novell Traditional NetWare filesystem)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList |
    FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  public string DefaultExtension => ".nwfs386";
  public IReadOnlyList<string> Extensions => [".nwfs386", ".nw386"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // The real HOTFIX00 signatures are already owned by FileSystem.Nwfs.
  // Registering them twice would make magic routing depend on registry order.
  // Nwfs386 is therefore an extension-routed compatibility id, exactly like
  // other aliases in the repository that share one physical format.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Novell NetWare 386 Traditional filesystem. R/W rebuilds are limited to the " +
    "single-segment DOS-namespace profile without compression/suballocation; " +
    "advanced legacy profiles are rejected before mutation.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema =>
  [
    new(
      "BlockSize",
      "Block size",
      FormatOptionKind.Integer,
      "4096",
      SupportedBlockSizes.Select(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
      "Traditional NetWare allocation-cluster size in bytes. Changing it rebuilds the volume; existing data is preserved byte-for-byte."),
    new(
      "VolumeName",
      "Volume name",
      FormatOptionKind.String,
      "SYS",
      Description: "NetWare volume name (1-15 ASCII characters).")
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var opened = Open(stream);
    var result = new List<ArchiveEntryInfo>();
    var index = 0;
    foreach (var item in opened.Volume.List())
      result.Add(new ArchiveEntryInfo(
        index++,
        item.Path,
        item.Length,
        item.Length,
        "stored",
        item.IsDirectory,
        false,
        null));
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var opened = Open(stream);
    Directory.CreateDirectory(outputDir);

    foreach (var item in opened.Volume.List()) {
      if (item.IsDirectory) continue;
      if (files is { Length: > 0 } && !MatchesFilter(item.Path, files)) continue;
      WriteFile(outputDir, item.Path, opened.Volume.Read(item));
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    ArgumentNullException.ThrowIfNull(output);
    var opened = Open(input);
    var item = opened.Volume.List().FirstOrDefault(candidate =>
      !candidate.IsDirectory &&
      candidate.Path.Equals(entryName, StringComparison.OrdinalIgnoreCase));

    if (item is null)
      throw new FileNotFoundException($"NWFS386 entry '{entryName}' was not found.", entryName);

    output.Write(opened.Volume.Read(item));
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var opened = Open(archive);
    var item = opened.Volume.List().FirstOrDefault(candidate =>
      !candidate.IsDirectory &&
      candidate.Path.Equals(entryName, StringComparison.OrdinalIgnoreCase));

    if (item is null)
      throw new FileNotFoundException($"NWFS386 entry '{entryName}' was not found.", entryName);

    return new MemoryStream(opened.Volume.Read(item), writable: false);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("NWFS386 creation requires a writable, seekable stream.", nameof(output));

    var blockSize = options.GetOptionInt(
      "BlockSize",
      options.GetOptionInt("ClusterSize", 4096));
    var volumeName = options.GetOption("VolumeName", "SYS");

    var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = NormalizePath(input.ArchiveName);
      if (name.Length == 0)
        throw new InvalidDataException("NWFS386 file entries need a non-empty path.");
      files[name] = input.ReadContent();
    }

    var image = BuildImage(files, blockSize, volumeName);
    output.Position = 0;
    output.SetLength(0);
    output.Write(image);
    output.Position = 0;
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var opened = OpenWritable(archive);
    var files = SnapshotFiles(opened.Volume);

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = NormalizePath(input.ArchiveName);
      if (name.Length == 0)
        throw new InvalidDataException("NWFS386 file entries need a non-empty path.");
      files[name] = input.ReadContent();
    }

    CommitRebuild(archive, files, opened.Volume.BlockSize, opened.Volume.VolumeName);
  }

  public void Remove(Stream archive, string[] entryNames) {
    var opened = OpenWritable(archive);
    var files = SnapshotFiles(opened.Volume);
    var removals = (entryNames ?? [])
      .Select(NormalizePath)
      .Where(static name => name.Length > 0)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    foreach (var name in files.Keys.ToArray())
      if (removals.Any(removal =>
            name.Equals(removal, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(removal + "/", StringComparison.OrdinalIgnoreCase)))
        files.Remove(name);

    CommitRebuild(archive, files, opened.Volume.BlockSize, opened.Volume.VolumeName);
  }

  public void Defragment(Stream archive) {
    var opened = OpenWritable(archive);
    CommitRebuild(
      archive,
      SnapshotFiles(opened.Volume),
      opened.Volume.BlockSize,
      opened.Volume.VolumeName);
  }

  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("NWFS386 shrink requires a writable, seekable target stream.", nameof(output));

    var opened = OpenWritable(input);
    var files = SnapshotFiles(opened.Volume);
    var rebuilt = BuildImage(files, opened.Volume.BlockSize, opened.Volume.VolumeName);
    VerifyImage(rebuilt, files, opened.Volume.BlockSize, opened.Volume.VolumeName);

    output.Position = 0;
    output.SetLength(0);
    if (rebuilt.LongLength >= opened.Image.LongLength)
      output.Write(opened.Image);
    else
      output.Write(rebuilt);
    output.Position = 0;
  }

  public LayoutAnalysis AnalyzeLayout(Stream image) {
    var opened = OpenWritable(image);
    var files = opened.Volume.List().Where(static item => !item.IsDirectory).ToArray();

    static long Slack(IEnumerable<NwfsReader.Item> items, int blockSize)
      => items.Sum(item => item.Length == 0
        ? 0
        : ((item.Length + blockSize - 1) / blockSize) * blockSize - item.Length);

    var currentSlack = Slack(files, opened.Volume.BlockSize);
    var optimal = SupportedBlockSizes
      .Select(blockSize => (BlockSize: blockSize, Slack: Slack(files, blockSize)))
      .MinBy(static candidate => (candidate.Slack, candidate.BlockSize));

    return new LayoutAnalysis {
      ImageSize = opened.Image.LongLength,
      CurrentUnitSize = opened.Volume.BlockSize,
      CurrentSlackBytes = currentSlack,
      OptimalUnitSize = optimal.BlockSize,
      OptimalSlackBytes = optimal.Slack,
      RequiresRebuild = optimal.BlockSize == opened.Volume.BlockSize
        ? []
        : ["BlockSize"],
      Notes = [
        "Optimization compares allocation slack for the plain NWFS386 profile; changing block size is a verified rebuild.",
        "Compression, suballocation, alternate namespaces and multi-segment volumes are deliberately outside the writable profile."
      ],
    };
  }

  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);
    if (!target.CanWrite || !target.CanSeek)
      throw new ArgumentException("NWFS386 layout rebuild requires a writable, seekable target stream.", nameof(target));
    if (options.MakeSparse || options.DeduplicateWithLinks)
      throw new NotSupportedException("NWFS386 layout rebuild does not support sparse-file or hard-link transforms.");

    var opened = OpenWritable(source);
    var blockSize = options.UnitSize > 0 ? options.UnitSize : opened.Volume.BlockSize;
    var volumeName = opened.Volume.VolumeName;

    if (options.Parameters is not null) {
      if (options.Parameters.TryGetValue("ClusterSize", out var clusterText) &&
          int.TryParse(clusterText, System.Globalization.CultureInfo.InvariantCulture, out var clusterSize))
        blockSize = clusterSize;
      if (options.Parameters.TryGetValue("BlockSize", out var blockText) &&
          int.TryParse(blockText, System.Globalization.CultureInfo.InvariantCulture, out var explicitBlockSize))
        blockSize = explicitBlockSize;
      if (options.Parameters.TryGetValue("VolumeName", out var explicitVolumeName))
        volumeName = explicitVolumeName;
    }

    var files = SnapshotFiles(opened.Volume);
    var rebuilt = BuildImage(files, blockSize, volumeName);
    VerifyImage(rebuilt, files, blockSize, volumeName);

    target.Position = 0;
    target.SetLength(0);
    target.Write(rebuilt);
    target.Position = 0;
    options.OnProgress?.Invoke(opened.Image.LongLength, opened.Image.LongLength);
  }

  private static OpenedVolume Open(Stream stream) {
    var image = ReadImage(stream);
    var volume = NwfsReader.TryOpen(image)
      ?? throw new InvalidDataException("The stream is not a readable NWFS386 Traditional NetWare volume.");
    return new OpenedVolume(image, volume);
  }

  private static OpenedVolume OpenWritable(Stream stream) {
    var opened = Open(stream);
    ValidatePlainWritableProfile(opened.Image, opened.Volume);
    return opened;
  }

  private static byte[] ReadImage(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("NWFS386 requires a readable, seekable stream.", nameof(stream));
    if (stream.Length > MaxManagedImageBytes)
      throw new NotSupportedException(
        $"NWFS386's shared managed reader currently caps images at {MaxManagedImageBytes:N0} bytes.");

    stream.Position = 0;
    var image = new byte[checked((int)stream.Length)];
    stream.ReadExactly(image);
    return image;
  }

  private static Dictionary<string, byte[]> SnapshotFiles(NwfsReader volume) {
    var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in volume.List().Where(static item => !item.IsDirectory)) {
      if (!files.TryAdd(item.Path, volume.Read(item)))
        throw new NotSupportedException($"NWFS386 writable profile rejects duplicate path '{item.Path}'.");
    }
    return files;
  }

  private static byte[] BuildImage(
      IReadOnlyDictionary<string, byte[]> files,
      int blockSize,
      string volumeName) {
    if (string.IsNullOrWhiteSpace(volumeName) || volumeName.Length > 15 ||
        volumeName.Any(static ch => ch is < ' ' or > '~'))
      throw new InvalidDataException("NWFS386 volume name must be 1-15 printable ASCII characters.");
    if (!SupportedBlockSizes.Contains(blockSize))
      throw new InvalidDataException("NWFS386 allocation-cluster size must be 4096, 8192, 16384, 32768 or 65536 bytes.");

    var writer = new NwfsWriter {
      BlockSize = blockSize,
      VolumeName = volumeName,
    };

    foreach (var (path, data) in files.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
      writer.AddFile(path, data);

    return writer.Build();
  }

  private static void CommitRebuild(
      Stream archive,
      IReadOnlyDictionary<string, byte[]> files,
      int blockSize,
      string volumeName) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("NWFS386 mutation requires a readable, writable, seekable stream.", nameof(archive));

    var rebuilt = BuildImage(files, blockSize, volumeName);
    VerifyImage(rebuilt, files, blockSize, volumeName);

    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(rebuilt);
    archive.Flush();
    archive.Position = 0;
  }

  private static void VerifyImage(
      byte[] image,
      IReadOnlyDictionary<string, byte[]> expectedFiles,
      int blockSize,
      string volumeName) {
    var verify = NwfsReader.TryOpen(image)
      ?? throw new InvalidOperationException("NWFS386 writer produced an image its own reader rejects.");
    if (verify.BlockSize != blockSize)
      throw new InvalidOperationException(
        $"NWFS386 rebuild changed block size ({blockSize} -> {verify.BlockSize}).");
    if (!verify.VolumeName.Equals(volumeName, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException(
        $"NWFS386 rebuild changed volume name ('{volumeName}' -> '{verify.VolumeName}').");

    var actual = SnapshotFiles(verify);
    if (!actual.Keys.Order(StringComparer.OrdinalIgnoreCase)
        .SequenceEqual(expectedFiles.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
      throw new InvalidOperationException("NWFS386 rebuild changed the live file set.");

    foreach (var (name, expected) in expectedFiles)
      if (!actual.TryGetValue(name, out var bytes) || !bytes.AsSpan().SequenceEqual(expected))
        throw new InvalidOperationException($"NWFS386 rebuild changed the bytes of '{name}'.");
  }

  private static void ValidatePlainWritableProfile(byte[] image, NwfsReader volume) {
    var sawVolumeInfo = false;
    foreach (var cluster in volume.WalkChain(volume.RootDirectoryBlock)) {
      var blockOffset = volume.BlockOffset(cluster);
      if (blockOffset < 0 || blockOffset + volume.BlockSize > image.LongLength)
        throw new NotSupportedException("NWFS386 mutation refused a directory chain outside the supported volume bounds.");

      var entriesPerBlock = volume.BlockSize / DirectoryEntryBytes;
      for (var i = 0; i < entriesPerBlock; ++i) {
        var offset = checked((int)(blockOffset + (long)i * DirectoryEntryBytes));
        var entry = image.AsSpan(offset, DirectoryEntryBytes);
        var parent = BinaryPrimitives.ReadUInt32LittleEndian(entry);

        if (parent == DirIdAvailable)
          continue;

        if (parent == DirIdVolumeInfo) {
          sawVolumeInfo = true;
          // ROOT/ROOT3X byte 23 is VolumeFlags. Rebuilds do not preserve
          // compression, suballocation, migration, auditing or NDS flags.
          if (entry[23] != 0)
            throw new NotSupportedException(
              "NWFS386 mutation is limited to volumes without compression, suballocation, migration, auditing or NDS flags.");
          continue;
        }

        if (parent == DirIdGrantOrDeleted || parent >= HighestSpecialDirectoryId)
          throw new NotSupportedException(
            "NWFS386 mutation refused special/deleted/trustee directory records that the plain writer cannot preserve.");

        var flags = entry[9];
        if ((flags & (FlagDeleted3 | FlagDeleted4)) != 0
            || entry[10] != 0
            || (flags & FlagPrimaryNamespace) == 0
            || (flags & ~(FlagSubdirectory | FlagPrimaryNamespace)) != 0)
          throw new NotSupportedException(
            "NWFS386 mutation is limited to live primary-DOS-namespace directory entries.");

        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
        if ((attributes & ~(AttributeDirectory | AttributeArchive)) != 0)
          throw new NotSupportedException(
            "NWFS386 mutation refused file attributes outside the plain writer's preserved subset.");

        var isDirectory = (attributes & AttributeDirectory) != 0;
        if (isDirectory != ((flags & FlagSubdirectory) != 0))
          throw new NotSupportedException(
            "NWFS386 mutation refused inconsistent directory attributes/flags.");

        if (!isDirectory && BinaryPrimitives.ReadUInt32LittleEndian(entry[104..]) != 0)
          throw new NotSupportedException(
            "NWFS386 mutation refused a volume containing salvage/deleted-file records.");
      }
    }

    if (!sawVolumeInfo)
      throw new NotSupportedException("NWFS386 mutation requires the root volume-information record.");

    var items = volume.List();
    ValidateFileChains(volume, items);

    // The compatibility descriptor rebuilds from files only, so unlike the
    // shared Nwfs descriptor it still cannot preserve an otherwise-empty DET directory.
    var files = items.Where(static item => !item.IsDirectory).Select(static item => item.Path).ToArray();
    foreach (var directory in items.Where(static item => item.IsDirectory))
      if (!files.Any(file => file.StartsWith(directory.Path + "/", StringComparison.OrdinalIgnoreCase)))
        throw new NotSupportedException(
          $"NWFS386 mutation refused empty directory '{directory.Path}' because the compatibility rebuild cannot preserve it.");
  }

  private static void ValidateFileChains(
      NwfsReader volume,
      IReadOnlyList<NwfsReader.Item> items) {
    foreach (var item in items.Where(static item => !item.IsDirectory)) {
      var expectedBlocks = item.Length == 0
        ? 0L
        : (item.Length + volume.BlockSize - 1) / volume.BlockSize;

      if (expectedBlocks == 0) {
        if (item.FirstBlock != NoBlock)
          throw new NotSupportedException(
            $"NWFS386 mutation refused empty file '{item.Path}' with an allocated data chain.");
        continue;
      }

      var chain = volume.WalkIndexedChain(item.FirstBlock).ToArray();
      if (chain.LongLength != expectedBlocks)
        throw new NotSupportedException(
          $"NWFS386 mutation refused truncated/overlong FAT chain for '{item.Path}'.");

      for (var i = 0; i < chain.Length; ++i)
        if (chain[i].Index != (uint)i)
          throw new NotSupportedException(
            $"NWFS386 mutation refused sparse/non-linear FAT indexing for '{item.Path}'.");
    }
  }

  private static string NormalizePath(string path)
    => (path ?? string.Empty).Replace('\\', '/').Trim('/');

  private readonly record struct OpenedVolume(byte[] Image, NwfsReader Volume);
}
