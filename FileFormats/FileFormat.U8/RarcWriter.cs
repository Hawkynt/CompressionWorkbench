#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Rarc;

public sealed class RarcWriter : IDisposable {
  private const RarcEntryAttributes LoadMask =
    RarcEntryAttributes.PreloadToMram | RarcEntryAttributes.PreloadToAram | RarcEntryAttributes.LoadFromDvd;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string _rootName;
  private readonly List<InputFile> _files = [];
  private readonly HashSet<string> _filePaths = new(StringComparer.Ordinal);
  private readonly HashSet<string> _directoryPaths = new(StringComparer.Ordinal);
  private bool _finished;
  private bool _disposed;

  public RarcWriter(Stream stream, bool leaveOpen = false, string rootName = "root") {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    ValidateName(rootName, nameof(rootName));
    this._rootName = rootName;
    this._leaveOpen = leaveOpen;
  }

  public void AddEntry(
      string path,
      byte[] data,
      RarcEntryAttributes attributes = RarcEntryAttributes.File | RarcEntryAttributes.PreloadToMram) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);

    var normalized = NormalizePath(path);
    attributes = NormalizeAttributes(attributes);
    if (!this._filePaths.Add(normalized))
      throw new ArgumentException($"RARC already contains a file named '{normalized}'.", nameof(path));
    if (this._directoryPaths.Contains(normalized)) {
      this._filePaths.Remove(normalized);
      throw new ArgumentException($"RARC path '{normalized}' is already used as a directory.", nameof(path));
    }

    var segments = normalized.Split('/');
    var prefix = string.Empty;
    var pendingDirectories = new List<string>();
    for (var i = 0; i < segments.Length - 1; ++i) {
      prefix = prefix.Length == 0 ? segments[i] : prefix + "/" + segments[i];
      if (this._filePaths.Contains(prefix)) {
        this._filePaths.Remove(normalized);
        throw new ArgumentException($"RARC path '{prefix}' is already used as a file.", nameof(path));
      }
      pendingDirectories.Add(prefix);
    }
    foreach (var directory in pendingDirectories)
      this._directoryPaths.Add(directory);

    this._files.Add(new InputFile(normalized, data, attributes));
  }

  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var root = new TreeDirectory(this._rootName, parent: null);
    foreach (var input in this._files.OrderBy(file => file.Path, StringComparer.Ordinal)) {
      var segments = input.Path.Split('/');
      var directory = root;
      for (var i = 0; i < segments.Length - 1; ++i) {
        if (!directory.Subdirectories.TryGetValue(segments[i], out var child)) {
          child = new TreeDirectory(segments[i], directory);
          directory.Subdirectories.Add(segments[i], child);
        }
        directory = child;
      }
      directory.Files.Add(new TreeFile(input.Path, segments[^1], input.Data, input.Attributes));
    }

    var directories = new List<TreeDirectory>();
    AssignDirectoryIndexes(root, directories);

    var totalFiles = this._files.Count;
    if (totalFiles > ushort.MaxValue)
      throw new InvalidDataException("RARC supports at most 65,535 file IDs.");

    var entries = new List<FileEntryRecord>();
    var nextFileId = 0;
    foreach (var directory in directories) {
      directory.FirstEntryIndex = checked((uint)entries.Count);
      entries.Add(FileEntryRecord.Directory(".", checked((uint)directory.Index)));
      entries.Add(FileEntryRecord.Directory("..",
        directory.Parent is null ? uint.MaxValue : checked((uint)directory.Parent.Index)));

      foreach (var child in directory.Subdirectories.Values)
        entries.Add(FileEntryRecord.Directory(child.Name, checked((uint)child.Index)));

      foreach (var file in directory.Files.OrderBy(file => file.Name, StringComparer.Ordinal)) {
        if (nextFileId >= RarcConstants.DirectoryFileId)
          throw new InvalidDataException("RARC file-ID space is exhausted.");
        entries.Add(FileEntryRecord.File(file, checked((ushort)nextFileId)));
        ++nextFileId;
      }

      var entryCount = entries.Count - checked((int)directory.FirstEntryIndex);
      if (entryCount > ushort.MaxValue)
        throw new InvalidDataException($"RARC directory '{directory.Name}' contains too many entries.");
      directory.EntryCount = checked((ushort)entryCount);
    }

    var strings = new StringPool();
    strings.GetOffset(".");
    strings.GetOffset("..");
    foreach (var directory in directories)
      directory.NameOffset = strings.GetOffset(directory.Name);
    foreach (var entry in entries)
      entry.NameOffset = strings.GetOffset(entry.Name);
    var stringBytes = strings.ToArray();

    var dataHeaderOffset = RarcConstants.HeaderSize;
    var nodeOffset = dataHeaderOffset + RarcConstants.DataHeaderSize;
    var fileEntryOffset = AlignUp(checked(nodeOffset + directories.Count * RarcConstants.NodeSize));
    var stringOffset = AlignUp(checked(fileEntryOffset + entries.Count * RarcConstants.FileEntrySize));
    var fileDataOffset = AlignUp(checked(stringOffset + stringBytes.Length));

    var dataCursor = 0L;
    var mramSize = AssignDataOffsets(entries, RarcEntryAttributes.PreloadToMram, ref dataCursor);
    var aramSize = AssignDataOffsets(entries, RarcEntryAttributes.PreloadToAram, ref dataCursor);
    var dvdSize = AssignDataOffsets(entries, RarcEntryAttributes.LoadFromDvd, ref dataCursor);
    var totalDataSize = checked(mramSize + aramSize + dvdSize);
    var fileSize = checked((long)fileDataOffset + totalDataSize);

    if (fileSize > uint.MaxValue || totalDataSize > uint.MaxValue
        || mramSize > uint.MaxValue || aramSize > uint.MaxValue || dvdSize > uint.MaxValue)
      throw new InvalidDataException("RARC output exceeds the 32-bit size fields supported by the format.");

    this._stream.Position = 0;
    Span<byte> header = stackalloc byte[RarcConstants.HeaderSize];
    header.Clear();
    "RARC"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..8], checked((uint)fileSize));
    BinaryPrimitives.WriteUInt32BigEndian(header[8..12], RarcConstants.HeaderSize);
    BinaryPrimitives.WriteUInt32BigEndian(header[12..16], checked((uint)(fileDataOffset - dataHeaderOffset)));
    BinaryPrimitives.WriteUInt32BigEndian(header[16..20], checked((uint)totalDataSize));
    BinaryPrimitives.WriteUInt32BigEndian(header[20..24], checked((uint)mramSize));
    BinaryPrimitives.WriteUInt32BigEndian(header[24..28], checked((uint)aramSize));
    BinaryPrimitives.WriteUInt32BigEndian(header[28..32], checked((uint)dvdSize));
    this._stream.Write(header);

    Span<byte> dataHeader = stackalloc byte[RarcConstants.DataHeaderSize];
    dataHeader.Clear();
    BinaryPrimitives.WriteUInt32BigEndian(dataHeader[0..4], checked((uint)directories.Count));
    BinaryPrimitives.WriteUInt32BigEndian(dataHeader[4..8], checked((uint)(nodeOffset - dataHeaderOffset)));
    BinaryPrimitives.WriteUInt32BigEndian(dataHeader[8..12], checked((uint)entries.Count));
    BinaryPrimitives.WriteUInt32BigEndian(dataHeader[12..16], checked((uint)(fileEntryOffset - dataHeaderOffset)));
    BinaryPrimitives.WriteUInt32BigEndian(dataHeader[16..20], checked((uint)stringBytes.Length));
    BinaryPrimitives.WriteUInt32BigEndian(dataHeader[20..24], checked((uint)(stringOffset - dataHeaderOffset)));
    BinaryPrimitives.WriteUInt16BigEndian(dataHeader[24..26], checked((ushort)totalFiles));
    dataHeader[26] = 0; // IDs are sequential over files, not global file-entry indexes.
    this._stream.Write(dataHeader);

    PadTo(nodeOffset);
    var nodeBuffer = new byte[RarcConstants.NodeSize];
    foreach (var directory in directories) {
      nodeBuffer.AsSpan().Clear();
      WriteDirectoryType(nodeBuffer.AsSpan(0, 4), directory);
      BinaryPrimitives.WriteUInt32BigEndian(nodeBuffer.AsSpan(4, 4), directory.NameOffset);
      BinaryPrimitives.WriteUInt16BigEndian(nodeBuffer.AsSpan(8, 2), RarcReader.CalculateNameHash(directory.Name));
      BinaryPrimitives.WriteUInt16BigEndian(nodeBuffer.AsSpan(10, 2), directory.EntryCount);
      BinaryPrimitives.WriteUInt32BigEndian(nodeBuffer.AsSpan(12, 4), directory.FirstEntryIndex);
      this._stream.Write(nodeBuffer);
    }

    PadTo(fileEntryOffset);
    var entryBuffer = new byte[RarcConstants.FileEntrySize];
    foreach (var entry in entries) {
      entryBuffer.AsSpan().Clear();
      BinaryPrimitives.WriteUInt16BigEndian(entryBuffer.AsSpan(0, 2), entry.Id);
      BinaryPrimitives.WriteUInt16BigEndian(entryBuffer.AsSpan(2, 2), RarcReader.CalculateNameHash(entry.Name));
      if (entry.NameOffset > 0x00FF_FFFFu)
        throw new InvalidDataException("RARC string-table offset exceeds the 24-bit file-entry field.");
      var typeAndName = ((uint)entry.Attributes << 24) | entry.NameOffset;
      BinaryPrimitives.WriteUInt32BigEndian(entryBuffer.AsSpan(4, 4), typeAndName);
      BinaryPrimitives.WriteUInt32BigEndian(entryBuffer.AsSpan(8, 4), entry.DataOffset);
      BinaryPrimitives.WriteUInt32BigEndian(entryBuffer.AsSpan(12, 4), entry.DataSize);
      this._stream.Write(entryBuffer);
    }

    PadTo(stringOffset);
    this._stream.Write(stringBytes);
    PadTo(fileDataOffset);

    foreach (var entry in entries
      .Where(entry => entry.File is not null)
      .OrderBy(entry => LoadOrder(entry.Attributes))
      .ThenBy(entry => entry.File!.Path, StringComparer.Ordinal)) {
      PadTo(checked((long)fileDataOffset + entry.DataOffset));
      if (entry.File!.Data.Length > 0)
        this._stream.Write(entry.File.Data);
    }

    PadTo(fileSize);
    this._stream.SetLength(fileSize);
    this._stream.Position = fileSize;
  }

  public static string NormalizePath(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var normalized = path.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new ArgumentException("RARC file path must name a file.", nameof(path));
    foreach (var segment in normalized.Split('/')) {
      ValidateName(segment, nameof(path));
      if (segment is "." or "..")
        throw new ArgumentException("RARC file paths may not contain '.' or '..' components.", nameof(path));
    }
    return normalized;
  }

  private static RarcEntryAttributes NormalizeAttributes(RarcEntryAttributes attributes) {
    attributes |= RarcEntryAttributes.File;
    attributes &= ~RarcEntryAttributes.Directory;
    if ((attributes & RarcEntryAttributes.Yaz0Compressed) != 0)
      attributes |= RarcEntryAttributes.Compressed;
    var load = attributes & LoadMask;
    if (load == RarcEntryAttributes.None)
      attributes |= RarcEntryAttributes.PreloadToMram;
    else if (load != RarcEntryAttributes.PreloadToMram
             && load != RarcEntryAttributes.PreloadToAram
             && load != RarcEntryAttributes.LoadFromDvd)
      throw new ArgumentException("RARC files must select exactly one of MRAM, ARAM, or DVD placement.", nameof(attributes));
    return attributes;
  }

  private static void ValidateName(string name, string parameterName) {
    if (string.IsNullOrEmpty(name))
      throw new ArgumentException("RARC names must not be empty.", parameterName);
    if (name.IndexOf('\0') >= 0 || name.Any(ch => ch > 0x7F))
      throw new ArgumentException("RARC writer currently emits canonical ASCII string tables; names must be ASCII.", parameterName);
  }

  private static void AssignDirectoryIndexes(TreeDirectory directory, List<TreeDirectory> output) {
    directory.Index = output.Count;
    output.Add(directory);
    foreach (var child in directory.Subdirectories.Values)
      AssignDirectoryIndexes(child, output);
  }

  private static long AssignDataOffsets(
      IEnumerable<FileEntryRecord> entries,
      RarcEntryAttributes target,
      ref long cursor) {
    var start = cursor;
    foreach (var entry in entries
      .Where(entry => entry.File is not null && (entry.Attributes & LoadMask) == target)
      .OrderBy(entry => entry.File!.Path, StringComparer.Ordinal)) {
      cursor = AlignUp(cursor);
      if (cursor > uint.MaxValue || entry.File!.Data.LongLength > uint.MaxValue)
        throw new InvalidDataException("RARC file-data offset or size exceeds the 32-bit format range.");
      entry.DataOffset = checked((uint)cursor);
      entry.DataSize = checked((uint)entry.File.Data.Length);
      cursor = checked(cursor + entry.File.Data.LongLength);
      cursor = AlignUp(cursor);
    }
    return cursor - start;
  }

  private static int LoadOrder(RarcEntryAttributes attributes)
    => (attributes & LoadMask) switch {
      RarcEntryAttributes.PreloadToMram => 0,
      RarcEntryAttributes.PreloadToAram => 1,
      RarcEntryAttributes.LoadFromDvd => 2,
      _ => 3,
    };

  private static int AlignUp(int value)
    => checked((value + RarcConstants.Alignment - 1) & ~(RarcConstants.Alignment - 1));

  private static long AlignUp(long value)
    => checked((value + RarcConstants.Alignment - 1) & ~((long)RarcConstants.Alignment - 1));

  private void PadTo(long position) {
    if (this._stream.Position > position)
      throw new InvalidDataException("RARC writer overran a precomputed section boundary.");
    Span<byte> zeros = stackalloc byte[RarcConstants.Alignment];
    while (this._stream.Position < position) {
      var count = (int)Math.Min(zeros.Length, position - this._stream.Position);
      this._stream.Write(zeros[..count]);
    }
  }

  private static void WriteDirectoryType(Span<byte> destination, TreeDirectory directory) {
    destination.Fill((byte)' ');
    if (directory.Parent is null) {
      "ROOT"u8.CopyTo(destination);
      return;
    }
    for (var i = 0; i < Math.Min(4, directory.Name.Length); ++i)
      destination[i] = (byte)char.ToUpperInvariant(directory.Name[i]);
  }

  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private sealed record InputFile(string Path, byte[] Data, RarcEntryAttributes Attributes);

  private sealed class TreeDirectory(string name, TreeDirectory? parent) {
    public string Name { get; } = name;
    public TreeDirectory? Parent { get; } = parent;
    public SortedDictionary<string, TreeDirectory> Subdirectories { get; } = new(StringComparer.Ordinal);
    public List<TreeFile> Files { get; } = [];
    public int Index { get; set; }
    public uint NameOffset { get; set; }
    public uint FirstEntryIndex { get; set; }
    public ushort EntryCount { get; set; }
  }

  private sealed record TreeFile(
    string Path,
    string Name,
    byte[] Data,
    RarcEntryAttributes Attributes);

  private sealed class FileEntryRecord {
    private FileEntryRecord() { }

    public ushort Id { get; init; }
    public required string Name { get; init; }
    public RarcEntryAttributes Attributes { get; init; }
    public uint NameOffset { get; set; }
    public uint DataOffset { get; set; }
    public uint DataSize { get; set; }
    public TreeFile? File { get; init; }

    public static FileEntryRecord Directory(string name, uint nodeIndex)
      => new() {
        Id = RarcConstants.DirectoryFileId,
        Name = name,
        Attributes = RarcEntryAttributes.Directory,
        DataOffset = nodeIndex,
        DataSize = RarcConstants.NodeSize,
      };

    public static FileEntryRecord File(TreeFile file, ushort id)
      => new() {
        Id = id,
        Name = file.Name,
        Attributes = file.Attributes,
        File = file,
      };
  }

  private sealed class StringPool {
    private readonly MemoryStream _stream = new();
    private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);

    public uint GetOffset(string value) {
      if (this._offsets.TryGetValue(value, out var existing))
        return existing;
      if (this._stream.Position > 0x00FF_FFFF)
        throw new InvalidDataException("RARC string table exceeds the 24-bit name-offset range.");
      var offset = checked((uint)this._stream.Position);
      var bytes = Encoding.ASCII.GetBytes(value);
      this._stream.Write(bytes);
      this._stream.WriteByte(0);
      this._offsets.Add(value, offset);
      return offset;
    }

    public byte[] ToArray() => this._stream.ToArray();
  }
}
