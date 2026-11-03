using System.Reflection;
using System.Security.Cryptography;
using FileFormat.FreeArc;

namespace Compression.Tests.FreeArc;

/// <summary>
/// The rep, delta and exe stages against archives FreeArc 0.67 wrote with <c>-mrep:1mb</c>,
/// <c>-mdelta</c>, <c>-mexe</c> and its <c>-mx</c> default chain <c>rep+exe+delta+lzma</c>
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
  public void GivenFreeArcArchive_WhenExtracted_ThenEveryFileMatchesArc(string vector, string method) {
    var expected = Manifest.Value.Where(r => r.Archive == vector + ".arc").ToArray();
    Assert.That(expected, Has.Length.EqualTo(5));
    using var reader = new FreeArcReader(new MemoryStream(FreeArcTests.Vector(vector)));

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

  [Test]
  public void GivenTornadoBehindFourByFour_WhenCheckedForSupport_ThenItIsStillRefused()
    => Assert.That(FreeArcMethods.IsSupported("rep:144kb+4x4:tor:3:144kb"), Is.False);

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
