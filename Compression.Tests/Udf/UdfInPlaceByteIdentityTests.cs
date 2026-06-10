#pragma warning disable CS1591
using FileSystem.Udf;

namespace Compression.Tests.Udf;

/// <summary>
/// Audit tests that lock the contract that <see cref="UdfModifier"/> Add/Remove
/// touches only specific sectors and preserves every other pre-existing
/// payload byte byte-identical at its original LBN.
/// </summary>
[TestFixture]
public class UdfInPlaceByteIdentityTests {

  private const int SectorSize = 2048;

  private static MemoryStream BuildSeed(params (string Name, byte[] Data)[] files) {
    var w = new UdfWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    // Re-wrap into a growable MemoryStream — the modifier may grow the image.
    var growable = new MemoryStream((int)ms.Length * 4);
    growable.Write(ms.GetBuffer(), 0, (int)ms.Length);
    growable.SetLength(ms.Length);
    growable.Position = 0;
    return growable;
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingFileBytesAtOriginalOffset() {
    var aData = new byte[5_000];
    var bData = new byte[3_000];
    for (var i = 0; i < aData.Length; i++) aData[i] = (byte)((i * 23 + 7) & 0xFF);
    for (var i = 0; i < bData.Length; i++) bData[i] = (byte)((i * 31 + 11) & 0xFF);
    using var ms = BuildSeed(("alpha.dat", aData), ("beta.dat", bData));

    var seedBytes = ms.ToArray();
    var alphaOffsetBefore = IndexOf(seedBytes, aData.AsSpan(0, 64));
    var betaOffsetBefore = IndexOf(seedBytes, bData.AsSpan(0, 64));
    Assert.That(alphaOffsetBefore, Is.GreaterThanOrEqualTo(0));
    Assert.That(betaOffsetBefore, Is.GreaterThanOrEqualTo(0));

    UdfModifier.AddFile(ms, "gamma.dat", "GAMMA"u8.ToArray());

    var afterBytes = ms.ToArray();
    var alphaSliceAfter = afterBytes.AsSpan(alphaOffsetBefore, aData.Length).ToArray();
    var betaSliceAfter = afterBytes.AsSpan(betaOffsetBefore, bData.Length).ToArray();
    Assert.That(alphaSliceAfter, Is.EqualTo(aData),
      "alpha.dat must stay byte-identical at its original LBN after Add.");
    Assert.That(betaSliceAfter, Is.EqualTo(bData),
      "beta.dat must stay byte-identical at its original LBN after Add.");

    ms.Position = 0;
    var r = new UdfReader(ms);
    var gamma = r.Entries.Single(e => e.Name == "gamma.dat");
    Assert.That(System.Text.Encoding.ASCII.GetString(r.Extract(gamma)), Is.EqualTo("GAMMA"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_LeavesNonTargetFileBytesByteIdentical() {
    var aData = new byte[4_000];
    var bData = new byte[6_000];
    for (var i = 0; i < aData.Length; i++) aData[i] = (byte)((i * 13) & 0xFF);
    for (var i = 0; i < bData.Length; i++) bData[i] = (byte)((i * 17 + 1) & 0xFF);
    using var ms = BuildSeed(("keep.dat", aData), ("victim.dat", bData));

    var seedBytes = ms.ToArray();
    var keepOffsetBefore = IndexOf(seedBytes, aData.AsSpan(0, 64));
    Assert.That(keepOffsetBefore, Is.GreaterThanOrEqualTo(0));

    UdfModifier.RemoveFile(ms, "victim.dat");

    var afterBytes = ms.ToArray();
    var keepAfter = afterBytes.AsSpan(keepOffsetBefore, aData.Length).ToArray();
    Assert.That(keepAfter, Is.EqualTo(aData),
      "keep.dat must stay byte-identical when a sibling is removed.");
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
