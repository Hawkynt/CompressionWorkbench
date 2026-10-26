using Compression.Core.Checksums;
using static FileFormat.FreeArc.FreeArcEncoding;

namespace FileFormat.FreeArc;

/// <summary>
/// Reads real FreeArc archives (<c>.arc</c>, signature <c>ArC\x01</c>) as written by FreeArc 0.4x–0.67.
/// </summary>
/// <remarks>
/// The archive is located from its end: the footer block's descriptor sits in the last 4 KiB, the
/// footer lists the control blocks (header, directories, recovery) and each directory block lists
/// its solid data blocks and files. Every control block and descriptor is CRC-checked; file data is
/// checked against the per-file CRC-32 on extraction. Layout in <c>docs/FREEARC-ON-DISK.md</c>.
/// Data whose method this library cannot decode (see <see cref="FreeArcMethods"/>) is listed but
/// refused on extraction.
/// </remarks>
public sealed class FreeArcReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<FreeArcEntry> _entries = [];
  private FreeArcDataBlock? _cachedBlock;
  private byte[]? _cachedData;

  /// <summary>Gets all files and directories recorded in the archive's directory blocks, in archive order.</summary>
  public IReadOnlyList<FreeArcEntry> Entries => this._entries;

  /// <summary>Gets the size of the data preceding the archive proper (an SFX module), or zero.</summary>
  public long SfxSize { get; private set; }

  /// <summary>Gets whether the archive is locked against further modification.</summary>
  public bool IsLocked { get; private set; }

  /// <summary>Gets the archive comment, or an empty string.</summary>
  public string Comment { get; private set; } = "";

  /// <summary>Opens a FreeArc archive and reads its footer and directory blocks.</summary>
  /// <param name="stream">A readable, seekable stream holding the archive.</param>
  /// <param name="leaveOpen"><see langword="true"/> to leave <paramref name="stream"/> open on dispose.</param>
  /// <exception cref="InvalidDataException">The stream is not a FreeArc archive or its structure is corrupt.</exception>
  /// <exception cref="NotSupportedException">A directory block is encrypted or compressed with an unsupported method.</exception>
  public FreeArcReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("FreeArc needs a readable, seekable stream.", nameof(stream));
    this._stream = stream;
    this._leaveOpen = leaveOpen;
    this.ReadStructure();
  }

  /// <summary>Extracts the contents of <paramref name="entry"/>, verifying its CRC-32.</summary>
  /// <exception cref="NotSupportedException">The entry's solid block uses an unsupported or encrypted method.</exception>
  /// <exception cref="InvalidDataException">The data is corrupt.</exception>
  public byte[] Extract(FreeArcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0)
      return [];

    var block = this.DecodeBlock(entry.Block);
    if (entry.OffsetInBlock + entry.Size > block.LongLength)
      throw new InvalidDataException($"FreeArc: \"{entry.Name}\" lies outside its solid block.");

    var data = block.AsSpan((int)entry.OffsetInBlock, (int)entry.Size).ToArray();
    if (Crc32.Compute(data) != entry.Crc)
      throw new InvalidDataException($"FreeArc: \"{entry.Name}\" failed its CRC check.");
    return data;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private byte[] DecodeBlock(FreeArcDataBlock block) {
    if (ReferenceEquals(block, this._cachedBlock))
      return this._cachedData!;

    var compressed = this.ReadAt(block.Position, block.CompressedSize);
    var data = FreeArcMethods.Decode(block.Compressor, compressed, block.OriginalSize);
    this._cachedBlock = block;
    this._cachedData = data;
    return data;
  }

  private void ReadStructure() {
    var footer = this.FindFooter();
    var footerData = this.ReadControlBlock(footer);

    var p = 0;
    var count = ReadCount(footerData, ref p);
    var controls = new List<ControlBlock>(count);
    for (var i = 0; i < count; ++i) {
      var type = ReadCount(footerData, ref p);
      var compressor = ReadString(footerData, ref p);
      var relative = ReadSize(footerData, ref p);
      var original = ReadSize(footerData, ref p);
      var compressed = ReadSize(footerData, ref p);
      var crc = ReadUInt32(footerData, ref p);
      var position = footer.Position - relative;
      if (position < 0 || position + compressed > footer.Position)
        throw new InvalidDataException("FreeArc: footer lists a block outside the archive.");
      controls.Add(new(type, compressor, position, original, compressed, crc));
    }

    if (p < footerData.Length)
      this.IsLocked = footerData[p++] != 0;
    if (p < footerData.Length) {
      // Pre-0.60 comment: a list of UCS-4 code points.
      var legacyLength = ReadCount(footerData, ref p);
      var chars = new System.Text.StringBuilder();
      for (var i = 0; i < legacyLength; ++i)
        chars.Append(char.ConvertFromUtf32((int)ReadUInt32(footerData, ref p)));
      this.Comment = chars.ToString();
    }
    if (p < footerData.Length)
      _ = ReadString(footerData, ref p); // recovery-record setting
    if (p < footerData.Length) {
      var commentLength = ReadCount(footerData, ref p);
      if (p + commentLength > footerData.Length)
        throw new InvalidDataException("FreeArc: comment runs past the footer.");
      if (commentLength > 0)
        this.Comment = System.Text.Encoding.UTF8.GetString(footerData, p, commentLength);
    }

    this.SfxSize = controls.Count > 0 ? Math.Max(0, controls.Min(c => c.Position)) : 0;

    foreach (var control in controls.Where(c => c.Type == BlockDirectory))
      this.ReadDirectory(control, this.ReadControlBlock(control));
  }

  private void ReadDirectory(ControlBlock directory, byte[] dir) {
    var p = 0;
    var blockCount = ReadCount(dir, ref p);
    var filesPerBlock = new int[blockCount];
    for (var i = 0; i < blockCount; ++i)
      filesPerBlock[i] = ReadCount(dir, ref p);
    var compressors = new string[blockCount];
    for (var i = 0; i < blockCount; ++i)
      compressors[i] = ReadString(dir, ref p);
    var offsets = new long[blockCount];
    for (var i = 0; i < blockCount; ++i)
      offsets[i] = ReadSize(dir, ref p);
    var compSizes = new long[blockCount];
    for (var i = 0; i < blockCount; ++i)
      compSizes[i] = ReadSize(dir, ref p);

    var dirCount = ReadCount(dir, ref p);
    var dirNames = new string[dirCount];
    for (var i = 0; i < dirCount; ++i)
      dirNames[i] = ReadString(dir, ref p).Replace('\\', '/');

    long total = 0;
    foreach (var n in filesPerBlock)
      total += n;
    if (total > dir.Length)
      throw new InvalidDataException("FreeArc: directory claims more files than it can hold.");
    var files = (int)total;

    var names = new string[files];
    for (var i = 0; i < files; ++i)
      names[i] = ReadString(dir, ref p).Replace('\\', '/');
    var dirNumbers = new int[files];
    for (var i = 0; i < files; ++i) {
      dirNumbers[i] = ReadCount(dir, ref p);
      if (dirNumbers[i] >= dirCount)
        throw new InvalidDataException("FreeArc: file refers to a directory name that does not exist.");
    }
    var sizes = new long[files];
    for (var i = 0; i < files; ++i)
      sizes[i] = ReadSize(dir, ref p);
    var times = new uint[files];
    for (var i = 0; i < files; ++i)
      times[i] = ReadUInt32(dir, ref p);
    if (p + files > dir.Length)
      throw new InvalidDataException("FreeArc: directory flags run past the block.");
    var isDir = new bool[files];
    for (var i = 0; i < files; ++i)
      isDir[i] = dir[p++] != 0;
    var crcs = new uint[files];
    for (var i = 0; i < files; ++i)
      crcs[i] = ReadUInt32(dir, ref p);
    // Optional tagged fields follow, ending with tag 0; FreeArc 0.67 writes none.

    var file = 0;
    for (var b = 0; b < blockCount; ++b) {
      long blockSize = 0;
      for (var k = 0; k < filesPerBlock[b]; ++k)
        blockSize += sizes[file + k];

      var position = directory.Position - offsets[b];
      if (position < 0 || position + compSizes[b] > directory.Position)
        throw new InvalidDataException("FreeArc: directory lists a data block outside the archive.");
      var block = new FreeArcDataBlock(compressors[b], position, compSizes[b], blockSize);

      long offset = 0;
      for (var k = 0; k < filesPerBlock[b]; ++k, ++file) {
        var folder = dirNames[dirNumbers[file]];
        this._entries.Add(new() {
          Name = folder.Length == 0 ? names[file] : folder.TrimEnd('/') + "/" + names[file],
          Size = sizes[file],
          CompressedSize = k == 0 ? compSizes[b] : 0,
          Method = compressors[b],
          IsDirectory = isDir[file],
          LastModified = DateTime.UnixEpoch.AddSeconds(times[file]),
          Crc = crcs[file],
          Block = block,
          OffsetInBlock = offset,
        });
        offset += sizes[file];
      }
    }
  }

  /// <summary>Scans the last 4 KiB backwards for a descriptor that checks out as the footer's.</summary>
  private ControlBlock FindFooter() {
    var length = this._stream.Length;
    if (length < 16)
      throw new InvalidDataException("FreeArc: too small to be an archive.");

    var windowStart = Math.Max(0, length - FooterScanWindow);
    var window = this.ReadAt(windowStart, length - windowStart);
    for (var at = window.Length - 4; at >= 0; --at) {
      if (window[at] != 'A' || window[at + 1] != 'r' || window[at + 2] != 'C' || window[at + 3] != 1)
        continue;
      if (TryParseDescriptor(window.AsSpan(at), windowStart + at, out var block) && block.Type == BlockFooter)
        return block;
    }

    throw new InvalidDataException("FreeArc: no footer block descriptor in the last 4 KiB — not a FreeArc archive or truncated.");
  }

  /// <summary>
  /// A descriptor: signature, type, compressor, original size, compressed size, CRC of the block's
  /// decoded contents, then the CRC of the descriptor itself. The block precedes its descriptor.
  /// The footer's descriptor runs to the end of the archive.
  /// </summary>
  private static bool TryParseDescriptor(ReadOnlySpan<byte> descriptor, long position, out ControlBlock block) {
    block = default;
    if (descriptor.Length < 12)
      return false;
    var body = descriptor[..^4];
    if (Crc32.Compute(body) != System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(descriptor[^4..]))
      return false;

    try {
      var p = 4;
      var type = ReadCount(body, ref p);
      var compressor = ReadString(body, ref p);
      var original = ReadSize(body, ref p);
      var compressed = ReadSize(body, ref p);
      var crc = ReadUInt32(body, ref p);
      if (p != body.Length || compressed > position)
        return false;
      block = new(type, compressor, position - compressed, original, compressed, crc);
      return true;
    } catch (InvalidDataException) {
      return false;
    }
  }

  private byte[] ReadControlBlock(ControlBlock block) {
    if (block.Original > int.MaxValue || block.Compressed > int.MaxValue)
      throw new InvalidDataException("FreeArc: control block too large.");
    var compressed = this.ReadAt(block.Position, block.Compressed);
    var data = FreeArcMethods.Decode(block.Compressor, compressed, block.Original);
    if (Crc32.Compute(data) != block.Crc)
      throw new InvalidDataException($"FreeArc: control block at {block.Position} failed its CRC check.");
    return data;
  }

  private byte[] ReadAt(long position, long count) {
    if (position < 0 || count < 0 || position + count > this._stream.Length || count > int.MaxValue)
      throw new InvalidDataException("FreeArc: block lies outside the archive.");
    var buffer = new byte[count];
    this._stream.Position = position;
    this._stream.ReadExactly(buffer);
    return buffer;
  }

  private readonly record struct ControlBlock(int Type, string Compressor, long Position, long Original, long Compressed, uint Crc);
}
