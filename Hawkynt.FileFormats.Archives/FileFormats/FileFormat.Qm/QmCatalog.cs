using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Qm;

/// <summary>One compiled message. Null fields were not stored in the record (see docs/QM-ON-DISK.md).</summary>
/// <param name="Translations">One entry per plural form; a null entry is a null string.</param>
public sealed record QmMessage(string? Context, string? SourceText, string? Comment, IReadOnlyList<string?> Translations);

/// <summary>
/// A compiled Qt translation catalog (<c>.qm</c>), the binary form <c>QTranslator</c> loads and
/// <c>lrelease</c> writes. Reading follows QTranslator's section and record rules; see
/// docs/QM-ON-DISK.md for the layout.
/// </summary>
public sealed class QmCatalog {

  /// <summary>The 16-byte QM magic.</summary>
  public static ReadOnlySpan<byte> Magic => [0x3C, 0xB8, 0x64, 0x18, 0xCA, 0xEF, 0x9C, 0x95, 0xCD, 0x21, 0x1C, 0xBF, 0x60, 0xA1, 0xBD, 0xDD];

  internal const byte ContextsTag = 0x2F, HashesTag = 0x42, MessagesTag = 0x69, NumerusRulesTag = 0x88, DependenciesTag = 0x96, LanguageTag = 0xA7;
  internal const byte TagEnd = 1, TagSourceText16 = 2, TagTranslation = 3, TagContext16 = 4, TagObsolete1 = 5, TagSourceText = 6, TagContext = 7, TagComment = 8, TagObsolete2 = 9;

  public QmCatalog(string? language, IReadOnlyList<string> dependencies, IReadOnlyList<QmMessage> messages, byte[]? numerusRules = null, bool hasContextTable = false) {
    this.Language = language;
    this.Dependencies = dependencies;
    this.Messages = messages;
    this.NumerusRules = numerusRules ?? [];
    this.HasContextTable = hasContextTable;
  }

  public string? Language { get; }
  public IReadOnlyList<string> Dependencies { get; }
  public IReadOnlyList<QmMessage> Messages { get; }
  public byte[] NumerusRules { get; }

  /// <summary>Whether the file carries the context hash table that <c>lrelease -compress</c> adds.</summary>
  public bool HasContextTable { get; }

  /// <summary>Whether some record omits its source text or comment (an <c>lrelease -compress</c> file).</summary>
  public bool IsStripped => this.Messages.Any(m => m.SourceText == null || m.Comment == null);

  public static bool HasMagic(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

  public static QmCatalog Read(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    if (!HasMagic(data))
      throw new InvalidDataException("Not a Qt QM file (magic missing).");
    string? language = null;
    var dependencies = new List<string>();
    byte[] rules = [];
    ReadOnlyMemory<byte> hashes = default, messages = default;
    var hasContexts = false;
    var pos = Magic.Length;
    while (pos < data.Length - 4) {
      var tag = data[pos];
      var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 1));
      pos += 5;
      if (tag == 0 || length == 0) break;
      if (length > (uint)(data.Length - pos))
        throw new InvalidDataException($"QM section 0x{tag:X2} runs past the end of the file.");
      var block = data.AsMemory(pos, (int)length);
      switch (tag) {
        case HashesTag: hashes = block; break;
        case MessagesTag: messages = block; break;
        case ContextsTag: hasContexts = true; break;
        case NumerusRulesTag: rules = block.ToArray(); break;
        case LanguageTag: language = Utf8(block.Span); break;
        case DependenciesTag:
          for (var p = 0; p < block.Length;) {
            var (s, n) = QString(block.Span[p..]);
            dependencies.Add(s ?? "");
            p += n;
          }
          break;
      }
      pos += (int)length;
    }
    if (hashes.Length % 8 != 0)
      throw new InvalidDataException("QM hash table length is not a multiple of 8.");

    // Records sit back to back in the Messages section; the hash table indexes every one of them.
    var offsets = new SortedSet<uint>();
    for (var i = 0; i < hashes.Length; i += 8) {
      var offset = BinaryPrimitives.ReadUInt32BigEndian(hashes.Span[(i + 4)..]);
      if (offset >= messages.Length)
        throw new InvalidDataException($"QM hash entry points past the message table (offset {offset}).");
      offsets.Add(offset);
    }
    var list = new List<QmMessage>(offsets.Count);
    foreach (var offset in offsets)
      list.Add(ReadRecord(messages.Span, (int)offset));
    return new QmCatalog(language, dependencies, list, rules, hasContexts);
  }

  private static QmMessage ReadRecord(ReadOnlySpan<byte> m, int p) {
    string? context = null, source = null, comment = null;
    var translations = new List<string?>();
    while (true) {
      if (p >= m.Length) throw new InvalidDataException("QM message record runs past the message table.");
      var tag = m[p++];
      switch (tag) {
        case TagEnd:
          return new QmMessage(context, source, comment, translations);
        case TagTranslation: {
          var (s, n) = QString(m[p..]);
          translations.Add(s);
          p += n;
          break;
        }
        case TagSourceText: source = Bytes(m, ref p); break;
        case TagContext: context = Bytes(m, ref p); break;
        case TagComment: comment = Bytes(m, ref p); break;
        case TagObsolete1: p += 4; break;
        case TagSourceText16 or TagContext16 or TagObsolete2:
          throw new InvalidDataException($"QM record uses the Qt 3 field tag {tag}, which this reader does not decode.");
        default:
          throw new InvalidDataException($"QM record has the unknown field tag {tag}.");
      }
    }
  }

  private static string Bytes(ReadOnlySpan<byte> m, ref int p) {
    if (p + 4 > m.Length) throw new InvalidDataException("QM field length runs past the message table.");
    var n = BinaryPrimitives.ReadUInt32BigEndian(m[p..]);
    p += 4;
    if (n > (uint)(m.Length - p)) throw new InvalidDataException("QM field runs past the message table.");
    var s = Utf8(m.Slice(p, (int)n));
    p += (int)n;
    return s;
  }

  /// <summary>A QDataStream QString: UInt32BE byte length (0xFFFFFFFF = null), UTF-16BE.</summary>
  private static (string? Value, int Consumed) QString(ReadOnlySpan<byte> m) {
    if (m.Length < 4) throw new InvalidDataException("QM string length runs past its section.");
    var n = BinaryPrimitives.ReadUInt32BigEndian(m);
    if (n == 0xFFFFFFFF) return (null, 4);
    if ((n & 1) != 0 || n > (uint)(m.Length - 4)) throw new InvalidDataException("QM UTF-16 string has an odd or overlong length.");
    return (Encoding.BigEndianUnicode.GetString(m.Slice(4, (int)n)), 4 + (int)n);
  }

  private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

  private static string Utf8(ReadOnlySpan<byte> b) {
    try {
      return StrictUtf8.GetString(b);
    } catch (DecoderFallbackException ex) {
      throw new InvalidDataException("QM file contains invalid UTF-8.", ex);
    }
  }

  // ── writing (lrelease default mode) ─────────────────────────────────

  /// <summary>ELF hash over the bytes, 1 when it comes out 0 (QTranslator's lookup hash).</summary>
  internal static uint ElfHash(ReadOnlySpan<byte> bytes) {
    uint h = 0;
    foreach (var b in bytes) {
      if (b == 0) break;
      h = (h << 4) + b;
      var g = h & 0xF0000000;
      if (g != 0) h ^= g >> 24;
      h &= ~g;
    }
    return h == 0 ? 1 : h;
  }

  /// <summary>
  /// Writes the catalog the way <c>lrelease</c> does in its default mode: records sorted by
  /// (context, source, comment) bytes, each carrying comment, source and context, the hash table
  /// sorted by (hash, offset). Every message must have context, source and comment set.
  /// </summary>
  public byte[] Write() {
    var sorted = this.Messages
      .Select(m => (Context: Encoding.UTF8.GetBytes(m.Context ?? throw new InvalidOperationException("QM message without context.")),
        Source: Encoding.UTF8.GetBytes(m.SourceText ?? throw new InvalidOperationException("QM message without source text.")),
        Comment: Encoding.UTF8.GetBytes(m.Comment ?? throw new InvalidOperationException("QM message without comment.")),
        m.Translations))
      .ToList();
    sorted.Sort((a, b) => {
      var c = a.Context.AsSpan().SequenceCompareTo(b.Context);
      if (c != 0) return c;
      c = a.Source.AsSpan().SequenceCompareTo(b.Source);
      return c != 0 ? c : a.Comment.AsSpan().SequenceCompareTo(b.Comment);
    });

    using var messages = new MemoryStream();
    var index = new List<(uint Hash, uint Offset)>();
    foreach (var (context, source, comment, translations) in sorted) {
      index.Add((ElfHash([.. source, .. comment]), (uint)messages.Position));
      foreach (var t in translations) {
        messages.WriteByte(TagTranslation);
        WriteQString(messages, t);
      }
      WriteField(messages, TagComment, comment);
      WriteField(messages, TagSourceText, source);
      WriteField(messages, TagContext, context);
      messages.WriteByte(TagEnd);
    }
    index.Sort();

    using var output = new MemoryStream();
    output.Write(Magic);
    if (!string.IsNullOrEmpty(this.Language))
      WriteSection(output, LanguageTag, Encoding.UTF8.GetBytes(this.Language));
    if (this.Dependencies.Count > 0) {
      using var deps = new MemoryStream();
      foreach (var d in this.Dependencies) WriteQString(deps, d);
      WriteSection(output, DependenciesTag, deps.ToArray());
    }
    if (index.Count > 0) {
      var table = new byte[index.Count * 8];
      for (var i = 0; i < index.Count; ++i) {
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(i * 8), index[i].Hash);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(i * 8 + 4), index[i].Offset);
      }
      WriteSection(output, HashesTag, table);
      WriteSection(output, MessagesTag, messages.ToArray());
    }
    if (this.NumerusRules.Length > 0)
      WriteSection(output, NumerusRulesTag, this.NumerusRules);
    return output.ToArray();
  }

  private static void WriteSection(Stream s, byte tag, byte[] data) {
    s.WriteByte(tag);
    WriteUInt32(s, (uint)data.Length);
    s.Write(data);
  }

  private static void WriteField(Stream s, byte tag, byte[] data) {
    s.WriteByte(tag);
    WriteUInt32(s, (uint)data.Length);
    s.Write(data);
  }

  private static void WriteQString(Stream s, string? value) {
    if (value == null) { WriteUInt32(s, 0xFFFFFFFF); return; }
    var bytes = Encoding.BigEndianUnicode.GetBytes(value);
    WriteUInt32(s, (uint)bytes.Length);
    s.Write(bytes);
  }

  private static void WriteUInt32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, v);
    s.Write(b);
  }
}
