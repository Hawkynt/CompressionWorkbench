#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Nwfs;

/// <summary>
/// NWFS386 (Novell NetWare 386 / Traditional NetWare File System) descriptor.
/// The supported writable profile is deliberately narrow: one ordinary volume,
/// ordinary FAT chains and DOS namespace directory entries. Suballocation,
/// compressed files, Turbo FAT, mirrored/spanned volumes and salvage recovery
/// remain outside the writable profile.
/// </summary>
/// <remarks>
/// <para>Novell did not publish the complete traditional filesystem layout. The
/// implementation follows publicly documented NetWare behaviour plus the
/// independently released NetWare filesystem implementation by Jeff Merkey.
/// That implementation is LGPL-2.1-or-later; this code uses the public on-disk
/// behaviour and constants rather than copying its implementation structure.</para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description><c>https://www.novell.com/documentation/developer/nlm_enu/data/sdk663.html</c> — 128-byte Directory Entry Table records</description></item>
///   <item><description><c>https://www.novell.com/documentation/nw6p/pdfdoc/trouble/trouble.pdf</c> — duplicate FAT and directory tables</description></item>
///   <item><description><c>https://github.com/jeffmerkey/netware-file-system</c> — LGPL-2.1-or-later behavioural/reference implementation</description></item>
/// </list>
/// </remarks>
public sealed class NwfsFormatDescriptor
  : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable,
    IArchiveDefragmentable, IArchiveShrinkable, IFilesystemExtentMap, ILayoutOptimizable,
    IFormatOptionsSchema {

  public string Id => "Nwfs";
  public string DisplayName => "NWFS (Novell NetWare 386 Traditional Filesystem)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nwfs";
  public IReadOnlyList<string> Extensions => [".nwfs", ".nwvol", ".netware"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(NwfsHeaders.HotfixMagic, Offset: (int)NwfsHeaders.HotfixOffset, Confidence: 0.85),
    new(NwfsHeaders.HotfixMagic, Offset: 0x8000, Confidence: 0.80),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NWFS (Traditional NetWare File System) — plain single-volume read/write profile with rebuild-based maintenance.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.PowerOfTwoSize(
      key: "BlockSize", displayName: "Block size",
      min: 1024, max: 256 * 1024, defaultLabel: "4 KB",
      description: "NetWare allocation-block size (1 KB through 256 KB, power of two)."),
    FilesystemSchemaPresets.VolumeLabel(NwfsLayout.MaxVolumeNameLength),
  ];

  /// <summary>
  /// Lists the real filesystem namespace when the supported volume profile can
  /// be opened. If it cannot, falls back to forensic renderings of the bytes so
  /// partially recognised images remain inspectable without pretending they are
  /// writable filesystems.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var volume = TryReadVolume(stream);
    if (volume != null) {
      var entries = new List<ArchiveEntryInfo>();
      foreach (var item in volume.List())
        entries.Add(new ArchiveEntryInfo(entries.Count, item.Path, item.Length, item.Length, "stored",
                                         item.IsDirectory, false, null));
      return entries;
    }

    var entriesFallback = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entriesFallback.Add(new ArchiveEntryInfo(0, "FULL.nwfs", 0, 0, "stored", false, false, null));
      entriesFallback.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entriesFallback;
    }

    NwfsHeaders hdr;
    try {
      hdr = NwfsHeaders.TryParse(image);
    } catch {
      entriesFallback.Add(new ArchiveEntryInfo(0, "FULL.nwfs", image.LongLength, image.LongLength, "stored", false, false, null));
      entriesFallback.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entriesFallback;
    }

    entriesFallback.Add(new ArchiveEntryInfo(0, "FULL.nwfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entriesFallback.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (hdr.AnyValid)
      entriesFallback.Add(new ArchiveEntryInfo(2, "volume_header.bin", hdr.HeaderRaw.LongLength, hdr.HeaderRaw.LongLength, "stored", false, false, null));
    return entriesFallback;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    NwfsHeaders hdr;
    try {
      hdr = NwfsHeaders.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.nwfs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    var volume = TryReadVolume(stream);
    WriteIfMatch(outputDir, "FULL.nwfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr, image.LongLength, volume), files);
    if (hdr.AnyValid)
      WriteIfMatch(outputDir, "volume_header.bin", hdr.HeaderRaw, files);

    if (volume == null) return;
    foreach (var item in volume.List()) {
      if (item.IsDirectory) {
        if (files is not { Length: > 0 } || MatchesFilter(item.Path, files))
          Directory.CreateDirectory(Path.Combine(outputDir, item.Path.Replace('/', Path.DirectorySeparatorChar)));
        continue;
      }
      WriteIfMatch(outputDir, item.Path, volume.Read(item), files);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var writer = NewWriter(options);
    foreach (var input in inputs) {
      if (input.IsDirectory) writer.AddDirectory(input.ArchiveName);
      else writer.AddFile(input.ArchiveName, input.ReadContent());
    }
    var image = writer.Build();
    output.Position = 0;
    output.SetLength(0);
    output.Write(image);
    output.Flush();
  }

  /// <summary>
  /// Adds/replaces entries transactionally by reading the supported namespace,
  /// authoring a fresh volume of the same capacity and committing only after the
  /// replacement image exists in full.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var snapshot = NwfsMaintenance.Read(archive);
    foreach (var input in inputs) {
      var name = NormalizePath(input.ArchiveName);
      if (input.IsDirectory) {
        if (name.Length > 0 && !snapshot.Directories.Contains(name, StringComparer.OrdinalIgnoreCase))
          snapshot.Directories.Add(name);
        continue;
      }
      snapshot.Files[name] = input.ReadContent();
    }
    NwfsMaintenance.Replace(archive, NwfsMaintenance.Build(snapshot, minimumImageSize: snapshot.ImageLength));
  }

  /// <summary>
  /// Removes files or whole directory subtrees through the same transactional
  /// rebuild. The fresh image is zero-initialised, so removed payload bytes are
  /// not carried into free blocks.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var snapshot = NwfsMaintenance.Read(archive);
    var removed = entryNames.Select(NormalizePath).Where(static n => n.Length > 0).ToArray();
    static bool Matches(string candidate, string removed)
      => candidate.Equals(removed, StringComparison.OrdinalIgnoreCase)
         || candidate.StartsWith(removed + "/", StringComparison.OrdinalIgnoreCase);

    foreach (var key in snapshot.Files.Keys.Where(k => removed.Any(r => Matches(k, r))).ToArray())
      snapshot.Files.Remove(key);
    snapshot.Directories.RemoveAll(d => removed.Any(r => Matches(d, r)));
    NwfsMaintenance.Replace(archive, NwfsMaintenance.Build(snapshot, minimumImageSize: snapshot.ImageLength));
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException("NWFS rebuild defragmentation supports ConsolidateAtStart.");
    options.CancellationToken.ThrowIfCancellationRequested();
    var before = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, Math.Max(1, archive.Length), before, "Reading NWFS allocation map"));
    var snapshot = NwfsMaintenance.Read(archive);
    var rebuilt = NwfsMaintenance.Build(snapshot, minimumImageSize: snapshot.ImageLength);
    options.CancellationToken.ThrowIfCancellationRequested();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "committing", 0.95, -1, 0, Math.Max(1, rebuilt.LongLength), null, "Committing packed NWFS image"));
    NwfsMaintenance.Replace(archive, rebuilt);
    var after = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, Math.Max(1, archive.Length), after, "NWFS files packed at the start of the volume"));
  }

  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var snapshot = NwfsMaintenance.Read(input);
    var rebuilt = NwfsMaintenance.Build(snapshot);
    output.Position = 0;
    output.SetLength(0);
    if (rebuilt.LongLength < input.Length)
      output.Write(rebuilt);
    else {
      input.Position = 0;
      input.CopyTo(output);
    }
    output.Flush();
    output.Position = 0;
  }

  public LayoutAnalysis AnalyzeLayout(Stream image) {
    var snapshot = NwfsMaintenance.Read(image);
    var optimal = NwfsMaintenance.FindOptimalBlockSize(snapshot);
    return new LayoutAnalysis {
      ImageSize = snapshot.ImageLength,
      CurrentUnitSize = snapshot.BlockSize,
      CurrentSlackBytes = NwfsMaintenance.FileSlack(snapshot, snapshot.BlockSize),
      OptimalUnitSize = optimal,
      OptimalSlackBytes = NwfsMaintenance.FileSlack(snapshot, optimal),
      RequiresRebuild = ["BlockSize"],
      Notes = [
        $"Supported writable profile: plain single-volume NWFS; current block size {snapshot.BlockSize:N0} bytes.",
        $"Tight rebuilt size at the recommended block size: {NwfsMaintenance.EstimateTightImageLength(snapshot, optimal):N0} bytes.",
      ],
    };
  }

  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.MakeSparse || options.DeduplicateWithLinks)
      throw new NotSupportedException("Traditional NWFS has no supported sparse-file or hard-link reclaim path.");

    var snapshot = NwfsMaintenance.Read(source);
    var blockSize = options.UnitSize > 0 ? options.UnitSize : NwfsMaintenance.FindOptimalBlockSize(snapshot);
    string? volumeName = null;
    if (options.Parameters != null) {
      if (options.Parameters.TryGetValue("BlockSize", out var explicitBlock)
          || options.Parameters.TryGetValue("ClusterSize", out explicitBlock)) {
        var parsed = ParseSize(explicitBlock);
        if (parsed > 0) blockSize = parsed;
      }
      if (options.Parameters.TryGetValue("VolumeLabel", out var label) && !string.IsNullOrWhiteSpace(label))
        volumeName = label;
    }
    if (!NwfsLayout.IsValidBlockSize(blockSize))
      throw new ArgumentOutOfRangeException(nameof(options), $"{blockSize} is not a valid NWFS block size.");

    var rebuilt = NwfsMaintenance.Build(snapshot, blockSize, Math.Max(0, options.ImageSize), volumeName);
    target.Position = 0;
    target.SetLength(0);
    target.Write(rebuilt);
    target.Flush();
    target.Position = 0;
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => NwfsMaintenance.EnumerateExtents(image);

  private static NwfsWriter NewWriter(FormatCreateOptions options) {
    var blockSize = ParseSize(options.GetString("BlockSize"));
    if (blockSize <= 0) blockSize = ParseSize(options.GetString("ClusterSize"));
    if (blockSize <= 0) blockSize = 4096;
    var volumeName = options.GetOption("VolumeLabel", "SYS");
    if (string.IsNullOrWhiteSpace(volumeName)) volumeName = "SYS";
    var minimumSize = 0L;
    if (long.TryParse(options.GetString("ImageSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested)
        && requested > 0)
      minimumSize = requested;
    return new NwfsWriter { BlockSize = blockSize, VolumeName = volumeName, MinimumImageSize = minimumSize };
  }

  private static int ParseSize(string? value) {
    var parsed = FilesystemSchemaPresets.ParseSize(value);
    if (parsed > 0) return parsed;
    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw) ? raw : 0;
  }

  private static string NormalizePath(string path)
    => (path ?? string.Empty).Replace('\\', '/').Trim('/');

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(NwfsHeaders h, long imageSize, NwfsReader? volume) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={(volume != null ? "ok" : "partial")}\n");
    b.Append("detection_basis=reverse_engineered\n");
    b.Append(ic, $"hotfix_found={h.HotfixFound}\n");
    if (h.HotfixFound) b.Append(ic, $"hotfix_offset={h.HotfixFoundOffset}\n");
    b.Append(ic, $"mirror_found={h.MirrorFound}\n");
    if (h.MirrorFound) b.Append(ic, $"mirror_offset={h.MirrorFoundOffset}\n");
    b.Append(ic, $"volumes_found={h.VolumesFound}\n");
    if (h.VolumesFound) b.Append(ic, $"volumes_offset={h.VolumesFoundOffset}\n");
    var detected = string.Join("+", new[] {
      h.HotfixFound ? "HOTFIX00" : null,
      h.MirrorFound ? "MIRROR00" : null,
      h.VolumesFound ? "NetWare Volumes" : null,
    }.Where(static s => s != null));
    b.Append(ic, $"detected_magic={(detected.Length > 0 ? detected : "none")}\n");
    if (imageSize >= 0) b.Append(ic, $"volume_size_if_visible={imageSize}\n");
    if (volume != null) {
      var items = volume.List();
      b.Append(ic, $"volume_name={volume.VolumeName}\n");
      b.Append(ic, $"block_size={volume.BlockSize}\n");
      b.Append(ic, $"file_count={items.Count(static i => !i.IsDirectory)}\n");
      b.Append(ic, $"directory_count={items.Count(static i => i.IsDirectory)}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  private const int HeaderReadCap = 64 * 1024;
  private const long VolumeReadCap = 512L * 1024 * 1024;

  private static NwfsReader? TryReadVolume(Stream stream) {
    if (!stream.CanSeek) return null;
    try {
      if (stream.Length > VolumeReadCap) return null;
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      return NwfsReader.TryOpen(ms.ToArray());
    } catch {
      return null;
    }
  }

  private static byte[] ReadAllBounded(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, (int)Math.Min(buf.Length, HeaderReadCap - ms.Length))) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}
