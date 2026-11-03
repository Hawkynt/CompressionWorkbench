using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using FileFormat.Bzip2;

namespace FileFormat.ClickteamInstaller;

/// <summary>One file of a Clickteam Install Creator installer.</summary>
/// <param name="Path">Install path, '/'-separated.</param>
/// <param name="Offset">Offset of its member in the file data block.</param>
/// <param name="CompressedSize">Stored member size.</param>
/// <param name="Size">Expanded size.</param>
/// <param name="Modified">Last-write time, when recorded.</param>
public sealed record ClickteamEntry(string Path, uint Offset, uint CompressedSize, uint Size, DateTime? Modified);

/// <summary>
/// Clickteam Install Creator installers (versions 2.0 to 4.0 of the file list): a PE stub followed
/// by an overlay that starts with <c>77 77 67 54 29 48</c> and holds typed blocks; the file list
/// block (0x143A) describes the files, the file data block (0x7F7F) holds their members, each
/// stored, zlib or bzip2 compressed.
/// </summary>
/// <remarks>
/// Converted from cicdec 3.0.1 (William Engelmann, BSD-3-Clause), <c>Program.cs</c> and
/// <c>FileInfo.cs</c>; see THIRD-PARTY-NOTICE.cicdec.txt. Deviations: the zlib members' Adler-32
/// trailers are checked (cicdec does not), and installers whose data lives in external <c>.Dnn</c>
/// files are refused rather than read.
/// </remarks>
public sealed class ClickteamReader {
  /// <summary>The overlay signature.</summary>
  public static ReadOnlySpan<byte> Signature => [0x77, 0x77, 0x67, 0x54, 0x29, 0x48];

  private const int BlockHeaderSize = 16 + 16 + 32;
  private const ushort BlockFileList = 0x143A, BlockFileData = 0x7F7F;

  private readonly byte[] _data;
  private readonly long _dataBlock;

  private ClickteamReader(byte[] data, long dataBlock, int version, List<ClickteamEntry> entries) {
    this._data = data;
    this._dataBlock = dataBlock;
    this.Version = version;
    this.Entries = entries;
  }

  /// <summary>The file list layout the installer was read with (20, 24, 30, 35 or 40).</summary>
  public int Version { get; }

  /// <summary>The installer's files.</summary>
  public IReadOnlyList<ClickteamEntry> Entries { get; }

  /// <summary>Whether a PE image carries the Install Creator overlay right after its sections.</summary>
  public static bool IsClickteam(Stream stream) {
    try {
      var overlay = PeEnd(stream);
      if (overlay <= 0 || overlay + 6 > stream.Length)
        return false;
      Span<byte> head = stackalloc byte[6];
      stream.Position = overlay;
      stream.ReadExactly(head);
      return head.SequenceEqual(Signature);
    } catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException or EndOfStreamException) {
      return false;
    }
  }

  private static long PeEnd(Stream stream) {
    Span<byte> b = stackalloc byte[64];
    stream.Position = 0;
    if (stream.Read(b) < 64 || b[0] != 'M' || b[1] != 'Z')
      return -1;
    long pe = BinaryPrimitives.ReadUInt32LittleEndian(b[0x3C..]);
    stream.Position = pe;
    Span<byte> h = stackalloc byte[24];
    if (stream.Read(h) < 24 || h[0] != 'P' || h[1] != 'E' || h[2] != 0 || h[3] != 0)
      return -1;
    int sections = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
    int optional = BinaryPrimitives.ReadUInt16LittleEndian(h[20..]);
    long end = 0;
    Span<byte> s = stackalloc byte[40];
    for (var i = 0; i < sections; ++i) {
      stream.Position = pe + 24 + optional + i * 40L;
      if (stream.Read(s) < 40)
        return -1;
      end = Math.Max(end, (long)BinaryPrimitives.ReadUInt32LittleEndian(s[20..]) + BinaryPrimitives.ReadUInt32LittleEndian(s[16..]));
    }
    return end;
  }

  /// <summary>Reads the installer's file list.</summary>
  /// <exception cref="InvalidDataException">No Install Creator overlay, or a damaged block.</exception>
  /// <exception cref="NotSupportedException">An encrypted or unknown file list, or external data files.</exception>
  public static ClickteamReader Open(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    var start = data.AsSpan().IndexOf(Signature);
    if (start < 0)
      throw new InvalidDataException("Clickteam: no Install Creator overlay signature.");
    long pos = start + Signature.Length;
    byte[]? fileList = null;
    long dataBlock = -1;
    while (pos + BlockHeaderSize <= data.Length) {
      var id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)pos));
      long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)pos + 4));
      pos += 8;
      var next = pos + size;
      if (id == BlockFileData) {
        dataBlock = pos;
        pos = next;
        continue;
      }
      if (id == BlockFileList) {
        if (next > data.Length)
          throw new InvalidDataException("Clickteam: the file list block runs past the end.");
        var expanded = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)pos));
        fileList = Unpack(data, pos + 4, size - 4, expanded)
          ?? throw new NotSupportedException("Clickteam: the file list is encrypted or uses an unknown compression.");
      }
      pos = next;
    }
    if (fileList == null)
      throw new NotSupportedException("Clickteam: the installer has no file list this reader understands.");
    if (dataBlock < 0)
      throw new NotSupportedException("Clickteam: the files live in external .Dnn data files, which are not read.");

    var (version, entries) = ParseFileList(fileList, data.Length);
    return new ClickteamReader(data, dataBlock, version, entries);
  }

  /// <summary>
  /// One member: a method byte (0 stored, 1 zlib, 2 bzip2) and the data; null for an unknown
  /// method (an encrypted installer).
  /// </summary>
  private static byte[]? Unpack(byte[] data, long offset, long size, uint expanded) {
    if (offset < 0 || size < 1 || offset + size > data.Length)
      throw new InvalidDataException("Clickteam: a member lies outside the installer.");
    var method = data[offset];
    var body = data.AsSpan((int)offset + 1, (int)size - 1);
    switch (method) {
      case 0:
        if (body.Length < expanded)
          throw new InvalidDataException("Clickteam: a stored member is shorter than its size.");
        return body[..(int)expanded].ToArray();
      case 1: {
          if (body.Length < 2)
            throw new InvalidDataException("Clickteam: truncated zlib member.");
          var input = new MemoryStream(body[2..].ToArray());
          var decoder = new DeflateDecompressor(input);
          var output = new byte[expanded];
          var got = 0;
          while (got < output.Length) {
            var n = decoder.Decompress(output.AsSpan(got));
            if (n == 0)
              break;
            got += n;
          }
          if (got != output.Length)
            throw new InvalidDataException($"Clickteam: a zlib member expands to {got} bytes instead of {expanded}.");
          // The zlib stream's Adler-32 follows the DEFLATE data; check it once the stream has ended.
          if (!decoder.IsFinished) {
            Span<byte> probe = stackalloc byte[1];
            if (decoder.Decompress(probe) != 0)
              throw new InvalidDataException("Clickteam: a zlib member expands past its size.");
          }
          var adlerAt = 2 + input.Position - decoder.UnconsumedBytes;
          if (decoder.IsFinished && adlerAt + 4 <= body.Length
              && BinaryPrimitives.ReadUInt32BigEndian(body[(int)adlerAt..]) != Adler32.Compute(output))
            throw new InvalidDataException("Clickteam: a zlib member fails its Adler-32.");
          return output;
        }
      case 2: {
          using var bz = new Bzip2Stream(new MemoryStream(body.ToArray()), Compression.Core.Streams.CompressionStreamMode.Decompress);
          var output = new byte[expanded];
          var got = 0;
          while (got < output.Length) {
            var n = bz.Read(output, got, output.Length - got);
            if (n == 0)
              break;
            got += n;
          }
          if (got != output.Length)
            throw new InvalidDataException($"Clickteam: a bzip2 member expands to {got} bytes instead of {expanded}.");
          return output;
        }
      default:
        return null;
    }
  }

  private sealed class Node {
    public long Start, End;
    public ushort Type;
    public uint Offset, Compressed, Uncompressed, Index;
    public string? Path;
    public long Modified;

    public bool IsValid(long length) =>
      !(this.Offset > length
        || (this.Compressed == 0 && this.Uncompressed > 3000000000)
        || (this.Uncompressed > 10 && this.Compressed > 10 && this.Compressed > unchecked(this.Uncompressed * 5u))
        || this.Index > 1000000000
        || (this.Path != null && this.Path.Any(c => c < 32 || c is '"' or '<' or '>' or '|')));
  }

  private sealed class Cursor(byte[] list) {
    public long Pos;
    public byte U8() => this.Pos < list.Length ? list[this.Pos++] : throw new EndOfStreamException();
    public ushort U16() {
      if (this.Pos + 2 > list.Length)
        throw new EndOfStreamException();
      var v = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan((int)this.Pos));
      this.Pos += 2;
      return v;
    }
    public uint U32() {
      if (this.Pos + 4 > list.Length)
        throw new EndOfStreamException();
      var v = BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan((int)this.Pos));
      this.Pos += 4;
      return v;
    }
    public long I64() {
      if (this.Pos + 8 > list.Length)
        throw new EndOfStreamException();
      var v = BinaryPrimitives.ReadInt64LittleEndian(list.AsSpan((int)this.Pos));
      this.Pos += 8;
      return v;
    }
    public void Skip(int n) => this.Pos += n;
    public byte[] List => list;
  }

  private static readonly Encoding Ansi = CreateAnsi();

  private static Encoding CreateAnsi() {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    return Encoding.GetEncoding(1252);
  }

  private static void ReadPath(Cursor c, Node node) {
    // The path ends the node, followed by zero bytes or a shortcut name after a zero byte.
    var length = node.End - c.Pos;
    if (length < 1)
      return;
    if (c.Pos + length > c.List.Length)
      throw new EndOfStreamException();
    var bytes = c.List.AsSpan((int)c.Pos, (int)length);
    c.Pos += length;
    var zero = bytes.IndexOf((byte)0);
    if (zero > -1)
      bytes = bytes[..zero];
    node.Path = Ansi.GetString(bytes);
  }

  private static Node Header(Cursor c, bool wideSize) {
    var start = c.Pos;
    var size = wideSize ? c.U32() : c.U16();
    return new Node { Start = start, End = start + size, Type = c.U16() };
  }

  private static Node Parse20(Cursor c) {
    var n = Header(c, wideSize: true);
    if (n.Type != 0)
      return n;
    c.Skip(14);
    n.Uncompressed = c.U32();
    n.Offset = c.U32();
    n.Compressed = c.U32();
    n.Modified = c.I64();
    c.I64();
    c.I64();
    ReadPath(c, n);
    return n;
  }

  private static Node Parse24(Cursor c) {
    var n = Header(c, wideSize: false);
    if (n.Type != 0)
      return n;
    c.Skip(2);
    n.Offset = c.U32();
    n.Compressed = c.U32();
    c.U32();
    n.Uncompressed = c.U32();
    c.Skip(16);
    n.Modified = c.I64();
    c.I64();
    c.I64();
    ReadPath(c, n);
    return n;
  }

  private static Node Parse30(Cursor c) {
    var n = Header(c, wideSize: false);
    if (n.Type != 0)
      return n;
    c.Skip(2);
    n.Offset = c.U32();
    n.Compressed = c.U32();
    c.U32();
    n.Uncompressed = c.U32();
    c.Skip(18);
    n.Index = c.U32();
    n.Modified = c.I64();
    c.I64();
    c.I64();
    ReadPath(c, n);
    return n;
  }

  private static Node Parse35(Cursor c) {
    var n = Header(c, wideSize: true);
    if (n.Type != 0)
      return n;
    c.Skip(3);
    c.Skip(c.U8() == 0xE2 ? 30 : 14); // empty files carry no times, so their node is shorter
    var size = c.U32();
    n.Offset = c.U32();
    n.Compressed = c.U32();
    c.U32();
    n.Uncompressed = size;
    n.Index = c.U32();
    c.Skip(2);
    n.Modified = c.I64();
    c.I64();
    c.I64();
    ReadPath(c, n);
    return n;
  }

  private static Node Parse40(Cursor c) {
    var n = Header(c, wideSize: true);
    if (n.Type != 0)
      return n;
    c.Skip(3);
    if (c.U8() == 0xE2)
      c.Skip(30); // an empty dummy file: no sizes, no times
    else {
      c.Skip(14);
      n.Uncompressed = c.U32();
      n.Offset = c.U32();
      n.Compressed = c.U32();
      c.Skip(4);
      n.Modified = c.I64();
      c.I64();
      c.I64();
    }
    ReadPath(c, n);
    return n;
  }

  private static readonly (int Version, Func<Cursor, Node> Parse)[] Layouts = [(40, Parse40), (35, Parse35), (30, Parse30), (24, Parse24), (20, Parse20)];

  private static (int, List<ClickteamEntry>) ParseFileList(byte[] list, long dataLength) {
    var c = new Cursor(list);
    int fileNumber = c.U16();
    c.Skip(2);
    var start = c.Pos;

    // Try the newest layout first: a layout fits when every node of the list parses plausibly.
    foreach (var (version, parse) in Layouts) {
      c.Pos = start;
      var fits = true;
      try {
        for (var i = 0; i < fileNumber && fits; ++i) {
          var node = parse(c);
          fits = node.IsValid(dataLength);
          if (node.Type != 0)
            c.Pos = node.End;
        }
      } catch (EndOfStreamException) {
        fits = false;
      }
      if (!fits)
        continue;

      c.Pos = start;
      var entries = new List<ClickteamEntry>();
      for (var i = 0; i < fileNumber; ++i) {
        var node = parse(c);
        if (!node.IsValid(dataLength))
          throw new InvalidDataException($"Clickteam: node {i} does not fit file list layout {version}.");
        if (node.Type != 0) {
          c.Pos = node.End;
          continue;
        }
        DateTime? modified = node.Modified > 0 && node.Modified < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(node.Modified) : null;
        entries.Add(new ClickteamEntry((node.Path ?? "").Replace('\\', '/').TrimStart('/'), node.Offset, node.Compressed, node.Uncompressed, modified));
      }
      return (version, entries);
    }
    throw new NotSupportedException("Clickteam: the file list matches none of the layouts 2.0 to 4.0.");
  }

  /// <summary>The expanded bytes of <paramref name="entry"/>.</summary>
  /// <exception cref="InvalidDataException">The member is damaged.</exception>
  /// <exception cref="NotSupportedException">The member is encrypted.</exception>
  public byte[] Extract(ClickteamEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0)
      return [];
    if (entry.CompressedSize < 7)
      throw new InvalidDataException($"Clickteam: '{entry.Path}' has an implausible stored size.");
    // The member starts four bytes into the data block.
    var at = this._dataBlock + entry.Offset + 4;
    return Unpack(this._data, at, Math.Min(entry.CompressedSize, this._data.Length - at), entry.Size)
      ?? throw new NotSupportedException($"Clickteam: '{entry.Path}' is encrypted or uses an unknown compression.");
  }
}
