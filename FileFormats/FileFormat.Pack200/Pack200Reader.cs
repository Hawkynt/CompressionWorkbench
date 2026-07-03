using System.IO.Compression;
using System.Text;

namespace FileFormat.Pack200;

/// <summary>
/// Decodes the segment structure of a Pack200 (JSR-200) archive: it inflates a
/// gzip-wrapped <c>.pack.gz</c> when needed, parses the archive header, walks the
/// constant-pool and class bands, and recovers the internal names of the classes
/// the archive defines.
/// </summary>
/// <remarks>
/// <para>
/// The archive header, constant pool and class-defining bands are decoded using
/// their JSR-200 default codings, which is sufficient for archives produced with
/// the standard <c>pack200</c> tool at its normal settings (no custom attribute
/// layouts). Reconstruction of the full byte content of each <c>.class</c> file
/// (method, code and bytecode bands) is intentionally out of scope; the decoder
/// therefore exposes class enumeration rather than class rebuilding.
/// </para>
/// <para>
/// Header parsing failures throw <see cref="InvalidDataException"/>; failures that
/// occur only while resolving class names are caught and reported as a
/// <see cref="Pack200DecodeStatus.Partial"/> result so that listing never throws.
/// </para>
/// </remarks>
public sealed class Pack200Reader {

  /// <summary>The Pack200 archive magic word, 0xCAFED00D, stored big-endian.</summary>
  public static readonly byte[] Magic = [0xCA, 0xFE, 0xD0, 0x0D];

  // ── Archive option bit flags (AO_* in JSR-200) ────────────────────────────
  private const int AoHaveSpecialFormats = 1 << 0;
  private const int AoHaveCpNumbers = 1 << 1;
  private const int AoHaveCpExtraCounts = 1 << 3;
  private const int AoHaveFileHeaders = 1 << 4;

  // Inner-class "long form" flag: outer-class and name bands carry an entry.
  private const int IcLongForm = 1 << 16;

  /// <summary>Returns true if <paramref name="header"/> begins with the raw or gzip-wrapped Pack200 magic.</summary>
  public static bool LooksLikePack200(ReadOnlySpan<byte> header) {
    if (header.Length >= 4 && header[0] == 0xCA && header[1] == 0xFE && header[2] == 0xD0 && header[3] == 0x0D)
      return true;
    // gzip-wrapped .pack.gz — the magic only appears after inflation, so we can
    // only confirm the gzip envelope here.
    return header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B;
  }

  /// <summary>Reads and decodes the first segment of a Pack200 archive from <paramref name="stream"/>.</summary>
  public Pack200Segment Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var raw = ReadAll(stream);
    var data = MaybeInflate(raw);

    if (data.Length < 4 || data[0] != 0xCA || data[1] != 0xFE || data[2] != 0xD0 || data[3] != 0x0D)
      throw new InvalidDataException("Pack200: missing 0xCAFED00D magic.");

    var reader = new Pack200BandReader(data);
    for (var i = 0; i < 4; ++i)
      reader.ReadByte(); // consume magic

    var u5 = Pack200Coding.Unsigned5;
    var minver = (int)reader.ReadValue(u5);
    var majver = (int)reader.ReadValue(u5);
    var options = (int)reader.ReadValue(u5);

    long modtime = 0;
    var resourceFiles = 0;
    if ((options & AoHaveFileHeaders) != 0) {
      reader.ReadValue(u5);                       // archive_size_hi
      reader.ReadValue(u5);                       // archive_size_lo
      resourceFiles = (int)reader.ReadValue(u5);  // archive_next_count (non-class files)
      modtime = reader.ReadValue(u5);             // archive_modtime
      reader.ReadValue(u5);                        // file_count
    }

    var cpUtf8 = (int)reader.ReadValue(u5);
    var haveNumbers = (options & AoHaveCpNumbers) != 0;
    int cpInt = 0, cpFloat = 0, cpLong = 0, cpDouble = 0;
    if (haveNumbers) {
      cpInt = (int)reader.ReadValue(u5);
      cpFloat = (int)reader.ReadValue(u5);
      cpLong = (int)reader.ReadValue(u5);
      cpDouble = (int)reader.ReadValue(u5);
    }

    var cpString = (int)reader.ReadValue(u5);
    var cpClass = (int)reader.ReadValue(u5);
    var cpSignature = (int)reader.ReadValue(u5);
    var cpDescr = (int)reader.ReadValue(u5);
    var cpField = (int)reader.ReadValue(u5);
    var cpMethod = (int)reader.ReadValue(u5);
    var cpImethod = (int)reader.ReadValue(u5);

    var haveExtra = (options & AoHaveCpExtraCounts) != 0;
    if (haveExtra) {
      for (var i = 0; i < 4; ++i)
        reader.ReadValue(u5); // cp_MethodHandle/Type/InvokeDynamic/BootstrapMethod counts
    }

    var icCount = (int)reader.ReadValue(u5);
    var defClassMinver = (int)reader.ReadValue(u5);
    var defClassMajver = (int)reader.ReadValue(u5);
    var classCount = (int)reader.ReadValue(u5);

    var baseSegment = new Pack200Segment {
      MinVersion = minver,
      MajVersion = majver,
      Options = options,
      DefaultClassMinVersion = defClassMinver,
      DefaultClassMajVersion = defClassMajver,
      Utf8Count = cpUtf8,
      ClassPoolCount = cpClass,
      ClassCount = classCount,
      ResourceFileCount = resourceFiles,
      ModTime = modtime,
    };

    // Only the default-coding band layout is supported. Custom attribute layouts
    // (special formats) and version-7 extra constant-pool bands are not decoded.
    if ((options & AoHaveSpecialFormats) != 0)
      return Partial(baseSegment, classCount, "custom attribute layouts (special formats) not decoded");
    if (haveExtra)
      return Partial(baseSegment, classCount, "version-7 extra constant-pool bands not decoded");

    try {
      var utf8 = DecodeUtf8Pool(reader, cpUtf8);

      // Numeric constant-pool bands (int/float/long/double) carry no class names
      // but must be consumed to keep the cursor aligned.
      var ud = Pack200Coding.Udelta5;
      reader.ReadBand(ud, cpInt);
      reader.ReadBand(ud, cpFloat);
      reader.ReadBand(ud, cpLong * 2);   // hi + lo bands
      reader.ReadBand(ud, cpDouble * 2); // hi + lo bands

      reader.ReadBand(ud, cpString);     // cp_String -> Utf8

      var classPool = DecodeClassPool(reader, utf8, cpClass);

      // cp_Signature: form (refs Utf8) then one class ref per 'L' descriptor char.
      var sigForms = reader.ReadBand(Pack200Coding.Delta5, cpSignature);
      var classRefs = 0;
      foreach (var f in sigForms) {
        var s = ResolveUtf8(utf8, (int)f);
        foreach (var c in s)
          if (c == 'L') ++classRefs;
      }
      reader.ReadBand(ud, classRefs);

      // cp_Descr / cp_Field / cp_Method / cp_Imethod: two bands each.
      ConsumePair(reader, cpDescr);
      ConsumePair(reader, cpField);
      ConsumePair(reader, cpMethod);
      ConsumePair(reader, cpImethod);

      // Inner-class bands.
      if (icCount > 0) {
        reader.ReadBand(ud, icCount);                       // ic_this_class
        var icFlags = reader.ReadBand(u5, icCount);         // ic_flags
        var longForms = 0;
        foreach (var fl in icFlags)
          if ((fl & IcLongForm) != 0) ++longForms;
        reader.ReadBand(ud, longForms);                     // ic_outer_class
        reader.ReadBand(ud, longForms);                     // ic_name
      }

      // class_this: one Class-pool reference per defined class.
      var thisRefs = reader.ReadBand(Pack200Coding.Delta5, classCount);
      var names = new List<string>(classCount);
      foreach (var r in thisRefs) {
        if (r < 0 || r >= classPool.Count)
          return Partial(baseSegment, classCount, "class_this index out of range");
        var name = classPool[(int)r];
        if (!IsPlausibleInternalName(name))
          return Partial(baseSegment, classCount, "decoded class name failed validation");
        names.Add(name);
      }

      return new Pack200Segment {
        MinVersion = minver,
        MajVersion = majver,
        Options = options,
        DefaultClassMinVersion = defClassMinver,
        DefaultClassMajVersion = defClassMajver,
        Utf8Count = cpUtf8,
        ClassPoolCount = cpClass,
        ClassCount = classCount,
        ResourceFileCount = resourceFiles,
        ModTime = modtime,
        ClassNames = names,
        Status = Pack200DecodeStatus.Full,
      };
    } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException) {
      return Partial(baseSegment, classCount, "band decode incomplete: " + ex.Message);
    }
  }

  // ── Constant-pool decoding ────────────────────────────────────────────────

  /// <summary>Reconstructs the UTF-8 constant pool from its prefix/suffix/char bands.</summary>
  private static List<string> DecodeUtf8Pool(Pack200BandReader reader, int count) {
    var strings = new List<string>(Math.Max(1, count));
    if (count <= 0)
      return strings;

    strings.Add(string.Empty); // entry 0 is always the empty string, never transmitted

    var prefix = reader.ReadBand(Pack200Coding.Delta5, count >= 2 ? count - 2 : 0);
    var suffix = reader.ReadBand(Pack200Coding.Unsigned5, count - 1);

    var smallChars = 0;
    var bigStrings = 0;
    foreach (var s in suffix) {
      if (s == 0) ++bigStrings; else smallChars += (int)s;
    }

    var chars = reader.ReadBand(Pack200Coding.Char3, smallChars);
    var bigSuffix = reader.ReadBand(Pack200Coding.Delta5, bigStrings);
    var bigLen = 0L;
    foreach (var b in bigSuffix) bigLen += b;
    var bigChars = reader.ReadBand(Pack200Coding.Delta5, (int)bigLen);

    var ci = 0;
    var bi = 0;
    var bigIdx = 0;
    var sb = new StringBuilder();
    for (var i = 1; i < count; ++i) {
      var p = i == 1 ? 0 : (int)prefix[i - 2];
      var prev = strings[i - 1];
      if (p < 0 || p > prev.Length)
        throw new InvalidDataException("Pack200: UTF-8 prefix length out of range.");

      sb.Clear();
      sb.Append(prev, 0, p);
      var s = (int)suffix[i - 1];
      if (s == 0) {
        var len = (int)bigSuffix[bigIdx++];
        for (var k = 0; k < len; ++k)
          sb.Append((char)bigChars[bi++]);
      } else {
        for (var k = 0; k < s; ++k)
          sb.Append((char)chars[ci++]);
      }
      strings.Add(sb.ToString());
    }
    return strings;
  }

  /// <summary>Decodes the Class constant pool as UTF-8 references.</summary>
  private static List<string> DecodeClassPool(Pack200BandReader reader, List<string> utf8, int count) {
    var refs = reader.ReadBand(Pack200Coding.Udelta5, count);
    var result = new List<string>(count);
    foreach (var r in refs)
      result.Add(ResolveUtf8(utf8, (int)r));
    return result;
  }

  private static string ResolveUtf8(List<string> utf8, int index)
    => index >= 0 && index < utf8.Count ? utf8[index] : string.Empty;

  /// <summary>Reads a two-band constant-pool member (a name/class band and a type/desc band).</summary>
  private static void ConsumePair(Pack200BandReader reader, int count) {
    reader.ReadBand(Pack200Coding.Delta5, count);
    reader.ReadBand(Pack200Coding.Udelta5, count);
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static Pack200Segment Partial(Pack200Segment b, int classCount, string note) {
    var placeholders = new List<string>(classCount);
    for (var i = 0; i < classCount; ++i)
      placeholders.Add($"class-{i:D4}");
    return new Pack200Segment {
      MinVersion = b.MinVersion,
      MajVersion = b.MajVersion,
      Options = b.Options,
      DefaultClassMinVersion = b.DefaultClassMinVersion,
      DefaultClassMajVersion = b.DefaultClassMajVersion,
      Utf8Count = b.Utf8Count,
      ClassPoolCount = b.ClassPoolCount,
      ClassCount = classCount,
      ResourceFileCount = b.ResourceFileCount,
      ModTime = b.ModTime,
      ClassNames = placeholders,
      Status = Pack200DecodeStatus.Partial,
      StatusNote = note,
    };
  }

  /// <summary>A plausible JVM internal class name: non-empty, ASCII, no path traversal.</summary>
  private static bool IsPlausibleInternalName(string name) {
    if (string.IsNullOrEmpty(name) || name.Length > 4096)
      return false;
    foreach (var c in name) {
      var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
        or '/' or '_' or '$' or '.' or '-';
      if (!ok)
        return false;
    }
    return true;
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream is MemoryStream ms)
      return ms.ToArray();
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  private static byte[] MaybeInflate(byte[] raw) {
    if (raw.Length < 2 || raw[0] != 0x1F || raw[1] != 0x8B)
      return raw;
    using var input = new MemoryStream(raw);
    using var gz = new GZipStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    gz.CopyTo(output);
    return output.ToArray();
  }
}
