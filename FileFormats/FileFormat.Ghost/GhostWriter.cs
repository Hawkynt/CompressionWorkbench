#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;

namespace FileFormat.Ghost;

/// <summary>
/// Writes a Ghost 11.x / 12.x record container. Single-file output only
/// — <c>.ghs</c> spanning is not emitted (the registry's
/// <c>IArchiveCreatable</c> contract always produces one stream).
/// </summary>
/// <remarks>
/// The byte layout follows the same record/block framing
/// <see cref="GhostReader"/> reads, so a write-then-read round trip
/// recovers byte-identical partition content for the
/// stored / Fast LZ / zlib (level 3-9) compression modes.
/// </remarks>
public sealed class GhostWriter : IDisposable {

  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly byte _compression;
  private readonly uint _id;
  private readonly string? _password;
  private bool _disposed;

    /// <summary>
  /// Initializes a new instance of <see cref="GhostWriter"/>.
  /// </summary>
public GhostWriter(Stream output, byte compression, uint id = 0x12345678, string? password = null, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanWrite)
      throw new ArgumentException("Ghost writer requires a writable stream.", nameof(output));
    this._output = output;
    this._leaveOpen = leaveOpen;
    this._compression = compression;
    this._id = id;
    this._password = password;
    this.WriteFileHeader();
  }

  private void WriteFileHeader() {
    Span<byte> hdr = stackalloc byte[GhostConstants.HeaderSize];
    hdr.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[..2], GhostConstants.FileMagic);
    hdr[2] = GhostConstants.FileTypeSingle;
    hdr[3] = this._compression;
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), this._id);
    if (!string.IsNullOrEmpty(this._password))
      hdr[12] |= 0x02; // Encryption flag.
    this._output.Write(hdr);
  }

  /// <summary>Write a Track-0 (MBR + boot sectors) record.</summary>
  public void WriteTrack0(ReadOnlySpan<byte> track0Data, byte sectors) {
    var body = new byte[6 + track0Data.Length];
    body[0] = 0x06;
    body[1] = sectors;
    track0Data.CopyTo(body.AsSpan(6));
    this.WriteRecord(GhostConstants.RecordTypeTrack0, body);
  }

  /// <summary>Write a partition record + FEEF header + compressed blocks.</summary>
  public void WritePartition(ReadOnlySpan<byte> partitionData) {
    Span<byte> descBody = stackalloc byte[20];
    descBody.Clear();
    this.WriteRecord(GhostConstants.RecordTypePartition, descBody);

    Span<byte> feef = stackalloc byte[GhostConstants.HeaderSize];
    feef.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(feef[..2], GhostConstants.FileMagic);
    feef[2] = GhostConstants.PartitionHeaderSubType;
    feef[3] = this._compression;
    BinaryPrimitives.WriteUInt32LittleEndian(feef.Slice(4, 4), this._id);
    this._output.Write(feef);

    var cipher = string.IsNullOrEmpty(this._password) ? null : new GhostCrc16Cipher(this._password);

    var pos = 0;
    while (pos < partitionData.Length) {
      var chunk = Math.Min(GhostConstants.BlockSize, partitionData.Length - pos);
      this.WriteBlock(partitionData.Slice(pos, chunk), cipher);
      pos += chunk;
    }
  }

  private void WriteBlock(ReadOnlySpan<byte> data, GhostCrc16Cipher? cipher) {
    // CompressionNone (Z0) writes raw data with no 4-byte block header — the
    // read path treats the entire payload as already-uncompressed (matching
    // nyarime/gho). Z1+ modes always emit the 4-byte tagged block header.
    var blockData = this._compression switch {
      GhostConstants.CompressionNone => data.ToArray(),
      GhostConstants.CompressionFast => GhostFastLz.Compress(data),
      GhostConstants.CompressionHigh3 or GhostConstants.CompressionHigh4 or
        GhostConstants.CompressionHigh5 or GhostConstants.CompressionHigh6 or
        GhostConstants.CompressionHigh7 or GhostConstants.CompressionHigh8 or
        GhostConstants.CompressionHigh9 => GhostZlib.Compress(data, this._compression),
      _ => throw new InvalidOperationException($"Ghost writer: unsupported compression byte {this._compression}.")
    };

    if (cipher != null) cipher.Encrypt(blockData);

    var storedLen = blockData.Length + 2;
    if (storedLen > 0xFFFF)
      throw new InvalidDataException($"Ghost writer: block too large for stored_len ({blockData.Length}).");

    Span<byte> lenBuf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, (ushort)storedLen);
    this._output.Write(lenBuf);
    this._output.Write(blockData);
  }

  /// <summary>Write a continuation record (used to split a partition across multiple data spans).</summary>
  public void WriteContinuation() {
    Span<byte> body = stackalloc byte[20];
    body.Clear();
    this.WriteRecord(GhostConstants.RecordTypeContinuation, body);
  }

  /// <summary>Write the end-of-image record. Always call this before disposing.</summary>
  public void WriteEnd() {
    Span<byte> body = stackalloc byte[24];
    body.Clear();
    this.WriteRecord(GhostConstants.RecordTypeEnd, body);
  }

  private void WriteRecord(ushort recType, ReadOnlySpan<byte> body) {
    if (body.Length > 0xFFFF)
      throw new InvalidDataException($"Ghost writer: record body too large ({body.Length}).");
    Span<byte> hdr = stackalloc byte[GhostConstants.RecordHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[..4], recType);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), GhostConstants.RecordMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(8, 2), (ushort)body.Length);
    this._output.Write(hdr);
    if (body.Length > 0) this._output.Write(body);
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._leaveOpen) this._output.Dispose();
  }
}

/// <summary>
/// Ghost "High" mode (Z3-Z9) compressed blocks: tag-1 = uncompressed,
/// otherwise the payload starting at offset 4 is a zlib stream.
/// </summary>
public static class GhostZlib {

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public static int Decompress(ReadOnlySpan<byte> data, int compLen, Span<byte> dst) {
    if (compLen <= 0 || data.Length < compLen)
      throw new InvalidDataException("Ghost zlib: truncated block.");
    if (data[0] == 1) {
      var n = compLen - 4;
      if (n <= 0 || n > dst.Length)
        throw new InvalidDataException("Ghost zlib: corrupt uncompressed block length.");
      data.Slice(4, n).CopyTo(dst);
      return n;
    }

    using var src = new MemoryStream(data.Slice(4, compLen - 4).ToArray(), writable: false);
    using var z = new ZLibStream(src, CompressionMode.Decompress);
    var read = 0;
    while (read < dst.Length) {
      var n = z.Read(dst[read..]);
      if (n <= 0) break;
      read += n;
    }
    return read;
  }

    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public static byte[] Compress(ReadOnlySpan<byte> src, byte level) {
    if (src.Length == 0) return [];
    using var ms = new MemoryStream();
    ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
    var cl = MapLevel(level);
    using (var z = new ZLibStream(ms, cl, leaveOpen: true))
      z.Write(src);

    if (ms.Length >= src.Length + 4) return GhostFastLz.StoreUncompressed(src);
    return ms.ToArray();
  }

  private static CompressionLevel MapLevel(byte level) => level switch {
    GhostConstants.CompressionHigh3 or GhostConstants.CompressionHigh4 => CompressionLevel.Fastest,
    GhostConstants.CompressionHigh5 or GhostConstants.CompressionHigh6 or GhostConstants.CompressionHigh7 => CompressionLevel.Optimal,
    GhostConstants.CompressionHigh8 or GhostConstants.CompressionHigh9 => CompressionLevel.SmallestSize,
    _ => CompressionLevel.Optimal
  };
}
