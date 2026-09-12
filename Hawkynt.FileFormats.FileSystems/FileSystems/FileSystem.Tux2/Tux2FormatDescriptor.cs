#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ext;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux2;

/// <summary>
/// Descriptor for Daniel Phillips's TUX2 phase-tree research filesystem.
/// </summary>
/// <remarks>
/// <para>The 2000 TUX2 announcement described the project as an Ext2 variation and explicitly
/// required an existing Ext2 partition to be mountable as TUX2. It did not publish a stable,
/// independently identifying TUX2 disk signature. Accordingly this descriptor never claims that
/// the Ext2 magic identifies TUX2 and never invents a private TUX2 container.</para>
/// <para>For structurally valid pointer-based Ext2 images, normal file access and offline
/// maintenance are delegated to CompressionWorkbench's Ext2 implementation. Creation likewise
/// emits that conservative Ext2 interoperability profile. Unknown images retain the opaque
/// <c>FULL.tux2</c> + metadata surface and are never modified.</para>
/// </remarks>
public sealed class Tux2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable,
  IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema,
  ILayoutOptimizable, ISyntheticEntryNames {

  private static readonly ExtFormatDescriptor Ext = new();
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.tux2", "metadata.ini" };
  private static readonly IReadOnlyList<FormatOptionDescriptor> Ext2OptionsSchema =
    Ext.OptionsSchema.Where(option => option.Key is "BlockSize" or "VolumeLabel").ToArray();

  /// <inheritdoc />
  public IReadOnlySet<string> SyntheticEntryNames => SyntheticNames;

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => Ext2OptionsSchema;

  /// <inheritdoc />
  public string Id => "Tux2";

  /// <inheritdoc />
  public string DisplayName => "TUX2";

  /// <inheritdoc />
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc />
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <inheritdoc />
  public string DefaultExtension => ".tux2";

  /// <inheritdoc />
  public IReadOnlyList<string> Extensions => [".tux2"];

  /// <inheritdoc />
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc />
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc />
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <inheritdoc />
  public string? TarCompressionFormatId => null;

  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc />
  public string Description =>
    "TUX2 phase-tree research filesystem — R/W and maintenance cover only its published Ext2-compatible profile; unknown images remain opaque.";

  /// <inheritdoc />
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (CanUseExt2CompatibilityProfile(stream)) {
      stream.Position = 0;
      return Ext.List(stream, password);
    }

    using var reader = new Tux2Reader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", entry.IsDirectory, false, null)).ToList();
  }

  /// <inheritdoc />
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (CanUseExt2CompatibilityProfile(stream)) {
      stream.Position = 0;
      Ext.Extract(stream, outputDir, password, files);
      return;
    }

    using var reader = new Tux2Reader(stream);
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
  /// Creates the Ext2 interoperability profile TUX2 explicitly targeted. This is not a
  /// reconstruction of an unpublished standalone phase-tree serialization.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    Ext.Create(output, inputs, AsExt2Options(options));
  }

  /// <inheritdoc />
  public void CreateFromStreams(
      Stream target,
      IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs,
      FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    Ext.CreateFromStreams(target, inputs, AsExt2Options(options));
  }

  /// <inheritdoc />
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    RequireExt2CompatibilityProfile(archive);
    archive.Position = 0;
    Ext.Add(archive, inputs);
  }

  /// <inheritdoc />
  public void Remove(Stream archive, string[] entryNames) {
    RequireExt2CompatibilityProfile(archive);
    archive.Position = 0;
    Ext.Remove(archive, entryNames);
  }

  /// <inheritdoc />
  public void Defragment(Stream archive) {
    RequireExt2CompatibilityProfile(archive);
    archive.Position = 0;
    Ext.Defragment(archive);
  }

  /// <inheritdoc />
  public void Defragment(Stream archive, DefragOptions options) {
    RequireExt2CompatibilityProfile(archive);
    archive.Position = 0;
    Ext.Defragment(archive, options);
  }

  /// <inheritdoc />
  public void Shrink(Stream input, Stream output) {
    RequireExt2CompatibilityProfile(input);
    input.Position = 0;
    Ext.Shrink(input, output);
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    RequireExt2CompatibilityProfile(image);
    image.Position = 0;
    return Ext.WipeUnusedSpace(image, wipeClusterTips, wipeDeletedEntries);
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    RequireExt2CompatibilityProfile(image);
    image.Position = 0;
    return Ext.EnumerateExtents(image);
  }

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    RequireExt2CompatibilityProfile(image);
    image.Position = 0;
    Ext.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(
      Stream image, string fileName, long oldOffset, long newOffset, long length) {
    RequireExt2CompatibilityProfile(image);
    image.Position = 0;
    Ext.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  /// <inheritdoc />
  public LayoutAnalysis AnalyzeLayout(Stream image) {
    RequireExt2CompatibilityProfile(image);
    image.Position = 0;
    return Ext.AnalyzeLayout(image);
  }

  /// <inheritdoc />
  public void PatchInPlace(Stream image, LayoutPatch patch) {
    RequireExt2CompatibilityProfile(image);
    image.Position = 0;
    Ext.PatchInPlace(image, patch);
  }

  /// <inheritdoc />
  public LayoutReclaim ReclaimSupport => Ext.ReclaimSupport;

  /// <inheritdoc />
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    RequireExt2CompatibilityProfile(source);
    source.Position = 0;
    Ext.RebuildStreaming(source, target, options);
  }

  private static FormatCreateOptions AsExt2Options(FormatCreateOptions options) {
    var result = options.Copy();
    result.FormatSpecific["Version"] = "ext2";
    result.FormatSpecific["Journal"] = "false";
    result.FormatSpecific["InodeSize"] = "128";
    return result;
  }

  private static bool CanUseExt2CompatibilityProfile(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek) return false;

    var originalPosition = stream.Position;
    try {
      using var reader = new Tux2Reader(stream);
      if (!reader.IsSupportedExt2CompatibilityProfile) return false;

      // The superblock gate intentionally stays cheap. One normal Ext walk additionally proves
      // that the inode/group geometry is coherent enough for the delegated operations before the
      // descriptor stops offering the opaque forensic fallback.
      stream.Position = 0;
      _ = Ext.List(stream, null);
      return true;
    } catch (InvalidDataException) {
      return false;
    } catch (IOException) {
      return false;
    } catch (ArgumentException) {
      return false;
    } catch (ArithmeticException) {
      return false;
    } catch (IndexOutOfRangeException) {
      return false;
    } finally {
      stream.Position = Math.Min(originalPosition, stream.Length);
    }
  }

  private static void RequireExt2CompatibilityProfile(Stream stream) {
    if (CanUseExt2CompatibilityProfile(stream)) return;
    throw new InvalidDataException(
      "TUX2 mutation and maintenance require a structurally valid pointer-based Ext2 compatibility image; " +
      "no standalone TUX2 phase-tree disk format is reconstructed or guessed.");
  }
}
