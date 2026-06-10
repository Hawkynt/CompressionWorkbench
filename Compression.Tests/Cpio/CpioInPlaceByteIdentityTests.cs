#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Cpio;

namespace Compression.Tests.Cpio;

/// <summary>
/// Locks the in-place contract for <see cref="CpioModifier"/>: bytes that
/// existed before the TRAILER!!! entry are byte-identical after Add — the
/// only writes happen at the old trailer's position onward.
/// </summary>
[TestFixture]
public class CpioInPlaceByteIdentityTests {

  [Test, Category("ByteIdentity")]
  public void AddFile_PreservesBytesBeforeOldTrailer() {
    var (seedBytes, oldTrailerOffset) = BuildSeedWithTrailerOffset(
      ("seed.txt", "seed-content"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(seedBytes);
    CpioModifier.AddFile(ms, "added.txt", "appended-data"u8.ToArray());

    var newBytes = ms.ToArray();
    Assert.That(newBytes.Length, Is.GreaterThan(seedBytes.Length),
      "Add must grow the archive (new entry + new trailer).");

    AssertBytesEqual(seedBytes.AsSpan(0, (int)oldTrailerOffset),
      newBytes.AsSpan(0, (int)oldTrailerOffset),
      "Bytes before the old trailer must be untouched.");
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_MultipleAppends_PreservesAllPriorEntryBytes() {
    var (seedBytes, oldTrailerOffset) = BuildSeedWithTrailerOffset(
      ("one.txt", "first"u8.ToArray()),
      ("two.txt", "second"u8.ToArray()));

    using var ms = new MemoryStream();
    ms.Write(seedBytes);
    CpioModifier.AddFile(ms, "three.txt", "third"u8.ToArray());

    var afterFirst = ms.ToArray();
    AssertBytesEqual(seedBytes.AsSpan(0, (int)oldTrailerOffset),
      afterFirst.AsSpan(0, (int)oldTrailerOffset),
      "First Add must not touch any pre-existing entry bytes.");

    // After the first Add, compute the new trailer offset by re-walking.
    var midTrailerOffset = LocateTrailerOffset(afterFirst);
    CpioModifier.AddFile(ms, "four.txt", "fourth"u8.ToArray());

    var afterSecond = ms.ToArray();
    AssertBytesEqual(afterFirst.AsSpan(0, (int)midTrailerOffset),
      afterSecond.AsSpan(0, (int)midTrailerOffset),
      "Second Add must not touch any bytes written by the first Add.");
  }

  [Test, Category("ByteIdentity")]
  public void Listing_ReflectsNewStateAfterAdd() {
    using var ms = BuildSeedCpio(("seed.txt", "seed-content"u8.ToArray()));
    CpioModifier.AddFile(ms, "added.txt", "fresh"u8.ToArray());

    ms.Position = 0;
    var entries = new CpioReader(ms, leaveOpen: true).ReadAll();
    var names = entries.Select(x => x.Entry.Name).ToList();
    Assert.That(names, Does.Contain("seed.txt"));
    Assert.That(names, Does.Contain("added.txt"));
  }

  [Test, Category("ByteIdentity")]
  public void MutateThenExtract_PreservesCallerPayload() {
    using var ms = BuildSeedCpio(("seed.txt", "seed-content"u8.ToArray()));
    var payload = new byte[2048];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)((i * 17 + 3) & 0xFF);
    CpioModifier.AddFile(ms, "payload.bin", payload);

    ms.Position = 0;
    var r = new CpioReader(ms, leaveOpen: true);
    foreach (var (entry, data) in r.ReadAll()) {
      if (entry.Name != "payload.bin") continue;
      Assert.That(data, Is.EqualTo(payload));
      return;
    }
    Assert.Fail("payload.bin not found after Add.");
  }

  [Test, Category("ByteIdentity")]
  public void Descriptor_AdvertisesCanModify_AndImplementsIArchiveModifiable() {
    var d = new CpioFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Capabilities & FormatCapabilities.CanModify, Is.EqualTo(FormatCapabilities.CanModify),
        "Descriptor must advertise CanModify.");
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
        "Descriptor must implement IArchiveModifiable.");
    });
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedCpio(params (string Name, byte[] Data)[] entries) {
    var ms = new MemoryStream();
    var w = new CpioWriter(ms, leaveOpen: true);
    foreach (var (name, data) in entries)
      w.AddFile(name, data);
    w.Finish();
    ms.Position = 0;
    return ms;
  }

  private static (byte[] Bytes, long TrailerOffset) BuildSeedWithTrailerOffset(params (string Name, byte[] Data)[] entries) {
    using var ms = BuildSeedCpio(entries);
    var bytes = ms.ToArray();
    var trailerOffset = LocateTrailerOffset(bytes);
    return (bytes, trailerOffset);
  }

  // cpio "new ASCII" (SVR4) constants — these are the same numbers as
  // FileFormat.Cpio's internal CpioConstants, inlined here so the test
  // owns its own ground-truth lookup without needing internals access.
  private const int NewAsciiHeaderSize = 110;
  private const string TrailerName = "TRAILER!!!";

  /// <summary>
  /// Re-walks the cpio entry chain to find the TRAILER!!! entry offset.
  /// Mirrors the same logic as <see cref="CpioModifier"/>'s private
  /// FindTrailerOffset so the test owns its own ground-truth lookup.
  /// </summary>
  private static long LocateTrailerOffset(byte[] cpioBytes) {
    using var s = new MemoryStream(cpioBytes, writable: false);
    var hdr = new byte[NewAsciiHeaderSize];
    while (s.Position + NewAsciiHeaderSize <= s.Length) {
      var entryStart = s.Position;
      s.ReadExactly(hdr);
      var fileSize = ParseHex(hdr, 54, 8);
      var nameSize = (int)ParseHex(hdr, 94, 8);
      var nameBuf = new byte[nameSize];
      s.ReadExactly(nameBuf);
      var name = System.Text.Encoding.ASCII.GetString(nameBuf, 0, Math.Max(0, nameSize - 1));
      var headerPlusName = NewAsciiHeaderSize + nameSize;
      var namePad = (4 - headerPlusName % 4) % 4;
      s.Position += namePad;
      if (name == TrailerName) return entryStart;
      s.Position += fileSize;
      var dataPad = (int)((4 - fileSize % 4) % 4);
      s.Position += dataPad;
    }
    return s.Length;
  }

  private static long ParseHex(byte[] hdr, int offset, int length) {
    var hex = System.Text.Encoding.ASCII.GetString(hdr, offset, length);
    return long.Parse(hex, System.Globalization.NumberStyles.HexNumber);
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
