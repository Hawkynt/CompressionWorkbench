#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Nrbf;

/// <summary>
/// Writes archive inputs as the MS-NRBF serialization of one <c>object[]</c>: a marker string,
/// then per entry its path and either a <c>byte[]</c> (a file) or <c>null</c> (a directory).
/// </summary>
/// <remarks>
/// <para>The records are the ones .NET Framework's <c>BinaryFormatter</c> emits for that very
/// array, in its order and with its object ids, so the output is byte-identical to
/// <c>BinaryFormatter.Serialize(stream, array)</c> and deserializes there into plain strings and
/// byte arrays — no type of ours, nothing that could be activated:</para>
/// <list type="bullet">
///   <item><description>SerializationHeaderRecord, root id 1;</description></item>
///   <item><description>ArraySingleObject (id 1) whose strings are inline BinaryObjectString
///   records, whose nulls are ObjectNull, and whose <c>byte[]</c> slots are MemberReferences;</description></item>
///   <item><description>each referenced <c>byte[]</c> afterwards as ArraySinglePrimitive of Byte,
///   in the order it was referenced;</description></item>
///   <item><description>MessageEnd.</description></item>
/// </list>
/// <para>Object ids are handed out as the array is walked, which is how BinaryFormatter's object
/// id generator numbers them: a string when it is written, a <c>byte[]</c> when it is first
/// referenced.</para>
/// </remarks>
internal static class NrbfWriter {
  internal const string Marker = "Hawkynt.CompressionWorkbench.NrbfArchive/1";

  private const byte SerializedStreamHeader = 0;
  private const byte BinaryObjectString = 6;
  private const byte MemberReference = 9;
  private const byte ObjectNull = 10;
  private const byte MessageEnd = 11;
  private const byte ArraySinglePrimitive = 15;
  private const byte ArraySingleObject = 16;
  private const byte PrimitiveByte = 2;

  private static readonly UTF8Encoding Utf8 = new(false, true);

  public static void Write(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite) throw new ArgumentException("NRBF output must be writable.", nameof(output));
    if (!string.IsNullOrEmpty(options.MethodName) && !options.MethodName.Equals("ms-nrbf", StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException($"NRBF does not support the method '{options.MethodName}'.", nameof(options));
    // IncompressiblePaths is not checked: the create path marks inputs that will not compress, and
    // NRBF stores every byte as it is, so the hint has nothing to change.
    if (options.Password is not null || options.EncryptFilenames || options.EncryptionMethod is not null
        || options.Optimize || options.OptimizeLevel != 0 || options.Level is not null || options.DictSize != 0
        || options.WordSize is not null || options.Threads != 1 || options.SolidSize != 0 || options.ForceCompress
        || options.FormatSpecific.Count != 0)
      throw new ArgumentException("NRBF supports only the ms-nrbf method; compression, encryption and format-specific options are not defined.", nameof(options));

    var entries = new List<(string Name, byte[]? Data)>(inputs.Count);
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var input in inputs.OrderBy(x => x.ArchiveName, StringComparer.Ordinal)) {
      var name = NormalizeName(input.ArchiveName);
      if (!names.Add(name)) throw new InvalidDataException($"Duplicate NRBF archive entry: {name}");
      entries.Add((name, input.IsDirectory ? null : input.ReadContent()));
    }
    var files = entries.Where(e => e.Data is not null).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
    foreach (var (name, _) in entries) {
      var parts = name.Split('/');
      for (var i = 1; i < parts.Length; ++i) {
        var parent = string.Join("/", parts[..i]);
        if (files.Contains(parent))
          throw new InvalidDataException($"NRBF archive path uses '{parent}' as both a file and a directory.");
      }
    }

    var length = checked(1 + entries.Count * 2);
    WriteByte(output, SerializedStreamHeader);
    WriteInt32(output, 1);  // root id
    WriteInt32(output, -1); // header id
    WriteInt32(output, 1);  // major version
    WriteInt32(output, 0);  // minor version

    WriteByte(output, ArraySingleObject);
    WriteInt32(output, 1);
    WriteInt32(output, length);

    var nextId = 2;
    WriteString(output, nextId++, Marker);
    var deferred = new List<(int Id, byte[] Data)>();
    foreach (var (name, data) in entries) {
      WriteString(output, nextId++, name);
      if (data is null) {
        WriteByte(output, ObjectNull);
        continue;
      }
      var id = nextId++;
      WriteByte(output, MemberReference);
      WriteInt32(output, id);
      deferred.Add((id, data));
    }

    foreach (var (id, data) in deferred) {
      WriteByte(output, ArraySinglePrimitive);
      WriteInt32(output, id);
      WriteInt32(output, data.Length);
      WriteByte(output, PrimitiveByte);
      output.Write(data);
    }
    WriteByte(output, MessageEnd);
  }

  private static string NormalizeName(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    var normalized = name.Replace('\\', '/').Trim('/');
    if (normalized.Length == 0 || normalized.Split('/').Any(x => x is "" or "." or ".."))
      throw new InvalidDataException($"Invalid NRBF archive path: {name}");
    return normalized;
  }

  private static void WriteString(Stream output, int objectId, string value) {
    var bytes = Utf8.GetBytes(value);
    WriteByte(output, BinaryObjectString);
    WriteInt32(output, objectId);
    // LengthPrefixedString: the UTF-8 byte count as a 7-bit variable-length integer.
    var remaining = (uint)bytes.Length;
    while (remaining >= 0x80) {
      WriteByte(output, (byte)(remaining | 0x80));
      remaining >>= 7;
    }
    WriteByte(output, (byte)remaining);
    output.Write(bytes);
  }

  private static void WriteByte(Stream output, byte value) => output.WriteByte(value);

  private static void WriteInt32(Stream output, int value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
    output.Write(buffer);
  }
}
