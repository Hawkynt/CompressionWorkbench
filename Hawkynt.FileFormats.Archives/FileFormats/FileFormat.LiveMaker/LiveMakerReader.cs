using System.Buffers.Binary;
using Compression.Core.Checksums;
using System.Text;
using FileFormat.Zlib;

namespace FileFormat.LiveMaker;

/// <summary>How a LiveMaker archive member is stored.</summary>
public enum LiveMakerMethod : byte {
  /// <summary>zlib stream.</summary>
  Zlib = 0,
  /// <summary>Stored as is.</summary>
  Stored = 1,
  /// <summary>LiveMaker Pro chunk scramble over the stored bytes.</summary>
  Scrambled = 2,
  /// <summary>LiveMaker Pro chunk scramble over a zlib stream.</summary>
  ScrambledZlib = 3,
}

/// <summary>One member of a LiveMaker archive.</summary>
/// <param name="Name">Path inside the archive, '/'-separated (stored with backslashes).</param>
/// <param name="Offset">Absolute offset of the stored bytes.</param>
/// <param name="StoredLength">Bytes the member occupies.</param>
/// <param name="Method">Storage method.</param>
/// <param name="Unknown">The directory's unexplained per-file word (LiveMaker writes 0).</param>
/// <param name="Checksum">CRC-32 of the stored bytes (of the decoded bytes when <paramref name="EncryptFlag"/> is set).</param>
/// <param name="EncryptFlag">Non-zero when LiveMaker Pro encrypted the member.</param>
public sealed record LiveMakerEntry(string Name, long Offset, long StoredLength, LiveMakerMethod Method, uint Unknown, uint Checksum, byte EncryptFlag);

/// <summary>
/// Reads a LiveMaker (Live Novel / LiveMaker 2-3) "VF" resource archive: the standalone
/// <c>game.dat</c> layout and the same archive appended to an executable (found through the
/// <c>offset + "lv"</c> trailer).
/// </summary>
/// <remarks>
/// <para>Directory (little-endian): "vf", UInt32 version (100-102), UInt32 count; count names as UInt32
/// length + bytes (CP932), XORed with the low byte of a TpRandom keystream (seed 0x75D6EE39,
/// key = key*5 + seed); count+1 offsets as two UInt32 halves relative to the archive start, the low
/// half XORed with a keystream restarted for the offsets and, from version 101, the high half XORed
/// with that key's sign extension; then per member a Byte method, a UInt32 unknown word, a UInt32
/// CRC-32 checksum and a Byte encryption flag. Member data follow the directory.</para>
/// <para>Converted from GARbro's <c>ArcFormats/LiveMaker/ArcVF.cs</c> (MIT, morkt), including the
/// LiveMaker Pro chunk scramble; the trailing three per-member arrays and the checksum rule were
/// measured on a save archive LiveMaker itself wrote. See THIRD-PARTY-NOTICE.GARbro.txt.</para>
/// </remarks>
public sealed class LiveMakerReader {

  /// <summary>Seed of LiveMaker 3's TpRandom keystream.</summary>
  public const uint XorSeed = 0x75D6EE39;

  /// <summary>Lowest and highest archive versions read.</summary>
  public const uint MinVersion = 100, MaxVersion = 102;

  private static readonly Encoding NameEncoding = CreateNameEncoding();

  private readonly Stream _stream;

  /// <summary>Parses the archive directory.</summary>
  /// <exception cref="InvalidDataException">The stream holds no well-formed LiveMaker archive.</exception>
  public LiveMakerReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    var length = stream.Length;
    this.BaseOffset = FindArchive(stream);
    stream.Position = this.BaseOffset + 2;
    this.Version = this.ReadUInt32();
    if (this.Version is < MinVersion or > MaxVersion)
      throw new InvalidDataException($"LiveMaker archive version {this.Version} is not 100 to 102.");
    var count = this.ReadUInt32();
    if (count > (length - stream.Position) / 22)
      throw new InvalidDataException("LiveMaker directory claims more members than the file can hold.");

    var names = new string[count];
    var key = new TpRandom(XorSeed);
    for (var i = 0; i < count; ++i) {
      var len = this.ReadUInt32();
      if (len == 0 || len > 0x1000 || len > length - stream.Position)
        throw new InvalidDataException("LiveMaker member name length is out of range.");
      var bytes = new byte[len];
      stream.ReadExactly(bytes);
      for (var j = 0; j < bytes.Length; ++j)
        bytes[j] ^= (byte)key.Next();
      names[i] = NameEncoding.GetString(bytes).Replace('\\', '/');
    }

    var offsets = new long[count + 1];
    key = new TpRandom(XorSeed);
    for (var i = 0; i <= count; ++i) {
      var k = key.Next();
      var low = this.ReadUInt32() ^ k;
      var high = this.ReadUInt32();
      if (this.Version >= 101)
        high ^= (k & 0x80000000) != 0 ? 0xFFFFFFFFu : 0u;
      offsets[i] = (long)(((ulong)high << 32) | low);
    }

    var methods = new byte[count];
    stream.ReadExactly(methods);
    var unknown = new uint[count];
    for (var i = 0; i < count; ++i) unknown[i] = this.ReadUInt32();
    var checksums = new uint[count];
    for (var i = 0; i < count; ++i) checksums[i] = this.ReadUInt32();
    var encrypt = new byte[count];
    stream.ReadExactly(encrypt);
    this.DirectoryEnd = stream.Position;

    var entries = new List<LiveMakerEntry>((int)count);
    for (var i = 0; i < count; ++i) {
      if (methods[i] > 3)
        throw new InvalidDataException($"LiveMaker member '{names[i]}' uses unknown method {methods[i]}.");
      var start = this.BaseOffset + offsets[i];
      var size = offsets[i + 1] - offsets[i];
      if (offsets[i] < 0 || size < 0 || start < this.DirectoryEnd || start + size > length)
        throw new InvalidDataException($"LiveMaker member '{names[i]}' points outside the archive.");
      entries.Add(new LiveMakerEntry(names[i], start, size, (LiveMakerMethod)methods[i], unknown[i], checksums[i], encrypt[i]));
    }
    this.Entries = entries;
  }

  /// <summary>Where the archive starts (0, or past the executable it is appended to).</summary>
  public long BaseOffset { get; }

  /// <summary>Archive version (100-102).</summary>
  public uint Version { get; }

  /// <summary>Absolute offset just past the directory.</summary>
  public long DirectoryEnd { get; }

  /// <summary>Members in directory order.</summary>
  public IReadOnlyList<LiveMakerEntry> Entries { get; }

  /// <summary>Returns a member's bytes exactly as stored.</summary>
  public byte[] ReadStored(LiveMakerEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var data = new byte[entry.StoredLength];
    this._stream.Position = entry.Offset;
    this._stream.ReadExactly(data);
    return data;
  }

  /// <summary>Decodes a member, checking its CRC-32.</summary>
  /// <exception cref="InvalidDataException">The checksum does not match or the stream does not decode.</exception>
  public byte[] Extract(LiveMakerEntry entry) {
    var stored = this.ReadStored(entry);
    if (entry.EncryptFlag == 0 && Crc32.Compute(stored) != entry.Checksum)
      throw new InvalidDataException($"LiveMaker member '{entry.Name}' fails its CRC-32 check.");
    var data = entry.Method switch {
      LiveMakerMethod.Stored => stored,
      LiveMakerMethod.Zlib => ZlibStream.Decompress(stored),
      LiveMakerMethod.Scrambled => Unscramble(stored),
      _ => ZlibStream.Decompress(Unscramble(stored)),
    };
    if (entry.EncryptFlag != 0 && Crc32.Compute(data) != entry.Checksum)
      throw new InvalidDataException($"LiveMaker member '{entry.Name}' fails its CRC-32 check.");
    return data;
  }

  /// <summary>Undoes LiveMaker Pro's chunk scramble: an 8-byte header (chunk size, seed) and shuffled chunks.</summary>
  public static byte[] Unscramble(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    if (data.Length <= 8)
      return [];
    var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    var seed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) ^ 0xF8EAu;
    var body = data.Length - 8;
    if (chunkSize <= 0)
      throw new InvalidDataException("LiveMaker scrambled member has a non-positive chunk size.");
    var count = (body - 1) / chunkSize + 1;
    var output = new byte[body];
    var dst = 0;
    foreach (var chunk in ChunkOrder(count, seed)) {
      var position = chunk * chunkSize;
      var len = Math.Min(chunkSize, body - position);
      Array.Copy(data, 8 + position, output, dst, len);
      dst += len;
    }
    return output;
  }

  private static int[] ChunkOrder(int count, uint seed) {
    var rng = new TpScramble(seed);
    var order = Enumerable.Range(0, count).ToList();
    var seq = new int[count];
    for (var i = 0; order.Count > 1; ++i) {
      var n = rng.NextInt(0, order.Count - 2);
      seq[order[n]] = i;
      order.RemoveAt(n);
    }
    seq[order[0]] = count - 1;
    return seq;
  }

  private static long FindArchive(Stream stream) {
    var length = stream.Length;
    Span<byte> head = stackalloc byte[2];
    stream.Position = 0;
    if (length >= 10 && stream.Read(head) == 2 && head[0] == (byte)'v' && head[1] == (byte)'f')
      return 0;
    if (length >= 16) {
      Span<byte> tail = stackalloc byte[6];
      stream.Position = length - 6;
      stream.ReadExactly(tail);
      if (tail[4] == (byte)'l' && tail[5] == (byte)'v') {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail);
        if (offset < length - 16) {
          stream.Position = offset;
          if (stream.Read(head) == 2 && head[0] == (byte)'v' && head[1] == (byte)'f')
            return offset;
        }
      }
    }
    throw new InvalidDataException("Not a LiveMaker archive (no \"vf\" directory).");
  }

  private uint ReadUInt32() {
    Span<byte> b = stackalloc byte[4];
    this._stream.ReadExactly(b);
    return BinaryPrimitives.ReadUInt32LittleEndian(b);
  }

  internal static Encoding CreateNameEncoding() {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
  }

  internal static Encoding Names => NameEncoding;

  /// <summary>LiveMaker's TpRandom keystream.</summary>
  internal sealed class TpRandom(uint seed) {
    private uint _current;

    public uint Next() {
      this._current = unchecked(this._current * 5 + seed);
      return this._current;
    }
  }

  /// <summary>LiveMaker's TpScramble generator (a five-word multiply-with-carry), used for chunk order.</summary>
  private sealed class TpScramble {
    private const uint FactorA = 2111111111, FactorB = 1492, FactorC = 1776, FactorD = 5115;
    private readonly uint[] _state = new uint[5];

    public TpScramble(uint seed) {
      var hash = seed != 0 ? seed : 0xFFFFFFFFu;
      for (var i = 0; i < 5; ++i) {
        hash ^= hash << 13;
        hash ^= hash >> 17;
        hash ^= hash << 5;
        this._state[i] = hash;
      }
      for (var i = 0; i < 19; ++i)
        this.NextUInt32();
    }

    public int NextInt(int first, int last) {
      var num = (double)this.NextUInt32() / 0x100000000L;
      return (int)(first + (long)(num * (last - first + 1)));
    }

    private uint NextUInt32() {
      var v = FactorA * (ulong)this._state[3] + FactorB * (ulong)this._state[2] + FactorC * (ulong)this._state[1]
        + FactorD * (ulong)this._state[0] + this._state[4];
      this._state[3] = this._state[2];
      this._state[2] = this._state[1];
      this._state[1] = this._state[0];
      this._state[4] = (uint)(v >> 32);
      return this._state[0] = (uint)v;
    }
  }
}
