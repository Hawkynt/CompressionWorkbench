#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Nrbf;

/// <summary>Writes a valid NRBF object array carrying the archive inputs as inert data.</summary>
internal static class NrbfWriter {
  private const string Marker = "Hawkynt.CompressionWorkbench.NrbfArchive/1";
  private static readonly UTF8Encoding Utf8 = new(false, true);

  public static void Write(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite) throw new ArgumentException("NRBF output must be writable.", nameof(output));
    if (!string.IsNullOrEmpty(options.MethodName) && !options.MethodName.Equals("ms-nrbf", StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException($"NRBF does not support the method '{options.MethodName}'.", nameof(options));
    if (options.Password is not null || options.EncryptFilenames || options.EncryptionMethod is not null
        || options.Optimize || options.OptimizeLevel != 0 || options.Level is not null || options.DictSize != 0
        || options.WordSize is not null || options.Threads != 1 || options.SolidSize != 0 || options.ForceCompress
        || options.IncompressiblePaths is not null || options.FormatSpecific.Count != 0)
      throw new ArgumentException("NRBF supports only the ms-nrbf method; compression, encryption and format-specific options are not defined.", nameof(options));
    if (inputs.Count > (1_000_000 - 1) / 3)
      throw new InvalidDataException("NRBF archive exceeds the supported entry limit.");

    var entries = new List<(string Name, bool IsDirectory, byte[] Data)>(inputs.Count);
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var input in inputs.OrderBy(x => x.ArchiveName, StringComparer.Ordinal)) {
      var name = NormalizeName(input.ArchiveName);
      if (!names.Add(name)) throw new InvalidDataException($"Duplicate NRBF archive entry: {name}");
      entries.Add((name, input.IsDirectory, input.IsDirectory ? [] : input.ReadContent()));
    }
    var entryKinds = entries.ToDictionary(x => x.Name, x => x.IsDirectory, StringComparer.Ordinal);
    foreach (var (name, _, _) in entries) {
      var parts = name.Split('/');
      for (var i = 1; i < parts.Length; ++i) {
        var parent = string.Join("/", parts[..i]);
        if (entryKinds.TryGetValue(parent, out var isDirectory) && !isDirectory)
          throw new InvalidDataException($"NRBF archive path uses '{parent}' as both a file and a directory.");
      }
    }

    using var writer = new BinaryWriter(output, Utf8, leaveOpen: true);
    writer.Write((byte)0); // SerializedStreamHeader
    writer.Write(1); // root object id
    writer.Write(-1); // header id
    writer.Write(1); // major version
    writer.Write(0); // minor version
    writer.Write((byte)7); // BinaryArray
    writer.Write(1); // object id
    writer.Write((byte)0); // Single array
    writer.Write(1); // rank
    writer.Write(checked(1 + entries.Count * 3));
    writer.Write((byte)1); // String element type
    WriteStringRecord(writer, 2, Marker);
    var objectId = 3;
    foreach (var (name, isDirectory, data) in entries) {
      WriteStringRecord(writer, objectId++, name);
      WriteStringRecord(writer, objectId++, isDirectory ? "D" : "F");
      WriteStringRecord(writer, objectId++, isDirectory ? string.Empty : Convert.ToBase64String(data));
    }
    writer.Write((byte)11); // MessageEnd
  }

  private static string NormalizeName(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    var normalized = name.Replace('\\', '/').Trim('/');
    if (normalized.Length == 0 || normalized.Split('/').Any(x => x is "" or "." or ".."))
      throw new InvalidDataException($"Invalid NRBF archive path: {name}");
    return normalized;
  }

  private static void WriteStringRecord(BinaryWriter writer, int objectId, string value) {
    var bytes = Utf8.GetBytes(value);
    writer.Write((byte)6); // BinaryObjectString
    writer.Write(objectId);
    Write7BitEncodedInt(writer, bytes.Length);
    writer.Write(bytes);
  }

  private static void Write7BitEncodedInt(BinaryWriter writer, int value) {
    var remaining = (uint)value;
    while (remaining >= 0x80) {
      writer.Write((byte)(remaining | 0x80));
      remaining >>= 7;
    }
    writer.Write((byte)remaining);
  }
}
