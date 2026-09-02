#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Qoi;

/// <summary>
/// QOI (Quite OK Image). 14-byte header: magic "qoif", width(u32 BE),
/// height(u32 BE), channels(u8, 3=RGB/4=RGBA), colorspace(u8, 0=sRGB/1=linear),
/// then QOI-encoded chunks (OP_RGB, OP_RGBA, OP_INDEX, OP_DIFF, OP_LUMA, OP_RUN)
/// terminated by seven 0x00 bytes and a final 0x01.
///
/// <para>Surfaces <c>FULL.qoi</c> verbatim, a <c>metadata.ini</c> (width, height,
/// channels, colorspace) and the fully decoded raw pixels as <c>pixels.bin</c>
/// (RGBA, 4 bytes/pixel; Kind="Raw"). Also implements <see cref="IArchiveCreatable"/>:
/// a single raw RGBA <c>pixels.bin</c> plus a <c>metadata.ini</c> declaring the
/// geometry is re-encoded into a valid QOI. Round-trip safe.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://qoiformat.org/</c> — official QOI site — one-page specification</description></item>
///   <item><description><c>https://github.com/phoboslab/qoi</c> — reference implementation by Dominic Szablewski</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/QOI_(image_format)</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class QoiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Qoi";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Quite OK Image (QOI)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".qoi";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".qoi"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("qoif"u8.ToArray(), Confidence: 0.92),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "Quite OK Image (QOI): header + decoded RGBA pixels; round-trip encoder/decoder.";

  private sealed record QoiHeader(uint Width, uint Height, byte Channels, byte Colorspace, bool Valid);

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var data = ReadAll(stream);
    var h = ParseHeader(data);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.qoi", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };
    if (h.Valid) {
      var pixels = Decode(data, h);
      if (pixels != null)
        entries.Add(new ArchiveEntryInfo(2, "pixels.bin", pixels.Length, pixels.Length, "Stored", false, false, null, "Raw"));
    }
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (Wants(files, "FULL.qoi"))
      WriteFile(outputDir, "FULL.qoi", data);

    var h = ParseHeader(data);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(h)));

    if (h.Valid && Wants(files, "pixels.bin")) {
      var pixels = Decode(data, h);
      if (pixels != null)
        WriteFile(outputDir, "pixels.bin", pixels);
    }
  }

  // Re-encode a raw RGBA pixels.bin into a QOI using geometry from metadata.ini.
  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    byte[]? pixels = null;
    uint width = 0, height = 0;
    byte channels = 4, colorspace = 0;

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var leaf = Path.GetFileName(input.ArchiveName);
      if (leaf.Equals("pixels.bin", StringComparison.OrdinalIgnoreCase))
        pixels = input.ReadContent();
      else if (leaf.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase))
        ParseCreateMeta(Encoding.UTF8.GetString(input.ReadContent()), ref width, ref height, ref channels, ref colorspace);
      else if (leaf.EndsWith(".qoi", StringComparison.OrdinalIgnoreCase)) {
        // Passthrough: a QOI input is copied verbatim.
        var raw = input.ReadContent();
        output.Write(raw, 0, raw.Length);
        return;
      }
    }

    if (pixels == null)
      throw new InvalidOperationException("QOI Create requires a pixels.bin input.");
    if (width == 0 || height == 0)
      throw new InvalidOperationException("QOI Create requires width and height in metadata.ini.");
    if (channels is not (3 or 4)) channels = 4;

    var expected = checked((long)width * height * 4);
    if (pixels.Length < expected)
      throw new InvalidOperationException("pixels.bin smaller than width*height*4 (RGBA).");

    var encoded = Encode(pixels, width, height, channels, colorspace);
    output.Write(encoded, 0, encoded.Length);
  }

  private static void ParseCreateMeta(string ini, ref uint w, ref uint h, ref byte ch, ref byte cs) {
    foreach (var line in ini.Split('\n')) {
      var t = line.TrimEnd('\r').Trim();
      var eq = t.IndexOf('=');
      if (eq <= 0) continue;
      var key = t[..eq].Trim();
      var val = t[(eq + 1)..].Trim();
      switch (key.ToLowerInvariant()) {
        case "width": uint.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out w); break;
        case "height": uint.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out h); break;
        case "channels": if (byte.TryParse(val, out var c)) ch = c; break;
        case "colorspace": if (byte.TryParse(val, out var s)) cs = s; break;
      }
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static QoiHeader ParseHeader(byte[] data) {
    if (data.Length < 14 || data[0] != 'q' || data[1] != 'o' || data[2] != 'i' || data[3] != 'f')
      return new QoiHeader(0, 0, 0, 0, Valid: false);
    var width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
    var height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
    var channels = data[12];
    var colorspace = data[13];
    if (channels is not (3 or 4)) return new QoiHeader(width, height, channels, colorspace, Valid: false);
    return new QoiHeader(width, height, channels, colorspace, Valid: true);
  }

  // QOI decoder -> raw RGBA (4 bytes/pixel). Returns null on implausible geometry.
  private static byte[]? Decode(byte[] data, QoiHeader h) {
    try {
      var pixelCount = (long)h.Width * h.Height;
      if (pixelCount <= 0 || pixelCount > 256L * 1024 * 1024) return null;
      var outBuf = new byte[pixelCount * 4];

      Span<byte> index = stackalloc byte[64 * 4];
      byte r = 0, g = 0, b = 0, a = 255;
      var p = 14;
      var run = 0;

      for (long px = 0; px < pixelCount; ++px) {
        if (run > 0) {
          --run;
        } else if (p < data.Length) {
          var op = data[p++];
          if (op == 0xFE) { // OP_RGB
            if (p + 3 > data.Length) break;
            r = data[p++]; g = data[p++]; b = data[p++];
          } else if (op == 0xFF) { // OP_RGBA
            if (p + 4 > data.Length) break;
            r = data[p++]; g = data[p++]; b = data[p++]; a = data[p++];
          } else if ((op & 0xC0) == 0x00) { // OP_INDEX
            var i = (op & 0x3F) * 4;
            r = index[i]; g = index[i + 1]; b = index[i + 2]; a = index[i + 3];
          } else if ((op & 0xC0) == 0x40) { // OP_DIFF
            r = (byte)(r + ((op >> 4) & 3) - 2);
            g = (byte)(g + ((op >> 2) & 3) - 2);
            b = (byte)(b + (op & 3) - 2);
          } else if ((op & 0xC0) == 0x80) { // OP_LUMA
            if (p >= data.Length) break;
            var b2 = data[p++];
            var vg = (op & 0x3F) - 32;
            r = (byte)(r + vg - 8 + ((b2 >> 4) & 0x0F));
            g = (byte)(g + vg);
            b = (byte)(b + vg - 8 + (b2 & 0x0F));
          } else { // OP_RUN
            run = op & 0x3F; // bias of -1 consumed by this iteration
          }
          var hash = (r * 3 + g * 5 + b * 7 + a * 11) % 64 * 4;
          index[hash] = r; index[hash + 1] = g; index[hash + 2] = b; index[hash + 3] = a;
        }
        var o = px * 4;
        outBuf[o] = r; outBuf[o + 1] = g; outBuf[o + 2] = b; outBuf[o + 3] = a;
      }
      return outBuf;
    } catch {
      return null;
    }
  }

  // QOI encoder from raw RGBA. When channels==3 alpha runs are ignored on output
  // (OP_RGB is emitted) but the input is still 4 bytes/pixel.
  private static byte[] Encode(byte[] rgba, uint width, uint height, byte channels, byte colorspace) {
    var pixelCount = (long)width * height;
    using var ms = new MemoryStream();
    Span<byte> hdr = stackalloc byte[14];
    hdr[0] = (byte)'q'; hdr[1] = (byte)'o'; hdr[2] = (byte)'i'; hdr[3] = (byte)'f';
    BinaryPrimitives.WriteUInt32BigEndian(hdr[4..8], width);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[8..12], height);
    hdr[12] = channels; hdr[13] = colorspace;
    ms.Write(hdr);

    Span<byte> index = stackalloc byte[64 * 4];
    byte pr = 0, pg = 0, pb = 0, pa = 255;
    var run = 0;

    for (long px = 0; px < pixelCount; ++px) {
      var o = px * 4;
      var r = rgba[o]; var g = rgba[o + 1]; var b = rgba[o + 2];
      var a = channels == 4 ? rgba[o + 3] : (byte)255;

      if (r == pr && g == pg && b == pb && a == pa) {
        ++run;
        if (run == 62 || px == pixelCount - 1) { ms.WriteByte((byte)(0xC0 | (run - 1))); run = 0; }
      } else {
        if (run > 0) { ms.WriteByte((byte)(0xC0 | (run - 1))); run = 0; }
        var hash = (r * 3 + g * 5 + b * 7 + a * 11) % 64;
        var hi = hash * 4;
        if (index[hi] == r && index[hi + 1] == g && index[hi + 2] == b && index[hi + 3] == a) {
          ms.WriteByte((byte)hash); // OP_INDEX
        } else {
          index[hi] = r; index[hi + 1] = g; index[hi + 2] = b; index[hi + 3] = a;
          if (a == pa) {
            int dr = r - pr, dg = g - pg, db = b - pb;
            var drDg = dr - dg; var dbDg = db - dg;
            if (dr is >= -2 and <= 1 && dg is >= -2 and <= 1 && db is >= -2 and <= 1) {
              ms.WriteByte((byte)(0x40 | ((dr + 2) << 4) | ((dg + 2) << 2) | (db + 2)));
            } else if (dg is >= -32 and <= 31 && drDg is >= -8 and <= 7 && dbDg is >= -8 and <= 7) {
              ms.WriteByte((byte)(0x80 | (dg + 32)));
              ms.WriteByte((byte)(((drDg + 8) << 4) | (dbDg + 8)));
            } else {
              ms.WriteByte(0xFE); ms.WriteByte(r); ms.WriteByte(g); ms.WriteByte(b);
            }
          } else {
            ms.WriteByte(0xFF); ms.WriteByte(r); ms.WriteByte(g); ms.WriteByte(b); ms.WriteByte(a);
          }
        }
      }
      pr = r; pg = g; pb = b; pa = a;
    }

    // End marker.
    for (var i = 0; i < 7; ++i) ms.WriteByte(0x00);
    ms.WriteByte(0x01);
    return ms.ToArray();
  }

  private static string BuildMetadataIni(QoiHeader h) {
    var sb = new StringBuilder();
    sb.Append("[Qoi]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(h.Valid ? 1 : 0)}\n");
    if (!h.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"width={h.Width}\n");
    sb.Append(CultureInfo.InvariantCulture, $"height={h.Height}\n");
    sb.Append(CultureInfo.InvariantCulture, $"channels={h.Channels}\n");
    sb.Append(CultureInfo.InvariantCulture, $"colorspace={h.Colorspace}\n");
    sb.Append("parse_status=ok\n");
    return sb.ToString();
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
