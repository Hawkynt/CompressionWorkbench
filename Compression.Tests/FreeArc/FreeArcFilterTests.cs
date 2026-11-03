using System.Reflection;
using System.Security.Cryptography;
using FileFormat.FreeArc;

namespace Compression.Tests.FreeArc;

/// <summary>
/// The rep, delta, exe, 4x4, tor, ppmd and grzip stages against archives FreeArc 0.67 wrote with
/// <c>-mrep:1mb</c>, <c>-mdelta</c>, <c>-mexe</c>, <c>-mgrzip:mN</c>, its default chains and more
/// (ReferenceVectors/mkfilters.py; expected bytes in filters.tsv are what <c>Arc.exe x</c> extracted).
/// </summary>
[TestFixture]
public sealed class FreeArcFilterTests {
  private sealed record Row(string Archive, string Name, long Size, string Sha256);

  private static readonly Lazy<Row[]> Manifest = new(() => {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FreeArcVectors.filters.tsv")!;
    using var reader = new StreamReader(s);
    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r'))
      .Where(l => l.Length > 0 && !l.StartsWith('#'))
      .Select(l => l.Split('\t'))
      .Select(f => new Row(f[0], f[1], long.Parse(f[2]), f[3]))
      .ToArray();
  });

  [TestCase("filters-rep", "rep:153kb")]
  [TestCase("filters-delta", "delta")]
  [TestCase("filters-exe", "exe")]
  [TestCase("filters-mx", "rep:153kb+exe+delta+lzma:153kb:normal:bt4:128")]
  [TestCase("filters-4x4", "4x4:b64kb:lzma:153kb:normal:32")]
  [TestCase("filters-m5x", "exe+delta+4x4:lzma:153kb:normal:32:mc32")]
  [TestCase("tor-1", "tor:1:153kb")]
  [TestCase("tor-2", "tor:2:153kb")]
  [TestCase("tor-3", "tor:3:153kb")]
  [TestCase("tor-5", "tor:153kb:h1mb")]
  [TestCase("tor-12", "tor:12:153kb:h1mb")]
  [TestCase("filters-m1", "rep:153kb:256+4x4:tor:3:153kb")]
  [TestCase("filters-m2", "*")]
  [TestCase("ppmd-6", "ppmd:6:16mb")]
  [TestCase("ppmd-restart", "ppmd:12:256kb")]
  [TestCase("ppmd-cutoff", "ppmd:12:256kb:r")]
  [TestCase("ppmd-freeze", "ppmd:12:256kb:r2")]
  [TestCase("grzip-m1", "grzip:153kb:m1:l32:h15")]
  [TestCase("grzip-m2", "grzip:153kb:m2:l32:h15")]
  [TestCase("grzip-m3", "grzip:153kb:m3:l32:h15")]
  [TestCase("grzip-m4", "grzip:153kb:m4:l32:h15")]
  [TestCase("grzip-strong", "grzip:153kb:m1:l32:h15:s")]
  [TestCase("grzip-nolzp", "grzip:153kb:m3:l")]
  [TestCase("grzip-delta", "grzip:64kb:m2:l32:h15:d")]
  [TestCase("text-m3", "grzip:168kb:m2:l32:h15")]
  [TestCase("text-m4", "grzip:168kb:m1:l32:h15")]
  [TestCase("lzp", "lzp:153kb:64:h18")]
  [TestCase("lzp-short", "lzp:64kb:12:h12:d8kb:s20")]
  [TestCase("lzp-tor", "lzp:153kb:16:h16+tor:3:153kb")]
  [TestCase("dict", "dict:168kb")]
  [TestCase("dict-p", "dict:64kb:l8192:m400:s100")]
  [TestCase("text-m5", "dict:168kb:80%:l8192:m400:s100+lzp:168kb:90%:65:h18:d1mb+4x4:b7mb:ppmd:8:96mb")]
  [TestCase("text-mx", "dict:168kb:80%:l8192:m400:s100+lzp:168kb:92%:145:h18:d1mb+ppmd:16:384mb")]
  [TestCase("text-m9x", "dict:168kb:75%+lzma:168kb:normal:bt4:128")]
  public void GivenFreeArcArchive_WhenExtracted_ThenEveryFileMatchesArc(string vector, string method) {
    var expected = Manifest.Value.Where(r => r.Archive == vector + ".arc").ToArray();
    Assert.That(expected, Is.Not.Empty);
    using var reader = new FreeArcReader(new MemoryStream(FreeArcTests.Vector(vector)));

    if (method != "*")
      Assert.That(reader.Entries.Select(e => e.Method).Distinct(), Is.EqualTo(new[] { method }));
    Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(expected.Select(r => r.Name)));
    Assert.Multiple(() => {
      foreach (var row in expected) {
        var data = reader.Extract(reader.Entries.Single(e => e.Name == row.Name));
        Assert.That(data.LongLength, Is.EqualTo(row.Size), row.Name);
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(row.Sha256), row.Name);
      }
    });
  }

  [Test]
  public void GivenTheDefaultMaximumChain_WhenCheckedForSupport_ThenItIsDecodable()
    => Assert.That(FreeArcMethods.IsSupported("rep:153kb+exe+delta+lzma:153kb:normal:bt4:128"), Is.True);

  [TestCase("4x4:lzma:447kb:fast:32:mc4", true)]
  [TestCase("4x4:b4mb:t2:lzma:1mb", true)]
  [TestCase("4x4:tor:3:144kb", true)]
  [TestCase("4x4:4x4:lzma", false)]
  [TestCase("4x4:b4mb", false)]
  public void GivenAFourByFourStage_WhenCheckedForSupport_ThenItsInnerMethodDecides(string method, bool supported)
    => Assert.That(FreeArcMethods.IsSupported(method), Is.EqualTo(supported));

  [Test]
  public void GivenAFourByFourStreamWithAStoredChunk_WhenDecoded_ThenTheChunkIsCopied() {
    var stream = Words(0, unchecked((int)0xFFFFFFFF), 3).Concat("abc"u8.ToArray()).ToArray();
    Assert.That(FreeArcMethods.Decode("4x4:lzma", stream, 3), Is.EqualTo("abc"u8.ToArray()));
  }

  [Test]
  public void GivenAFourByFourStreamOfAnUnknownVersion_WhenDecoded_ThenItIsRefused() {
    var stream = Words(1, unchecked((int)0xFFFFFFFF), 3).Concat("abc"u8.ToArray()).ToArray();
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("4x4:lzma", stream, 3));
  }

  [Test]
  public void GivenAFourByFourChunkCutShort_WhenDecoded_ThenItIsRefused() {
    var stream = Words(0, unchecked((int)0xFFFFFFFF), 10).Concat("abc"u8.ToArray()).ToArray();
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("4x4:lzma", stream, 10));
  }

  [TestCase("grzip:153kb:m1:l32:h15", true)]
  [TestCase("grzip:64kb:m2:l32:h15:d:t2:i0", true)]
  [TestCase("grzip:m3:l:s:a", true)]
  [TestCase("grzip:8mb:m4:24", true)]
  [TestCase("grzip:m3:x7", false)]
  public void GivenAGrzipStage_WhenCheckedForSupport_ThenOnlyFreeArcsOptionsAreAccepted(string method, bool supported)
    => Assert.That(FreeArcMethods.IsSupported(method), Is.EqualTo(supported));

  [Test]
  public void GivenAGrzipStoredBlock_WhenDecoded_ThenItsBytesAreCopied() {
    var stream = GrzipBlock(size: 3, mode: -1, aux: 0, "abc"u8);
    Assert.That(FreeArcMethods.Decode("grzip", stream, 3), Is.EqualTo("abc"u8.ToArray()));
  }

  [Test]
  public void GivenAGrzipBlockWithAReservedFieldSet_WhenDecoded_ThenItIsRefused() {
    var stream = GrzipBlock(size: 3, mode: -1, aux: 0, "abc"u8);
    stream[24] = 1;
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("grzip", stream, 3));
  }

  [Test]
  public void GivenAGrzipBlockCutShort_WhenDecoded_ThenItIsRefused() {
    var stream = GrzipBlock(size: 3, mode: -1, aux: 0, "abc"u8);
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("grzip", stream[..^1], 3));
  }

  [Test]
  public void GivenAGrzipBlockWhoseSizeDisagreesWithItsData_WhenDecoded_ThenItIsRefused() {
    var stream = GrzipBlock(size: 4, mode: -1, aux: 0, "abc"u8);
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("grzip", stream, 4));
  }

  [TestCase(-2, 9)]
  [TestCase(-3, 0)]
  public void GivenAGrzipBlockOfAnUnknownMode_WhenDecoded_ThenItIsRefused(int mode, int aux) {
    var stream = GrzipBlock(size: 0, mode, aux, []);
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("grzip", stream, 0));
  }

  [Test]
  public void GivenAGrzipArchiveWithOneByteFlipped_WhenExtracted_ThenItNeverYieldsTheWrongBytesSilently() {
    var data = FreeArcTests.Vector("grzip-m2");
    var expected = Manifest.Value.Single(r => r.Archive == "grzip-m2.arc" && r.Name == "text.txt");
    data[data.Length / 2] ^= 0x40;
    try {
      using var reader = new FreeArcReader(new MemoryStream(data));
      var text = reader.Extract(reader.Entries.Single(e => e.Name == "text.txt"));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(text)), Is.Not.EqualTo(expected.Sha256));
    } catch (InvalidDataException) {
      // refused: the block no longer decodes to its recorded size or CRC
    }
  }

  private static byte[] GrzipBlock(int size, int mode, int aux, ReadOnlySpan<byte> payload) {
    var block = new byte[28 + payload.Length];
    BitConverter.TryWriteBytes(block.AsSpan(0), size);
    BitConverter.TryWriteBytes(block.AsSpan(4), mode);
    BitConverter.TryWriteBytes(block.AsSpan(8), aux);
    BitConverter.TryWriteBytes(block.AsSpan(16), payload.Length);
    payload.CopyTo(block.AsSpan(28));
    return block;
  }

  [TestCase("lzp:153kb:64:h18", true)]
  [TestCase("lzp:160mb:145:h23:d1gb", true)]
  [TestCase("lzp:3346kb:90%:65:h22:d1mb:s24", true)]
  [TestCase("lzp:b64kb:l12:h12", true)]
  [TestCase("lzp:h40", false)]
  [TestCase("lzp:x1", false)]
  public void GivenAnLzpStage_WhenCheckedForSupport_ThenFreeArcsParametersAreRead(string method, bool supported)
    => Assert.That(FreeArcMethods.IsSupported(method), Is.EqualTo(supported));

  [Test]
  public void GivenAStoredLzpChunk_WhenDecoded_ThenItsBytesAreCopied() {
    var stream = new byte[] { 0xFD, 0xFF, 0xFF, 0xFF, (byte)'a', (byte)'b', (byte)'c' };
    Assert.That(FreeArcMethods.Decode("lzp:64:h10", stream, 3), Is.EqualTo("abc"u8.ToArray()));
  }

  [Test]
  public void GivenAnLzpBlockOfOnlyItsHead_WhenDecoded_ThenItIsRefused() {
    var stream = new byte[16];
    stream[0] = 12;
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("lzp:64:h10", stream, 12));
  }

  [Test]
  public void GivenAnLzpBlockOfLiterals_WhenDecoded_ThenTheyAreOutputInOrder() {
    // 12 head bytes and four literal tokens; no 0xB5, so no match length is read from the back.
    var body = "0123456789abcdef"u8.ToArray();
    var stream = new byte[] { 16, 0, 0, 0 }.Concat(body).ToArray();
    Assert.That(FreeArcMethods.Decode("lzp:64:h10", stream, 16), Is.EqualTo(body));
  }

  [Test]
  public void GivenAnLzpBlockLargerThanItsBlockSize_WhenDecoded_ThenItIsRefused() {
    var body = "0123456789abcdef"u8.ToArray();
    var stream = new byte[] { 16, 0, 0, 0 }.Concat(body).ToArray();
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("lzp:b15b:64:h10", stream, 16));
  }

  [Test]
  public void GivenAnLzpChunkCutShort_WhenDecoded_ThenItIsRefused() {
    var stream = new byte[] { 20, 0, 0, 0, 1, 2, 3 };
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("lzp:64:h10", stream, 20));
  }

  [TestCase("dict:168kb", true)]
  [TestCase("dict:p:64mb:85%", true)]
  [TestCase("dict:3346kb:80%:l8192:m400:s100", true)]
  [TestCase("dict:f:c30:r2:b1mb", true)]
  [TestCase("dict:q7", false)]
  public void GivenADictStage_WhenCheckedForSupport_ThenFreeArcsParametersAreRead(string method, bool supported)
    => Assert.That(FreeArcMethods.IsSupported(method), Is.EqualTo(supported));

  [Test]
  public void GivenADictBlock_WhenDecoded_ThenWordsLiteralsAndEscapesAreExpanded() {
    // Byte 'A' is the one-byte word "the", byte 'B' starts two-byte codes; 'B',0 is "hello" and
    // 'B',1 shares 3 bytes with it: "help". The escape byte 'Z' passes 'A' through literally.
    var dictionary = new List<byte>();
    var single = new byte[256];
    single['A'] = 3;
    single['B'] = 1;
    dictionary.AddRange(single);
    var shared = new byte[256];
    shared[1] = 3;
    dictionary.AddRange(shared);
    dictionary.AddRange("the"u8.ToArray());
    dictionary.Add(0); // separator
    dictionary.AddRange("hello"u8.ToArray());
    dictionary.Add(0);
    dictionary.AddRange("p"u8.ToArray());
    dictionary.Add(0);
    for (var d = 2; d < 256; ++d)
      dictionary.Add(0);
    dictionary.Add((byte)'Z'); // escape
    dictionary.AddRange(new byte[] { (byte)'A', (byte)' ', (byte)'B', 0, (byte)' ', (byte)'B', 1, (byte)'!', (byte)'Z', (byte)'A' });
    var block = dictionary.ToArray();
    var stream = BitConverter.GetBytes(block.Length).Concat(block).ToArray();

    Assert.That(FreeArcMethods.Decode("dict", stream, 16), Is.EqualTo("the hello help!A"u8.ToArray()));
  }

  [Test]
  public void GivenADictBlockWhoseFirstWordSharesAPrefix_WhenDecoded_ThenItIsRefused() {
    var block = new byte[256 + 256 + 2];
    block['B'] = 1;
    block[256] = 2; // the first two-byte word claims 2 shared bytes
    var stream = BitConverter.GetBytes(block.Length).Concat(block).ToArray();
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("dict", stream, 0));
  }

  [Test]
  public void GivenADictBlockCutInsideItsDictionary_WhenDecoded_ThenItIsRefused() {
    var block = new byte[100];
    var stream = BitConverter.GetBytes(block.Length).Concat(block).ToArray();
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("dict", stream, 0));
  }

  [Test]
  public void GivenAStoredDictChunk_WhenDecoded_ThenItsBytesAreCopied() {
    var stream = new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, (byte)'o', (byte)'k' };
    Assert.That(FreeArcMethods.Decode("dict", stream, 2), Is.EqualTo("ok"u8.ToArray()));
  }

  [Test]
  public void GivenTornadoBehindFourByFour_WhenCheckedForSupport_ThenItIsDecodable()
    => Assert.That(FreeArcMethods.IsSupported("rep:144kb+4x4:tor:3:144kb"), Is.True);

  [TestCase(new byte[] { 9, 3, 0, 0, 1, 0 })]
  [TestCase(new byte[] { 1, 3, 0 })]
  public void GivenABrokenTornadoHeader_WhenDecoded_ThenItIsRefused(byte[] stream)
    => Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("tor:3", stream, 10));

  [Test]
  public void GivenATornadoStreamCutShort_WhenDecoded_ThenItIsRefused() {
    var full = FreeArcTests.Vector("tor-3");
    // The block starts after the 31-byte archive header; cut its tornado stream in the middle.
    var stream = full.AsSpan(31, 2000).ToArray();
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("tor:3:153kb", stream, 153796));
  }

  // ── damaged and malformed stage streams are refused ──────────────────────

  [Test]
  public void GivenARepMatchReachingBeforeTheStart_WhenDecoded_ThenItIsRefused() {
    // window 1 MiB; frame: 1 match of length 4 at distance 10, literal runs 2 and 0.
    var frame = Words(1, 4, 10, 2, 0).Concat("ab"u8.ToArray()).ToArray();
    var stream = Words(1 << 20).Concat(Words((uint)frame.Length)).Concat(frame).Concat(Words(0)).ToArray();
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("rep:1mb", stream, 6));
  }

  [Test]
  public void GivenAWellFormedRepStream_WhenDecoded_ThenOverlappingMatchesRepeat() {
    // "ab" then a match of 6 at distance 2 -> "abababab", then literal "!".
    var frame = Words(1, 6, 2, 2, 1).Concat("ab!"u8.ToArray()).ToArray();
    var stream = Words(1 << 20).Concat(Words((uint)frame.Length)).Concat(frame).Concat(Words(0)).ToArray();
    Assert.That(FreeArcMethods.Decode("rep:1mb", stream, 9), Is.EqualTo("abababab!"u8.ToArray()));
  }

  [Test]
  public void GivenATruncatedRepStream_WhenDecoded_ThenItIsRefused() {
    var frame = Words(0, 3).Concat("abc"u8.ToArray()).ToArray();
    var stream = Words(1 << 20).Concat(Words((uint)frame.Length)).Concat(frame).ToArray(); // no end marker
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("rep:1mb", stream, 3));
  }

  [Test]
  public void GivenADeltaTableReachingPastItsBlock_WhenDecoded_ThenItIsRefused() {
    // block of 8 data bytes with one table: skip 0, type 1<<4 (4-byte elements, all differenced), 3 rows = 12 bytes.
    var stream = Words(8, 4, 0, 1 << 4, 3).Concat(new byte[8]).ToArray();
    Assert.Throws<InvalidDataException>(() => FreeArcMethods.Decode("delta", stream, 8));
  }

  [Test]
  public void GivenADeltaTable_WhenDecoded_ThenRowsAreAddedBackWithCarries() {
    // 2-byte elements, both columns differenced: rows 0x00FF, +0x0001 -> 0x0100, +0x0001 -> 0x0101.
    var data = new byte[] { 0xFF, 0x00, 0x01, 0x00, 0x01, 0x00 };
    var stream = Words(6, 4, 0, 1 << 2, 3).Concat(data).ToArray();
    Assert.That(FreeArcMethods.Decode("delta", stream, 6), Is.EqualTo(new byte[] { 0xFF, 0x00, 0x00, 0x01, 0x01, 0x01 }));
  }

  private static byte[] Words(params uint[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
  private static byte[] Words(params int[] values) => values.SelectMany(v => BitConverter.GetBytes((uint)v)).ToArray();
}
