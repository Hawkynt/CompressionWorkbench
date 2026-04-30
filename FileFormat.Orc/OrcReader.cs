#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.Orc;

/// <summary>
/// Read-only walker for Apache ORC files. Validates the leading "ORC" magic, reads the 1-byte
/// PostScript-length trailer at file end, parses the uncompressed PostScript Protobuf, and (when
/// the file's compression codec is NONE) walks the uncompressed Footer Protobuf to surface the
/// total row count, type count, and stripe count. When the footer is compressed (ZLIB/SNAPPY/
/// LZO/LZ4/ZSTD), the codec name is reported but the footer is not decompressed and parse status
/// is "partial". Full record decode is intentionally out of scope.
/// </summary>
public sealed class OrcReader {

  /// <summary>True when the leading 3 bytes are "ORC" and the PostScript magic field also reads "ORC".</summary>
  public bool MagicOk { get; }

  /// <summary>The 1-byte PostScript length read from the very last byte of the file.</summary>
  public int PsLength { get; }

  /// <summary>PostScript field 1: byte length of the Footer Protobuf preceding the PostScript.</summary>
  public long FooterLength { get; }

  /// <summary>Compression codec name (NONE / ZLIB / SNAPPY / LZO / LZ4 / ZSTD / unknown).</summary>
  public string Compression { get; }

  /// <summary>Writer version as dotted "major.minor.patch" string from PostScript repeated field 4.</summary>
  public string WriterVersion { get; }

  /// <summary>Footer field 5: total number of rows. 0 when unknown (compressed footer or missing).</summary>
  public long NumberOfRows { get; }

  /// <summary>Number of stripe descriptors found in Footer field 2. 0 when unknown.</summary>
  public int StripeCount { get; }

  /// <summary>Number of type descriptors found in Footer field 3. 0 when unknown.</summary>
  public int TypeCount { get; }

  /// <summary>"full" if PostScript and (when uncompressed) Footer were both walked end-to-end; "partial" otherwise.</summary>
  public string ParseStatus { get; }

  public OrcReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    var fileSize = stream.Length;
    if (fileSize < OrcConstants.MinFileLength)
      throw new InvalidDataException("File too small to be an ORC file.");

    stream.Seek(0, SeekOrigin.Begin);
    var leading = ReadExact(stream, OrcConstants.MagicLength);
    if (!leading.AsSpan().SequenceEqual(OrcConstants.Magic))
      throw new InvalidDataException("Not an ORC file (missing leading ORC magic).");

    stream.Seek(fileSize - 1, SeekOrigin.Begin);
    var lastByte = stream.ReadByte();
    if (lastByte < 0) throw new EndOfStreamException("EOF reading ORC PostScript-length trailer.");
    var psLength = lastByte;
    this.PsLength = psLength;

    if (psLength <= 0 || psLength > fileSize - 1 - OrcConstants.MagicLength) {
      this.MagicOk = true;
      this.FooterLength = 0;
      this.Compression = "unknown";
      this.WriterVersion = string.Empty;
      this.NumberOfRows = 0;
      this.StripeCount = 0;
      this.TypeCount = 0;
      this.ParseStatus = "partial";
      return;
    }

    var psStart = fileSize - 1 - psLength;
    stream.Seek(psStart, SeekOrigin.Begin);
    var psBytes = ReadExact(stream, psLength);

    long footerLength = 0;
    var compressionEnum = -1;
    var versions = new List<long>();
    var psMagic = string.Empty;
    var psStatus = "full";

    try {
      using var ps = new MemoryStream(psBytes, writable: false);
      while (ps.Position < ps.Length) {
        var (field, wire) = ProtobufWalker.ReadTag(ps);
        switch (field) {
          case OrcConstants.PsFieldFooterLength when wire == OrcConstants.WireVarint:
            footerLength = ProtobufWalker.ReadVarLong(ps);
            break;
          case OrcConstants.PsFieldCompression when wire == OrcConstants.WireVarint:
            compressionEnum = (int)ProtobufWalker.ReadVarLong(ps);
            break;
          case OrcConstants.PsFieldVersion when wire == OrcConstants.WireVarint:
            versions.Add(ProtobufWalker.ReadVarLong(ps));
            break;
          case OrcConstants.PsFieldVersion when wire == OrcConstants.WireLengthDelimited: {
            var packed = ProtobufWalker.ReadLengthDelimited(ps);
            using var inner = new MemoryStream(packed, writable: false);
            while (inner.Position < inner.Length) versions.Add(ProtobufWalker.ReadVarLong(inner));
            break;
          }
          case OrcConstants.PsFieldMagic when wire == OrcConstants.WireLengthDelimited:
            psMagic = ProtobufWalker.ReadString(ps);
            break;
          default:
            ProtobufWalker.Skip(ps, wire);
            break;
        }
      }
    } catch (EndOfStreamException) {
      psStatus = "partial";
    } catch (InvalidDataException) {
      psStatus = "partial";
    }

    this.FooterLength = footerLength;
    this.Compression = compressionEnum < 0 ? "NONE" : OrcConstants.CompressionName(compressionEnum);
    this.WriterVersion = string.Join(".", versions.Select(v => v.ToString(CultureInfo.InvariantCulture)));
    this.MagicOk = psMagic.Length == 0 || psMagic == "ORC";

    var footerStart = psStart - footerLength;
    // footerLength == 0 is a legit empty footer (file with no rows/stripes/types).
    // Only treat negative or out-of-bounds footers as partial.
    if (psStatus == "partial" || footerLength < 0 || footerStart < OrcConstants.MagicLength) {
      this.NumberOfRows = 0;
      this.StripeCount = 0;
      this.TypeCount = 0;
      this.ParseStatus = "partial";
      return;
    }

    if (compressionEnum > OrcConstants.CompressionNone) {
      this.NumberOfRows = 0;
      this.StripeCount = 0;
      this.TypeCount = 0;
      this.ParseStatus = "partial";
      return;
    }

    stream.Seek(footerStart, SeekOrigin.Begin);
    var footerBytes = ReadExact(stream, (int)footerLength);

    long numRows = 0;
    var stripes = 0;
    var types = 0;
    var footerStatus = "full";

    try {
      using var ft = new MemoryStream(footerBytes, writable: false);
      while (ft.Position < ft.Length) {
        var (field, wire) = ProtobufWalker.ReadTag(ft);
        switch (field) {
          case OrcConstants.FooterFieldStripes when wire == OrcConstants.WireLengthDelimited:
            _ = ProtobufWalker.ReadLengthDelimited(ft);
            stripes++;
            break;
          case OrcConstants.FooterFieldTypes when wire == OrcConstants.WireLengthDelimited:
            _ = ProtobufWalker.ReadLengthDelimited(ft);
            types++;
            break;
          case OrcConstants.FooterFieldNumberOfRows when wire == OrcConstants.WireVarint:
            numRows = ProtobufWalker.ReadVarLong(ft);
            break;
          default:
            ProtobufWalker.Skip(ft, wire);
            break;
        }
      }
    } catch (EndOfStreamException) {
      footerStatus = "partial";
    } catch (InvalidDataException) {
      footerStatus = "partial";
    }

    this.NumberOfRows = numRows;
    this.StripeCount = stripes;
    this.TypeCount = types;
    this.ParseStatus = footerStatus;
  }

  private static byte[] ReadExact(Stream stream, int count) {
    var buf = new byte[count];
    var read = 0;
    while (read < count) {
      var n = stream.Read(buf, read, count - read);
      if (n <= 0) throw new EndOfStreamException("Unexpected EOF reading ORC container.");
      read += n;
    }
    return buf;
  }
}
