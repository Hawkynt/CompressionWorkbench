#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.UImage;

/// <summary>
/// Writer for the legacy U-Boot uImage container: the fixed 64-byte big-endian
/// header defined by <c>include/image.h</c> followed by the body, with both CRCs
/// computed the way <c>mkimage</c> computes them.
/// </summary>
/// <remarks>
/// <para>The header is the whole format, and every field of it is either supplied
/// by the caller or derived from the body, so writing one is a transcription. The
/// header CRC is taken over the 64 bytes with its own field zeroed, which is what
/// <see cref="UImageReader"/> re-computes when it reports <c>header_crc_ok</c>.</para>
///
/// <para>The body is written verbatim and the compression byte is recorded as
/// stated rather than applied: this project stays dependency-light and does not
/// carry the six codecs the field can name. A caller writing a compressed body
/// compresses it first and says which scheme it used.</para>
/// </remarks>
public static class UImageWriter {

  /// <summary>The name the reader gives the body as stored.</summary>
  public const string PayloadName = "payload.bin";

  /// <summary>The name the reader gives the rendered summary.</summary>
  public const string MetadataName = "metadata.ini";

  /// <summary>The name the reader gives the header as stored.</summary>
  public const string HeaderName = "header.bin";

  /// <summary>The name the reader adds for an uncompressed body.</summary>
  public const string DecompressedName = "payload_decompressed.bin";

  /// <summary>The header fields that are not derived from the body.</summary>
  public sealed record Header(
    uint Timestamp = 0,
    uint LoadAddress = 0,
    uint EntryPoint = 0,
    byte Os = 5,            // LINUX
    byte Architecture = 2,  // ARM
    byte Type = 2,          // KERNEL
    byte Compression = 0,   // none
    string Name = ""
  );

  /// <summary>
  /// Writes a uImage carrying <paramref name="body"/> under <paramref name="header"/>.
  /// </summary>
  public static void Write(Stream output, Header header, ReadOnlySpan<byte> body) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(header);

    var image = new byte[UImageReader.HeaderSize];
    BinaryPrimitives.WriteUInt32BigEndian(image, UImageReader.Magic);
    // image[4..8] is the header CRC and stays zero until the rest is written.
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8), header.Timestamp);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(12), (uint)body.Length);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16), header.LoadAddress);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(20), header.EntryPoint);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(24), Crc32Ieee.Compute(body));
    image[28] = header.Os;
    image[29] = header.Architecture;
    image[30] = header.Type;
    image[31] = header.Compression;

    // The name field is a fixed 32 bytes, NUL-padded and NUL-terminated when it
    // does not fill them; a longer name is cut so the terminator still fits.
    var name = Encoding.ASCII.GetBytes(header.Name ?? "");
    var kept = Math.Min(name.Length, UImageReader.NameLength - 1);
    name.AsSpan(0, kept).CopyTo(image.AsSpan(32));

    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), Crc32Ieee.Compute(image));

    output.Write(image, 0, image.Length);
    output.Write(body);
  }

  /// <summary>
  /// The body and header a create or edit describes: the single payload input as
  /// the body, and the header fields read out of <c>metadata.ini</c> when the
  /// caller passes the one the reader rendered.
  /// </summary>
  public static (Header Header, byte[] Body) From(IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);

    byte[]? body = null;
    var header = new Header();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var leaf = Path.GetFileName(input.ArchiveName);
      if (leaf.Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) {
        header = ReadMetadata(input.ReadContent(), header);
        continue;
      }
      // header.bin is the reader's view of the header, which metadata.ini already
      // describes field by field; taking it as the body would nest the container
      // in itself. payload_decompressed.bin is the same bytes as payload.bin for
      // the only compression this project writes.
      if (leaf.Equals(HeaderName, StringComparison.OrdinalIgnoreCase)) continue;
      if (leaf.Equals(DecompressedName, StringComparison.OrdinalIgnoreCase) && body != null) continue;
      if (leaf.Equals(PayloadName, StringComparison.OrdinalIgnoreCase)) { body = input.ReadContent(); continue; }
      body ??= input.ReadContent();
    }

    return (header, body ?? []);
  }

  private static Header ReadMetadata(byte[] metadata, Header header) {
    foreach (var raw in Encoding.UTF8.GetString(metadata).Split('\n')) {
      var line = raw.Trim();
      var equals = line.IndexOf('=', StringComparison.Ordinal);
      if (equals < 0) continue;
      var key = line[..equals].Trim();
      var value = line[(equals + 1)..].Trim();

      switch (key.ToLowerInvariant()) {
        case "name": header = header with { Name = value }; break;
        case "timestamp" when uint.TryParse(value, CultureInfo.InvariantCulture, out var stamp):
          header = header with { Timestamp = stamp }; break;
        case "load_address" when TryHex(value, out var load):
          header = header with { LoadAddress = load }; break;
        case "entry_point" when TryHex(value, out var entry):
          header = header with { EntryPoint = entry }; break;
        // The reader renders these as "5 (LINUX)", so the number is the leading token.
        case "os" when TryLeadingByte(value, out var os): header = header with { Os = os }; break;
        case "arch" when TryLeadingByte(value, out var arch): header = header with { Architecture = arch }; break;
        case "type" when TryLeadingByte(value, out var type): header = header with { Type = type }; break;
        case "comp" when TryLeadingByte(value, out var comp): header = header with { Compression = comp }; break;
        default: break;
      }
    }
    return header;
  }

  private static bool TryHex(string value, out uint parsed) {
    parsed = 0;
    return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
  }

  private static bool TryLeadingByte(string value, out byte parsed) {
    var space = value.IndexOf(' ', StringComparison.Ordinal);
    return byte.TryParse(space < 0 ? value : value[..space], CultureInfo.InvariantCulture, out parsed);
  }
}
