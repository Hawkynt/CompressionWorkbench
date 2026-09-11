#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.CephFs;

/// <summary>
/// Managed reader/writer for the serialized pool format used by <c>rados export</c>
/// and <c>rados import</c>. The wire layout is derived from Ceph's RadosDump /
/// PoolDump sources; no Ceph native library is required.
/// </summary>
internal static class RadosPoolDump {
  internal const uint SuperMagic = 0xFFCEFFCE;
  internal const uint EndMagic = 0xECFFFFCE;
  internal const ushort ShortMagic = 0xFFCE;
  internal const uint SuperVersion = 2;
  internal const int SuperHeaderSize = 16;
  internal const int SectionHeaderSize = 18;
  internal const int SectionFooterSize = 10;
  internal const int DataChunkSize = 4 * 1024 * 1024;
  internal const ulong CephNoSnap = ulong.MaxValue - 1;

  private static readonly UTF8Encoding Utf8 = new(false, true);

  internal enum SectionType : byte {
    None = 0,
    PgBegin = 1,
    PgEnd = 2,
    ObjectBegin = 3,
    ObjectEnd = 4,
    Data = 5,
    Attrs = 6,
    OmapHeader = 7,
    Omap = 8,
    PgMetadata = 9,
    PoolBegin = 10,
    PoolEnd = 11,
    EndOfTypes = 12,
  }

  internal sealed class ObjectModel {
    public required string ObjectId { get; init; }
    public string Namespace { get; init; } = "";
    public string LocatorKey { get; init; } = "";
    public byte[] Data { get; set; } = [];
    public Dictionary<string, byte[]> Attributes { get; } = new(StringComparer.Ordinal);
    public byte[] OmapHeader { get; set; } = [];
    public SortedDictionary<string, byte[]> Omap { get; } = new(StringComparer.Ordinal);
    public long FirstPayloadOffset { get; set; } = -1;

    public string EntryName => EncodeEntryName(this.Namespace, this.ObjectId);
  }

  internal sealed record LayoutExtent(long Offset, long Length, bool IsPayload, string? EntryName = null, bool IsFree = false);

  internal sealed class ParsedDump {
    public required byte[] RawData { get; init; }
    public List<ObjectModel> Objects { get; } = [];
    public List<LayoutExtent> Layout { get; } = [];
    public bool CanRewrite { get; set; } = true;
  }

  private readonly record struct Section(SectionType Type, int Start, int BodyOffset, int BodyLength, int End);

  internal static ParsedDump Parse(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Parse(ms.ToArray());
  }

  internal static ParsedDump Parse(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    if (data.Length < SuperHeaderSize)
      throw new InvalidDataException("RADOS pool dump is shorter than the 16-byte super header.");

    var result = new ParsedDump { RawData = data };
    var pos = 0;
    var magic = ReadUInt32(data, ref pos, data.Length);
    var version = ReadUInt32(data, ref pos, data.Length);
    var headerSize = checked((int)ReadUInt32(data, ref pos, data.Length));
    var footerSize = checked((int)ReadUInt32(data, ref pos, data.Length));

    if (magic != SuperMagic)
      throw new InvalidDataException($"RADOS pool dump has invalid super magic 0x{magic:X8}.");
    if (version > SuperVersion)
      throw new InvalidDataException($"RADOS pool dump version {version} is newer than supported version {SuperVersion}.");
    if (headerSize < SectionHeaderSize || footerSize < SectionFooterSize)
      throw new InvalidDataException("RADOS pool dump declares undersized section framing.");

    result.Layout.Add(new LayoutExtent(0, SuperHeaderSize, IsPayload: false));

    var first = ReadSection(data, ref pos, headerSize, footerSize, result);
    AddMetadataLayout(result, first);
    if (first.Type != SectionType.PoolBegin)
      throw new InvalidDataException("This descriptor supports portable 'rados export' pool dumps; the stream is not in pool mode.");
    if (first.BodyLength != 0)
      throw new InvalidDataException("RADOS TYPE_POOL_BEGIN must be empty.");

    while (true) {
      if (pos >= data.Length)
        throw new InvalidDataException("RADOS pool dump ended before TYPE_POOL_END.");

      var section = ReadSection(data, ref pos, headerSize, footerSize, result);
      if ((byte)section.Type >= (byte)SectionType.EndOfTypes) {
        result.CanRewrite = false;
        AddMetadataLayout(result, section);
        continue;
      }

      switch (section.Type) {
        case SectionType.ObjectBegin:
          ParseObject(data, ref pos, headerSize, footerSize, result, section);
          break;
        case SectionType.PoolEnd:
          AddMetadataLayout(result, section);
          if (section.BodyLength != 0)
            throw new InvalidDataException("RADOS TYPE_POOL_END must be empty.");
          if (pos < data.Length)
            result.Layout.Add(new LayoutExtent(pos, data.Length - pos, IsPayload: false, IsFree: true));
          return result;
        default:
          throw new InvalidDataException($"Unexpected RADOS pool-level section {(byte)section.Type}.");
      }
    }
  }

  private static void ParseObject(byte[] data, ref int pos, int headerSize, int footerSize, ParsedDump result, Section begin) {
    AddMetadataLayout(result, begin);
    var bodyPos = begin.BodyOffset;
    var bodyEnd = begin.BodyOffset + begin.BodyLength;
    var objectStruct = ReadStructHeader(data, ref bodyPos, bodyEnd, supportedVersion: 3, "object_begin", result, allowKnownNewerVersion: false);
    if (objectStruct.Version < 1)
      throw new InvalidDataException("Unsupported RADOS object_begin version.");

    var hobject = ReadHObject(data, ref bodyPos, objectStruct.End, result);
    if (hobject.Snap != CephNoSnap)
      throw new InvalidDataException("Portable RADOS pool dumps must contain head objects only.");

    if (objectStruct.Version > 1) {
      _ = ReadUInt64(data, ref bodyPos, objectStruct.End); // generation
      _ = ReadByte(data, ref bodyPos, objectStruct.End);   // shard_id
    }
    // v3 appends object_info_t. rados import explicitly does not use it; skip it.
    bodyPos = objectStruct.End;

    var model = new ObjectModel {
      ObjectId = hobject.ObjectId,
      Namespace = hobject.Namespace,
      LocatorKey = hobject.Key,
    };
    using var objectData = new MemoryStream();

    while (true) {
      if (pos >= data.Length)
        throw new InvalidDataException($"RADOS object '{model.ObjectId}' ended before TYPE_OBJECT_END.");

      var section = ReadSection(data, ref pos, headerSize, footerSize, result);
      if ((byte)section.Type >= (byte)SectionType.EndOfTypes) {
        result.CanRewrite = false;
        AddMetadataLayout(result, section);
        continue;
      }

      switch (section.Type) {
        case SectionType.Data:
          ParseDataSection(data, section, objectData, model, result);
          break;
        case SectionType.Attrs:
          ParseAttributes(data, section, model, result);
          AddMetadataLayout(result, section);
          break;
        case SectionType.OmapHeader:
          ParseOmapHeader(data, section, model, result);
          AddMetadataLayout(result, section);
          break;
        case SectionType.Omap:
          ParseOmap(data, section, model, result);
          AddMetadataLayout(result, section);
          break;
        case SectionType.ObjectEnd:
          AddMetadataLayout(result, section);
          if (section.BodyLength != 0)
            throw new InvalidDataException("RADOS TYPE_OBJECT_END must be empty.");
          model.Data = objectData.ToArray();
          result.Objects.Add(model);
          return;
        default:
          throw new InvalidDataException($"Unexpected section {(byte)section.Type} inside RADOS object '{model.ObjectId}'.");
      }
    }
  }

  private static void ParseDataSection(byte[] data, Section section, MemoryStream target, ObjectModel model, ParsedDump result) {
    var pos = section.BodyOffset;
    var end = section.BodyOffset + section.BodyLength;
    var structure = ReadStructHeader(data, ref pos, end, supportedVersion: 1, "data_section", result, allowKnownNewerVersion: false);
    var offset = ReadUInt64(data, ref pos, structure.End);
    var declaredLength = ReadUInt64(data, ref pos, structure.End);
    var payloadLength = checked((int)ReadUInt32(data, ref pos, structure.End));
    if (declaredLength != (ulong)payloadLength)
      throw new InvalidDataException("RADOS DATA section length does not match its encapsulated buffer length.");
    if ((ulong)pos + (ulong)payloadLength > (ulong)structure.End)
      throw new InvalidDataException("RADOS DATA section payload exceeds its structure boundary.");
    if (offset > int.MaxValue || declaredLength > int.MaxValue || offset + declaredLength > int.MaxValue)
      throw new NotSupportedException("RADOS objects larger than 2 GiB are not supported by this managed editor yet.");

    var payloadOffset = pos;
    if (model.FirstPayloadOffset < 0)
      model.FirstPayloadOffset = payloadOffset;
    target.Position = (long)offset;
    target.Write(data, payloadOffset, payloadLength);
    pos += payloadLength;
    if (pos != structure.End)
      result.CanRewrite = false;

    AddDataLayout(result, section, payloadOffset, payloadLength, model.EntryName);
  }

  private static void ParseAttributes(byte[] data, Section section, ObjectModel model, ParsedDump result) {
    var pos = section.BodyOffset;
    var end = section.BodyOffset + section.BodyLength;
    var structure = ReadStructHeader(data, ref pos, end, supportedVersion: 1, "attr_section", result, allowKnownNewerVersion: false);
    foreach (var (key, value) in ReadMap(data, ref pos, structure.End)) {
      // RadosImport restores only user xattrs encoded as '_' + key.
      if (key.Length > 1 && key[0] == '_')
        model.Attributes[key[1..]] = value;
    }
    if (pos != structure.End)
      result.CanRewrite = false;
  }

  private static void ParseOmapHeader(byte[] data, Section section, ObjectModel model, ParsedDump result) {
    var pos = section.BodyOffset;
    var end = section.BodyOffset + section.BodyLength;
    var structure = ReadStructHeader(data, ref pos, end, supportedVersion: 1, "omap_hdr_section", result, allowKnownNewerVersion: false);
    model.OmapHeader = ReadBuffer(data, ref pos, structure.End);
    if (pos != structure.End)
      result.CanRewrite = false;
  }

  private static void ParseOmap(byte[] data, Section section, ObjectModel model, ParsedDump result) {
    var pos = section.BodyOffset;
    var end = section.BodyOffset + section.BodyLength;
    var structure = ReadStructHeader(data, ref pos, end, supportedVersion: 1, "omap_section", result, allowKnownNewerVersion: false);
    foreach (var (key, value) in ReadMap(data, ref pos, structure.End))
      model.Omap[key] = value;
    if (pos != structure.End)
      result.CanRewrite = false;
  }

  internal static void Write(Stream output, IReadOnlyList<ObjectModel> objects) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(objects);
    if (!output.CanWrite)
      throw new ArgumentException("RADOS output stream must be writable.", nameof(output));
    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }

    Span<byte> super = stackalloc byte[SuperHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(super, SuperMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(super[4..], SuperVersion);
    BinaryPrimitives.WriteUInt32LittleEndian(super[8..], SectionHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(super[12..], SectionFooterSize);
    output.Write(super);

    WriteSection(output, SectionType.PoolBegin, []);
    foreach (var obj in objects) {
      WriteSection(output, SectionType.ObjectBegin, EncodeObjectBegin(obj));
      for (var offset = 0; offset < obj.Data.Length; offset += DataChunkSize) {
        var count = Math.Min(DataChunkSize, obj.Data.Length - offset);
        WriteSection(output, SectionType.Data, EncodeData(offset, obj.Data.AsSpan(offset, count)));
      }
      WriteSection(output, SectionType.Attrs, EncodeAttributes(obj.Attributes));
      WriteSection(output, SectionType.OmapHeader, EncodeOmapHeader(obj.OmapHeader));
      if (obj.Omap.Count > 0) {
        var batch = new List<KeyValuePair<string, byte[]>>(512);
        foreach (var item in obj.Omap) {
          batch.Add(item);
          if (batch.Count != 512) continue;
          WriteSection(output, SectionType.Omap, EncodeMapStructure(batch));
          batch.Clear();
        }
        if (batch.Count > 0)
          WriteSection(output, SectionType.Omap, EncodeMapStructure(batch));
      }
      WriteSection(output, SectionType.ObjectEnd, []);
    }
    WriteSection(output, SectionType.PoolEnd, []);
  }

  private static byte[] EncodeObjectBegin(ObjectModel obj) {
    var hobject = EncodeStruct(4, 3, body => {
      WriteString(body, obj.LocatorKey == obj.ObjectId ? "" : obj.LocatorKey);
      WriteString(body, obj.ObjectId);
      WriteUInt64(body, CephNoSnap);
      WriteUInt32(body, 0); // hash is not used by rados import for a pool dump
      body.WriteByte(0);    // max=false
      WriteString(body, obj.Namespace);
      WriteInt64(body, long.MinValue); // pool is not carried by rados export
    });

    // object_begin v2 deliberately omits object_info_t. Ceph's RadosImport decoder
    // accepts v2 and explicitly does not use object_info for pool imports.
    return EncodeStruct(2, 1, body => {
      body.Write(hobject);
      WriteUInt64(body, ulong.MaxValue); // ghobject_t::NO_GEN
      body.WriteByte(0xFF);              // shard_id_t::NO_SHARD (-1)
    });
  }

  private static byte[] EncodeData(long offset, ReadOnlySpan<byte> payload)
    => EncodeStruct(1, 1, body => {
      WriteUInt64(body, checked((ulong)offset));
      WriteUInt64(body, checked((ulong)payload.Length));
      WriteBuffer(body, payload);
    });

  private static byte[] EncodeAttributes(IReadOnlyDictionary<string, byte[]> attributes) {
    var pairs = attributes.OrderBy(static p => p.Key, StringComparer.Ordinal)
      .Select(static p => new KeyValuePair<string, byte[]>("_" + p.Key, p.Value));
    return EncodeMapStructure(pairs);
  }

  private static byte[] EncodeOmapHeader(byte[] header)
    => EncodeStruct(1, 1, body => WriteBuffer(body, header));

  private static byte[] EncodeMapStructure(IEnumerable<KeyValuePair<string, byte[]>> values)
    => EncodeStruct(1, 1, body => WriteMap(body, values));

  private static void WriteSection(Stream output, SectionType type, byte[] body) {
    using var header = new MemoryStream(SectionHeaderSize);
    var debugType = ((uint)type << 24) | ((uint)type << 16) | ShortMagic;
    var encodedHeader = EncodeStruct(1, 1, inner => {
      WriteUInt32(inner, debugType);
      WriteInt64(inner, body.LongLength);
    });
    if (encodedHeader.Length != SectionHeaderSize)
      throw new InvalidOperationException("Internal RADOS section-header size mismatch.");
    output.Write(encodedHeader);
    if (body.Length == 0)
      return;

    output.Write(body);
    var footer = EncodeStruct(1, 1, inner => WriteUInt32(inner, EndMagic));
    if (footer.Length != SectionFooterSize)
      throw new InvalidOperationException("Internal RADOS section-footer size mismatch.");
    output.Write(footer);
  }

  private static byte[] EncodeStruct(byte version, byte compat, Action<MemoryStream> writeBody) {
    using var body = new MemoryStream();
    writeBody(body);
    using var result = new MemoryStream(checked((int)body.Length + 6));
    result.WriteByte(version);
    result.WriteByte(compat);
    WriteUInt32(result, checked((uint)body.Length));
    body.Position = 0;
    body.CopyTo(result);
    return result.ToArray();
  }

  private static void WriteMap(Stream stream, IEnumerable<KeyValuePair<string, byte[]>> values) {
    var materialized = values as ICollection<KeyValuePair<string, byte[]>> ?? values.ToArray();
    WriteUInt32(stream, checked((uint)materialized.Count));
    foreach (var (key, value) in materialized) {
      WriteString(stream, key);
      WriteBuffer(stream, value);
    }
  }

  private static void WriteString(Stream stream, string value) {
    var bytes = Utf8.GetBytes(value);
    WriteUInt32(stream, checked((uint)bytes.Length));
    stream.Write(bytes);
  }

  private static void WriteBuffer(Stream stream, ReadOnlySpan<byte> value) {
    WriteUInt32(stream, checked((uint)value.Length));
    stream.Write(value);
  }

  private static void WriteUInt32(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteUInt64(Stream stream, ulong value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void WriteInt64(Stream stream, long value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
    stream.Write(bytes);
  }

  private static Section ReadSection(byte[] data, ref int pos, int headerSize, int footerSize, ParsedDump result) {
    var start = pos;
    EnsureAvailable(pos, headerSize, data.Length, "RADOS section header");
    var headerEnd = pos + headerSize;
    var headerStruct = ReadStructHeader(data, ref pos, headerEnd, supportedVersion: 1, "section header", result, allowKnownNewerVersion: false);
    var debugType = ReadUInt32(data, ref pos, headerStruct.End);
    var size = ReadInt64(data, ref pos, headerStruct.End);
    if ((debugType & 0xFFFF) != ShortMagic || ((debugType >> 16) & 0xFF) != (debugType >> 24))
      throw new InvalidDataException("RADOS section header has invalid debug magic/type bytes.");
    if (size < 0 || size > int.MaxValue)
      throw new NotSupportedException("RADOS section bodies larger than 2 GiB are not supported by this managed editor yet.");
    pos = headerEnd;

    var type = (SectionType)(byte)(debugType >> 24);
    var bodyOffset = pos;
    var bodyLength = checked((int)size);
    EnsureAvailable(pos, bodyLength, data.Length, "RADOS section body");
    pos += bodyLength;

    if (bodyLength > 0) {
      EnsureAvailable(pos, footerSize, data.Length, "RADOS section footer");
      var footerEnd = pos + footerSize;
      var footerStruct = ReadStructHeader(data, ref pos, footerEnd, supportedVersion: 1, "section footer", result, allowKnownNewerVersion: false);
      var footerMagic = ReadUInt32(data, ref pos, footerStruct.End);
      if (footerMagic != EndMagic)
        throw new InvalidDataException($"RADOS section has invalid footer magic 0x{footerMagic:X8}.");
      pos = footerEnd;
    }

    return new Section(type, start, bodyOffset, bodyLength, pos);
  }

  private readonly record struct StructHeader(byte Version, byte Compat, int End);

  private static StructHeader ReadStructHeader(byte[] data, ref int pos, int limit, byte supportedVersion, string name,
      ParsedDump result, bool allowKnownNewerVersion) {
    var version = ReadByte(data, ref pos, limit);
    var compat = ReadByte(data, ref pos, limit);
    var length = checked((int)ReadUInt32(data, ref pos, limit));
    if (compat > supportedVersion)
      throw new InvalidDataException($"RADOS {name} requires decoder version {compat}, but only {supportedVersion} is supported.");
    if (version > supportedVersion && !allowKnownNewerVersion)
      result.CanRewrite = false;
    EnsureAvailable(pos, length, limit, $"RADOS {name} body");
    return new StructHeader(version, compat, pos + length);
  }

  private readonly record struct HObject(string Key, string ObjectId, ulong Snap, string Namespace);

  private static HObject ReadHObject(byte[] data, ref int pos, int limit, ParsedDump result) {
    var structure = ReadStructHeader(data, ref pos, limit, supportedVersion: 4, "hobject_t", result, allowKnownNewerVersion: false);
    if (structure.Version < 4)
      throw new InvalidDataException("RADOS pool dump uses an hobject_t older than version 4; namespace/pool decoding is unavailable.");
    var key = ReadString(data, ref pos, structure.End);
    var objectId = ReadString(data, ref pos, structure.End);
    var snap = ReadUInt64(data, ref pos, structure.End);
    _ = ReadUInt32(data, ref pos, structure.End); // hash
    _ = ReadByte(data, ref pos, structure.End);   // max
    var nspace = ReadString(data, ref pos, structure.End);
    _ = ReadInt64(data, ref pos, structure.End);  // pool
    if (pos != structure.End)
      result.CanRewrite = false;
    pos = structure.End;
    return new HObject(key, objectId, snap, nspace);
  }

  private static Dictionary<string, byte[]> ReadMap(byte[] data, ref int pos, int limit) {
    var count = ReadUInt32(data, ref pos, limit);
    if (count > 1_000_000)
      throw new InvalidDataException($"RADOS map declares unreasonable item count {count}.");
    var result = new Dictionary<string, byte[]>(checked((int)count), StringComparer.Ordinal);
    for (var i = 0U; i < count; ++i)
      result[ReadString(data, ref pos, limit)] = ReadBuffer(data, ref pos, limit);
    return result;
  }

  private static string ReadString(byte[] data, ref int pos, int limit) {
    var length = checked((int)ReadUInt32(data, ref pos, limit));
    EnsureAvailable(pos, length, limit, "RADOS string");
    try {
      var value = Utf8.GetString(data, pos, length);
      pos += length;
      return value;
    } catch (DecoderFallbackException ex) {
      throw new InvalidDataException("RADOS object identifiers must be valid UTF-8 for this managed archive surface.", ex);
    }
  }

  private static byte[] ReadBuffer(byte[] data, ref int pos, int limit) {
    var length = checked((int)ReadUInt32(data, ref pos, limit));
    EnsureAvailable(pos, length, limit, "RADOS bufferlist");
    var result = data.AsSpan(pos, length).ToArray();
    pos += length;
    return result;
  }

  private static byte ReadByte(byte[] data, ref int pos, int limit) {
    EnsureAvailable(pos, 1, limit, "byte");
    return data[pos++];
  }

  private static uint ReadUInt32(byte[] data, ref int pos, int limit) {
    EnsureAvailable(pos, 4, limit, "UInt32");
    var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
    pos += 4;
    return value;
  }

  private static ulong ReadUInt64(byte[] data, ref int pos, int limit) {
    EnsureAvailable(pos, 8, limit, "UInt64");
    var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, 8));
    pos += 8;
    return value;
  }

  private static long ReadInt64(byte[] data, ref int pos, int limit) {
    EnsureAvailable(pos, 8, limit, "Int64");
    var value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos, 8));
    pos += 8;
    return value;
  }

  private static void EnsureAvailable(int pos, int length, int limit, string what) {
    if (length < 0 || pos < 0 || pos > limit || length > limit - pos)
      throw new InvalidDataException($"Truncated {what}.");
  }

  private static void AddMetadataLayout(ParsedDump result, Section section)
    => result.Layout.Add(new LayoutExtent(section.Start, section.End - section.Start, IsPayload: false));

  private static void AddDataLayout(ParsedDump result, Section section, int payloadOffset, int payloadLength, string entryName) {
    if (payloadOffset > section.Start)
      result.Layout.Add(new LayoutExtent(section.Start, payloadOffset - section.Start, IsPayload: false));
    if (payloadLength > 0)
      result.Layout.Add(new LayoutExtent(payloadOffset, payloadLength, IsPayload: true, entryName));
    var payloadEnd = payloadOffset + payloadLength;
    if (payloadEnd < section.End)
      result.Layout.Add(new LayoutExtent(payloadEnd, section.End - payloadEnd, IsPayload: false));
  }

  internal static string EncodeEntryName(string nspace, string objectId)
    => $"rados/{(nspace.Length == 0 ? "@default" : EscapeComponent(nspace))}/{EscapeComponent(objectId)}";

  internal static (string Namespace, string ObjectId) DecodeEntryName(string archiveName) {
    ArgumentException.ThrowIfNullOrEmpty(archiveName);
    var normalized = archiveName.Replace('\\', '/');
    if (!normalized.StartsWith("rados/", StringComparison.Ordinal))
      return ("", normalized);
    var remainder = normalized[6..];
    var slash = remainder.IndexOf('/');
    if (slash < 0 || remainder.IndexOf('/', slash + 1) >= 0)
      throw new ArgumentException("RADOS entry names use 'rados/<namespace>/<object-id>'; slashes inside components must be percent-encoded.", nameof(archiveName));
    var nsPart = remainder[..slash];
    var objectPart = remainder[(slash + 1)..];
    return (nsPart == "@default" ? "" : UnescapeComponent(nsPart), UnescapeComponent(objectPart));
  }

  private static string EscapeComponent(string value) {
    var bytes = Utf8.GetBytes(value);
    var sb = new StringBuilder(bytes.Length);
    foreach (var b in bytes) {
      if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
          || b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~') {
        sb.Append((char)b);
      } else {
        sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
      }
    }
    return sb.ToString();
  }

  private static string UnescapeComponent(string value) {
    using var bytes = new MemoryStream(value.Length);
    for (var i = 0; i < value.Length;) {
      if (value[i] == '%') {
        if (i + 2 >= value.Length
            || !byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
              System.Globalization.CultureInfo.InvariantCulture, out var b))
          throw new ArgumentException($"Invalid percent escape in RADOS entry component '{value}'.", nameof(value));
        bytes.WriteByte(b);
        i += 3;
        continue;
      }
      if (value[i] > 0x7F)
        throw new ArgumentException("Unescaped non-ASCII characters are not canonical in RADOS entry names.", nameof(value));
      bytes.WriteByte((byte)value[i++]);
    }
    try {
      return Utf8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
    } catch (DecoderFallbackException ex) {
      throw new ArgumentException("RADOS entry component contains invalid UTF-8.", nameof(value), ex);
    }
  }
}