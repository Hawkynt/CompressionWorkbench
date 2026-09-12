#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileSystem.TahoeLafs;

/// <summary>
/// Reads Tahoe-LAFS storage-server share-container files. The outer storage
/// container is parsed, while the contained immutable/mutable Tahoe share data
/// remains an opaque capability-protected blob.
/// </summary>
public sealed class TahoeLafsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<TahoeLafsEntry> _entries = [];
  private TahoeLafsContainerLayout _layout;

  /// <summary>Gets the rendered entries.</summary>
  public IReadOnlyList<TahoeLafsEntry> Entries => _entries;

  /// <summary>Gets whether this is an immutable or mutable storage-share container.</summary>
  public TahoeLafsShareKind ShareKind { get; private set; }

  /// <summary>Gets the storage-container schema version.</summary>
  public uint Version { get; private set; }

  /// <summary>
  /// Gets the actual opaque share-data length in bytes. Retained as <see cref="uint"/>
  /// for source compatibility with the original reader; the current reader is
  /// byte-array backed and therefore cannot represent a payload approaching 4 GiB.
  /// </summary>
  public uint DataSize { get; private set; }

  /// <summary>Gets the actual opaque share-data length without the legacy API width.</summary>
  public long ActualDataSize { get; private set; }

  /// <summary>
  /// Gets the legacy 32-bit immutable data-length header field. Modern Tahoe
  /// storage servers do not use this field to locate the immutable lease tail.
  /// Mutable containers do not have this field.
  /// </summary>
  public uint? HeaderDataSize { get; private set; }

  /// <summary>Gets the number of active leases represented by the container.</summary>
  public uint LeaseCount { get; private set; }

  /// <summary>Gets the byte offset of the immutable lease list or mutable extra-lease table.</summary>
  public long LeaseOffset { get; private set; }

  /// <summary>Gets bytes proven unused by the outer storage container.</summary>
  public long FreeSpace { get; private set; }

  /// <summary>Gets whether a recognized, structurally bounded header was parsed.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>Initializes a new Tahoe-LAFS share-container reader.</summary>
  public TahoeLafsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek)
      stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    _layout = TahoeLafsContainer.Parse(_data);
    this.ShareKind = _layout.Kind;
    this.Version = _layout.Version;
    this.ActualDataSize = _layout.DataLength;
    this.DataSize = checked((uint)_layout.DataLength);
    this.HeaderDataSize = _layout.LegacyDataLength;
    this.LeaseCount = _layout.LeaseCount;
    this.LeaseOffset = _layout.LeaseOffset;
    this.FreeSpace = _layout.FreeSpace;
    this.ValidHeader = true;

    var metadata = BuildMetadata();
    _entries.Add(new TahoeLafsEntry {
      Name = "FULL.tahoe-share",
      Size = _data.LongLength,
      IsDirectory = false,
      Data = _data,
    });
    _entries.Add(new TahoeLafsEntry {
      Name = "metadata.ini",
      Size = metadata.LongLength,
      IsDirectory = false,
      Data = metadata,
    });

    if (_layout.DataLength > 0) {
      var payload = _data.AsSpan(_layout.DataOffset, checked((int)_layout.DataLength)).ToArray();
      _entries.Add(new TahoeLafsEntry {
        Name = _layout.Kind == TahoeLafsShareKind.Immutable ? "share.immutable.bin" : "share.mutable.bin",
        Size = payload.LongLength,
        IsDirectory = false,
        Data = payload,
      });
    }
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=Tahoe-LAFS storage share\n");
    bldr.Append("share_kind=").Append(this.ShareKind == TahoeLafsShareKind.Immutable ? "immutable" : "mutable").Append('\n');
    bldr.Append(CultureInfo.InvariantCulture, $"container_version={this.Version}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"data_size={this.ActualDataSize}\n");
    if (this.HeaderDataSize is { } headerDataSize)
      bldr.Append(CultureInfo.InvariantCulture, $"legacy_header_data_size={headerDataSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"lease_count={this.LeaseCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"lease_offset={this.LeaseOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"unused_bytes={this.FreeSpace}\n");
    bldr.Append("note=Share data is capability-protected and is surfaced without decryption or erasure decoding.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>Returns the bytes of a rendered entry.</summary>
  public byte[] Extract(TahoeLafsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() { }
}
