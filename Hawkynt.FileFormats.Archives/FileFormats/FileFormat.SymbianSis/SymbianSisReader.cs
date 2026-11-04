using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.SymbianSis;

/// <summary>A file a Symbian SIS package installs, runs or shows.</summary>
/// <param name="FileName">The install target with its drive taken off ("sys/bin/app.exe"); "embeddedN/..." for a file of the Nth embedded package, "notarget/fileN" for one with no target.</param>
/// <param name="Target">The SISFileDescription target as stored ("!:\sys\bin\app.exe"), or empty.</param>
/// <param name="MimeType">The SISFileDescription MIME type, or empty.</param>
/// <param name="Operation">TSISFileOperation: 1 install, 2 run, 4 show as text.</param>
/// <param name="OperationOptions">The operation's option bits.</param>
/// <param name="CompressedSize">Its length as stored in the SIS file.</param>
/// <param name="Size">Its uncompressed length.</param>
/// <param name="DataUnit">Absolute index of the SISDataUnit that holds it.</param>
/// <param name="FileIndex">Index of its SISFileData inside that data unit.</param>
/// <param name="Hash">The SHA-1 the controller records for the uncompressed file, or null for an unknown hash algorithm.</param>
public sealed record SymbianSisEntry(
  string FileName, string Target, string MimeType, uint Operation, uint OperationOptions,
  long CompressedSize, long Size, int DataUnit, int FileIndex, byte[]? Hash);

/// <summary>
/// Reads Symbian OS 9.x SIS packages (".sis", ".sisx"): the SISController's install blocks,
/// conditional blocks, logo and embedded packages, and the files the SISData section holds for
/// them.
/// </summary>
/// <remarks>
/// Implemented from "Symbian OS v9.X SIS File Format Specification", version 1.1 (June 2006).
/// The controller and data CRC-16s and every file's SHA-1 are checked. Pre-9 EPOC SIS files
/// (UID2 0x10000419) are a different format and are not read.
/// </remarks>
public sealed class SymbianSisReader {
  /// <summary>UID 1 of every Symbian OS 9 SIS file.</summary>
  public const uint Uid1 = 0x10201A7A;

  private const uint EOpNull = 8;
  private const uint ESISHashAlgSHA1 = 1;
  private const uint ECompressNone = 0;
  private const uint ECompressDeflate = 1;

  internal enum FieldType : uint {
    String = 1, Array = 2, Compressed = 3, Version = 4, DateTime = 8, Uid = 9, Language = 11,
    Contents = 12, Controller = 13, Info = 14, SupportedLanguages = 15, Logo = 23,
    FileDescription = 24, Hash = 25, If = 26, ElseIf = 27, InstallBlock = 28, Data = 30, DataUnit = 31,
    FileData = 32, ControllerChecksum = 34, DataChecksum = 35, Blob = 37,
    SignatureCertificateChain = 39, DataIndex = 40, Capabilities = 41,
  }

  /// <summary>A SISField: its type, the whole encoded field (header, value and padding, which makesis covers by its CRC-16s) and its value.</summary>
  private readonly record struct Field(FieldType Type, ReadOnlyMemory<byte> Whole, ReadOnlyMemory<byte> Value);

  private readonly List<SymbianSisEntry> _entries = [];
  private readonly List<List<ReadOnlyMemory<byte>>> _dataUnits = [];
  private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
  private int _embedded;

  /// <summary>Gets UID 3, the package UID.</summary>
  public uint PackageUid { get; }

  /// <summary>Gets the top-level package's names, one per supported language.</summary>
  public IReadOnlyList<string> Names { get; private set; } = [];

  /// <summary>Gets the top-level package's non-localised vendor name.</summary>
  public string VendorUniqueName { get; private set; } = "";

  /// <summary>Gets the top-level package's vendor names, one per supported language.</summary>
  public IReadOnlyList<string> VendorNames { get; private set; } = [];

  /// <summary>Gets the creation time SISInfo records (UTC), or null when it is not a valid date.</summary>
  public DateTime? CreationTime { get; private set; }

  /// <summary>Gets the top-level package's version.</summary>
  public (int Major, int Minor, int Build) Version { get; private set; }

  /// <summary>Gets the top-level package's TInstallType (0 SA, 1 SP, 2 PU, 3 PA, 4 PP).</summary>
  public byte InstallType { get; private set; }

  /// <summary>Gets the number of signature certificate chains on the top-level controller (0 = unsigned).</summary>
  public int SignatureChains { get; private set; }

  /// <summary>Gets the number of embedded packages, at any depth.</summary>
  public int EmbeddedPackages => this._embedded;

  /// <summary>Gets the files, in controller order.</summary>
  public IReadOnlyList<SymbianSisEntry> Entries => this._entries;

  /// <summary>True when the stream starts with the Symbian OS 9 SIS UID 1.</summary>
  public static bool IsSis(Stream stream) {
    Span<byte> head = stackalloc byte[16];
    var pos = stream.Position;
    try {
      return stream.ReadAtLeast(head, 16, throwOnEndOfStream: false) == 16 && IsSis(head);
    } finally {
      stream.Position = pos;
    }
  }

  /// <summary>True when the header carries UID 1 0x10201A7A and a SISContents field follows it.</summary>
  public static bool IsSis(ReadOnlySpan<byte> head)
    => head.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Uid1
       && (head.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(head[16..]) == (uint)FieldType.Contents);

  /// <summary>Opens a SIS package.</summary>
  /// <exception cref="InvalidDataException">Not a Symbian OS 9 SIS file, a checksum that does not match, or a structure that does not parse.</exception>
  public SymbianSisReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();
    if (file.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Uid1)
      throw new InvalidDataException("Not a Symbian OS 9 SIS file (UID 1 is not 0x10201A7A).");
    this.PackageUid = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));

    var contents = Fields(file.AsMemory(16)).First();
    Expect(contents, FieldType.Contents);

    ushort? controllerCrc = null, dataCrc = null;
    Field? controller = null, data = null;
    foreach (var f in Fields(contents.Value))
      switch (f.Type) {
        case FieldType.ControllerChecksum: controllerCrc = U16(f.Value); break;
        case FieldType.DataChecksum: dataCrc = U16(f.Value); break;
        case FieldType.Compressed: controller = f; break;
        case FieldType.Data: data = f; break;
      }
    if (controller is not { } c || data is not { } d)
      throw new InvalidDataException("SISContents lacks its controller or its data.");
    // 4.2.2/4.2.3: CRC-16 over the whole SISCompressed<SISController> and SISData fields.
    if (controllerCrc is { } cc && Crc16Ccitt.Compute(c.Whole.Span) != cc)
      throw new InvalidDataException("SISControllerChecksum does not match the controller.");
    if (dataCrc is { } dc && Crc16Ccitt.Compute(d.Whole.Span) != dc)
      throw new InvalidDataException("SISDataChecksum does not match the data.");

    this.ReadData(d);
    var top = Fields(Uncompress(c.Value)).First();
    Expect(top, FieldType.Controller);
    this.ReadController(top, 0, "", isTop: true);
  }

  /// <summary>Returns a file's contents, checked against the size and SHA-1 the controller records.</summary>
  /// <exception cref="InvalidDataException">The data does not match its description.</exception>
  public byte[] Extract(SymbianSisEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var data = Uncompress(this._dataUnits[entry.DataUnit][entry.FileIndex]);
    if (data.Length != entry.Size)
      throw new InvalidDataException($"{entry.FileName}: {data.Length} bytes, the controller says {entry.Size}.");
    if (entry.Hash is { } hash && !SHA1.HashData(data).AsSpan().SequenceEqual(hash))
      throw new InvalidDataException($"{entry.FileName}: SHA-1 does not match the controller.");
    return data;
  }

  private void ReadData(Field data) {
    foreach (var unit in Elements(Single(data, FieldType.Array), FieldType.DataUnit)) {
      var files = new List<ReadOnlyMemory<byte>>();
      foreach (var fileData in Elements(Single(unit, FieldType.Array), FieldType.FileData))
        files.Add(Single(fileData, FieldType.Compressed).Value);
      this._dataUnits.Add(files);
    }
  }

  // 4.2.4: the controller's files live in data unit (sum of the data indices of its embedding chain).
  private void ReadController(Field controller, int parentIndex, string prefix, bool isTop, int depth = 0) {
    // 2.5: the installer refuses nesting deeper than 8.
    if (depth > 8)
      throw new InvalidDataException("SIS packages are embedded more than 8 deep.");
    var files = new List<ReadOnlyMemory<byte>>();
    var embedded = new List<Field>();
    var dataIndex = 0;
    foreach (var f in Fields(controller.Value))
      switch (f.Type) {
        case FieldType.Info when isTop: this.ReadInfo(f); break;
        case FieldType.Logo: files.Add(Single(f, FieldType.FileDescription).Value); break;
        case FieldType.InstallBlock: ReadInstallBlock(f, files, embedded); break;
        case FieldType.SignatureCertificateChain when isTop: ++this.SignatureChains; break;
        case FieldType.DataIndex: dataIndex = checked((int)U32(f.Value)); break;
      }
    // The data index is the controller's last field; embedded controllers count from it.
    var unit = parentIndex + dataIndex;
    foreach (var description in files)
      this.AddFile(description, unit, prefix);
    foreach (var child in embedded)
      this.ReadController(child, unit, prefix + $"embedded{++this._embedded}/", isTop: false, depth + 1);
  }

  // 4.4.3: files, embedded controllers, and SISIf blocks whose branches are install blocks again.
  private static void ReadInstallBlock(Field block, List<ReadOnlyMemory<byte>> files, List<Field> embedded) {
    foreach (var array in Fields(block.Value)) {
      Expect(array, FieldType.Array);
      if (array.Value.Length < 4)
        continue;
      switch ((FieldType)U32(array.Value)) {
        case FieldType.FileDescription:
          files.AddRange(Elements(array, FieldType.FileDescription).Select(e => e.Value));
          break;
        case FieldType.Controller:
          embedded.AddRange(Elements(array, FieldType.Controller));
          break;
        case FieldType.If:
          foreach (var ifBlock in Elements(array, FieldType.If))
            foreach (var part in Fields(ifBlock.Value))
              if (part.Type == FieldType.InstallBlock)
                ReadInstallBlock(part, files, embedded);
              else if (part.Type == FieldType.Array && part.Value.Length >= 4)
                foreach (var elseIf in Elements(part, FieldType.ElseIf))
                  foreach (var elsePart in Fields(elseIf.Value))
                    if (elsePart.Type == FieldType.InstallBlock)
                      ReadInstallBlock(elsePart, files, embedded);
          break;
      }
    }
  }

  // 4.2.5: UID, vendor unique name, names, vendor names, version, creation time, then install type and flags.
  private void ReadInfo(Field info) {
    var cursor = 0;
    var fields = new List<Field>();
    while (fields.Count < 6)
      fields.Add(ReadField(info.Value, ref cursor, null));
    this.VendorUniqueName = Text(fields[1]);
    this.Names = Elements(fields[2], FieldType.String).Select(Text).ToList();
    this.VendorNames = Elements(fields[3], FieldType.String).Select(Text).ToList();
    Expect(fields[4], FieldType.Version);
    var v = fields[4].Value.Span;
    if (v.Length >= 12)
      this.Version = (BinaryPrimitives.ReadInt32LittleEndian(v), BinaryPrimitives.ReadInt32LittleEndian(v[4..]), BinaryPrimitives.ReadInt32LittleEndian(v[8..]));
    Expect(fields[5], FieldType.DateTime);
    var dateTime = Fields(fields[5].Value).ToList();
    if (dateTime is [{ Value.Length: >= 4 } date, { Value.Length: >= 3 } time]) {
      var d = date.Value.Span;
      var t = time.Value.Span;
      // 4.1.6.2: months count from 0 (makesis stores struct tm's tm_mon).
      try {
        this.CreationTime = new DateTime(BinaryPrimitives.ReadUInt16LittleEndian(d), d[2] + 1, d[3], t[0], t[1], t[2], DateTimeKind.Utc);
      } catch (ArgumentOutOfRangeException) {
        this.CreationTime = null; // not a valid date; nothing else depends on it
      }
    }
    this.InstallType = cursor < info.Value.Length ? info.Value.Span[cursor] : (byte)0;
  }

  private void AddFile(ReadOnlyMemory<byte> description, int unit, string prefix) {
    var cursor = 0;
    var span = description.Span;
    string target = "", mime = "";
    var strings = 0;
    byte[]? hash = null;
    while (true) {
      var f = ReadField(description, ref cursor, null);
      if (f.Type == FieldType.String) {
        if (strings++ == 0) target = Text(f); else mime = Text(f);
      } else if (f.Type == FieldType.Hash) {
        var algorithm = U32(f.Value);
        var blob = Fields(f.Value[4..]).First();
        Expect(blob, FieldType.Blob);
        if (algorithm == ESISHashAlgSHA1)
          hash = blob.Value.ToArray();
        break;
      }
    }
    if (span.Length - cursor < 28)
      throw new InvalidDataException("SISFileDescription is truncated.");
    var fixedPart = span[cursor..];
    var operation = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart);
    var options = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[4..]);
    var length = BinaryPrimitives.ReadInt64LittleEndian(fixedPart[8..]);
    var uncompressed = BinaryPrimitives.ReadInt64LittleEndian(fixedPart[16..]);
    var index = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fixedPart[24..]));
    if (operation == EOpNull)
      return; // FN: nothing stored, the file is only removed on uninstall
    if (unit >= this._dataUnits.Count || index >= this._dataUnits[unit].Count)
      throw new InvalidDataException($"File '{target}' points at data unit {unit} file {index}, which the SISData lacks.");
    var name = target.Length == 0 ? $"notarget/file{index}" : TargetToName(target);
    this._entries.Add(new(this.Unique(prefix + name), target, mime, operation, options, length, uncompressed, unit, index, hash));
  }

  // Language variants of one target sit in different SISIf branches; keep each under its own name.
  private string Unique(string name) {
    if (this._names.Add(name))
      return name;
    var slash = name.LastIndexOf('/');
    var dot = name.LastIndexOf('.');
    if (dot <= slash)
      dot = name.Length;
    for (var n = 2; ; ++n) {
      var candidate = $"{name[..dot]}~{n}{name[dot..]}";
      if (this._names.Add(candidate))
        return candidate;
    }
  }

  /// <summary>"!:\sys\bin\app.exe" or "c:\data\x" to "sys/bin/app.exe" / "data/x".</summary>
  internal static string TargetToName(string target) {
    var t = target.Replace('\\', '/');
    if (t.Length >= 2 && t[1] == ':')
      t = t[2..];
    var parts = t.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p is not ("." or "..")).ToArray();
    return parts.Length == 0 ? "notarget/file" : string.Join('/', parts);
  }

  private static byte[] Uncompress(ReadOnlyMemory<byte> compressed) {
    var span = compressed.Span;
    if (span.Length < 12)
      throw new InvalidDataException("SISCompressed is truncated.");
    var algorithm = BinaryPrimitives.ReadUInt32LittleEndian(span);
    var size = BinaryPrimitives.ReadUInt64LittleEndian(span[4..]);
    var payload = span[12..];
    if (size > int.MaxValue)
      throw new InvalidDataException("SISCompressed data is larger than 2 GB.");
    var data = algorithm switch {
      ECompressNone => payload.ToArray(),
      // The specification says RFC 1951; makesis writes it with the zlib (RFC 1950) wrapper.
      ECompressDeflate => FileFormat.Zlib.ZlibStream.Decompress(payload),
      _ => throw new InvalidDataException($"Unknown SISCompressed algorithm {algorithm}."),
    };
    if ((ulong)data.Length != size)
      throw new InvalidDataException($"SISCompressed: {data.Length} bytes, the header says {size}.");
    return data;
  }

  private static IEnumerable<Field> Fields(ReadOnlyMemory<byte> region) {
    var cursor = 0;
    while (region.Length - cursor >= 8)
      yield return ReadField(region, ref cursor, null);
  }

  // 4.1.2: array elements carry no type of their own.
  private static IEnumerable<Field> Elements(Field array, FieldType expected) {
    Expect(array, FieldType.Array);
    if (array.Value.Length < 4)
      yield break;
    var type = (FieldType)U32(array.Value);
    if (type != expected)
      throw new InvalidDataException($"SISArray of {type} where {expected} was expected.");
    var body = array.Value[4..];
    var cursor = 0;
    while (body.Length - cursor >= 4)
      yield return ReadField(body, ref cursor, type);
  }

  // 2.12: type (absent inside arrays), a 31- or 63-bit length, the value, zero padding to 4 bytes.
  private static Field ReadField(ReadOnlyMemory<byte> region, ref int cursor, FieldType? arrayType) {
    var span = region.Span;
    var start = cursor;
    FieldType type;
    if (arrayType is { } t)
      type = t;
    else {
      Need(span, cursor, 4);
      type = (FieldType)BinaryPrimitives.ReadUInt32LittleEndian(span[cursor..]);
      cursor += 4;
    }
    Need(span, cursor, 4);
    ulong length = BinaryPrimitives.ReadUInt32LittleEndian(span[cursor..]);
    cursor += 4;
    if ((length & 0x8000_0000) != 0) {
      Need(span, cursor, 4);
      length = (length & 0x7FFF_FFFF) | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(span[cursor..]) << 31);
      cursor += 4;
    }
    if (length > (ulong)(span.Length - cursor))
      throw new InvalidDataException($"SISField {type} runs past its container.");
    var valueStart = cursor;
    cursor += (int)length;
    var value = region[valueStart..cursor];
    cursor = Math.Min(span.Length, cursor + (int)((4 - length % 4) % 4));
    return new(type, region[start..cursor], value);
  }

  private static Field Single(Field parent, FieldType expected) {
    var f = Fields(parent.Value).FirstOrDefault();
    if (f.Value.IsEmpty && f.Type == 0)
      throw new InvalidDataException($"{parent.Type} is empty.");
    Expect(f, expected);
    return f;
  }

  private static void Expect(Field f, FieldType type) {
    if (f.Type != type)
      throw new InvalidDataException($"SISField {f.Type} where {type} was expected.");
  }

  private static void Need(ReadOnlySpan<byte> span, int cursor, int count) {
    if (span.Length - cursor < count)
      throw new InvalidDataException("SISField header is truncated.");
  }

  private static string Text(Field f) {
    Expect(f, FieldType.String);
    return Encoding.Unicode.GetString(f.Value.Span);
  }

  private static uint U32(ReadOnlyMemory<byte> value)
    => value.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(value.Span) : throw new InvalidDataException("SISField value is truncated.");

  private static ushort U16(ReadOnlyMemory<byte> value)
    => value.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(value.Span) : throw new InvalidDataException("SISField value is truncated.");
}
