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
  /// <summary>Method 2, Darkhorse: LZ77 with adaptive binary range coding.</summary>
  Darkhorse,
  /// <summary>Method 0, Brimstone: PPMd variant G (order 8, 16 MiB model).</summary>
  Brimstone,
  /// <summary>Method 1, Cyanide: Burrows-Wheeler blocks with range-coded "move one from front" ranks.</summary>
  Cyanide,
  /// <summary>Method 4, Blend: 64 KiB segments, each Darkhorse, Cyanide or Brimstone, whichever is smallest.</summary>
  Blend,
  /// <summary>Method 5: the data RC4-obscured under a one-byte key (no compression).</summary>
  Obscured,
  /// <summary>Method 6, Iron: Burrows-Wheeler blocks whose recency-list runs are coded with adaptive binary weights.</summary>
  Iron,
}

/// <summary>The filter <see cref="StuffItXWriter"/> runs in front of the coder.</summary>
public enum StuffItXPreprocess {
  /// <summary>No filter.</summary>
  None,
  /// <summary>Preprocess 0: dictionary words replaced by word numbers, with a sentence-start case model.</summary>
  English,
  /// <summary>Preprocess 2: x86 relative call and jump operands made absolute.</summary>
  X86,
}

/// <summary>
/// Writes StuffIt X (.sitx) archives: one solid data stream holding every file's data fork,
/// stored or coded (Deflate, Brimstone, Darkhorse, Cyanide, Blend, Obscured or Iron, optionally behind
/// the English or x86 preprocessor) and CRC-32 checked, and a stored catalog of names and dates.
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
  /// <param name="preprocess">The filter run in front of the coder.</param>
  public static void Write(Stream output, IEnumerable<Input> inputs, StuffItXCompression compression = StuffItXCompression.Deflate, StuffItXPreprocess preprocess = StuffItXPreprocess.None) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var (method, encode) = compression switch {
      StuffItXCompression.Store => (-1L, (Func<byte[], byte[]>)(static d => d)),
      StuffItXCompression.Deflate => (3L, EncodeDeflate),
      StuffItXCompression.Darkhorse => (2L, static d => StuffItXDarkhorseEncoder.Encode(d)),
      StuffItXCompression.Brimstone => (0L, static d => StuffItXBrimstone.Encode(d)),
      StuffItXCompression.Cyanide => (1L, static d => StuffItXCyanideEncoder.Encode(d)),
      StuffItXCompression.Blend => (4L, EncodeBlend),
      StuffItXCompression.Obscured => (5L, EncodeObscured),
      StuffItXCompression.Iron => (6L, static d => StuffItXIronEncoder.Encode(d)),
      _ => throw new ArgumentOutOfRangeException(nameof(compression)),
    };
    // the English filter changes the length, so its coder must end by itself; Brimstone runs to a length
    if (preprocess == StuffItXPreprocess.English && compression == StuffItXCompression.Brimstone)
      throw new NotSupportedException("StuffIt X's English preprocessor needs a coder that marks its own end; Brimstone does not.");
    switch (preprocess) {
      case StuffItXPreprocess.None: WriteCoded(output, inputs, method, -1, encode); break;
      case StuffItXPreprocess.English: WriteCoded(output, inputs, method, 0, d => encode(StuffItXEnglishEncoder.Encode(d))); break;
      case StuffItXPreprocess.X86: WriteCoded(output, inputs, method, 2, d => encode(StuffItXX86.Encode(d))); break;
      default: throw new ArgumentOutOfRangeException(nameof(preprocess));
    }
  }

  /// <summary>
  /// Method 4's payload: segments of 64 KiB (a multiple of the 0x2000 the reader's resynchronisation
  /// trusts), each behind its six-byte header (0x77, coding, big-endian size) and coded with
  /// whichever of Darkhorse, Cyanide and Brimstone gives the fewest bytes. Each coder's output ends
  /// exactly where its decoder stops, so the next header follows directly.
  /// </summary>
  private static byte[] EncodeBlend(byte[] data) {
    const int Segment = 0x10000;
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[6];
    for (var start = 0; start < data.Length; start += Segment) {
      var part = data.AsSpan(start, Math.Min(Segment, data.Length - start)).ToArray();
      var candidates = new (byte Coding, byte[] Payload)[] {
        (1, StuffItXDarkhorseEncoder.Encode(part)),
        (2, StuffItXCyanideEncoder.Encode(part)),
        (3, StuffItXBrimstone.Encode(part)),
      };
      var best = candidates.MinBy(c => c.Payload.Length);
      header[0] = 0x77;
      header[1] = best.Coding;
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[2..], (uint)part.Length);
      ms.Write(header);
      ms.Write(best.Payload);
    }
    return ms.ToArray();
  }

  /// <summary>Method 5's payload: two unused bytes, the one-byte RC4 key, then the RC4-obscured data.</summary>
  private static byte[] EncodeObscured(byte[] data) {
    const byte Key = 0x5A;
    var output = new byte[data.Length + 3];
    output[2] = Key;
    Span<byte> s = stackalloc byte[256];
    for (var k = 0; k < 256; ++k) s[k] = (byte)k;
    var j = 0;
    for (var k = 0; k < 256; ++k) {
      j = (j + s[k] + Key) & 255;
      (s[k], s[j]) = (s[j], s[k]);
    }
    int x = 0, y = 0;
    for (var n = 0; n < data.Length; ++n) {
      x = (x + 1) & 255;
      y = (y + s[x]) & 255;
      (s[x], s[y]) = (s[y], s[x]);
      output[n + 3] = (byte)(data[n] ^ s[(s[x] + s[y]) & 255]);
    }
    return output;
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
  internal sealed class BitWriter {
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
