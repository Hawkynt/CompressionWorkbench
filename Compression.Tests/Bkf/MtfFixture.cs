using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Bkf;

/// <summary>
/// MTF 1.00a media built field by field from the specification (§4-§6), independently of the
/// writer under test: TAPE, SFMB, then per Data Set SSET, the given blocks, SFMB, ESET, SFMB.
/// </summary>
internal sealed class MtfFixture(int flb = 1024, ushort mediaBasedCatalog = 0) {
  private readonly MemoryStream _ms = new();
  private readonly List<Action> _sets = [];

  public static void Seal(Span<byte> header) {
    ushort x = 0;
    for (var i = 0; i < 50; i += 2) x ^= BinaryPrimitives.ReadUInt16LittleEndian(header[i..]);
    BinaryPrimitives.WriteUInt16LittleEndian(header[50..], x);
  }

  public void Set(int number, Action<SetBuilder> fill) => this._sets.Add(() => {
    var s = new SetBuilder(this, number);
    this.Dblk("SSET", 98, 2, d => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(62), (ushort)number), []);
    fill(s);
    this.Filemark();
    this.Dblk("ESET", 85, 0, d => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(78), (ushort)number), []);
    this.Filemark();
  });

  public byte[] Build() {
    this.Dblk("TAPE", 94, 2, d => {
      BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(56), 1);          // soft filemarks
      BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(64), (ushort)(flb / 512));
      BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(66), mediaBasedCatalog);
      BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(84), (ushort)flb);
      d[93] = 1;
    }, []);
    this.Filemark();
    foreach (var set in this._sets) set();
    return this._ms.ToArray();
  }

  private void Filemark() {
    var b = new byte[flb];
    Encoding.ASCII.GetBytes("SFMB", b);
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)flb);
    Seal(b);
    this._ms.Write(b);
  }

  /// <summary>A DBLK of the given fixed size, then its streams (each 4-aligned), then SPAD to the block.</summary>
  public void Dblk(string type, int fixedSize, byte stringType, Action<byte[]> fill, IEnumerable<(string Id, byte[] Data, ushort Media, bool BadStreamChecksum)> streams,
      params (int At, string Text)[] strings) {
    var encoded = strings.Select(s => (s.At, Bytes: stringType == 1 ? Encoding.Latin1.GetBytes(s.Text) : Encoding.Unicode.GetBytes(s.Text))).ToList();
    var size = fixedSize + encoded.Sum(e => e.Bytes.Length);
    var d = new byte[(size + 3) / 4 * 4];
    Encoding.ASCII.GetBytes(type, d);
    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), (ushort)d.Length);
    d[10] = 14;
    d[48] = stringType;
    var at = fixedSize;
    foreach (var (field, bytes) in encoded) {
      BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(field), (ushort)bytes.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(field + 2), (ushort)at);
      bytes.CopyTo(d, at);
      at += bytes.Length;
    }
    fill(d);
    Seal(d);
    this._ms.Write(d);
    foreach (var (id, data, media, bad) in streams) {
      this._ms.Write(StreamHeader(id, data.Length, media, bad));
      this._ms.Write(data);
      this._ms.Write(new byte[(4 - this._ms.Length % 4) % 4]);
    }
    var end = this._ms.Length + 22;
    var boundary = (end + flb - 1) / flb * flb;
    this._ms.Write(StreamHeader("SPAD", boundary - end, 0, false));
    this._ms.Write(new byte[boundary - end]);
  }

  /// <summary>A block that is no DBLK: a type-like start whose header checksum does not hold.</summary>
  public void Garbage() {
    var b = Enumerable.Range(0, flb).Select(i => (byte)(i * 7 + 3)).ToArray();
    Encoding.ASCII.GetBytes("FILE", b);
    this._ms.Write(b);
  }

  private static byte[] StreamHeader(string id, long length, ushort media, bool bad) {
    var h = new byte[22];
    Encoding.ASCII.GetBytes(id, h);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), media);
    BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8), (ulong)length);
    ushort x = 0;
    for (var i = 0; i < 20; i += 2) x ^= BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(i));
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), (ushort)(bad ? x ^ 1 : x));
    return h;
  }

  public sealed class SetBuilder(MtfFixture m, int number) {
    public SetBuilder Volb(string device, bool ansi = false) {
      m.Dblk("VOLB", 73, ansi ? (byte)1 : (byte)2, d => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), 4), [], (56, device));
      _ = number;
      return this;
    }

    /// <summary>A NACL (Windows NT security) stream with its CSUM, as NTBackup writes before the data.</summary>
    public static (string, byte[], ushort, bool)[] Security(byte seed) {
      var acl = Enumerable.Range(0, 44).Select(i => (byte)(i * 13 + seed)).ToArray();
      var sum = new byte[4];
      uint x = 0;
      for (var i = 0; i < 44; i += 4) x ^= BinaryPrimitives.ReadUInt32LittleEndian(acl.AsSpan(i));
      BinaryPrimitives.WriteUInt32LittleEndian(sum, x);
      return [("NACL", acl, (ushort)0x20, false), ("CSUM", sum, (ushort)0, false)];
    }

    public SetBuilder Dirb(string name, bool inStream = false, bool security = false, uint attributes = 0) {
      var streams = new List<(string, byte[], ushort, bool)>();
      if (inStream) streams.Add(("PNAM", Encoding.Unicode.GetBytes(name), (ushort)0, false));
      if (security) streams.AddRange(Security(7));
      var attr = attributes | (inStream ? 1u << 17 : 0);
      if (inStream)
        m.Dblk("DIRB", 84, 2, d => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), attr), streams);
      else
        m.Dblk("DIRB", 84, 2, d => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), attr), streams, (80, name));
      return this;
    }

    public SetBuilder File(string name, byte[] data, bool inStream = false, bool ansi = false, bool badChecksum = false, bool compressed = false,
        bool security = false, uint attributes = 0, DateTime? modified = null) {
      uint sum = 0;
      var padded = data.Concat(new byte[(4 - data.Length % 4) % 4]).ToArray();
      for (var i = 0; i < padded.Length; i += 4) sum ^= BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(i));
      if (badChecksum) sum ^= 0x01010101;
      var csum = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(csum, sum);
      var stringType = ansi ? (byte)1 : (byte)2;
      var streams = new List<(string, byte[], ushort, bool)>();
      if (inStream) streams.Add(("FNAM", ansi ? Encoding.Latin1.GetBytes(name) : Encoding.Unicode.GetBytes(name), 0, false));
      if (security) streams.AddRange(Security(11));
      streams.Add(("STAN", data, (ushort)(compressed ? 0x30 : 0x20), false));
      streams.Add(("CSUM", csum, 0, false));
      var attr = attributes | (inStream ? 1u << 17 : 0);
      void Fill(byte[] d) {
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(52), attr);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(12), (ulong)data.Length);
        if (modified is { } t) {
          var v = ((ulong)(uint)t.Year << 26) | ((ulong)(uint)t.Month << 22) | ((ulong)(uint)t.Day << 17) | ((ulong)(uint)t.Hour << 12) | ((ulong)(uint)t.Minute << 6) | (uint)t.Second;
          for (var i = 4; i >= 0; --i, v >>= 8) d[56 + i] = (byte)v;
        }
      }
      if (inStream)
        m.Dblk("FILE", 88, stringType, Fill, streams);
      else
        m.Dblk("FILE", 88, stringType, Fill, streams, (84, name));
      return this;
    }

    public SetBuilder Garbage() {
      m.Garbage();
      return this;
    }
  }
}
