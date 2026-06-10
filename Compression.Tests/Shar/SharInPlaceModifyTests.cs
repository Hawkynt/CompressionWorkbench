#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Shar;

namespace Compression.Tests.Shar;

/// <summary>
/// Locks the in-place contract for <see cref="SharInPlaceModifier"/>: bytes
/// that existed before the trailing <c>exit 0\n</c> sentinel are
/// byte-identical after Add — only the sentinel and EOF tail get rewritten.
/// </summary>
[TestFixture]
public class SharInPlaceModifyTests {

  private static readonly byte[] ExitSentinel = "exit 0\n"u8.ToArray();

  [Test, Category("ByteIdentity")]
  public void AddFile_PreservesBytesBeforeOldExitSentinel() {
    var oldBytes = BuildSeedShar(("seed.txt", "seed-content"));
    var oldSentinelOffset = LastIndexOf(oldBytes, ExitSentinel);
    Assert.That(oldSentinelOffset, Is.GreaterThan(0), "Seed shar must contain an exit 0 sentinel.");

    using var ms = new MemoryStream();
    ms.Write(oldBytes);
    SharInPlaceModifier.AddFile(ms, "added.txt", "appended-data"u8.ToArray());

    var newBytes = ms.ToArray();
    Assert.That(newBytes.Length, Is.GreaterThan(oldBytes.Length),
      "Add must grow the archive (new entry + fresh sentinel).");

    AssertBytesEqual(oldBytes.AsSpan(0, oldSentinelOffset),
      newBytes.AsSpan(0, oldSentinelOffset),
      "Bytes before the old exit 0 sentinel must be untouched.");
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_MultipleAppends_PreservesAllPriorEntryBytes() {
    var seedBytes = BuildSeedShar(
      ("one.txt", "first"),
      ("two.txt", "second"));
    var oldSentinelOffset = LastIndexOf(seedBytes, ExitSentinel);

    using var ms = new MemoryStream();
    ms.Write(seedBytes);
    SharInPlaceModifier.AddFile(ms, "three.txt", "third"u8.ToArray());

    var afterFirst = ms.ToArray();
    AssertBytesEqual(seedBytes.AsSpan(0, oldSentinelOffset),
      afterFirst.AsSpan(0, oldSentinelOffset),
      "First Add must not touch any pre-existing entry bytes.");

    var midSentinelOffset = LastIndexOf(afterFirst, ExitSentinel);
    SharInPlaceModifier.AddFile(ms, "four.txt", "fourth"u8.ToArray());

    var afterSecond = ms.ToArray();
    AssertBytesEqual(afterFirst.AsSpan(0, midSentinelOffset),
      afterSecond.AsSpan(0, midSentinelOffset),
      "Second Add must not touch any bytes written by the first Add.");
  }

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var seedBytes = BuildSeedShar(("seed.txt", "seed-content"));
    using var ms = new MemoryStream();
    ms.Write(seedBytes);
    SharInPlaceModifier.AddFile(ms, "added.txt", "fresh-data"u8.ToArray());

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
    Assert.That(entries["added.txt"], Is.EqualTo("fresh-data"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_BinaryPayload_RoundTripsViaUudecode() {
    var seedBytes = BuildSeedShar(("seed.txt", "seed-content"));
    using var ms = new MemoryStream();
    ms.Write(seedBytes);

    var payload = new byte[128];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i & 0xFF); // includes NULs → forces uuencode
    SharInPlaceModifier.AddFile(ms, "payload.bin", payload);

    ms.Position = 0;
    var r = new SharReader(ms);
    var found = r.Entries.FirstOrDefault(e => e.FileName == "payload.bin");
    Assert.That(found, Is.Not.Null, "payload.bin entry must round-trip through uudecode.");
    Assert.That(found!.Data, Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_TerminatesWithFreshExitSentinel() {
    var seedBytes = BuildSeedShar(("seed.txt", "seed-content"));
    using var ms = new MemoryStream();
    ms.Write(seedBytes);
    SharInPlaceModifier.AddFile(ms, "added.txt", "fresh"u8.ToArray());

    var newBytes = ms.ToArray();
    var tail = Encoding.UTF8.GetString(newBytes, Math.Max(0, newBytes.Length - 8), Math.Min(8, newBytes.Length));
    Assert.That(tail, Does.EndWith("exit 0\n"),
      "Trailing sentinel must remain in place so the script is still self-extracting.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify_AndImplementsIArchiveModifiable() {
    var d = new SharFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Capabilities & FormatCapabilities.CanModify, Is.EqualTo(FormatCapabilities.CanModify),
        "Descriptor must advertise CanModify.");
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
        "Descriptor must implement IArchiveModifiable.");
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var seedBytes = BuildSeedShar(("seed.txt", "seed-content"));
    using var ms = new MemoryStream();
    ms.Write(seedBytes);
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new SharFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "viaif.txt", false)]);

      ms.Position = 0;
      var entries = ReadAll(ms);
      Assert.That(entries["viaif.txt"], Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("HonestScope")]
  public void Descriptor_Remove_ThrowsNotSupported() {
    using var ms = new MemoryStream(BuildSeedShar(("seed.txt", "seed-content")));
    var d = (IArchiveModifiable)new SharFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => d.Remove(ms, ["seed.txt"]),
      "In-place Remove must throw — heredoc bodies can collide with the delimiter probe.");
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static byte[] BuildSeedShar(params (string Name, string Content)[] entries) {
    var w = new SharWriter();
    foreach (var (name, content) in entries)
      w.AddFile(name, Encoding.UTF8.GetBytes(content));
    return w.ToByteArray();
  }

  private static Dictionary<string, string> ReadAll(Stream s) {
    s.Position = 0;
    var r = new SharReader(s);
    var result = new Dictionary<string, string>();
    foreach (var e in r.Entries)
      result[e.FileName] = Encoding.UTF8.GetString(e.Data);
    return result;
  }

  private static int LastIndexOf(byte[] haystack, byte[] needle) {
    if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
    for (var i = haystack.Length - needle.Length; i >= 0; --i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }

  private static void AssertBytesEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string message) {
    if (expected.Length != actual.Length)
      Assert.Fail($"{message} (length: expected {expected.Length}, got {actual.Length})");
    for (var i = 0; i < expected.Length; ++i) {
      if (expected[i] != actual[i])
        Assert.Fail($"{message} (first difference at offset {i}: expected 0x{expected[i]:X2}, got 0x{actual[i]:X2})");
    }
  }
}
