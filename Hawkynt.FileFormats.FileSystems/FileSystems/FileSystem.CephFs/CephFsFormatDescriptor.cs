#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.CephFs;

/// <summary>
/// Portable RADOS pool export support for Ceph / CephFS data pools.
///
/// <para>CephFS itself has no single disk image: pathname/inode metadata lives in
/// an MDS-managed RADOS metadata pool and file data is distributed across RADOS
/// objects. This descriptor therefore operates at the honest self-contained
/// boundary: the serialized object stream produced by <c>rados export</c> and
/// consumed by <c>rados import</c>.</para>
///
/// <para>The pool dump contains object identifiers, namespaces, locator keys,
/// object bytes, user xattrs, OMAP headers and OMAP entries. Those semantics are
/// sufficient for offline object-level create/add/replace/remove, purge,
/// canonical shrink/compact and a complete byte-layout map without librados or
/// a live cluster. They are not sufficient to reconstruct CephFS pathnames.</para>
///
/// <para>Wire-format reference: Ceph <c>src/tools/RadosDump.*</c>,
/// <c>src/tools/rados/PoolDump.*</c> and <c>RadosImport.*</c>. Those files are
/// LGPL-2.1; this implementation was independently written from their public
/// serialized behavior and format constants.</para>
/// </summary>
public sealed class CephFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveShrinkable, IArchiveLayoutMap {

  public string Id => "CephFs";
  public string DisplayName => "CephFS / RADOS pool export";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest
    | FormatCapabilities.CanCreate | FormatCapabilities.CanModify | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rados";
  public IReadOnlyList<string> Extensions => [".rados", ".ceph"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(CephFsReader.RadosExportMagic, Offset: 0, Confidence: 0.99),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Portable Ceph RADOS pool export ('rados export') with object-level R/W. " +
    "Preserves object bytes, namespaces, locator keys, user xattrs and OMAP state; " +
    "supports canonical shrink/compact, layout, wipe of importer-ignored trailing bytes, and purge. " +
    "CephFS pathname reconstruction remains out of scope because it requires the distributed MDS metadata pool and cluster state.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ResetForRead(stream);
    using var reader = new CephFsReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ResetForRead(stream);
    using var reader = new CephFsReader(stream);
    foreach (var entry in reader.Entries) {
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      WriteFile(outputDir, entry.Name, entry.Data);
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    ResetForRead(archive);
    using var reader = new CephFsReader(archive);
    var entry = reader.Entries.FirstOrDefault(candidate => string.Equals(candidate.Name, entryName, StringComparison.Ordinal))
      ?? throw new FileNotFoundException($"RADOS object entry not found: {entryName}");
    return new BoundedEntryStream(new MemoryStream(entry.Data, writable: false), entry.Data.LongLength, leaveOpen: false);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var objects = new List<RadosPoolDump.ObjectModel>();
    var byIdentity = new Dictionary<(string Namespace, string ObjectId), int>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var identity = RadosPoolDump.DecodeEntryName(input.ArchiveName);
      var model = new RadosPoolDump.ObjectModel {
        Namespace = identity.Namespace,
        ObjectId = identity.ObjectId,
        Data = input.ReadContent(),
      };
      var key = (identity.Namespace, identity.ObjectId);
      if (byIdentity.TryGetValue(key, out var existing))
        objects[existing] = model;
      else {
        byIdentity.Add(key, objects.Count);
        objects.Add(model);
      }
    }
    RadosPoolDump.Write(output, objects);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var parsed = ReadForRewrite(archive);
    var byIdentity = parsed.Objects.ToDictionary(
      static obj => (obj.Namespace, obj.ObjectId), static obj => obj);

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var identity = RadosPoolDump.DecodeEntryName(input.ArchiveName);
      var key = (identity.Namespace, identity.ObjectId);
      if (byIdentity.TryGetValue(key, out var existing)) {
        existing.Data = input.ReadContent();
        continue;
      }
      var added = new RadosPoolDump.ObjectModel {
        Namespace = identity.Namespace,
        ObjectId = identity.ObjectId,
        Data = input.ReadContent(),
      };
      parsed.Objects.Add(added);
      byIdentity.Add(key, added);
    }

    Rewrite(archive, parsed.Objects);
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var parsed = ReadForRewrite(archive);
    var identities = new HashSet<(string Namespace, string ObjectId)>();
    foreach (var name in entryNames) {
      if (string.IsNullOrEmpty(name)) continue;
      var identity = RadosPoolDump.DecodeEntryName(name);
      identities.Add((identity.Namespace, identity.ObjectId));
    }
    parsed.Objects.RemoveAll(obj => identities.Contains((obj.Namespace, obj.ObjectId)));
    Rewrite(archive, parsed.Objects);
  }

  /// <summary>
  /// Canonicalizes the serialized pool stream and keeps it only when smaller.
  /// The canonical writer coalesces DATA writes, xattr/OMAP updates and drops
  /// object_info bytes that Ceph's pool importer explicitly ignores.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    byte[] original;
    try {
      ResetForRead(input);
      using var copy = new MemoryStream();
      input.CopyTo(copy);
      original = copy.ToArray();
    } catch {
      throw;
    }

    byte[] selected = original;
    try {
      var parsed = RadosPoolDump.Parse(original);
      if (parsed.CanRewrite) {
        using var canonical = new MemoryStream();
        RadosPoolDump.Write(canonical, parsed.Objects);
        if (canonical.Length < original.LongLength)
          selected = canonical.ToArray();
      }
    } catch {
      selected = original;
    }

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }
    output.Write(selected);
  }

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    ResetForRead(archive);
    RadosPoolDump.ParsedDump parsed;
    try {
      using var reader = new CephFsReader(archive);
      parsed = reader.Parsed;
    } catch {
      yield break;
    }

    foreach (var extent in parsed.Layout.OrderBy(static e => e.Offset)) {
      if (extent.Length <= 0) continue;
      yield return new DefragBlockInfo(
        extent.Offset,
        extent.Length,
        extent.IsFree ? DefragBlockKind.Free : extent.IsPayload ? DefragBlockKind.Used : DefragBlockKind.MetadataReserved,
        extent.EntryName);
    }
  }

  private static RadosPoolDump.ParsedDump ReadForRewrite(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("RADOS modification requires a readable, writable, seekable stream.", nameof(archive));
    archive.Position = 0;
    using var reader = new CephFsReader(archive);
    if (!reader.CanRewrite)
      throw new NotSupportedException("This RADOS dump contains future/unknown serialized fields; refusing to rewrite and silently discard them.");
    return reader.Parsed;
  }

  private static void Rewrite(Stream archive, IReadOnlyList<RadosPoolDump.ObjectModel> objects) {
    using var staged = new MemoryStream();
    RadosPoolDump.Write(staged, objects);
    archive.Position = 0;
    archive.SetLength(0);
    staged.Position = 0;
    staged.CopyTo(archive);
    archive.Flush();
    archive.Position = 0;
  }

  private static void ResetForRead(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek)
      stream.Position = 0;
  }
}