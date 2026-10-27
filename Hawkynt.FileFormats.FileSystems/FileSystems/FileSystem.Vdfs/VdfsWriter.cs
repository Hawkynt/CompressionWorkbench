using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.DiskImage;

namespace FileSystem.Vdfs;

/// <summary>
/// Writes a ZenGin VDF volume — stored, or zipped the way Union zips one — from a set of files.
/// </summary>
/// <remarks>
/// The layout follows GothicVDFS: the 296-byte header, the catalog straight behind it, then
/// the payloads, deepest directory first. With the comment and timestamp of a GothicVDFS
/// volume, a stored volume of the same files comes out byte-identical to it.
/// </remarks>
public sealed class VdfsWriter {

  private readonly List<(string Path, FilePayload Payload)> _files = [];

  /// <summary>Whether payloads are written as Union ZippedStreams (volume flags 0xA0).</summary>
  public bool Zipped { get; init; }

  /// <summary>Deflate effort for zipped payloads.</summary>
  public DeflateCompressionLevel Level { get; init; } = DeflateCompressionLevel.Default;

  /// <summary>Bytes per ZippedStream block.</summary>
  public int BlockSize { get; init; } = VdfsZippedStream.DefaultBlockSize;

  /// <summary>Free-text comment, at most 256 bytes.</summary>
  public string Comment { get; init; } = "";

  /// <summary>When the volume says it was written. Defaults to now.</summary>
  public DateTime Timestamp { get; init; } = DateTime.Now;

  /// <summary>
  /// Whether names are upper-cased, as GothicVDFS and VDFSTool both write them. The engine
  /// looks names up without regard to case, so this changes nothing a game sees.
  /// </summary>
  public bool UpperCaseNames { get; init; } = true;

  /// <summary>Whether to write Gothic 1's signature instead of Gothic 2's.</summary>
  public bool Gothic1Signature { get; init; }

  /// <summary>Adds a file; a later file of the same path replaces an earlier one.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    this._files.Add((name, FilePayload.FromBytes(data)));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must match
  /// what <paramref name="openStream" /> yields.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    this._files.Add((name, FilePayload.FromStream(size, openStream)));
  }

  /// <summary>Materialises the whole volume.</summary>
  public byte[] Build() {
    using var output = new MemoryStream();
    this.WriteTo(output);
    return output.ToArray();
  }

  /// <summary>
  /// Writes the volume into <paramref name="output" />. Payloads are copied (or deflated) one
  /// at a time and the header and catalog written last, so only one block is ever resident.
  /// </summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      using var buffer = new MemoryStream();
      this.WriteTo(buffer);
      buffer.Position = 0;
      buffer.CopyTo(output);
      return;
    }

    var catalog = this.BuildTree();
    var basePosition = output.Position;
    var position = (long)VdfsLayout.HeaderSize + (long)catalog.EntryCount * VdfsLayout.EntrySize;
    output.SetLength(Math.Max(output.Length, basePosition + position));
    output.Position = basePosition + position;

    var dataSize = 0L;
    var empty = new List<VdfsCatalog.Node>();
    foreach (var node in catalog.FilesInDataOrder()) {
      var payload = (FilePayload)node.Tag!;
      node.Size = payload.Size;
      if (payload.Size == 0) {
        empty.Add(node);
        continue;
      }
      if (position > uint.MaxValue)
        throw new InvalidOperationException("VDFS: payload offsets are 32-bit; the volume has grown past 4 GiB.");
      node.Offset = position;
      position += this.WritePayload(node.Name, payload, output);
      dataSize += payload.Size;
    }

    // An empty file points at the end of the volume. Anywhere else, ZenKit reads a zipped
    // volume's empty file as whatever follows its offset: it takes a zero catalog size for
    // "unknown". At the end it skips the entry instead of inventing content for it.
    foreach (var node in empty) node.Offset = Math.Min(position, uint.MaxValue);

    var header = BuildHeader(this.Comment, this.Gothic1Signature, catalog, this.Timestamp, dataSize, this.Zipped);
    output.Position = basePosition;
    output.Write(header);
    output.Write(catalog.Serialize());
    output.Position = basePosition + position;
    output.Flush();
  }

  private VdfsCatalog BuildTree() {
    var catalog = new VdfsCatalog();
    foreach (var (path, payload) in this._files) {
      if (payload.Size > uint.MaxValue)
        throw new ArgumentException($"VDFS: '{path}' is larger than the 32-bit size field.");
      catalog.GetOrAddFile(this.UpperCaseNames ? path.ToUpperInvariant() : path).Tag = payload;
    }
    return catalog;
  }

  private long WritePayload(string name, FilePayload payload, Stream output) {
    if (payload.Size == 0) return 0;
    using var source = payload.Open();
    if (this.Zipped && !VdfsLayout.IsStoredRawWhenZipped(name))
      return VdfsZippedStream.Encode(source, payload.Size, output, this.BlockSize, this.Level);

    var buffer = new byte[64 * 1024];
    var remaining = payload.Size;
    while (remaining > 0) {
      var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
      if (read <= 0) throw new EndOfStreamException($"VDFS: '{name}' yielded fewer bytes than its declared {payload.Size:N0}.");
      output.Write(buffer, 0, read);
      remaining -= read;
    }
    return payload.Size;
  }

  /// <summary>The 296-byte header for <paramref name="catalog" />.</summary>
  internal static byte[] BuildHeader(string comment, bool gothic1, VdfsCatalog catalog, DateTime timestamp, long dataSize, bool zipped) {
    var header = new byte[VdfsLayout.HeaderSize];
    header.AsSpan(0, VdfsLayout.CommentSize).Fill(VdfsLayout.CommentPad);
    var text = VdfsLayout.NameEncoding.GetBytes(comment ?? "");
    if (text.Length > VdfsLayout.CommentSize)
      throw new ArgumentException($"VDFS: the comment is longer than {VdfsLayout.CommentSize} bytes.", nameof(comment));
    text.CopyTo(header, 0);

    (gothic1 ? VdfsLayout.SignatureGothic1 : VdfsLayout.SignatureGothic2).CopyTo(header.AsSpan(VdfsLayout.SignatureOffset));
    WriteHeaderFields(header, catalog, VdfsLayout.ToDosTime(timestamp), dataSize, zipped ? VdfsLayout.VolumeZipped : VdfsLayout.VolumeNormal);
    return header;
  }

  /// <summary>Fills the six header fields after the signature.</summary>
  internal static void WriteHeaderFields(Span<byte> header, VdfsCatalog catalog, uint dosTime, long dataSize, uint flags) {
    BinaryPrimitives.WriteUInt32LittleEndian(header[VdfsLayout.EntryCountOffset..], (uint)catalog.EntryCount);
    BinaryPrimitives.WriteUInt32LittleEndian(header[VdfsLayout.FileCountOffset..], (uint)catalog.FileCount);
    BinaryPrimitives.WriteUInt32LittleEndian(header[VdfsLayout.TimestampOffset..], dosTime);
    BinaryPrimitives.WriteUInt32LittleEndian(header[VdfsLayout.DataSizeOffset..], (uint)Math.Min(dataSize, uint.MaxValue));
    BinaryPrimitives.WriteUInt32LittleEndian(header[VdfsLayout.CatalogOffsetOffset..], VdfsLayout.HeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header[VdfsLayout.VolumeFlagsOffset..], flags);
  }
}
