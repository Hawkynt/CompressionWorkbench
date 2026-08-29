#pragma warning disable CS1591
using Compression.Core.Deflate;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Egg;

/// <summary>
/// EGG (ALZip) archive — ESTsoft's native container for newer ALZip versions,
/// with Unicode filenames, per-file algorithm selection, solid/split modes, and
/// optional encryption. The native implementation reads Store/Deflate and creates
/// non-solid, single-volume Store/Deflate archives; unsupported methods are listed
/// honestly and rejected on extraction rather than returning incorrect data.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/alkegi/docs/blob/master/egg.md</c> — CC0 EGG Archive Format Specification</description></item>
///   <item><description><c>http://justsolve.archiveteam.org/wiki/EGG_(ALZip)</c> — ArchiveTeam format summary and historical references</description></item>
///   <item><description><c>EGG Format Specification, Version 1.0</c> (ESTsoft Corp.) — original published layout</description></item>
/// </list>
/// </summary>
public sealed class EggFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable {

  public string Id => "Egg";
  public string DisplayName => "EGG";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".egg";
  public IReadOnlyList<string> Extensions => [".egg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'E', (byte)'G', (byte)'G', (byte)'A'], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("store", "Store"),
    new("deflate", "Deflate", SupportsOptimize: true),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ESTsoft ALZip EGG archive (Store/Deflate read + write)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new EggReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.UncompressedSize, e.CompressedSize,
      e.MethodName, e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new EggReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files))
        continue;
      if (e.IsDirectory) {
        CreateSafeDirectory(outputDir, e.Name);
        continue;
      }
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The reader materialises the
  /// entry's decoded bytes; the result is wrapped in a bounded stream sized to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var r = new EggReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>Creates a single-volume, non-solid EGG archive using Store/Deflate.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!string.IsNullOrEmpty(options.Password) || !string.IsNullOrEmpty(options.EncryptionMethod))
      throw new NotSupportedException("Native EGG creation does not yet implement encryption.");

    var requestedMethod = ResolveMethod(options.MethodName);
    var level = ResolveLevel(options);
    using var writer = new EggWriter(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        writer.AddDirectory(input.ArchiveName);
        continue;
      }

      var method = requestedMethod;
      if (method == EggCompressionMethod.Auto) {
        if (options.ForceCompress)
          method = EggCompressionMethod.Deflate;
        else if (options.IncompressiblePaths?.Contains(input.ArchiveName) == true)
          method = EggCompressionMethod.Store;
      }
      writer.AddEntry(input.ArchiveName, input.ReadContent(), method, level);
    }
  }

  private static EggCompressionMethod ResolveMethod(string? methodName) {
    if (string.IsNullOrWhiteSpace(methodName) || methodName.Equals("auto", StringComparison.OrdinalIgnoreCase))
      return EggCompressionMethod.Auto;
    if (methodName.Equals("store", StringComparison.OrdinalIgnoreCase)
        || methodName.Equals("stored", StringComparison.OrdinalIgnoreCase))
      return EggCompressionMethod.Store;
    if (methodName.Equals("deflate", StringComparison.OrdinalIgnoreCase))
      return EggCompressionMethod.Deflate;
    throw new NotSupportedException($"Native EGG creation supports Store and Deflate, not '{methodName}'.");
  }

  private static DeflateCompressionLevel ResolveLevel(FormatCreateOptions options) {
    if (options.Optimize)
      return DeflateCompressionLevel.Maximum;
    return options.Level switch {
      null => DeflateCompressionLevel.Default,
      <= 0 => DeflateCompressionLevel.None,
      <= 3 => DeflateCompressionLevel.Fast,
      <= 8 => DeflateCompressionLevel.Default,
      _ => DeflateCompressionLevel.Best,
    };
  }

  private static void CreateSafeDirectory(string baseDir, string entryName) {
    var safeName = entryName.Replace('\\', '/').TrimStart('/');
    if (safeName.Contains("..", StringComparison.Ordinal))
      safeName = Path.GetFileName(safeName);
    if (safeName.Length == 0)
      return;
    Directory.CreateDirectory(Path.Combine(baseDir, safeName));
  }
}
