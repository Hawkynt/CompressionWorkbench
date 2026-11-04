using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.StuffItX;

/// <summary>
/// Reads StuffIt X (.sitx) archives.
/// </summary>
/// <remarks>
/// <para>
/// After the eight-byte "StuffIt!" signature the archive is a sequence of elements, each read as a
/// least-significant-bit-first bit stream: a flag bit, the element type, attribute pairs and
/// algorithm pairs, all as "P2" numbers (see <see cref="StuffItXInput.ReadP2"/>). Files and
/// directories carry an object id and a parent id; forks bind a file to a slice of a solid data
/// stream (type 0 = data fork, 1 = resource fork, others such as previews are not exposed); data
/// elements hold the streams as length-prefixed blocks followed by an optional CRC-32 of the
/// decoded stream; a Brimstone-compressed catalog element holds names, dates and Finder info.
/// </para>
/// <para>
/// Supported stream codings are none, Brimstone (method 0), Cyanide (1), the RC4-obscured stored
/// method (5) and Iron (6). Darkhorse (2), Deflate (3), Blend (4), method 7, the English and x86
/// preprocessors and encrypted streams are listed but not extracted.
/// </para>
/// <para>Converted from XADMaster's <c>XADStuffItXParser.m</c>, <c>StuffItXUtilities.m</c>,
/// <c>XADStuffItXBlockHandle.m</c> and <c>XADRC4Handle.m</c> (LGPL-2.1-or-later, see
/// THIRD-PARTY-NOTICE.XADMaster.txt).</para>
/// </remarks>
public sealed class StuffItXReader : IDisposable {
  private static readonly byte[] Signature = "StuffIt"u8.ToArray();

  private const int ElementEnd = 0;
  private const int ElementData = 1;
  private const int ElementFile = 2;
  private const int ElementFork = 3;
  private const int ElementDirectory = 4;
  private const int ElementCatalog = 5;
  private const int ElementClue = 6;
  private const int ElementRoot = 7;
  private const int ElementBoundary = 8;
  private const int ElementUnknown9 = 9;

  private const int ForkData = 0;
  private const int ForkResource = 1;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly byte[] _data;
  private readonly List<StuffItXEntry> _entries = [];
  private readonly Dictionary<long, DataStream> _streams = [];
  private readonly Dictionary<long, byte[]> _decoded = [];

  /// <summary>Opens a StuffIt X archive and reads its directory.</summary>
  /// <exception cref="InvalidDataException">The data is not a well-formed StuffIt X archive.</exception>
  /// <exception cref="NotSupportedException">The archive uses a base-N text encoding or an unknown catalog tag.</exception>
  public StuffItXReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    using (var ms = new MemoryStream()) {
      stream.CopyTo(ms);
      this._data = ms.ToArray();
    }
    this.Parse();
  }

  /// <summary>The archive's files, directories and resource forks.</summary>
  public IReadOnlyList<StuffItXEntry> Entries => this._entries;

  /// <summary>Whether <paramref name="data"/> starts with a StuffIt X signature.</summary>
  public static bool IsStuffItX(ReadOnlySpan<byte> data)
    => data.Length >= 8 && data[..7].SequenceEqual(Signature) && data[7] is (byte)'!' or (byte)'?';

  /// <summary>Decodes one entry.</summary>
  /// <exception cref="NotSupportedException">The entry's stream uses a method this reader does not decode.</exception>
  /// <exception cref="InvalidDataException">The stream is corrupt or fails its CRC-32.</exception>
  public byte[] Extract(StuffItXEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.StreamId < 0 || entry.OriginalSize == 0) return [];
    var stream = this.DecodeStream(entry.StreamId);
    if (entry.StreamOffset + entry.OriginalSize > stream.Length) throw new InvalidDataException($"StuffIt X fork '{entry.FullPath}' runs past its stream.");
    return stream.AsSpan((int)entry.StreamOffset, (int)entry.OriginalSize).ToArray();
  }

  /// <summary>Ids of every data stream, including the ones only previews refer to.</summary>
  internal IEnumerable<long> StreamIds => this._streams.Keys;

  /// <summary>Decodes a whole data stream and checks its CRC-32 where the archive stores one.</summary>
  internal byte[] DecodeStream(long id) {
    if (this._decoded.TryGetValue(id, out var cached)) return cached;
    if (!this._streams.TryGetValue(id, out var stream)) throw new InvalidDataException($"StuffIt X data stream {id} is missing.");
    var decoded = this.Decode(stream.Element, stream.Length);
    if (decoded.Length < stream.Length) throw new InvalidDataException($"StuffIt X data stream {id} is truncated.");
    if (stream.Element.Algorithms[1] == 0 && stream.Crc is { } crc && Crc32.Compute(decoded.AsSpan(0, (int)stream.Length)) != crc)
      throw new InvalidDataException($"StuffIt X data stream {id} fails its CRC-32.");
    this._decoded[id] = decoded;
    return decoded;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._leaveOpen) this._stream.Dispose();
  }

  // ── element stream ───────────────────────────────────────────────────────────

  private sealed class Element {
    public int Type;
    public readonly long[] Attributes = [-1, -1, -1, -1, -1, -1, -1, -1, -1, -1];
    public readonly long[] Algorithms = [-1, -1, -1, -1, -1, -1];
    public int DataOffset;
  }

  private sealed record Fork(List<long> Files, long Type, long Length);

  private sealed class DataStream(Element element, uint? crc, long compressedSize) {
    public Element Element { get; } = element;
    public uint? Crc { get; } = crc;
    public long CompressedSize { get; } = compressedSize;
    public long Length { get; set; }
  }

  private sealed class Node(long id, long parent, bool isDirectory) {
    public long Id { get; } = id;
    public long Parent { get; } = parent;
    public bool IsDirectory { get; } = isDirectory;
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? Modified { get; set; }
    public DateTime? Created { get; set; }
    public string? Comment { get; set; }
  }

  private static Element ReadElement(StuffItXInput input) {
    var element = new Element();
    input.ReadBit();
    element.Type = input.ReadP2Int();
    for (;;) {
      var key = input.ReadP2();
      if (key == 0) break;
      var value = (long)input.ReadP2();
      if (key <= 10) element.Attributes[key - 1] = value;
    }
    for (;;) {
      var key = input.ReadP2();
      if (key == 0) break;
      var value = (long)input.ReadP2();
      if (key <= 6) element.Algorithms[key - 1] = value;
      if (key == 4) input.ReadP2();
    }
    element.DataOffset = input.Position;
    return element;
  }

  /// <summary>Walks an element's data blocks and trailer; returns the trailer CRC if present.</summary>
  private static uint? ScanElementData(StuffItXInput input, Element element) {
    input.Position = element.DataOffset;
    for (;;) {
      var length = input.ReadP2();
      if (length == 0) break;
      input.Skip((long)length);
    }
    input.Align();
    uint? crc = null;
    var trailer = input.ReadP2();
    if (trailer == 0) return null;
    if (trailer == 4) {
      crc = input.ReadUInt32BigEndian();
      trailer = input.ReadP2();
    }
    while (trailer != 0) {
      input.Skip((long)trailer);
      trailer = input.ReadP2();
    }
    return crc;
  }

  private void Parse() {
    if (this._data.Length < 8 || !this._data.AsSpan(0, 7).SequenceEqual(Signature)) throw new InvalidDataException("Not a StuffIt X archive.");
    if (this._data[7] == '?') throw new NotSupportedException("This StuffIt X archive is base-N text encoded.");
    if (this._data[7] != '!') throw new InvalidDataException("Not a StuffIt X archive.");

    var input = new StuffItXInput(this._data) { Position = 8 };
    var nodes = new List<Node>();
    var nodesById = new Dictionary<long, Node>();
    var streamForks = new Dictionary<long, List<Fork?>>();

    for (var done = false; !done;) {
      if (input.Position >= input.End) throw new InvalidDataException("StuffIt X archive has no end element.");
      var element = ReadElement(input);
      switch (element.Type) {
        case ElementEnd:
          done = true;
          break;
        case ElementData: {
          var crc = ScanElementData(input, element);
          var end = input.Position;
          this._streams[element.Attributes[0]] = new DataStream(element, crc, end - element.DataOffset) { Length = Math.Max(0, element.Attributes[4]) };
          input.Position = end;
          break;
        }
        case ElementFile:
        case ElementDirectory: {
          var node = new Node(element.Attributes[0], element.Attributes[1], element.Type == ElementDirectory);
          nodes.Add(node);
          nodesById[node.Id] = node;
          break;
        }
        case ElementFork: {
          long file = element.Attributes[1], stream = element.Attributes[2], index = element.Attributes[3], length = element.Attributes[4];
          var type = (long)input.ReadP2();
          if (index < 0 || index > 0xFFFF || length < 0) throw new InvalidDataException("StuffIt X fork is not valid.");
          if (!streamForks.TryGetValue(stream, out var forks)) streamForks[stream] = forks = [];
          while (forks.Count <= index) forks.Add(null);
          if (forks[(int)index] is { } existing) {
            if (existing.Length != length) throw new InvalidDataException("StuffIt X forks disagree on a shared stream slice.");
            existing.Files.Add(file);
          } else forks[(int)index] = new Fork([file], type, length);
          break;
        }
        case ElementCatalog: {
          ScanElementData(input, element);
          var end = input.Position;
          var catalog = this.Decode(element, element.Attributes[4]);
          ParseCatalog(new StuffItXInput(catalog), nodes, nodesById);
          input.Position = end;
          break;
        }
        case ElementClue:
          input.Skip(element.Attributes[4]);
          break;
        case ElementRoot:
          input.ReadP2();
          break;
        case ElementBoundary:
        case ElementUnknown9:
          break;
        default:
          if (element.Type > 10) {
            ScanElementData(input, element);
            break;
          }
          throw new NotSupportedException($"StuffIt X element type {element.Type} is not supported.");
      }
      input.Align();
    }

    this.BuildEntries(nodes, nodesById, streamForks);
  }

  private void BuildEntries(List<Node> nodes, Dictionary<long, Node> nodesById, Dictionary<long, List<Fork?>> streamForks) {
    var forksByFile = new Dictionary<long, List<(long Stream, long Offset, Fork Fork)>>();
    foreach (var (streamId, forks) in streamForks) {
      long offset = 0;
      foreach (var fork in forks) {
        if (fork == null) throw new InvalidDataException("StuffIt X stream has a gap between its forks.");
        foreach (var file in fork.Files) {
          if (!forksByFile.TryGetValue(file, out var list)) forksByFile[file] = list = [];
          list.Add((streamId, offset, fork));
        }
        offset += fork.Length;
      }
      // The data element records the decoded size itself. It is the length the CRC-32 covers,
      // and it can exceed the forks' total: a preview fork may claim fewer bytes than its stream holds.
      if (this._streams.TryGetValue(streamId, out var stream)) stream.Length = Math.Max(offset, stream.Element.Attributes[4]);
    }

    foreach (var node in nodes) {
      var path = node.Path.Length > 0 ? node.Path : $"entry{node.Id}";
      var name = node.Name.Length > 0 ? node.Name : path;
      if (node.IsDirectory) {
        this._entries.Add(new StuffItXEntry { Name = name, FullPath = path, IsDirectory = true, LastModified = node.Modified, Created = node.Created, Comment = node.Comment });
        continue;
      }
      forksByFile.TryGetValue(node.Id, out var forks);
      var dataFork = forks?.FirstOrDefault(f => f.Fork.Type == ForkData);
      var resourceFork = forks?.FirstOrDefault(f => f.Fork.Type == ForkResource);
      this._entries.Add(this.MakeEntry(node, name, path, dataFork?.Fork != null ? dataFork : null, false));
      if (resourceFork?.Fork != null) this._entries.Add(this.MakeEntry(node, name + ".rsrc", path + ".rsrc", resourceFork, true));
    }
  }

  private StuffItXEntry MakeEntry(Node node, string name, string path, (long Stream, long Offset, Fork Fork)? fork, bool resource) {
    if (fork is not { } f || !this._streams.TryGetValue(f.Stream, out var stream))
      return new StuffItXEntry { Name = name, FullPath = path, IsResourceFork = resource, LastModified = node.Modified, Created = node.Created, Comment = node.Comment };
    var compressed = stream.Length > 0 ? f.Fork.Length * stream.CompressedSize / stream.Length : 0;
    return new StuffItXEntry {
      Name = name, FullPath = path, IsResourceFork = resource,
      OriginalSize = f.Fork.Length, CompressedSize = compressed,
      Method = MethodName(stream.Element), DataOffset = stream.Element.DataOffset,
      LastModified = node.Modified, Created = node.Created, Comment = node.Comment,
      StreamId = f.Stream, StreamOffset = f.Offset,
    };
  }

  private static string MethodName(Element element) {
    var method = element.Algorithms[0] switch {
      -1 => "Stored",
      0 => "Brimstone",
      1 => "Cyanide",
      2 => "Darkhorse",
      3 => "Deflate",
      4 => "Blend",
      5 => "None",
      6 => "Iron",
      var m => $"Method {m}",
    };
    return element.Algorithms[2] switch {
      -1 => method,
      0 => method + "+English",
      2 => method + "+x86",
      var p => $"{method}+Preprocess {p}",
    };
  }

  // ── stream decoding ──────────────────────────────────────────────────────────

  /// <summary>Joins an element's length-prefixed data blocks.</summary>
  private byte[] ReadBlocks(Element element) {
    var input = new StuffItXInput(this._data) { Position = element.DataOffset };
    var output = new MemoryStream();
    for (;;) {
      var length = input.ReadP2();
      if (length == 0) break;
      output.Write(input.ReadBytes((long)length));
    }
    return output.ToArray();
  }

  private byte[] Decode(Element element, long length) {
    if (element.Algorithms[3] >= 0) throw new NotSupportedException("Encrypted StuffIt X streams are not supported.");
    if (element.Algorithms[2] >= 0) throw new NotSupportedException($"StuffIt X stream preprocessing '{MethodName(element)}' is not supported.");
    if (length < 0 || length > int.MaxValue) throw new InvalidDataException("StuffIt X stream length is out of range.");

    var payload = this.ReadBlocks(element);
    var input = new StuffItXInput(payload);
    switch (element.Algorithms[0]) {
      case -1:
        return payload;
      case 0:
        return StuffItXBrimstone.Decode(input, length);
      case 1:
        return StuffItXCyanide.Decode(input, length);
      case 5:
        return Rc4(payload);
      case 6:
        return StuffItXIron.Decode(input, length);
      default:
        throw new NotSupportedException($"StuffIt X compression method '{MethodName(element)}' is not supported.");
    }
  }

  /// <summary>Method 5: two skipped bytes, a one-byte RC4 key, then the RC4-obscured data.</summary>
  private static byte[] Rc4(byte[] payload) {
    if (payload.Length < 3) throw new InvalidDataException("StuffIt X obscured stream is too short.");
    Span<byte> s = stackalloc byte[256];
    for (var k = 0; k < 256; ++k) s[k] = (byte)k;
    var key = payload[2];
    var j = 0;
    for (var k = 0; k < 256; ++k) {
      j = (j + s[k] + key) & 255;
      (s[k], s[j]) = (s[j], s[k]);
    }
    var output = payload.AsSpan(3).ToArray();
    int x = 0, y = 0;
    for (var n = 0; n < output.Length; ++n) {
      x = (x + 1) & 255;
      y = (y + s[x]) & 255;
      (s[x], s[y]) = (s[y], s[x]);
      output[n] ^= s[(s[x] + s[y]) & 255];
    }
    return output;
  }

  // ── catalog ──────────────────────────────────────────────────────────────────

  private static uint ReadBitsUInt32(StuffItXInput input) {
    var v = 0u;
    for (var i = 0; i < 4; ++i) v = (v << 8) | (uint)input.ReadBits(8);
    return v;
  }

  private static DateTime? ReadFileTime(StuffItXInput input) {
    var v = 0UL;
    for (var i = 0; i < 8; ++i) v = (v << 8) | (uint)input.ReadBits(8);
    return v is > 0 and < 2650467743999999999UL ? DateTime.FromFileTimeUtc((long)v) : null;
  }

  private static string ReadString(StuffItXInput input) {
    var length = input.ReadP2();
    var bytes = input.ReadBytes((long)length);
    input.Align();
    try {
      return new UTF8Encoding(false, true).GetString(bytes);
    } catch (DecoderFallbackException) {
      return Encoding.Latin1.GetString(bytes);
    }
  }

  private static void ParseCatalog(StuffItXInput input, List<Node> nodes, Dictionary<long, Node> nodesById) {
    foreach (var node in nodes) {
      for (;;) {
        var key = input.ReadP2();
        if (key == 0) break;
        switch (key) {
          case 1: {
            var name = ReadString(input).Replace('/', '_');
            node.Name = name;
            node.Path = nodesById.TryGetValue(node.Parent, out var parent) && parent != node && parent.Path.Length > 0 ? parent.Path + "/" + name : name;
            break;
          }
          case 2:
            node.Modified = ReadFileTime(input);
            break;
          case 3:
            ReadBitsUInt32(input);
            break;
          case 4:
          case 5:
            for (var i = 0; i < 32; ++i) input.ReadBits(8);
            break;
          case 6: {
            var hasOwner = input.ReadBits(8);
            ReadBitsUInt32(input);
            if (hasOwner != 0) {
              ReadBitsUInt32(input);
              ReadBitsUInt32(input);
            }
            break;
          }
          case 7:
            input.ReadP2();
            break;
          case 8:
            node.Created = ReadFileTime(input);
            break;
          case 9: {
            var comment = ReadString(input);
            if (comment.Length > 0) node.Comment = comment;
            break;
          }
          case 10: {
            // A list of names (StuffIt 7 for Mac repeats the file name here). The strings start on
            // a byte boundary: XADMaster reads them straight after the count and loses its place.
            var count = input.ReadP2();
            input.Align();
            for (var i = 0UL; i < count; ++i) ReadString(input);
            break;
          }
          case 11:
          case 12:
            ReadString(input);
            break;
          default:
            throw new NotSupportedException($"StuffIt X catalog tag {key} is not supported.");
        }
      }
      input.Align();
    }
  }
}
