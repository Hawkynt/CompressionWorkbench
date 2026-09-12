#pragma warning disable CS1591

namespace FileSystem.CephFs;

/// <summary>
/// Reads the portable serialized pool format produced by <c>rados export</c>.
/// CephFS itself is distributed and has no standalone filesystem image; this
/// reader therefore exposes the RADOS objects contained in an export rather
/// than inventing CephFS pathname semantics that require live MDS metadata.
/// </summary>
public sealed class CephFsReader : IDisposable {
  private readonly List<CephFsEntry> _entries = [];

  internal RadosPoolDump.ParsedDump Parsed { get; }

  /// <summary>Ceph RADOS dump super magic as serialized little-endian.</summary>
  public static readonly byte[] RadosExportMagic = [0xCE, 0xFF, 0xCE, 0xFF];

  /// <summary>Gets the RADOS object entries.</summary>
  public IReadOnlyList<CephFsEntry> Entries => this._entries;

  /// <summary>Gets the serialized dump version.</summary>
  public uint Version { get; }

  /// <summary>Whether the parsed dump can be safely rewritten without dropping unknown future sections.</summary>
  public bool CanRewrite => this.Parsed.CanRewrite;

  /// <summary>Initializes a reader over a portable RADOS pool dump.</summary>
  public CephFsReader(Stream stream) {
    this.Parsed = RadosPoolDump.Parse(stream);
    this.Version = RadosPoolDump.SuperVersion;
    foreach (var obj in this.Parsed.Objects) {
      this._entries.Add(new CephFsEntry {
        Name = obj.EntryName,
        Size = obj.Data.LongLength,
        IsDirectory = false,
        Offset = obj.FirstPayloadOffset,
        Data = obj.Data,
        ObjectId = obj.ObjectId,
        Namespace = obj.Namespace,
        LocatorKey = obj.LocatorKey,
        Attributes = obj.Attributes,
        OmapHeader = obj.OmapHeader,
        Omap = obj.Omap,
      });
    }
  }

  /// <summary>Returns an object's data bytes.</summary>
  public byte[] Extract(CephFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>Releases resources held by this reader.</summary>
  public void Dispose() { }
}