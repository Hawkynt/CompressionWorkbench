#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Arsc;

/// <summary>One package entry surfaced by <see cref="ArscReader"/>.</summary>
public readonly record struct ArscPackageInfo(uint PackageId, string Name, int TypeChunkCount);

/// <summary>
/// Read-only walker for Android compiled resource tables (<c>resources.arsc</c>).
/// Validates the root <c>RES_TABLE_TYPE</c> chunk, walks the global string pool and each
/// package chunk, and surfaces package count, package id/name list, total type-chunk count
/// and the global string-pool string count. Tolerant of unknown chunk types and truncation:
/// unknown chunks are skipped via <see cref="ChunkWalker.SkipBody"/> and any structural
/// failure flips <see cref="ParseStatus"/> to <c>partial</c> instead of throwing.
/// </summary>
public sealed class ArscReader {

  /// <summary>Number of packages declared in the <c>RES_TABLE_TYPE</c> root header.</summary>
  public uint PackageCount { get; }

  /// <summary>String count of the global value-string pool that follows the root header. 0 if absent or unparsed.</summary>
  public uint GlobalStringCount { get; }

  /// <summary>Decoded package entries (id + null-trimmed UTF-16 name + type-chunk count).</summary>
  public IReadOnlyList<ArscPackageInfo> Packages { get; }

  /// <summary>Total <c>RES_TABLE_TYPE_TYPE</c> chunks summed across all walked packages.</summary>
  public int TotalTypeCount { get; }

  /// <summary><c>full</c> when every chunk header read cleanly to EOF; <c>partial</c> on any truncation, structural error or trailing-byte shortfall.</summary>
  public string ParseStatus { get; }

  public ArscReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    stream.Seek(0, SeekOrigin.Begin);
    var fileSize = stream.Length;
    if (fileSize < ArscConstants.ChunkHeaderSize)
      throw new InvalidDataException("File too small to be an ARSC resource table.");

    var rootHeader = ChunkWalker.ReadHeader(stream);
    if (rootHeader.Type != ArscConstants.ResTableType)
      throw new InvalidDataException($"Not an ARSC file (root chunk type=0x{rootHeader.Type:X4}, expected 0x0003).");

    var rootEnd = (long)rootHeader.Size;
    if (rootEnd > fileSize) {
      var packageCountTruncated = TryReadPackageCount(stream, rootHeader);
      this.PackageCount = packageCountTruncated;
      this.GlobalStringCount = 0;
      this.Packages = Array.Empty<ArscPackageInfo>();
      this.TotalTypeCount = 0;
      this.ParseStatus = "partial";
      return;
    }

    var declaredPackageCount = TryReadPackageCount(stream, rootHeader);
    var rootHeaderEnd = (long)rootHeader.HeaderSize;
    if (stream.Position < rootHeaderEnd) stream.Position = rootHeaderEnd;

    uint globalStringCount = 0;
    var packages = new List<ArscPackageInfo>();
    var totalTypes = 0;
    var status = "full";

    while (stream.Position < rootEnd) {
      ChunkHeader chunk;
      var chunkStart = stream.Position;
      try {
        chunk = ChunkWalker.ReadHeader(stream);
      } catch (EndOfStreamException) {
        status = "partial";
        break;
      } catch (InvalidDataException) {
        status = "partial";
        break;
      }

      var chunkEnd = chunkStart + chunk.Size;
      if (chunkEnd > rootEnd) {
        status = "partial";
        break;
      }

      switch (chunk.Type) {
        case ArscConstants.ResStringPoolType: {
          if (globalStringCount == 0 && TryReadStringPoolCount(stream, chunk, out var sc))
            globalStringCount = sc;
          stream.Position = chunkEnd;
          break;
        }
        case ArscConstants.ResTablePackageType: {
          if (TryReadPackage(stream, chunk, chunkStart, out var pkg, out var typeCount)) {
            packages.Add(pkg);
            totalTypes += typeCount;
          } else {
            status = "partial";
          }
          stream.Position = chunkEnd;
          break;
        }
        default: {
          stream.Position = chunkEnd;
          break;
        }
      }
    }

    if (stream.Position != rootEnd) status = "partial";

    this.PackageCount = declaredPackageCount;
    this.GlobalStringCount = globalStringCount;
    this.Packages = packages;
    this.TotalTypeCount = totalTypes;
    this.ParseStatus = status;
  }

  private static uint TryReadPackageCount(Stream stream, ChunkHeader rootHeader) {
    if (rootHeader.HeaderSize < ArscConstants.ChunkHeaderSize + 4) return 0;
    Span<byte> buf = stackalloc byte[4];
    var read = 0;
    while (read < buf.Length) {
      var n = stream.Read(buf[read..]);
      if (n <= 0) return 0;
      read += n;
    }
    return (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
  }

  private static bool TryReadStringPoolCount(Stream stream, ChunkHeader chunk, out uint stringCount) {
    stringCount = 0;
    if (chunk.HeaderSize < ArscConstants.ChunkHeaderSize + 4) return false;
    Span<byte> buf = stackalloc byte[4];
    var read = 0;
    while (read < buf.Length) {
      var n = stream.Read(buf[read..]);
      if (n <= 0) return false;
      read += n;
    }
    stringCount = (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
    return true;
  }

  private static bool TryReadPackage(Stream stream, ChunkHeader chunk, long chunkStart,
                                     out ArscPackageInfo pkg, out int typeCount) {
    pkg = default;
    typeCount = 0;

    var minHeader = ArscConstants.ChunkHeaderSize + 4 + ArscConstants.PackageNameLengthBytes;
    if (chunk.HeaderSize < minHeader) return false;

    Span<byte> idBuf = stackalloc byte[4];
    if (!ReadExact(stream, idBuf)) return false;
    var packageId = (uint)(idBuf[0] | (idBuf[1] << 8) | (idBuf[2] << 16) | (idBuf[3] << 24));

    var nameBuf = new byte[ArscConstants.PackageNameLengthBytes];
    if (!ReadExact(stream, nameBuf)) return false;
    var name = DecodeUtf16Name(nameBuf);

    var headerEnd = chunkStart + chunk.HeaderSize;
    if (stream.Position < headerEnd) stream.Position = headerEnd;

    var chunkEnd = chunkStart + chunk.Size;
    var types = 0;
    while (stream.Position < chunkEnd) {
      ChunkHeader child;
      var childStart = stream.Position;
      try {
        child = ChunkWalker.ReadHeader(stream);
      } catch (EndOfStreamException) {
        return false;
      } catch (InvalidDataException) {
        return false;
      }
      var childEnd = childStart + child.Size;
      if (childEnd > chunkEnd) return false;
      if (child.Type == ArscConstants.ResTableTypeType) types++;
      stream.Position = childEnd;
    }

    pkg = new ArscPackageInfo(packageId, name, types);
    typeCount = types;
    return true;
  }

  private static bool ReadExact(Stream stream, Span<byte> buf) {
    var read = 0;
    while (read < buf.Length) {
      var n = stream.Read(buf[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }

  private static string DecodeUtf16Name(byte[] raw) {
    var decoded = Encoding.Unicode.GetString(raw);
    var nul = decoded.IndexOf('\0');
    if (nul >= 0) decoded = decoded[..nul];
    return decoded;
  }
}
