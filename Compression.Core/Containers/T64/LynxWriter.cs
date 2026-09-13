#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Lynx;

internal sealed record LynxDirectorySpec(
  string Name,
  byte[] RawName,
  char FileType,
  int RecordSize,
  int ArchiveBlocks,
  int DataBlocks,
  int LastBlockCount
);

internal static class LynxWriter {
  public const string DefaultSignature = "*LYNX BY COMPRESSION WB*";

  public static readonly byte[] CanonicalBasicHeader = [
    0x01, 0x08, 0x5B, 0x08, 0x0A, 0x00, 0x97, 0x35,
    0x33, 0x32, 0x38, 0x30, 0x2C, 0x30, 0x3A, 0x97,
    0x35, 0x33, 0x32, 0x38, 0x31, 0x2C, 0x30, 0x3A,
    0x97, 0x36, 0x34, 0x36, 0x2C, 0xC2, 0x28, 0x31,
    0x36, 0x32, 0x29, 0x3A, 0x99, 0x22, 0x93, 0x11,
    0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x22,
    0x3A, 0x99, 0x22, 0x20, 0x20, 0x20, 0x20, 0x20,
    0x55, 0x53, 0x45, 0x20, 0x4C, 0x59, 0x4E, 0x58,
    0x20, 0x54, 0x4F, 0x20, 0x44, 0x49, 0x53, 0x53,
    0x4F, 0x4C, 0x56, 0x45, 0x20, 0x54, 0x48, 0x49,
    0x53, 0x20, 0x46, 0x49, 0x4C, 0x45, 0x22, 0x3A,
    0x89, 0x31, 0x30, 0x00, 0x00, 0x00, 0x0D,
  ];

  public static void WriteArchive(
      Stream output,
      IReadOnlyList<(string Name, byte[] Data)> files,
      char fileType = 'P',
      string signature = DefaultSignature) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(files);
    EnsureWritable(output);
    fileType = ValidateCreatableFileType(fileType);
    signature = ValidateSignature(signature);

    var normalized = new List<(LynxDirectorySpec Spec, byte[] Data)>(files.Count);
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, data) in files) {
      ArgumentNullException.ThrowIfNull(data);
      var spec = CreateSpec(name, data.Length, fileType);
      if (!names.Add(spec.Name))
        throw new ArgumentException($"Duplicate Lynx file name after 16-byte normalization: '{spec.Name}'.", nameof(files));
      normalized.Add((spec, data));
    }

    var directory = BuildDirectory(normalized.Select(item => item.Spec).ToArray(), 1, CanonicalBasicHeader, signature);
    output.Position = 0;
    output.SetLength(0);
    output.Write(directory);

    foreach (var item in normalized)
      WritePaddedPayload(output, item.Data, item.Spec.ArchiveBlocks);

    output.SetLength(output.Position);
    output.Flush();
    output.Position = 0;
  }

  public static LynxDirectorySpec CreateSpec(string name, int length, char fileType = 'P') {
    ArgumentOutOfRangeException.ThrowIfNegative(length);
    fileType = ValidateCreatableFileType(fileType);
    var (normalizedName, rawName) = NormalizeName(name);
    var blocks = BlocksForLength(length);
    var last = LastBlockCountForLength(length);
    return new LynxDirectorySpec(normalizedName, rawName, fileType, 0, blocks, blocks, last);
  }

  public static LynxDirectorySpec FromEntry(LynxEntry entry)
    => new(
      entry.Name,
      entry.RawName.ToArray(),
      entry.FileType,
      entry.RecordSize,
      entry.ArchiveBlocks,
      entry.DataBlocks,
      entry.LastBlockCount);

  public static byte[] BuildDirectory(
      IReadOnlyList<LynxDirectorySpec> entries,
      int minimumBlocks,
      byte[]? basicHeader,
      string? signature) {
    ArgumentNullException.ThrowIfNull(entries);
    if (minimumBlocks < 1) minimumBlocks = 1;
    var header = basicHeader is { Length: > 0 } ? basicHeader : CanonicalBasicHeader;
    var sig = ValidateSignature(signature ?? DefaultSignature);

    var blocks = minimumBlocks;
    while (true) {
      var payload = BuildDirectoryPayload(entries, blocks, header, sig);
      var required = Math.Max(minimumBlocks, BlocksForLength(payload.Length));
      if (required == blocks) {
        var result = new byte[checked(blocks * LynxReader.BlockSize)];
        payload.CopyTo(result, 0);
        return result;
      }
      blocks = required;
    }
  }

  public static int BlocksForLength(int length)
    => length <= 0 ? 0 : checked((length + LynxReader.BlockSize - 1) / LynxReader.BlockSize);

  public static int LastBlockCountForLength(int length) {
    if (length == 0) return 0;
    var remainder = length % LynxReader.BlockSize;
    // Classic Lynx copies the 1541 terminal-sector count byte. A full 254-byte
    // payload block therefore uses 255, otherwise the stored count is bytes+1.
    return remainder == 0 ? 255 : remainder + 1;
  }

  public static (string Name, byte[] RawName) NormalizeName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var flattened = Path.GetFileName(name.Replace('\\', '/'));
    if (string.IsNullOrWhiteSpace(flattened))
      throw new ArgumentException("Lynx entries require a non-empty flat file name.", nameof(name));
    if (flattened.IndexOfAny(['\r', '\n', '\0']) >= 0)
      throw new ArgumentException("Lynx file names cannot contain CR, LF or NUL.", nameof(name));

    var normalized = flattened.Length > 16 ? flattened[..16] : flattened;
    if (normalized.Any(character => character > 0x7F))
      throw new ArgumentException("Lynx creation currently requires ASCII-compatible Commodore file names.", nameof(name));

    var bytes = Encoding.ASCII.GetBytes(normalized);
    var raw = Enumerable.Repeat((byte)0xA0, 16).ToArray();
    bytes.CopyTo(raw, 0);
    return (normalized, raw);
  }

  public static string ValidateSignature(string signature) {
    ArgumentNullException.ThrowIfNull(signature);
    if (signature.Length != 24 || !signature.Contains("LYNX", StringComparison.OrdinalIgnoreCase)
        || signature.Any(character => character is < ' ' or > '~'))
      throw new ArgumentException("Lynx signature must be exactly 24 printable ASCII characters and contain 'LYNX'.", nameof(signature));
    return signature;
  }

  public static void WritePaddedPayload(Stream output, byte[] data, int blocks) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(data);
    var allocated = checked(blocks * LynxReader.BlockSize);
    if (data.Length > allocated)
      throw new InvalidOperationException("Lynx payload exceeds its declared block allocation.");
    output.Write(data);
    WriteZeros(output, allocated - data.Length);
  }

  public static void WriteZeros(Stream output, long count) {
    if (count <= 0) return;
    Span<byte> zeros = stackalloc byte[256];
    while (count > 0) {
      var chunk = (int)Math.Min(count, zeros.Length);
      output.Write(zeros[..chunk]);
      count -= chunk;
    }
  }

  private static byte[] BuildDirectoryPayload(
      IReadOnlyList<LynxDirectorySpec> entries,
      int directoryBlocks,
      byte[] basicHeader,
      string signature) {
    using var memory = new MemoryStream();
    memory.Write(basicHeader);
    WriteAscii(memory, $" {directoryBlocks}  ");
    WriteAscii(memory, signature);
    memory.WriteByte(13);
    WriteAscii(memory, $" {entries.Count} ");
    memory.WriteByte(13);

    foreach (var entry in entries) {
      if (entry.RawName.Length != 16)
        throw new InvalidDataException("Lynx directory names must occupy exactly 16 bytes before CR.");
      memory.Write(entry.RawName);
      memory.WriteByte(13);
      WriteAscii(memory, $" {entry.ArchiveBlocks} ");
      memory.WriteByte(13);
      memory.WriteByte((byte)entry.FileType);
      memory.WriteByte(13);
      if (entry.FileType == 'R') {
        WriteAscii(memory, $" {entry.RecordSize} ");
        memory.WriteByte(13);
      }
      WriteAscii(memory, $" {entry.LastBlockCount} ");
      memory.WriteByte(13);
    }

    return memory.ToArray();
  }

  private static char ValidateCreatableFileType(char fileType) {
    var normalized = char.ToUpperInvariant(fileType);
    if (normalized is not ('D' or 'S' or 'P' or 'U'))
      throw new NotSupportedException("Fresh Lynx creation supports DEL/SEQ/PRG/USR entries; REL requires side-sector metadata and is read-only.");
    return normalized;
  }

  private static void WriteAscii(Stream stream, string text)
    => stream.Write(Encoding.ASCII.GetBytes(text));

  private static void EnsureWritable(Stream stream) {
    if (!stream.CanSeek || !stream.CanRead || !stream.CanWrite)
      throw new ArgumentException("Lynx writing requires a readable, writable, seekable stream.", nameof(stream));
  }
}
