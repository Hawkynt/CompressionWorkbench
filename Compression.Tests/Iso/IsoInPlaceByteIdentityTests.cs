#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// Audit tests that lock the contract that <see cref="IsoModifier"/> Add
/// touches only specific sectors and preserves every other byte byte-identical
/// at its original LBN. Distinguishes a real in-place modifier from a
/// rebuild-disguised modifier.
/// </summary>
[TestFixture]
public class IsoInPlaceByteIdentityTests {

  private const int SectorSize = 2048;

  private static MemoryStream BuildSeed(params (string Name, byte[] Data)[] files) {
    var w = new IsoWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    ms.Write(w.Build());
    return ms;
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingFileDataBytesAtOriginalOffset() {
    var aData = new byte[6 * SectorSize];
    var bData = new byte[3 * SectorSize];
    for (var i = 0; i < aData.Length; i++) aData[i] = (byte)((i * 23 + 7) & 0xFF);
    for (var i = 0; i < bData.Length; i++) bData[i] = (byte)((i * 31 + 11) & 0xFF);
    using var ms = BuildSeed(("ALPHA.DAT", aData), ("BETA.DAT", bData));

    // Locate the byte offset of each pre-existing payload by searching the
    // raw image for the unique prefix. Both prefixes must remain at the
    // SAME offset after Add — that's the in-place LBN guarantee.
    var seedBytes = ms.ToArray();
    var alphaOffsetBefore = IndexOf(seedBytes, aData.AsSpan(0, 64));
    var betaOffsetBefore = IndexOf(seedBytes, bData.AsSpan(0, 64));
    Assert.That(alphaOffsetBefore, Is.GreaterThanOrEqualTo(0));
    Assert.That(betaOffsetBefore, Is.GreaterThanOrEqualTo(0));

    IsoModifier.AddFile(ms, "GAMMA.DAT", "GAMMA"u8.ToArray());

    var afterBytes = ms.ToArray();
    var alphaSliceAfter = afterBytes.AsSpan(alphaOffsetBefore, aData.Length).ToArray();
    var betaSliceAfter = afterBytes.AsSpan(betaOffsetBefore, bData.Length).ToArray();
    Assert.That(alphaSliceAfter, Is.EqualTo(aData),
      "ALPHA.DAT's data must stay byte-identical at its original file offset after Add.");
    Assert.That(betaSliceAfter, Is.EqualTo(bData),
      "BETA.DAT's data must stay byte-identical at its original file offset after Add.");

    // And the new file is readable.
    ms.Position = 0;
    var r2 = new IsoReader(ms);
    var gamma = r2.Entries.Single(e => e.Name == "GAMMA.DAT");
    Assert.That(System.Text.Encoding.ASCII.GetString(r2.Extract(gamma)), Is.EqualTo("GAMMA"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_LeavesNonTargetFileDataByteIdentical() {
    var aData = new byte[4 * SectorSize];
    var bData = new byte[5 * SectorSize];
    for (var i = 0; i < aData.Length; i++) aData[i] = (byte)((i * 13) & 0xFF);
    for (var i = 0; i < bData.Length; i++) bData[i] = (byte)((i * 17 + 1) & 0xFF);
    using var ms = BuildSeed(("KEEP.DAT", aData), ("VICTIM.DAT", bData));

    var seedBytes = ms.ToArray();
    var keepOffsetBefore = IndexOf(seedBytes, aData.AsSpan(0, 64));
    Assert.That(keepOffsetBefore, Is.GreaterThanOrEqualTo(0));

    IsoModifier.RemoveFile(ms, "VICTIM.DAT");

    var afterBytes = ms.ToArray();
    var keepAfter = afterBytes.AsSpan(keepOffsetBefore, aData.Length).ToArray();
    Assert.That(keepAfter, Is.EqualTo(aData),
      "KEEP.DAT's data must stay byte-identical when a sibling is removed.");
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }
}
