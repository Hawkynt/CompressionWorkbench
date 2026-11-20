using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.EcSbx;

/// <summary>
/// Writes error-correcting SeqBox containers as blkar lays them out (SBX_FORMAT.md, and blkar's
/// encoder for what the specification leaves open; MIT): 1 + N copies of the metadata block, the file
/// in data blocks grouped into Reed-Solomon sets of M data and N parity blocks (a short last set
/// filled with padding blocks of 0x1A), all of it interleaved in super block sets of (M + N) · B
/// blocks so that a burst of up to B consecutive lost blocks costs each set at most one block.
/// Parity is a systematic Reed-Solomon code over GF(2^8) (polynomial 0x11D): the Vandermonde
/// matrix V[r][c] = r^c of M + N rows, multiplied by the inverse of its top M rows.
/// </summary>
public static class EcSbxWriter {
  private const int HeaderSize = 16;
  private const byte Padding = 0x1A;

  /// <summary>Creates a container for <paramref name="data"/>.</summary>
  /// <param name="data">The file to protect.</param>
  /// <param name="fileName">Its name (stored as FNM).</param>
  /// <param name="containerName">The container's own name (SNM).</param>
  /// <param name="modified">The file's modification time (FDT), if known.</param>
  /// <param name="version">17 (512-byte blocks), 18 (128) or 19 (4096).</param>
  /// <param name="dataShards">Data blocks per Reed-Solomon set (1-128).</param>
  /// <param name="parityShards">Parity blocks per set (1-128, at most 256 together).</param>
  /// <param name="burst">Burst resistance: consecutive lost blocks each set survives (0: no interleaving).</param>
  /// <param name="uid">The 6-byte file UID; random when null.</param>
  /// <param name="created">The container's creation time (SDT); now when null.</param>
  public static byte[] Create(ReadOnlySpan<byte> data, string fileName, string containerName, DateTime? modified = null,
      int version = 17, int dataShards = 10, int parityShards = 2, int burst = 12, byte[]? uid = null, DateTime? created = null) {
    var blockSize = EcSbxFormatDescriptor.BlockSize(version);
    if (dataShards is < 1 or > 128 || parityShards is < 1 or > 128 || dataShards + parityShards > 256)
      throw new ArgumentOutOfRangeException(nameof(dataShards), "EC-SBX: 1-128 data and 1-128 parity shards, at most 256 together.");
    if (burst is < 0 or > 1000)
      throw new ArgumentOutOfRangeException(nameof(burst), "EC-SBX: a burst resistance of 0 to 1000 blocks.");
    if (uid is { Length: not 6 })
      throw new ArgumentException("EC-SBX: the UID is 6 bytes.", nameof(uid));
    uid ??= RandomNumberGenerator.GetBytes(6);
    var payload = blockSize - HeaderSize;
    var setSize = dataShards + parityShards;
    var chunks = (data.Length + payload - 1L) / payload;
    var sets = (chunks + dataShards - 1) / dataShards;
    var lastSeq = sets * setSize;
    if (lastSeq > uint.MaxValue - 1)
      throw new NotSupportedException("EC-SBX: the file needs more blocks than sequence numbers allow.");

    // metadata block, written at every metadata position
    var meta = new byte[blockSize];
    WriteMetadata(meta.AsSpan(HeaderSize), fileName, containerName, data, modified, created ?? DateTime.UtcNow, dataShards, parityShards);
    Seal(meta, version, uid, 0);

    var parityRows = ParityMatrix(dataShards, parityShards);
    var size = ContainerBlocks(lastSeq, dataShards, parityShards, burst) * blockSize;
    if (size > int.MaxValue)
      throw new NotSupportedException("EC-SBX: containers over 2 GiB are not written.");
    var output = new byte[size];
    foreach (var index in MetaIndices(parityShards, burst))
      meta.CopyTo(output, index * blockSize);

    var shards = new byte[setSize][];
    for (var i = 0; i < setSize; ++i) shards[i] = new byte[payload];
    for (var set = 0L; set < sets; ++set) {
      for (var d = 0; d < dataShards; ++d) {
        var chunk = set * dataShards + d;
        var shard = shards[d];
        shard.AsSpan().Fill(Padding);
        if (chunk < chunks) {
          var start = chunk * payload;
          data.Slice((int)start, (int)Math.Min(payload, data.Length - start)).CopyTo(shard);
        }
      }
      for (var p = 0; p < parityShards; ++p) {
        var parity = shards[dataShards + p];
        Array.Clear(parity);
        for (var d = 0; d < dataShards; ++d)
          Gf.MultiplyAdd(parityRows[p, d], shards[d], parity);
      }
      for (var i = 0; i < setSize; ++i) {
        var seq = (uint)(set * setSize + i + 1);
        var at = DataIndex(seq, dataShards, parityShards, burst) * blockSize;
        var block = output.AsSpan((int)at, blockSize);
        shards[i].CopyTo(block[HeaderSize..]);
        Seal(block, version, uid, seq);
      }
    }
    return output;
  }

  // ── layout (blkar's write positions) ───────────────────────────────

  /// <summary>Block indices of the metadata block and its copies: 0, then i·(1 + B) for i = 1..N.</summary>
  public static IEnumerable<long> MetaIndices(int parity, int burst) {
    yield return 0;
    for (var i = 1; i <= parity; ++i)
      yield return (long)i * (1 + burst);
  }

  /// <summary>
  /// The block index of data sequence number <paramref name="seq"/> (1-based): sets are transposed
  /// inside each super block set of (M + N)·B blocks, behind the metadata blocks in front of it.
  /// </summary>
  public static long DataIndex(uint seq, int dataShards, int parityShards, int burst) {
    var index = (long)seq - 1;
    if (burst == 0)
      return 1 + parityShards + index;
    long total = dataShards + parityShards, superSize = total * burst;
    var superIndex = index / superSize;
    var inSuper = index % superSize;
    var subA = inSuper / total;
    var inSubA = inSuper % total;
    var newInSuper = inSubA * burst + subA;
    var metaBefore = superIndex == 0 ? (inSubA < 1 + parityShards ? 1 + inSubA : 1 + parityShards) : 1 + parityShards;
    return metaBefore + superSize * superIndex + newInSuper;
  }

  private static long ContainerBlocks(long lastSeq, int dataShards, int parityShards, int burst) {
    var lastMeta = MetaIndices(parityShards, burst).Max();
    if (lastSeq == 0)
      return lastMeta + 1;
    if (burst == 0)
      return 1 + parityShards + lastSeq;
    var superSize = (long)(dataShards + parityShards) * burst;
    var first = (lastSeq - 1) / superSize * superSize + 1;
    var last = 0L;
    for (var s = first; s <= lastSeq; ++s)
      last = Math.Max(last, DataIndex((uint)s, dataShards, parityShards, burst));
    return Math.Max(last, lastMeta) + 1;
  }

  // ── blocks ─────────────────────────────────────────────────────────

  private static void Seal(Span<byte> block, int version, byte[] uid, uint seq) {
    "SBx"u8.CopyTo(block);
    block[3] = (byte)version;
    uid.CopyTo(block[6..]);
    BinaryPrimitives.WriteUInt32BigEndian(block[12..], seq);
    BinaryPrimitives.WriteUInt16BigEndian(block[4..], EcSbxFormatDescriptor.BlockCrc(block));
  }

  /// <summary>FNM, SNM, FSZ, FDT, SDT, HSH (SHA-256 multihash), RSD, RSP; the rest 0x1A.</summary>
  private static void WriteMetadata(Span<byte> target, string fileName, string containerName, ReadOnlySpan<byte> data,
      DateTime? modified, DateTime created, int dataShards, int parityShards) {
    var fields = new List<(string Id, byte[] Value)> {
      ("FNM", Encoding.UTF8.GetBytes(fileName)),
      ("SNM", Encoding.UTF8.GetBytes(containerName)),
      ("FSZ", BigEndian((ulong)data.Length)),
    };
    if (modified is { } m) fields.Add(("FDT", BigEndian((ulong)new DateTimeOffset(DateTime.SpecifyKind(m, m.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : m.Kind)).ToUnixTimeSeconds())));
    fields.Add(("SDT", BigEndian((ulong)new DateTimeOffset(DateTime.SpecifyKind(created, created.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : created.Kind)).ToUnixTimeSeconds())));
    fields.Add(("HSH", [0x12, 0x20, .. SHA256.HashData(data)]));
    fields.Add(("RSD", [(byte)dataShards]));
    fields.Add(("RSP", [(byte)parityShards]));
    var at = 0;
    foreach (var (id, value) in fields) {
      if (value.Length > 255 || at + 4 + value.Length > target.Length)
        throw new ArgumentException($"EC-SBX: the metadata ({id}) does not fit a {target.Length + HeaderSize}-byte block; use shorter names or a larger version.");
      Encoding.ASCII.GetBytes(id).CopyTo(target[at..]);
      target[at + 3] = (byte)value.Length;
      value.CopyTo(target[(at + 4)..]);
      at += 4 + value.Length;
    }
    target[at..].Fill(Padding);
  }

  private static byte[] BigEndian(ulong value) {
    var b = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(b, value);
    return b;
  }

  // ── Reed-Solomon ───────────────────────────────────────────────────

  /// <summary>The parity rows of the systematic matrix V · (top M rows of V)^-1, V[r][c] = r^c.</summary>
  internal static byte[,] ParityMatrix(int dataShards, int parityShards) {
    var total = dataShards + parityShards;
    var v = new byte[total, dataShards];
    for (var r = 0; r < total; ++r)
      for (var c = 0; c < dataShards; ++c)
        v[r, c] = Gf.Power((byte)r, c);
    var inverse = Gf.Invert(v, dataShards);
    var result = new byte[parityShards, dataShards];
    for (var p = 0; p < parityShards; ++p)
      for (var c = 0; c < dataShards; ++c) {
        byte sum = 0;
        for (var k = 0; k < dataShards; ++k)
          sum ^= Gf.Multiply(v[dataShards + p, k], inverse[k, c]);
        result[p, c] = sum;
      }
    return result;
  }

  private static class Gf {
    private static readonly byte[] Exp = new byte[510];
    private static readonly byte[] Log = new byte[256];

    static Gf() {
      var x = 1;
      for (var i = 0; i < 255; ++i) {
        Exp[i] = Exp[i + 255] = (byte)x;
        Log[x] = (byte)i;
        x <<= 1;
        if ((x & 0x100) != 0) x ^= 0x11D;
      }
    }

    public static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    public static byte Power(byte a, int n) => n == 0 ? (byte)1 : a == 0 ? (byte)0 : Exp[Log[a] * n % 255];

    private static byte Inverse(byte a) => Exp[255 - Log[a]];

    public static void MultiplyAdd(byte c, byte[] source, byte[] target) {
      if (c == 0) return;
      Span<byte> table = stackalloc byte[256];
      for (var v = 1; v < 256; ++v) table[v] = Exp[Log[c] + Log[v]];
      for (var i = 0; i < source.Length; ++i) target[i] ^= table[source[i]];
    }

    /// <summary>The inverse of the top n×n of <paramref name="m"/>, by Gauss-Jordan elimination.</summary>
    public static byte[,] Invert(byte[,] m, int n) {
      var a = new byte[n, 2 * n];
      for (var r = 0; r < n; ++r) {
        for (var c = 0; c < n; ++c) a[r, c] = m[r, c];
        a[r, n + r] = 1;
      }
      for (var col = 0; col < n; ++col) {
        var pivot = col;
        while (pivot < n && a[pivot, col] == 0) ++pivot;
        if (pivot == n) throw new InvalidOperationException("EC-SBX: singular Reed-Solomon matrix.");
        if (pivot != col)
          for (var c = 0; c < 2 * n; ++c) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
        var scale = Inverse(a[col, col]);
        for (var c = 0; c < 2 * n; ++c) a[col, c] = Multiply(a[col, c], scale);
        for (var r = 0; r < n; ++r) {
          if (r == col || a[r, col] == 0) continue;
          var f = a[r, col];
          for (var c = 0; c < 2 * n; ++c) a[r, c] ^= Multiply(f, a[col, c]);
        }
      }
      var result = new byte[n, n];
      for (var r = 0; r < n; ++r)
        for (var c = 0; c < n; ++c) result[r, c] = a[r, n + c];
      return result;
    }
  }
}
