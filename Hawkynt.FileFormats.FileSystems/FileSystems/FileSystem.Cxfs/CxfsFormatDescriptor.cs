#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Cxfs;

/// <summary>
/// R/W descriptor for SGI CXFS filesystem images.
///
/// <para>SGI documents CXFS as using the same filesystem structure as XFS and
/// creating the filesystem with the same <c>mkfs</c> command. CXFS clustering,
/// metadata-server selection, fencing and mount policy live in the external
/// cluster database / XVM management layer, not in another filesystem format.</para>
///
/// <para>Read support delegates the filesystem walk to the repository's XFS
/// reader. Authoring deliberately targets the pre-CRC XFS v4 family
/// (<c>mkfs.xfs -m crc=0</c>) instead of emitting the repository's modern XFS-v5
/// profile and calling it CXFS. The writable profile is conservative: 4 KiB
/// blocks, 256-byte v2 inodes, dir2 short-form directories and rebuild-style edits.
/// Unsupported v4/v5 structures remain readable but are refused for mutation.</para>
///
/// <para>This descriptor does not claim to author the surrounding CXFS cluster
/// database, XVM volume definition, fencing policy or metadata-server
/// configuration. Extension-only detection avoids colliding with XFS because
/// both use the same <c>XFSB</c> filesystem magic.</para>
/// </summary>
public sealed class CxfsFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveShrinkable,
  IArchiveWriteConstraints,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IFormatOptionsSchema {

  private sealed class LabelPreservingCreator(CxfsFormatDescriptor owner, string volumeLabel) : IArchiveCreatable {
    public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
      var forwarded = options.Copy();
      forwarded.FormatSpecific["VolumeLabel"] = volumeLabel;
      owner.Create(output, inputs, forwarded);
    }
  }

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: "",
      Description: "CXFS/XFS volume label stored in sb_fname (max 12 ASCII chars)."),
  ];

  // CxfsV4Writer currently materialises the complete image in one managed array.
  // With its two equal power-of-two AGs the largest representable profile below
  // Array.MaxLength is 2 * 131072 * 4096 = 1 GiB.
  public long? MaxTotalArchiveSize => 1L << 30;
  public long? MinTotalArchiveSize => CxfsV4Writer.MinAgBlocks * CxfsV4Writer.BlockSize * CxfsV4Writer.AgCount;

  public string AcceptedInputsDescription =>
    "CXFS-compatible XFS v4 image profile: regular files and directories in dir2 short-form, " +
    "4 KiB blocks, 256-byte v2 inodes; cluster/XVM configuration is external.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    var name = input.ArchiveName.Replace('\\', '/').Trim('/');
    if (name.Length == 0) {
      reason = "A CXFS v4 entry name must not be empty.";
      return false;
    }
    if (name.Contains("//", StringComparison.Ordinal)) {
      reason = "A CXFS v4 path must not contain an empty component.";
      return false;
    }
    foreach (var segment in name.Split('/')) {
      if (segment is "." or "..") {
        reason = "A CXFS v4 path must not contain '.' or '..' components.";
        return false;
      }
      if (System.Text.Encoding.UTF8.GetByteCount(segment) > 255) {
        reason = "XFS directory entry names are limited to 255 UTF-8 bytes in this profile.";
        return false;
      }
    }
    reason = null;
    return true;
  }

  public string Id => "Cxfs";
  public string DisplayName => "SGI CXFS (Cluster XFS)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cxfs";
  public IReadOnlyList<string> Extensions => [".cxfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "SGI CXFS filesystem image — XFS on disk with external cluster/XVM state. " +
    "Read support accepts XFS-compatible images; create and mutation use a conservative XFS-v4 (crc=0) CXFS profile.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new CxfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new CxfsReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, e.Name.Replace('\\', '/').TrimStart('/')));
        continue;
      }
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    using var r = new CxfsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase))
      ?? throw new FileNotFoundException($"CXFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var writer = new CxfsV4Writer();
    writer.SetVolumeLabel(options.GetOption("VolumeLabel", ""));
    foreach (var input in inputs) {
      if (!this.CanAccept(input, out var reason))
        throw new NotSupportedException(reason);
      if (input.IsDirectory)
        writer.AddDirectory(input.ArchiveName);
      else
        writer.AddFile(input.ArchiveName, input.ReadContent());
    }
    writer.WriteTo(output);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    EnsureMutationProfile(archive);
    foreach (var input in inputs)
      if (!this.CanAccept(input, out var reason))
        throw new NotSupportedException(reason);

    var creator = CreatorPreservingMetadata(archive);
    RebuildVerb.EditViaRebuild(archive, this, creator, tmpDir => {
      foreach (var input in inputs) {
        var target = Path.Combine(tmpDir, input.ArchiveName.Replace('\\', '/').TrimStart('/'));
        if (input.IsDirectory) {
          Directory.CreateDirectory(target);
          continue;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, input.ReadContent());
      }
    });
  }

  public void Remove(Stream archive, string[] entryNames) {
    EnsureMutationProfile(archive);
    var creator = CreatorPreservingMetadata(archive);
    var remove = new HashSet<string>(entryNames ?? [], StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, creator, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (remove.Contains(relative))
          File.Delete(file);
      }
      foreach (var directory in Directory.GetDirectories(tmpDir, "*", SearchOption.AllDirectories)
                 .OrderByDescending(x => x.Length)) {
        var relative = Path.GetRelativePath(tmpDir, directory).Replace('\\', '/');
        if (remove.Contains(relative) || remove.Contains(relative + "/"))
          Directory.Delete(directory, recursive: true);
      }
    });
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException("The CXFS v4 rebuild profile currently supports ConsolidateAtStart only.");
    EnsureMutationProfile(archive);
    var creator = CreatorPreservingMetadata(archive);
    RebuildVerb.RebuildInPlace(archive, this, creator,
      onProgress: options.OnProgress, cancellationToken: options.CancellationToken);
  }

  public void Shrink(Stream input, Stream output) {
    EnsureMutationProfile(input);
    var creator = CreatorPreservingMetadata(input);
    using var rebuilt = RebuildVerb.CreateScratchStream();
    var useRebuilt = false;
    try {
      RebuildVerb.RebuildToStream(input, rebuilt, this, creator);
      useRebuilt = rebuilt.Length > 0 && rebuilt.Length < input.Length;
    } catch {
      useRebuilt = false;
    }

    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    if (useRebuilt) {
      rebuilt.Position = 0;
      rebuilt.CopyTo(output);
    } else {
      input.CopyTo(output);
    }
  }

  private IArchiveCreatable CreatorPreservingMetadata(Stream archive)
    => new LabelPreservingCreator(this, ReadVolumeLabel(archive));

  private static string ReadVolumeLabel(Stream archive) {
    var original = archive.Position;
    try {
      Span<byte> sb = stackalloc byte[120];
      archive.Position = 0;
      archive.ReadExactly(sb);
      var label = sb[108..120];
      var terminator = label.IndexOf((byte)0);
      if (terminator >= 0)
        label = label[..terminator];
      return System.Text.Encoding.ASCII.GetString(label);
    } finally {
      archive.Position = original;
    }
  }

  private void EnsureMutationProfile(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!CxfsV4Writer.IsSupportedMutationProfile(archive))
      throw new NotSupportedException(
        "CXFS mutation is limited to the conservative XFS-v4/crc=0 profile; broader XFS/CXFS images remain read-only.");

    archive.Position = 0;
    using var reader = new CxfsReader(archive);
    if (!reader.DelegatedToXfs)
      throw new NotSupportedException(
        "CXFS mutation requires an image the XFS layer can walk; detection-only images remain read-only.");
    archive.Position = 0;
  }
}
