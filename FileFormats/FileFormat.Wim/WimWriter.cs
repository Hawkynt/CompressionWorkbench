using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Dictionary.Lzms;
using Compression.Core.Dictionary.Lzx;
using Compression.Core.Dictionary.Xpress;

namespace FileFormat.Wim;

/// <summary>
/// Writes a WIM (Windows Imaging) file to a stream.
/// </summary>
/// <remarks>
/// <para>A WIM is two halves that only work together. File <em>contents</em> live
/// in the lookup table, each identified by the SHA-1 of its uncompressed bytes;
/// file <em>names</em> live in the image's metadata resource, which is a
/// directory tree whose entries carry those same hashes. A container with the
/// first half and not the second holds resources nobody can name, and readers
/// say so by opening it and listing nothing.</para>
///
/// <para>Because contents are addressed by hash, two files with the same bytes
/// are stored once and pointed at twice — the format's single-instance store,
/// which costs nothing here beyond hashing what we were going to hash anyway.
/// Empty files are stored no times at all: they hash to nothing and carry an
/// all-zero hash in their directory entry.</para>
///
/// <para>The layout written is:</para>
/// <list type="number">
///   <item><description>A 208-byte file header.</description></item>
///   <item><description>One payload per distinct non-empty file.</description></item>
///   <item><description>The image metadata resource — security block, then tree.</description></item>
///   <item><description>The lookup table naming every resource above.</description></item>
///   <item><description>The XML description of the image.</description></item>
/// </list>
/// </remarks>
public sealed class WimWriter {
  private readonly Stream _output;
  private readonly uint _compressionType;
  private readonly int _chunkSize;

  /// <summary>
  /// Initializes a new <see cref="WimWriter"/>.
  /// </summary>
  /// <param name="output">The stream to write the WIM to. Must be seekable.</param>
  /// <param name="compressionType">
  /// The compression type to use for resources.
  /// Use one of the <c>WimConstants.Compression*</c> constants.
  /// Defaults to <see cref="WimConstants.CompressionXpress"/>.
  /// </param>
  /// <param name="chunkSize">
  /// The maximum uncompressed size of each chunk within a resource.
  /// Defaults to <see cref="WimConstants.DefaultChunkSize"/> (32 KB).
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="output"/> is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="chunkSize"/> is not positive.
  /// </exception>
  public WimWriter(
    Stream output,
    uint compressionType = WimConstants.CompressionXpress,
    int chunkSize = WimConstants.DefaultChunkSize) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(chunkSize, 0);

    if (compressionType == WimConstants.CompressionLzms)
      throw new NotSupportedException(
        "LZMS resources here are not the ones a WIM holds: the range-coded and Huffman-coded "
        + "halves of a chunk run in the opposite directions from the format's, the offset slots "
        + "are a scheme of our own, and an image using LZMS is version 3584 with 128 KB chunks "
        + "rather than 1.13 with 32 KB. Writing one would produce a container no reader opens. "
        + "Use CompressionXpress or CompressionLzx, both of which reference readers accept.");

    this._output = output;
    this._compressionType = compressionType;
    this._chunkSize = chunkSize;
  }

  /// <summary>
  /// Writes a complete WIM file holding the given resources, naming them
  /// <c>resource_0</c>, <c>resource_1</c> and so on.
  /// </summary>
  /// <param name="resources">The resource byte arrays to store in the WIM.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="resources"/> is null.</exception>
  public void Write(IReadOnlyList<byte[]> resources) {
    ArgumentNullException.ThrowIfNull(resources);
    this.Write(resources
      .Select((data, index) => (Name: "resource_" + index.ToString(CultureInfo.InvariantCulture), Data: data))
      .ToList());
  }

  /// <summary>
  /// Writes a complete WIM file holding one image of the given named files.
  /// </summary>
  /// <param name="files">
  /// The files to store. A name may carry a path, in which case the directories
  /// it names are created in the image.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="files"/> is null.</exception>
  public void Write(IReadOnlyList<(string Name, byte[] Data)> files) {
    ArgumentNullException.ThrowIfNull(files);

    var start = this._output.Position;

    // Reserve space for the header — we will seek back and fill it at the end.
    Span<byte> zeroes = stackalloc byte[WimConstants.HeaderSize];
    zeroes.Clear();
    this._output.Write(zeroes);

    // One payload per distinct content, in the order the contents first appear.
    // Empty files never become a resource: there is nothing to store and the
    // format gives them an all-zero hash instead of a pointer.
    var byHash = new Dictionary<string, PendingResource>(StringComparer.Ordinal);
    var order = new List<PendingResource>();
    var hashed = new List<(string Path, byte[] Hash)>(files.Count);
    var payloadBytes = 0L;

    foreach (var (name, data) in files) {
      var payload = data ?? [];
      payloadBytes += payload.Length;
      if (payload.Length == 0) {
        hashed.Add((name, new byte[WimConstants.HashLength]));
        continue;
      }

      var hash = SHA1.HashData(payload);
      var key = Convert.ToHexString(hash);
      if (!byHash.TryGetValue(key, out var pending)) {
        pending = new PendingResource(hash, payload);
        byHash.Add(key, pending);
        order.Add(pending);
      }

      ++pending.ReferenceCount;
      hashed.Add((name, hash));
    }

    foreach (var pending in order)
      pending.Entry = this.WriteResource(pending.Data, WimConstants.ResourceFlagUncompressed);

    // The image metadata: the directory tree that gives those payloads names.
    var tree = WimImageMetadata.BuildTree(hashed);
    var metadata = WimImageMetadata.Serialize(tree);
    var metadataEntry = this.WriteResource(metadata, WimConstants.ResourceFlagMetadata);
    var metadataHash = SHA1.HashData(metadata);

    var offsetTableOffset = this._output.Position;
    var offsetTableSize = this.WriteResourceTable(order, metadataEntry, metadataHash);

    var xmlOffset = this._output.Position;
    var xmlSize = this.WriteXmlMetadata(tree, xmlOffset - start, payloadBytes);

    var wimFlags = WimConstants.FlagRpFix | this._compressionType switch {
      WimConstants.CompressionXpress        => WimConstants.FlagCompression | WimConstants.FlagXpressCompression,
      WimConstants.CompressionLzx           => WimConstants.FlagCompression | WimConstants.FlagLzxCompression,
      WimConstants.CompressionLzms          => WimConstants.FlagCompression | WimConstants.FlagLzmsCompression,
      // The Huffman variant is what a WIM means by XPRESS, so it is written
      // under that name rather than under a second one of its own.
      WimConstants.CompressionXpressHuffman => WimConstants.FlagCompression | WimConstants.FlagXpressCompression,
      _                                     => 0u,
    };

    var end = this._output.Position;
    this._output.Seek(start, SeekOrigin.Begin);
    var header = new WimHeader {
      WimFlags        = wimFlags,
      CompressionType = this._compressionType,
      // Only a compressed WIM has a chunk size; on an uncompressed one the field
      // would describe chunks that are never cut.
      ChunkSize       = this._compressionType == WimConstants.CompressionNone ? 0 : (uint)this._chunkSize,
      Guid            = DeriveGuid(order, metadataHash),
      ImageCount      = 1,
      OffsetTableResource = new WimResourceEntry(
        CompressedSize: offsetTableSize,
        OriginalSize:   offsetTableSize,
        Offset:         offsetTableOffset,
        Flags:          WimConstants.ResourceFlagMetadata),
      XmlDataResource = new WimResourceEntry(
        CompressedSize: xmlSize,
        OriginalSize:   xmlSize,
        Offset:         xmlOffset,
        Flags:          WimConstants.ResourceFlagMetadata),
    };
    header.Write(this._output);
    this._output.Seek(end, SeekOrigin.Begin);
  }

  /// <summary>A resource waiting to be written, and how many names point at it.</summary>
  private sealed class PendingResource(byte[] hash, byte[] data) {
    public byte[] Hash { get; } = hash;
    public byte[] Data { get; } = data;
    public int ReferenceCount { get; set; }
    public WimResourceEntry? Entry { get; set; }
  }

  // -------------------------------------------------------------------------
  // Resource writing
  // -------------------------------------------------------------------------

  /// <summary>
  /// Writes <paramref name="data"/> as one resource, compressed if that makes it
  /// smaller, and returns the table entry describing where it landed.
  /// </summary>
  private WimResourceEntry WriteResource(byte[] data, uint extraFlags) {
    var resourceOffset = this._output.Position;

    var payload = data.Length == 0 || this._compressionType == WimConstants.CompressionNone
      ? null
      : this.CompressResource(data);

    if (payload is null) {
      this._output.Write(data);
      return new WimResourceEntry(
        CompressedSize: data.Length,
        OriginalSize:   data.Length,
        Offset:         resourceOffset,
        Flags:          WimConstants.ResourceFlagUncompressed | extraFlags);
    }

    this._output.Write(payload);
    return new WimResourceEntry(
      CompressedSize: payload.Length,
      OriginalSize:   data.Length,
      Offset:         resourceOffset,
      Flags:          WimConstants.ResourceFlagCompressed | extraFlags);
  }

  /// <summary>
  /// Builds the stored form of a compressed resource: the chunk table, then the
  /// chunks. Returns null when the result would not be smaller than the input,
  /// in which case the resource belongs in the file uncompressed — a resource
  /// that says it is compressed and occupies its own full length is a shape
  /// readers take for a damaged image rather than an incompressible one.
  /// </summary>
  private byte[]? CompressResource(byte[] data) {
    var chunkCount = (data.Length + this._chunkSize - 1) / this._chunkSize;

    // The chunk table gives the start of every chunk after the first, relative
    // to the end of the table; the last chunk's end is implicit in the resource
    // size. Entries are four bytes while the resource is under 4 GB, and a
    // byte[] never reaches that.
    const int entryWidth = 4;
    var chunkTableBytes = (chunkCount - 1) * entryWidth;

    var chunks = new byte[chunkCount][];
    var total = (long)chunkTableBytes;

    for (var i = 0; i < chunkCount; ++i) {
      var chunkStart  = i * this._chunkSize;
      var chunkLength = Math.Min(this._chunkSize, data.Length - chunkStart);
      var chunkData   = data.AsSpan(chunkStart, chunkLength);

      var compressed = this.CompressChunk(chunkData);

      // A chunk that did not get smaller is stored as it is. There is no
      // per-chunk flag saying which of the two it is — a reader tells them apart
      // by a chunk whose stored size is its full length — so a "compressed"
      // chunk that grew could not be told from raw data and would come back as
      // noise.
      chunks[i] = compressed.Length >= chunkLength ? chunkData.ToArray() : compressed;
      total += chunks[i].Length;

      if (total >= data.Length)
        return null;
    }

    var payload = new byte[total];
    var span = payload.AsSpan();

    long cumulative = 0;
    for (var i = 0; i < chunkCount - 1; ++i) {
      cumulative += chunks[i].Length;
      BinaryPrimitives.WriteUInt32LittleEndian(span[(i * entryWidth)..], (uint)cumulative);
    }

    var at = chunkTableBytes;
    foreach (var chunk in chunks) {
      chunk.CopyTo(span[at..]);
      at += chunk.Length;
    }

    return payload;
  }

  // -------------------------------------------------------------------------
  // Compression dispatch
  // -------------------------------------------------------------------------

  /// <remarks>
  /// The XPRESS a WIM names is the Huffman variant; the plain one of the same
  /// name belongs to NTFS compression and is a different encoding entirely.
  /// Writing that one under this label produced containers whose every other
  /// reader reported damaged data.
  /// </remarks>
  private byte[] CompressChunk(ReadOnlySpan<byte> chunk) =>
    this._compressionType switch {
      WimConstants.CompressionXpress        => new XpressHuffmanCompressor().Compress(chunk),
      WimConstants.CompressionXpressHuffman => new XpressHuffmanCompressor().Compress(chunk),
      WimConstants.CompressionLzx           => CompressLzx(chunk),
      WimConstants.CompressionLzms => new LzmsCompressor().Compress(chunk),
      _ => throw new NotSupportedException(
        $"Unsupported WIM compression type: {this._compressionType}.")
    };

  /// <summary>
  /// Compresses one chunk with LZX, rewriting x86 call targets first as the
  /// format requires. A reader undoes that rewriting whether it was done or not,
  /// so skipping it hands back a chunk with bytes that merely looked like calls
  /// silently altered.
  /// </summary>
  private static byte[] CompressLzx(ReadOnlySpan<byte> chunk) {
    var filtered = chunk.ToArray();
    LzxWimE8Filter.Preprocess(filtered);
    return new LzxCompressor(WimConstants.LzxWindowBits).Compress(filtered);
  }

  // -------------------------------------------------------------------------
  // Resource table
  // -------------------------------------------------------------------------

  /// <summary>
  /// Writes the lookup table describing every resource written, and returns the
  /// number of bytes it took.
  /// </summary>
  private long WriteResourceTable(
    List<PendingResource> resources,
    WimResourceEntry metadataEntry,
    byte[] metadataHash) {
    var start = this._output.Position;

    foreach (var resource in resources)
      this.WriteResourceTableEntry(resource.Entry!, resource.ReferenceCount, resource.Hash);
    this.WriteResourceTableEntry(metadataEntry, 1, metadataHash);

    return this._output.Position - start;
  }

  private void WriteResourceTableEntry(WimResourceEntry entry, int referenceCount, byte[] hash) {
    Span<byte> buf = stackalloc byte[WimConstants.LookupTableEntrySize];
    buf.Clear();

    // RESHDR_DISK_SHORT: packed size+flags (8), offset (8), original size (8)
    var sizeAndFlags = (entry.CompressedSize & 0x00FFFFFFFFFFFFFF)
                     | ((long)entry.Flags << 56);
    BinaryPrimitives.WriteInt64LittleEndian(buf,       sizeAndFlags);
    BinaryPrimitives.WriteInt64LittleEndian(buf[8..],  entry.Offset);
    BinaryPrimitives.WriteInt64LittleEndian(buf[16..], entry.OriginalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(buf[24..], 1);              // part number
    BinaryPrimitives.WriteUInt32LittleEndian(buf[26..], (uint)referenceCount);
    hash.CopyTo(buf[30..]);

    this._output.Write(buf);
  }

  // -------------------------------------------------------------------------
  // XML metadata
  // -------------------------------------------------------------------------

  /// <summary>
  /// Writes the XML description of the image and returns its byte length.
  /// </summary>
  /// <remarks>
  /// UTF-16 with a byte-order mark, which is the encoding the field is defined
  /// in — an ASCII-looking UTF-8 document parses as one enormous CJK glyph.
  /// </remarks>
  private long WriteXmlMetadata(WimImageMetadata.Node tree, long bytesBeforeXml, long payload) {
    var start = this._output.Position;

    var xml = new StringBuilder();
    xml.Append("<WIM><TOTALBYTES>")
       .Append(bytesBeforeXml.ToString(CultureInfo.InvariantCulture))
       .Append("</TOTALBYTES><IMAGE INDEX=\"1\"><NAME>1</NAME><DIRCOUNT>")
       .Append(WimImageMetadata.CountDirectories(tree).ToString(CultureInfo.InvariantCulture))
       .Append("</DIRCOUNT><FILECOUNT>")
       .Append(WimImageMetadata.CountFiles(tree).ToString(CultureInfo.InvariantCulture))
       .Append("</FILECOUNT><TOTALBYTES>")
       .Append(payload.ToString(CultureInfo.InvariantCulture))
       .Append("</TOTALBYTES></IMAGE></WIM>");

    this._output.Write(Encoding.Unicode.GetPreamble());
    this._output.Write(Encoding.Unicode.GetBytes(xml.ToString()));

    return this._output.Position - start;
  }

  // -------------------------------------------------------------------------
  // Identity
  // -------------------------------------------------------------------------

  /// <summary>
  /// Derives the image's GUID from what the image holds.
  /// </summary>
  /// <remarks>
  /// The field identifies one image across the parts of a split WIM, so it has
  /// to be a real value rather than the zeros written before. Deriving it from
  /// the content — a name-based UUID, which is what version 5 means — keeps two
  /// writes of the same input identical, where a random one would make every
  /// rebuild differ from the last for no reason a reader can see.
  /// </remarks>
  private static Guid DeriveGuid(List<PendingResource> resources, byte[] metadataHash) {
    var material = new List<byte>((resources.Count + 1) * WimConstants.HashLength);
    foreach (var resource in resources)
      material.AddRange(resource.Hash);
    material.AddRange(metadataHash);

    var digest = SHA1.HashData(material.ToArray());
    var bytes = digest.AsSpan(0, 16).ToArray();

    // The version nibble and the variant bits sit where the textual form shows
    // them, which in this byte layout is the top of the third group and the top
    // of the fourth — not the sixth and eighth bytes of the array.
    bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);        // version 5: name-based, SHA-1
    bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);        // variant: RFC 4122
    return new Guid(bytes);
  }

  /// <summary>
  /// Creates a WIM file split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="resources">The resource data to store.</param>
  /// <param name="compressionType">The compression type to use.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IReadOnlyList<byte[]> resources,
      uint compressionType = WimConstants.CompressionXpress) {
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms, compressionType);
    writer.Write(resources);
    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }
}
