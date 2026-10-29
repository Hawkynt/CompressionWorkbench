using Compression.Core.Checksums;
using static FileFormat.FreeArc.FreeArcEncoding;

namespace FileFormat.FreeArc;

/// <summary>
/// Writes real FreeArc archives that FreeArc 0.67's <c>arc</c> and <c>unarc</c> list, test and extract.
/// </summary>
/// <remarks>
/// The layout is FreeArc's own (<c>docs/FREEARC-ON-DISK.md</c>): a header block, the solid data
/// blocks, one directory block and the footer block, each control block followed by its CRC-checked
/// descriptor. Directories go into an empty <c>storing</c> block of their own, as FreeArc does; all
/// files share one solid block compressed with the chosen method — <c>storing</c> or <c>lzma</c>.
/// The directory and footer blocks are stored uncompressed.
/// </remarks>
public sealed class FreeArcWriter {
  private readonly List<(string Name, byte[] Data, DateTime Modified)> _files = [];
  private readonly List<(string Name, DateTime Modified)> _directories = [];

  /// <summary>Creates a writer that compresses the file block with <paramref name="method"/>.</summary>
  /// <param name="method"><c>storing</c>, or <c>lzma</c> with optional FreeArc parameters such as <c>lzma:1mb</c>.</param>
  public FreeArcWriter(string method = FreeArcMethods.Storing) {
    var name = method.Split(':')[0].Trim().ToLowerInvariant();
    if (method.Contains('+') || name is not (FreeArcMethods.Storing or "lzma"))
      throw new NotSupportedException($"FreeArc: writing method \"{method}\" is not supported (storing or lzma only).");
    this.Method = method;
  }

  /// <summary>Gets the method string used for the file block.</summary>
  public string Method { get; }

  /// <summary>Adds a file.</summary>
  public void AddFile(string name, byte[] data, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((Normalize(name), data, modified ?? DateTime.UtcNow));
  }

  /// <summary>Adds a directory entry.</summary>
  public void AddDirectory(string name, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(name);
    this._directories.Add((Normalize(name), modified ?? DateTime.UtcNow));
  }

  /// <summary>Builds the archive.</summary>
  public byte[] Build() {
    using var output = new MemoryStream();
    this.WriteTo(output);
    return output.ToArray();
  }

  /// <summary>Writes the archive to <paramref name="output"/>, which must be positioned at its start.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    using var archive = new MemoryStream();

    // Header block: signature + version, then its descriptor.
    var header = new MemoryStream();
    WriteUInt32(header, Signature);
    WriteUInt32(header, ArchiveVersion);
    var headerBlock = WriteControlBlock(archive, BlockHeader, header.ToArray());

    // Data blocks: directories in an empty storing block, files in one solid block.
    var blocks = new List<(int Files, string Compressor, long Position, long Size)>();
    var entries = new List<(string Name, long Size, DateTime Modified, bool IsDir, uint Crc)>();
    if (this._directories.Count > 0) {
      blocks.Add((this._directories.Count, FreeArcMethods.Storing, archive.Position, 0));
      foreach (var (name, modified) in this._directories)
        entries.Add((name, 0, modified, true, 0));
    }
    if (this._files.Count > 0) {
      using var solid = new MemoryStream();
      foreach (var (name, data, modified) in this._files) {
        solid.Write(data);
        entries.Add((name, data.LongLength, modified, false, Crc32.Compute(data)));
      }
      var method = FreeArcMethods.WithDictionary(this.Method, solid.Length);
      var compressed = FreeArcMethods.Encode(method, solid.ToArray());
      blocks.Add((this._files.Count, method, archive.Position, compressed.LongLength));
      archive.Write(compressed);
    }

    var directoryPosition = archive.Position;
    var directoryBlock = WriteControlBlock(archive, BlockDirectory, BuildDirectory(blocks, entries, directoryPosition));

    var footerPosition = archive.Position;
    var footer = new MemoryStream();
    WriteVarInt(footer, 2);
    foreach (var block in new[] { headerBlock, directoryBlock }) {
      WriteVarInt(footer, (ulong)block.Type);
      WriteString(footer, FreeArcMethods.Storing);
      WriteVarInt(footer, (ulong)(footerPosition - block.Position));
      WriteVarInt(footer, (ulong)block.Size);
      WriteVarInt(footer, (ulong)block.Size);
      WriteUInt32(footer, block.Crc);
    }
    footer.WriteByte(0);        // not locked
    WriteVarInt(footer, 0);     // no legacy (UCS-4) comment
    WriteString(footer, "");    // no recovery-record setting
    WriteVarInt(footer, 0);     // no comment
    WriteControlBlock(archive, BlockFooter, footer.ToArray());

    archive.Position = 0;
    archive.CopyTo(output);
  }

  private static byte[] BuildDirectory(
      List<(int Files, string Compressor, long Position, long Size)> blocks,
      List<(string Name, long Size, DateTime Modified, bool IsDir, uint Crc)> entries,
      long directoryPosition) {
    var dir = new MemoryStream();
    WriteVarInt(dir, (ulong)blocks.Count);
    foreach (var b in blocks) WriteVarInt(dir, (ulong)b.Files);
    foreach (var b in blocks) WriteString(dir, b.Compressor);
    foreach (var b in blocks) WriteVarInt(dir, (ulong)(directoryPosition - b.Position));
    foreach (var b in blocks) WriteVarInt(dir, (ulong)b.Size);

    var folders = new List<string>();
    var folderIndex = new Dictionary<string, int>(StringComparer.Ordinal);
    var numbers = new List<int>();
    var baseNames = new List<string>();
    foreach (var e in entries) {
      var slash = e.Name.LastIndexOf('/');
      var folder = slash < 0 ? "" : e.Name[..slash];
      baseNames.Add(slash < 0 ? e.Name : e.Name[(slash + 1)..]);
      if (!folderIndex.TryGetValue(folder, out var index)) {
        index = folders.Count;
        folderIndex[folder] = index;
        folders.Add(folder);
      }
      numbers.Add(index);
    }

    WriteVarInt(dir, (ulong)folders.Count);
    foreach (var f in folders) WriteString(dir, f);
    foreach (var n in baseNames) WriteString(dir, n);
    foreach (var n in numbers) WriteVarInt(dir, (ulong)n);
    foreach (var e in entries) WriteVarInt(dir, (ulong)e.Size);
    foreach (var e in entries) WriteUInt32(dir, ToUnixTime(e.Modified));
    foreach (var e in entries) dir.WriteByte(e.IsDir ? (byte)1 : (byte)0);
    foreach (var e in entries) WriteUInt32(dir, e.Crc);
    WriteVarInt(dir, 0); // end of optional fields
    return dir.ToArray();
  }

  /// <summary>Writes a stored control block followed by its descriptor and the descriptor's CRC.</summary>
  private static (int Type, long Position, long Size, uint Crc) WriteControlBlock(Stream archive, int type, byte[] data) {
    var position = archive.Position;
    archive.Write(data);
    var crc = Crc32.Compute(data);

    var descriptor = new MemoryStream();
    WriteUInt32(descriptor, Signature);
    WriteVarInt(descriptor, (ulong)type);
    WriteString(descriptor, FreeArcMethods.Storing);
    WriteVarInt(descriptor, (ulong)data.LongLength);
    WriteVarInt(descriptor, (ulong)data.LongLength);
    WriteUInt32(descriptor, crc);
    var body = descriptor.ToArray();
    archive.Write(body);
    WriteUInt32(archive, Crc32.Compute(body));
    return (type, position, data.LongLength, crc);
  }

  private static uint ToUnixTime(DateTime time) {
    var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;
    var seconds = (long)(utc - DateTime.UnixEpoch).TotalSeconds;
    return (uint)Math.Clamp(seconds, 0, uint.MaxValue);
  }

  private static string Normalize(string name) => name.Replace('\\', '/').Trim('/');
}
