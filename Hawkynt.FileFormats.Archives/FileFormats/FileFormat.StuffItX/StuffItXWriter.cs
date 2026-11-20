using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.StuffItX;

/// <summary>How <see cref="StuffItXWriter"/> codes the archive's data stream.</summary>
public enum StuffItXCompression {
  /// <summary>Stored (no coding).</summary>
  Store,
  /// <summary>Method 3, StuffIt X's Deflate (a six-bit distance-code count in dynamic blocks).</summary>
  Deflate,
}

/// <summary>
/// Writes StuffIt X (.sitx) archives: one solid data stream holding every file's data fork,
/// stored or Deflate-coded and CRC-32 checked, and a stored catalog of names and dates.
/// </summary>
/// <remarks>
/// <para>
/// Elements, in the order The Unarchiver needs them: root, boundary, a file or directory element
/// per object (object id, parent id, file index), the catalog, the forks binding each file to its
/// slice of the stream, the data element and the end. Every element is a flag bit, a P2 type, P2
/// attribute pairs and P2 algorithm pairs, each list ended by 0; data and catalog elements then
/// hold P2-length-prefixed blocks from the next byte, a 0, and a trailer whose entry of length 4 is
/// the big-endian CRC-32 of the decoded stream.
/// </para>
/// <para>The layout is the one <see cref="StuffItXReader"/> reads (converted from XADMaster), written
/// back; archives are verified by extracting them with The Unarchiver.</para>
/// </remarks>
public static class StuffItXWriter {
  private const int ElementEnd = 0, ElementData = 1, ElementFile = 2, ElementFork = 3, ElementDirectory = 4;
  private const int ElementCatalog = 5, ElementRoot = 7, ElementBoundary = 8;
  private const int BlockSize = 0x10000;

  /// <summary>An object to write: a file with data, or a directory (null data).</summary>
  public readonly record struct Input(string Path, byte[]? Data, DateTime? LastModified = null);

  /// <summary>Writes a StuffIt X archive of <paramref name="inputs"/>.</summary>
  public static void Write(Stream output, IEnumerable<Input> inputs, StuffItXCompression compression = StuffItXCompression.Deflate) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var (method, encode) = compression switch {
      StuffItXCompression.Store => (-1L, (Func<byte[], byte[]>)(static d => d)),
      StuffItXCompression.Deflate => (3L, EncodeDeflate),
      _ => throw new ArgumentOutOfRangeException(nameof(compression)),
    };
    WriteCoded(output, inputs, method, -1, encode);
  }

  /// <summary>Method 3's payload: the window-size byte 15, then Deflate with a six-bit HDIST.</summary>
  private static byte[] EncodeDeflate(byte[] data) {
    using var ms = new MemoryStream();
    ms.WriteByte(15);
    var deflater = new DeflateCompressor(ms, DeflateCompressionLevel.Default) { DistanceCountBits = 6 };
    deflater.Write(data);
    deflater.Finish();
    return ms.ToArray();
  }

  /// <summary>
  /// Writes an archive whose stream has the given compression and preprocess ids and whose payload
  /// <paramref name="encode"/> makes from the joined file data; the stream's CRC-32 covers that
  /// data as given (or is left out). Lets tests hand The Unarchiver payloads in codings this writer cannot produce.
  /// </summary>
  internal static void WriteCoded(Stream output, IEnumerable<Input> inputs, long method, long preprocess, Func<byte[], byte[]> encode, bool checksum = true) {
    var objects = Objects(inputs);
    var w = new BitWriter();
    w.Bytes("StuffIt!"u8);

    Element(w, ElementRoot, [], []);
    w.P2(0);
    w.Align();
    Element(w, ElementBoundary, [], []);
    w.Align();

    var fileIndex = 0;
    foreach (var o in objects) {
      if (o.Data is null) Element(w, ElementDirectory, [(1, o.Id), (2, o.Parent)], []);
      else Element(w, ElementFile, [(1, o.Id), (2, o.Parent), (7, fileIndex++)], []);
      w.Align();
    }

    var catalog = Catalog(objects);
    Element(w, ElementCatalog, [(1, 1), (5, catalog.Length)], []);
    Blocks(w, catalog);
    w.P2(0); // no trailer
    w.Align();

    const long streamId = 1;
    var stream = new MemoryStream();
    var forkIndex = 0;
    foreach (var o in objects) {
      if (o.Data is not { Length: > 0 } data) continue;
      Element(w, ElementFork, [(2, o.Id), (3, streamId), (4, forkIndex++), (5, data.Length)], []);
      w.P2(0); // data fork
      w.Align();
      stream.Write(data);
    }

    var plain = stream.ToArray();
    List<(int, long)> algorithms = checksum ? [(2, 0)] : []; // 0 = CRC-32
    if (method >= 0) algorithms.Insert(0, (1, method));
    if (preprocess >= 0) algorithms.Add((3, preprocess));
    Element(w, ElementData, [(1, streamId), (5, plain.Length)], algorithms);
    Blocks(w, plain.Length == 0 ? [] : encode(plain));
    if (checksum) {
      w.P2(4);
      w.Align();
      var crc = Crc32.Compute(plain);
      w.Bytes([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }
    w.P2(0);
    w.Align();

    Element(w, ElementEnd, [], []);
    w.Align();
    output.Write(w.ToArray());
  }

  private sealed record Node(long Id, long Parent, string Name, byte[]? Data, DateTime? Modified);

  /// <summary>Objects in writing order with ids from 1 (0 is the root), parents before children.</summary>
  private static List<Node> Objects(IEnumerable<Input> inputs) {
    var nodes = new List<Node>();
    var dirs = new Dictionary<string, long>(StringComparer.Ordinal);
    long Dir(string path, DateTime? time) {
      if (path.Length == 0) return 0;
      if (dirs.TryGetValue(path, out var id)) return id;
      var slash = path.LastIndexOf('/');
      var parent = Dir(slash < 0 ? "" : path[..slash], time);
      id = nodes.Count + 1;
      nodes.Add(new Node(id, parent, path[(slash + 1)..], null, time));
      dirs[path] = id;
      return id;
    }
    foreach (var input in inputs) {
      var path = input.Path.Replace('\\', '/').Trim('/');
      if (path.Length == 0) continue;
      if (input.Data is null) {
        Dir(path, input.LastModified);
        continue;
      }
      var slash = path.LastIndexOf('/');
      var parent = Dir(slash < 0 ? "" : path[..slash], input.LastModified);
      nodes.Add(new Node(nodes.Count + 1, parent, path[(slash + 1)..], input.Data, input.LastModified));
    }
    return nodes;
  }

  /// <summary>Per object: tag 1 the name, tag 2 the modification time (FILETIME, big-endian), 0.</summary>
  private static byte[] Catalog(List<Node> nodes) {
    var w = new BitWriter();
    foreach (var n in nodes) {
      var name = Encoding.UTF8.GetBytes(n.Name);
      w.P2(1);
      w.P2((ulong)name.Length);
      w.Align();
      w.Bytes(name);
      if (n.Modified is { } t) {
        w.P2(2);
        var fileTime = (ulong)t.ToUniversalTime().ToFileTimeUtc();
        for (var i = 7; i >= 0; --i) w.Bits((uint)(byte)(fileTime >> (i * 8)), 8);
      }
      w.P2(0);
      w.Align();
    }
    return w.ToArray();
  }

  private static void Element(BitWriter w, int type, List<(int Key, long Value)> attributes, List<(int Key, long Value)> algorithms) {
    w.Bits(1, 1);
    w.P2((ulong)type);
    foreach (var (k, v) in attributes) {
      w.P2((ulong)k);
      w.P2((ulong)v);
    }
    w.P2(0);
    foreach (var (k, v) in algorithms) {
      w.P2((ulong)k);
      w.P2((ulong)v);
    }
    w.P2(0);
  }

  /// <summary>The data of an element: from the next byte, P2 length and bytes per block, then 0.</summary>
  private static void Blocks(BitWriter w, byte[] data) {
    w.Align();
    for (var offset = 0; offset < data.Length; offset += BlockSize) {
      var length = Math.Min(BlockSize, data.Length - offset);
      w.P2((ulong)length);
      w.Align();
      w.Bytes(data.AsSpan(offset, length));
    }
    w.P2(0);
    w.Align();
  }

  /// <summary>Least-significant-bit-first bit writer with the "P2" number coding.</summary>
  private sealed class BitWriter {
    private readonly MemoryStream _out = new();
    private int _byte, _count;

    public void Bits(uint value, int count) {
      for (var i = 0; i < count; ++i) {
        this._byte |= (int)((value >> i) & 1) << this._count;
        if (++this._count == 8) this.Flush();
      }
    }

    private void Flush() {
      this._out.WriteByte((byte)this._byte);
      this._byte = this._count = 0;
    }

    public void Align() {
      if (this._count > 0) this.Flush();
    }

    public void Bytes(ReadOnlySpan<byte> bytes) {
      this.Align();
      this._out.Write(bytes);
    }

    /// <summary>
    /// v + 1 = x with n one-bits: n − 1 one-bits and a zero, then x's bits from bit 0 up to its
    /// highest one.
    /// </summary>
    public void P2(ulong value) {
      var x = value + 1;
      var ones = System.Numerics.BitOperations.PopCount(x);
      for (var i = 1; i < ones; ++i) this.Bits(1, 1);
      this.Bits(0, 1);
      var top = 63 - System.Numerics.BitOperations.LeadingZeroCount(x);
      for (var i = 0; i <= top; ++i) this.Bits((uint)((x >> i) & 1), 1);
    }

    public byte[] ToArray() {
      this.Align();
      return this._out.ToArray();
    }
  }
}
