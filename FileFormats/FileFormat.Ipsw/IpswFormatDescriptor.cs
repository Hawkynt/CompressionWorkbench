#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ipsw;

/// <summary>
/// Apple IPSW / OTA firmware package. IPSW files are ZIP containers whose entry paths are
/// semantically significant: manifests refer to firmware members by their ZIP path, so the
/// descriptor exposes those paths losslessly instead of projecting them into a flattened view.
/// </summary>
/// <remarks>
/// <para>The ZIP container itself is mutable. Inner DMG, IMG4/IM4P and other firmware payloads
/// remain opaque byte streams and are handled by their own descriptors when opened separately.</para>
/// <para>Rebuild maintenance (defrag/shrink) re-encodes only the ZIP envelope. Entry bytes and
/// entry paths are preserved; Apple restore tooling also accepts an extracted IPSW directory,
/// so ZIP physical offsets are not part of the firmware payload identity.</para>
/// <para><c>FULL.ipsw</c> and <c>metadata.ini</c> are synthetic views reserved by this descriptor
/// and are never written back as ZIP members.</para>
/// </remarks>
public sealed class IpswFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveLayoutMap,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IArchiveShrinkable,
  ISyntheticEntryNames {

  private static readonly HashSet<string> SyntheticEntries = new(StringComparer.OrdinalIgnoreCase) {
    "FULL.ipsw",
    "metadata.ini",
  };

  private sealed record IpswEntry(
    string Name,
    long Size,
    long CompressedSize,
    string Method,
    bool IsDirectory,
    DateTime? LastModified,
    string Kind);

  public IReadOnlySet<string> SyntheticEntryNames => SyntheticEntries;

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive)
    => FileFormat.Zip.ZipLayoutMap.Enumerate(archive);

  public string Id => "Ipsw";
  public string DisplayName => "Apple IPSW";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ipsw";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [".ipsw", ".otazip"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Apple firmware package (ZIP containing BuildManifest.plist, Firmware/, boot firmware and disk images). " +
    "R/W edits operate on the ZIP namespace; defrag/shrink rebuild the ZIP envelope without changing payload bytes.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var entries = EnumerateEntries(stream);
    var result = new List<ArchiveEntryInfo>(entries.Count + SyntheticEntries.Count) {
      new(0, "FULL.ipsw", stream.Length, stream.Length, "Stored", false, false, null, Kind: "container"),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: $"total_zip_entries={entries.Count}"),
    };

    foreach (var entry in entries) {
      if (IsSynthetic(entry.Name))
        continue;
      result.Add(new ArchiveEntryInfo(
        result.Count,
        entry.Name,
        entry.Size,
        entry.CompressedSize,
        entry.Method,
        entry.IsDirectory,
        false,
        entry.LastModified,
        Kind: entry.Kind));
    }

    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);

    if (Wants(files, "FULL.ipsw")) {
      stream.Position = 0;
      var fullPath = SafeCombine(outputDir, "FULL.ipsw");
      Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
      using var output = File.Create(fullPath);
      stream.CopyTo(output);
    }

    stream.Position = 0;
    using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    foreach (var entry in zip.Entries) {
      var name = NormalizeZipPath(entry.FullName);
      if (IsSynthetic(name) || !Wants(files, name))
        continue;

      var destination = SafeCombine(outputDir, name);
      if (IsDirectoryEntry(entry)) {
        Directory.CreateDirectory(destination);
        continue;
      }

      var parent = Path.GetDirectoryName(destination);
      if (!string.IsNullOrEmpty(parent))
        Directory.CreateDirectory(parent);
      using var source = entry.Open();
      using var output = File.Create(destination);
      source.CopyTo(output);
    }

    if (Wants(files, "metadata.ini")) {
      var metadata = BuildMetadata(zip);
      var metadataPath = SafeCombine(outputDir, "metadata.ini");
      Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
      File.WriteAllBytes(metadataPath, metadata);
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

    archive.Position = 0;
    if (string.Equals(entryName, "FULL.ipsw", StringComparison.OrdinalIgnoreCase))
      return new BoundedEntryStream(archive, archive.Length, leaveOpen: true);

    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
      var metadata = BuildMetadata(zip);
      return new BoundedEntryStream(new MemoryStream(metadata, writable: false), metadata.Length, leaveOpen: false);
    }

    var reader = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
    var normalized = NormalizeZipPath(entryName);
    var entry = reader.Entries.FirstOrDefault(candidate =>
      string.Equals(NormalizeZipPath(candidate.FullName), normalized, StringComparison.OrdinalIgnoreCase));
    if (entry == null || IsDirectoryEntry(entry)) {
      reader.Dispose();
      return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
    }

    var owned = new ZipOwnedReadStream(entry.Open(), reader);
    return new BoundedEntryStream(owned, entry.Length, leaveOpen: false);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!output.CanWrite)
      throw new ArgumentException("IPSW creation requires a writable output stream.", nameof(output));

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }

    using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
    foreach (var input in inputs) {
      var name = NormalizeZipPath(input.ArchiveName);
      if (IsSynthetic(name))
        continue;

      if (input.IsDirectory) {
        if (!name.EndsWith('/'))
          name += '/';
        zip.CreateEntry(name, CompressionLevel.NoCompression);
        continue;
      }

      var size = input.InMemoryContent?.LongLength ?? new FileInfo(input.FullPath).Length;
      var entry = zip.CreateEntry(name, SelectCompression(name, size));
      TryApplyTimestamp(entry, input);
      using var destination = entry.Open();
      if (input.InMemoryContent is { } bytes) {
        destination.Write(bytes);
      } else {
        using var source = File.OpenRead(input.FullPath);
        source.CopyTo(destination);
      }
    }
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var name = NormalizeZipPath(input.ArchiveName);
      if (IsSynthetic(name))
        continue;
      IpswInPlaceModifier.AddEntry(archive, name, input.ReadContent());
    }
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames) {
      var normalized = NormalizeZipPath(name);
      if (IsSynthetic(normalized))
        continue;
      IpswInPlaceModifier.RemoveEntry(archive, normalized);
    }
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException($"IPSW defrag supports only {DefragMode.ConsolidateAtStart}.");
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("IPSW defrag requires a readable, writable, seekable stream.", nameof(archive));

    using var rebuilt = CreateScratchStream();
    RebuildVerb.RebuildToStream(
      archive,
      rebuilt,
      this,
      this,
      syntheticNames: SyntheticEntries,
      onProgress: options.OnProgress,
      cancellationToken: options.CancellationToken);

    options.CancellationToken.ThrowIfCancellationRequested();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "committing", 0.99, -1, 0, Math.Max(1, rebuilt.Length), null,
      "Committing verified IPSW rebuild"));

    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();

    archive.Position = 0;
    var finalLayout = this.EnumerateLayout(archive).ToArray();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, Math.Max(1, archive.Length), finalLayout,
      "IPSW rebuild committed successfully"));
  }

  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("IPSW shrink requires a readable, seekable input stream.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("IPSW shrink requires a writable, seekable output stream.", nameof(output));

    using var rebuilt = CreateScratchStream();
    var useRebuilt = false;
    try {
      RebuildVerb.RebuildToStream(input, rebuilt, this, this, syntheticNames: SyntheticEntries);
      useRebuilt = rebuilt.Length > 0 && rebuilt.Length < input.Length;
    } catch {
      useRebuilt = false;
    }

    if (ReferenceEquals(input, output)) {
      if (!useRebuilt) {
        input.Position = 0;
        return;
      }
      output.Position = 0;
      output.SetLength(0);
      rebuilt.Position = 0;
      rebuilt.CopyTo(output);
      output.Flush();
      output.Position = 0;
      return;
    }

    output.Position = 0;
    output.SetLength(0);
    if (useRebuilt) {
      rebuilt.Position = 0;
      rebuilt.CopyTo(output);
    } else {
      input.Position = 0;
      input.CopyTo(output);
    }
    output.Flush();
    output.Position = 0;
  }

  private static List<IpswEntry> EnumerateEntries(Stream stream) {
    stream.Position = 0;
    using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    var result = new List<IpswEntry>(zip.Entries.Count);
    foreach (var entry in zip.Entries) {
      var name = NormalizeZipPath(entry.FullName);
      var isDirectory = IsDirectoryEntry(entry);
      var method = entry.CompressedLength == entry.Length ? "Stored" : "Deflate";
      result.Add(new IpswEntry(
        name,
        entry.Length,
        entry.CompressedLength,
        method,
        isDirectory,
        entry.LastWriteTime.DateTime,
        ClassifyKind(name, isDirectory)));
    }
    return result;
  }

  private static byte[] BuildMetadata(ZipArchive zip) {
    string? identifier = null;
    string? productVersion = null;
    string? buildVersion = null;
    var manifest = zip.Entries.FirstOrDefault(entry =>
      string.Equals(NormalizeZipPath(entry.FullName), "BuildManifest.plist", StringComparison.OrdinalIgnoreCase));
    if (manifest != null && manifest.Length <= 16 * 1024 * 1024) {
      using var source = manifest.Open();
      using var memory = new MemoryStream();
      source.CopyTo(memory);
      TryParsePlistFields(memory.ToArray(), out identifier, out productVersion, out buildVersion);
    }
    return Encoding.UTF8.GetBytes(BuildMetadataIni(identifier, productVersion, buildVersion, zip.Entries.Count));
  }

  private static string ClassifyKind(string name, bool isDirectory) {
    if (isDirectory)
      return "directory";
    var fileName = LeafName(name);
    if (string.Equals(name, "BuildManifest.plist", StringComparison.OrdinalIgnoreCase))
      return "manifest";
    if (string.Equals(name, "Restore.plist", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "RestoreVersion.plist", StringComparison.OrdinalIgnoreCase))
      return "restore-metadata";
    if (name.StartsWith("Firmware/", StringComparison.OrdinalIgnoreCase))
      return "firmware";
    if (IsBootloaderStage(fileName))
      return "bootloader";
    if (fileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
      return "disk-image";
    return "other";
  }

  private static CompressionLevel SelectCompression(string name, long size) {
    var fileName = LeafName(name);
    if (size >= 32L * 1024 * 1024 ||
        fileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".img4", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".im4p", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".im4m", StringComparison.OrdinalIgnoreCase))
      return CompressionLevel.NoCompression;
    return CompressionLevel.Optimal;
  }

  private static void TryApplyTimestamp(ZipArchiveEntry entry, ArchiveInputInfo input) {
    if (input.InMemoryContent != null || !File.Exists(input.FullPath))
      return;
    try {
      entry.LastWriteTime = File.GetLastWriteTime(input.FullPath);
    } catch (ArgumentOutOfRangeException) {
      // ZIP/DOS timestamps are bounded to 1980..2107; payload data is unaffected.
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static bool IsSynthetic(string name)
    => SyntheticEntries.Contains(name);

  private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

  private static string NormalizeZipPath(string name)
    => name.Replace('\\', '/').TrimStart('/');

  private static string LeafName(string name) {
    var normalized = NormalizeZipPath(name).TrimEnd('/');
    var slash = normalized.LastIndexOf('/');
    return slash < 0 ? normalized : normalized[(slash + 1)..];
  }

  private static bool IsBootloaderStage(string filename)
    => filename.StartsWith("LLB.", StringComparison.OrdinalIgnoreCase) ||
       filename.StartsWith("iBSS.", StringComparison.OrdinalIgnoreCase) ||
       filename.StartsWith("iBEC.", StringComparison.OrdinalIgnoreCase) ||
       filename.StartsWith("iBoot.", StringComparison.OrdinalIgnoreCase);

  private static string SafeCombine(string baseDir, string entryName) {
    var normalized = NormalizeZipPath(entryName);
    var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Any(segment => segment is "." or ".."))
      throw new InvalidDataException($"IPSW entry path escapes extraction root: {entryName}");

    var root = Path.GetFullPath(baseDir);
    var candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
    var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException($"IPSW entry path escapes extraction root: {entryName}");
    return candidate;
  }

  private static void TryParsePlistFields(byte[] data, out string? identifier, out string? productVersion, out string? buildVersion) {
    identifier = null;
    productVersion = null;
    buildVersion = null;
    if (data.Length < 16)
      return;
    if (data.AsSpan().StartsWith("bplist"u8))
      return;

    var text = Encoding.UTF8.GetString(data);
    productVersion = FindStringValue(text, "ProductVersion");
    buildVersion = FindStringValue(text, "ProductBuildVersion") ?? FindStringValue(text, "BuildVersion");
    identifier = FindStringValue(text, "ProductType") ?? FindStringValue(text, "Identifier");
  }

  private static string? FindStringValue(string plistXml, string key) {
    var keyTag = $"<key>{key}</key>";
    var keyIndex = plistXml.IndexOf(keyTag, StringComparison.Ordinal);
    if (keyIndex < 0)
      return null;
    var openIndex = plistXml.IndexOf("<string>", keyIndex + keyTag.Length, StringComparison.Ordinal);
    if (openIndex < 0)
      return null;
    var closeIndex = plistXml.IndexOf("</string>", openIndex + 8, StringComparison.Ordinal);
    return closeIndex < 0 ? null : plistXml[(openIndex + 8)..closeIndex];
  }

  private static string BuildMetadataIni(string? identifier, string? productVersion, string? buildVersion, int totalZipEntries) {
    var builder = new StringBuilder();
    builder.Append("[Ipsw]\n");
    builder.Append(CultureInfo.InvariantCulture, $"identifier={identifier ?? string.Empty}\n");
    builder.Append(CultureInfo.InvariantCulture, $"product_version={productVersion ?? string.Empty}\n");
    builder.Append(CultureInfo.InvariantCulture, $"build_version={buildVersion ?? string.Empty}\n");
    builder.Append(CultureInfo.InvariantCulture, $"total_zip_entries={totalZipEntries}\n");
    return builder.ToString();
  }

  private static FileStream CreateScratchStream()
    => new(
      Path.Combine(Path.GetTempPath(), "cwb_ipsw_" + Guid.NewGuid().ToString("N") + ".tmp"),
      FileMode.CreateNew,
      FileAccess.ReadWrite,
      FileShare.None,
      64 * 1024,
      FileOptions.DeleteOnClose);

  private sealed class ZipOwnedReadStream(Stream inner, ZipArchive owner) : Stream {
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
      if (disposing) {
        inner.Dispose();
        owner.Dispose();
      }
      base.Dispose(disposing);
    }
  }
}
