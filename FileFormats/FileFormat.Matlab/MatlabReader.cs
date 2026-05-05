#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Matlab;

/// <summary>
/// Read-only walker for MATLAB MAT v5 files. Parses the 128-byte header, validates the
/// "MATLAB" prefix and version field, detects endian, and walks top-level data elements,
/// surfacing each top-level miMATRIX array's name, class, and dimensions. Numeric data
/// is intentionally not extracted — that is a substantial follow-up.
/// </summary>
public sealed class MatlabReader {

  public string Description { get; }
  public int Version { get; }
  public bool IsLittleEndian { get; }
  public IReadOnlyList<MatlabArrayInfo> Arrays { get; }

  /// <summary>"full" if every top-level element parsed cleanly to EOF; "partial" if a truncated or unrecognized element was encountered partway through.</summary>
  public string ParseStatus { get; }

  public MatlabReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    stream.Seek(0, SeekOrigin.Begin);
    var header = ReadExact(stream, MatlabConstants.HeaderSize, "MAT v5 header");

    if (header.Length < MatlabConstants.Magic.Length ||
        !header.AsSpan(0, MatlabConstants.Magic.Length).SequenceEqual(MatlabConstants.Magic))
      throw new InvalidDataException("Not a MATLAB MAT v5 file (missing 'MATLAB' prefix).");

    var endianBytes = header.AsSpan(MatlabConstants.EndianIndicatorOffset, 2);
    bool littleEndian;
    if (endianBytes.SequenceEqual(MatlabConstants.EndianIM))
      littleEndian = true;
    else if (endianBytes.SequenceEqual(MatlabConstants.EndianMI))
      littleEndian = false;
    else
      throw new InvalidDataException("Invalid MAT v5 endian indicator (expected 'IM' or 'MI').");

    var versionRaw = header.AsSpan(MatlabConstants.VersionOffset, 2);
    var version = littleEndian
      ? BinaryPrimitives.ReadUInt16LittleEndian(versionRaw)
      : BinaryPrimitives.ReadUInt16BigEndian(versionRaw);

    if (version != MatlabConstants.ExpectedVersion)
      throw new InvalidDataException(
        "Unsupported MAT version 0x" + version.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
        + " (expected 0x0100).");

    this.Description = ExtractDescription(header);
    this.Version = 1;
    this.IsLittleEndian = littleEndian;

    var arrays = new List<MatlabArrayInfo>();
    var status = "full";
    while (stream.Position < stream.Length) {
      var elementStart = stream.Position;
      var element = TryReadElement(stream, littleEndian);
      if (element == null) {
        status = "partial";
        break;
      }

      try {
        if (element.Type == MatlabConstants.MiCOMPRESSED) {
          var inner = DecompressZlib(element.Payload);
          using var innerStream = new MemoryStream(inner, writable: false);
          var innerElement = TryReadElement(innerStream, littleEndian);
          if (innerElement != null && innerElement.Type == MatlabConstants.MiMATRIX) {
            var info = TryParseMatrix(innerElement.Payload, littleEndian);
            if (info != null) arrays.Add(info);
            else status = "partial";
          } else if (innerElement == null) {
            status = "partial";
          }
        } else if (element.Type == MatlabConstants.MiMATRIX) {
          var info = TryParseMatrix(element.Payload, littleEndian);
          if (info != null) arrays.Add(info);
          else status = "partial";
        }
        // Unknown top-level types are skipped silently.
      } catch (InvalidDataException) {
        status = "partial";
        // Continue walking — element framing was valid, only payload parse failed.
      } catch (NotSupportedException) {
        status = "partial";
      }

      if (stream.Position <= elementStart) {
        // Defensive — should not happen, but prevents an infinite loop on a buggy walker.
        status = "partial";
        break;
      }
    }

    this.Arrays = arrays;
    this.ParseStatus = status;
  }

  private static string ExtractDescription(byte[] header) {
    var raw = Encoding.ASCII.GetString(header, 0, MatlabConstants.DescriptionLength);
    var trimmed = raw.TrimEnd(' ', '\0');
    var firstBreak = trimmed.IndexOfAny(['\r', '\n']);
    if (firstBreak >= 0) trimmed = trimmed[..firstBreak];
    return trimmed;
  }

  private sealed class Element {
    public uint Type { get; }
    public byte[] Payload { get; }
    public Element(uint type, byte[] payload) {
      this.Type = type;
      this.Payload = payload;
    }
  }

  /// <summary>Reads one tagged data element (handling the small-element form), or null on EOF/truncation.</summary>
  private static Element? TryReadElement(Stream stream, bool littleEndian) {
    var tagBuf = new byte[8];
    var read = ReadAtMost(stream, tagBuf, 8);
    if (read < 8) return null;

    var first = ReadUInt32(tagBuf.AsSpan(0, 4), littleEndian);
    var second = ReadUInt32(tagBuf.AsSpan(4, 4), littleEndian);

    uint type;
    uint length;
    byte[] payload;
    long padded;

    if ((first & 0xFFFF0000u) != 0) {
      // Small-element form: high 16 = byte length, low 16 = type. Payload is the trailing 4 bytes of the tag.
      length = (first >> 16) & 0xFFFFu;
      type = first & 0xFFFFu;
      if (length > 4) return null;
      payload = new byte[length];
      Buffer.BlockCopy(tagBuf, 4, payload, 0, (int)length);
      // Total element size in this form is exactly 8 bytes (already consumed).
      return new Element(type, payload);
    }

    type = first;
    length = second;
    if (length > int.MaxValue) return null;

    payload = new byte[length];
    var got = ReadAtMost(stream, payload, (int)length);
    if (got < (int)length) return null;

    // Pad payload up to 8-byte boundary.
    padded = (length + 7) & ~7L;
    var padBytes = (int)(padded - length);
    if (padBytes > 0) {
      var skipped = SkipBytes(stream, padBytes);
      if (skipped < padBytes) return null;
    }

    return new Element(type, payload);
  }

  /// <summary>Parses a miMATRIX payload and extracts (name, class, dims). Returns null if the payload is malformed.</summary>
  private static MatlabArrayInfo? TryParseMatrix(byte[] payload, bool littleEndian) {
    using var ms = new MemoryStream(payload, writable: false);

    // Sub-element 1: ArrayFlags (miUINT32, 8-byte payload — 2x uint32).
    var flags = TryReadElement(ms, littleEndian);
    if (flags == null || flags.Payload.Length < 8) return null;
    var classCode = (byte)(ReadUInt32(flags.Payload.AsSpan(0, 4), littleEndian) & 0xFFu);

    // Sub-element 2: DimensionsArray (miINT32, len bytes).
    var dimsElem = TryReadElement(ms, littleEndian);
    if (dimsElem == null) return null;
    var dimCount = dimsElem.Payload.Length / 4;
    var dims = new int[dimCount];
    for (var i = 0; i < dimCount; i++)
      dims[i] = (int)ReadUInt32(dimsElem.Payload.AsSpan(i * 4, 4), littleEndian);

    // Sub-element 3: ArrayName (miINT8 ASCII).
    var nameElem = TryReadElement(ms, littleEndian);
    if (nameElem == null) return null;
    var name = Encoding.ASCII.GetString(nameElem.Payload).TrimEnd('\0');

    return new MatlabArrayInfo(name, MatlabConstants.ClassName(classCode), dims);
  }

  private static byte[] DecompressZlib(byte[] compressed) {
    using var ms = new MemoryStream(compressed, writable: false);
    using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
    using var output = new MemoryStream();
    zlib.CopyTo(output);
    return output.ToArray();
  }

  private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian)
    => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

  private static byte[] ReadExact(Stream stream, int count, string what) {
    var buf = new byte[count];
    var read = 0;
    while (read < count) {
      var n = stream.Read(buf, read, count - read);
      if (n <= 0) throw new EndOfStreamException("Unexpected EOF reading " + what + ".");
      read += n;
    }
    return buf;
  }

  private static int ReadAtMost(Stream stream, byte[] buffer, int count) {
    var read = 0;
    while (read < count) {
      var n = stream.Read(buffer, read, count - read);
      if (n <= 0) break;
      read += n;
    }
    return read;
  }

  private static int SkipBytes(Stream stream, int count) {
    if (stream.CanSeek) {
      var remaining = stream.Length - stream.Position;
      var toSkip = (int)Math.Min(count, remaining);
      stream.Seek(toSkip, SeekOrigin.Current);
      return toSkip;
    }
    var skip = new byte[count];
    return ReadAtMost(stream, skip, count);
  }
}
