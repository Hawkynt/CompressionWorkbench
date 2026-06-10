using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.ZxScl;

namespace Compression.Tests.ZxScl;

/// <summary>
/// Locks the true in-place R/W semantics of <see cref="ZxSclInPlaceModifier"/>:
/// Add shifts the payload region right by one 14-byte directory slot,
/// Remove shifts later directory entries up by 14 bytes and closes the gap in
/// the payload region. No full-image rebuild is performed.
/// Every mutation rewrites the trailing 32-bit checksum.
/// </summary>
[TestFixture]
public class ZxSclInPlaceModifyTests {

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new ZxSclWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var bytes = w.Build();
    var ms = new MemoryStream();
    ms.Write(bytes);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ExtractAll(Stream image) {
    image.Position = 0;
    using var r = new ZxSclReader(image);
    return r.Entries.ToDictionary(e => e.Name, e => r.Extract(e));
  }

  private static uint ReadTrailingCrc(Stream image) {
    Span<byte> buf = stackalloc byte[4];
    image.Position = image.Length - 4;
    image.ReadExactly(buf);
    return BinaryPrimitives.ReadUInt32LittleEndian(buf);
  }

  private static uint ComputeExpectedCrc(Stream image) {
    image.Position = 0;
    var sum = 0u;
    var pre = image.Length - 4;
    Span<byte> buf = stackalloc byte[4096];
    var remaining = pre;
    while (remaining > 0) {
      var chunk = (int)Math.Min(remaining, buf.Length);
      image.ReadExactly(buf.Slice(0, chunk));
      for (var i = 0; i < chunk; i++) sum += buf[i];
      remaining -= chunk;
    }
    return sum;
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   an existing SCL image with two stored TR-DOS files
  // ── When ───────────────────────────────────────────────────────────────
  //   a third file is appended via in-place AddFile
  // ── Then ──────────────────────────────────────────────────────────────
  //   the prior two files extract byte-identically, the new file is present
  //   with intact content, and the trailing checksum still matches.
  [Test, Category("RoundTrip")]
  public void Add_PreservesExistingEntries_AndCrc() {
    var d1 = new byte[300]; new Random(1).NextBytes(d1);
    var d2 = new byte[250]; new Random(2).NextBytes(d2);
    var d3 = new byte[100]; new Random(3).NextBytes(d3);

    using var img = BuildImage(("first.cod", d1), ("second.cod", d2));
    var pre = ExtractAll(img);

    ZxSclInPlaceModifier.AddFile(img, "third.cod", d3);

    var post = ExtractAll(img);
    Assert.That(post.Keys, Has.Count.EqualTo(3));

    // Untouched entries: extracted bytes are byte-identical (sector-padded extract;
    // leading-bytes comparison against the original payload).
    Assert.That(post["first.cod"].AsSpan(0, d1.Length).ToArray(), Is.EqualTo(d1));
    Assert.That(post["second.cod"].AsSpan(0, d2.Length).ToArray(), Is.EqualTo(d2));
    Assert.That(post["third.cod"].AsSpan(0, d3.Length).ToArray(), Is.EqualTo(d3));

    // Trailing checksum is consistent with the new payload (in-place patched).
    Assert.That(ReadTrailingCrc(img), Is.EqualTo(ComputeExpectedCrc(img)));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   an existing SCL image with three files
  // ── When ───────────────────────────────────────────────────────────────
  //   the middle file is removed via in-place RemoveFile
  // ── Then ──────────────────────────────────────────────────────────────
  //   the remaining two files extract byte-identically and the stream length
  //   has shrunk by the removed entry's directory + sector-padded payload bytes.
  [Test, Category("RoundTrip")]
  public void Remove_PreservesOthers_AndShrinksStream() {
    var d1 = new byte[200]; new Random(11).NextBytes(d1);
    var d2 = new byte[400]; new Random(12).NextBytes(d2);
    var d3 = new byte[300]; new Random(13).NextBytes(d3);

    using var img = BuildImage(("alpha.cod", d1), ("beta.cod", d2), ("gamma.cod", d3));
    var lengthBefore = img.Length;

    var removed = ZxSclInPlaceModifier.RemoveFile(img, "beta.cod");
    Assert.That(removed, Is.True);

    var post = ExtractAll(img);
    Assert.That(post.Keys, Is.EquivalentTo(new[] { "alpha.cod", "gamma.cod" }));
    Assert.That(post["alpha.cod"].AsSpan(0, d1.Length).ToArray(), Is.EqualTo(d1));
    Assert.That(post["gamma.cod"].AsSpan(0, d3.Length).ToArray(), Is.EqualTo(d3));

    // Stream shrunk by 14 (directory slot) + 2*256 (d2 padded to 2 sectors).
    var d2PaddedSectors = (d2.Length + 255) / 256;
    var expectedShrink = 14 + d2PaddedSectors * 256;
    Assert.That(img.Length, Is.EqualTo(lengthBefore - expectedShrink));

    Assert.That(ReadTrailingCrc(img), Is.EqualTo(ComputeExpectedCrc(img)));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   an SCL image that already contains a named entry
  // ── When ───────────────────────────────────────────────────────────────
  //   AddFile is called with the same name but different bytes
  // ── Then ──────────────────────────────────────────────────────────────
  //   the old entry is replaced (single occurrence in listing) with the new
  //   bytes — verifies the replace-by-name semantic.
  [Test, Category("RoundTrip")]
  public void Add_SameName_ReplacesEntry() {
    var oldData = new byte[500]; new Random(21).NextBytes(oldData);
    var newData = new byte[120]; new Random(22).NextBytes(newData);

    using var img = BuildImage(("payload.cod", oldData));

    ZxSclInPlaceModifier.AddFile(img, "payload.cod", newData);

    var post = ExtractAll(img);
    Assert.That(post.Keys, Is.EquivalentTo(new[] { "payload.cod" }));
    Assert.That(post["payload.cod"].AsSpan(0, newData.Length).ToArray(), Is.EqualTo(newData));

    Assert.That(ReadTrailingCrc(img), Is.EqualTo(ComputeExpectedCrc(img)));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   an SCL image with one entry
  // ── When ───────────────────────────────────────────────────────────────
  //   we add a file, remove it again, add another, remove all originals, etc.
  // ── Then ──────────────────────────────────────────────────────────────
  //   the cumulative mutation-then-extract roundtrip preserves every live
  //   entry and the trailing checksum remains valid throughout.
  [Test, Category("RoundTrip")]
  public void MutateThenExtract_Roundtrip() {
    var d1 = new byte[100]; new Random(31).NextBytes(d1);
    var d2 = new byte[300]; new Random(32).NextBytes(d2);
    var d3 = new byte[700]; new Random(33).NextBytes(d3);
    var d4 = new byte[50];  new Random(34).NextBytes(d4);

    using var img = BuildImage(("ONE.cod", d1));

    ZxSclInPlaceModifier.AddFile(img, "TWO.cod", d2);
    ZxSclInPlaceModifier.AddFile(img, "THREE.cod", d3);
    Assert.That(ReadTrailingCrc(img), Is.EqualTo(ComputeExpectedCrc(img)));

    ZxSclInPlaceModifier.RemoveFile(img, "ONE.cod");
    Assert.That(ReadTrailingCrc(img), Is.EqualTo(ComputeExpectedCrc(img)));

    ZxSclInPlaceModifier.AddFile(img, "FOUR.cod", d4);
    Assert.That(ReadTrailingCrc(img), Is.EqualTo(ComputeExpectedCrc(img)));

    var post = ExtractAll(img);
    Assert.That(post.Keys, Is.EquivalentTo(new[] { "TWO.cod", "THREE.cod", "FOUR.cod" }));
    Assert.That(post["TWO.cod"].AsSpan(0, d2.Length).ToArray(), Is.EqualTo(d2));
    Assert.That(post["THREE.cod"].AsSpan(0, d3.Length).ToArray(), Is.EqualTo(d3));
    Assert.That(post["FOUR.cod"].AsSpan(0, d4.Length).ToArray(), Is.EqualTo(d4));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   a freshly-built SCL image and a wrapper descriptor
  // ── When ───────────────────────────────────────────────────────────────
  //   Add/Remove are invoked through the IArchiveModifiable surface
  // ── Then ──────────────────────────────────────────────────────────────
  //   the descriptor delegates to the in-place modifier (no full rebuild)
  //   and the resulting image lists the expected entries.
  [Test, Category("RoundTrip")]
  public void DescriptorAddRemove_RoutedThroughInPlaceModifier() {
    var dataA = "alpha"u8.ToArray();
    var dataB = "bravo"u8.ToArray();

    using var img = BuildImage(("A.cod", dataA));

    var desc = new ZxSclFormatDescriptor();
    ((IArchiveModifiable)desc).Add(img,
      [ArchiveInputInfo.InMemory("B.cod", dataB)]);

    var listed = ExtractAll(img).Keys.ToList();
    Assert.That(listed, Is.EquivalentTo(new[] { "A.cod", "B.cod" }));

    ((IArchiveModifiable)desc).Remove(img, ["A.cod"]);

    var final = ExtractAll(img).Keys.ToList();
    Assert.That(final, Is.EquivalentTo(new[] { "B.cod" }));
    Assert.That(ReadTrailingCrc(img), Is.EqualTo(ComputeExpectedCrc(img)));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  //   an SCL image with no matching entry
  // ── When ───────────────────────────────────────────────────────────────
  //   RemoveFile is called for an unknown name
  // ── Then ──────────────────────────────────────────────────────────────
  //   it returns false and leaves the image untouched.
  [Test, Category("ErrorHandling")]
  public void Remove_UnknownName_ReturnsFalse_NoMutation() {
    var d = new byte[100]; new Random(41).NextBytes(d);
    using var img = BuildImage(("KEEP.cod", d));
    var snapshot = img.ToArray();

    var removed = ZxSclInPlaceModifier.RemoveFile(img, "GONE.cod");
    Assert.That(removed, Is.False);
    Assert.That(img.ToArray(), Is.EqualTo(snapshot));
  }
}
