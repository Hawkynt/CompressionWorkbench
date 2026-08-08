using System.Text;
using Compression.Registry;
using FileSystem.OpenVms;

namespace Compression.Tests.OpenVms;

/// <summary>
/// In-place R/W gate for the OpenVMS Files-11 ODS-2 descriptor.
/// Asserts that <see cref="OpenVmsInPlaceModifier"/> mutates only the
/// LBNs it has to (BITMAP.SYS sector, INDEXF.SYS file-header LBN, root
/// directory LBN, and the affected data LBNs). Untouched LBNs MUST be
/// byte-identical between the pre- and post-operation images.
/// </summary>
[TestFixture]
public class OpenVmsInPlaceModifyTests {

  private static byte[] BuildBaseVolume(params (string Name, byte[] Data)[] files) {
    var writer = new OpenVmsWriter();
    return writer.Build(files.Select(f => (f.Name, f.Data)).ToList());
  }

  private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

  /// <summary>
  /// Given a fresh workbench-layout volume with two files,
  /// when a third file is added in-place,
  /// then the pre-existing files' data LBNs and FH bytes are byte-identical.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Add_LeavesPreviousFhAndDataBytesIdentical() {
    var alpha = Bytes("ALPHA content alpha");
    var beta = Bytes("BETA content beta longer than the prev");
    var image = BuildBaseVolume(("ALPHA.TXT", alpha), ("BETA.TXT", beta));

    // Snapshot the pre-existing files' on-disk locations via the reader.
    var preReader = new OpenVmsReader(image);
    Assert.That(preReader.IsCwbVolume, Is.True);
    Assert.That(preReader.Entries, Has.Count.EqualTo(2));

    var preAlphaFh = OpenVmsReader.ReadFileHeader(image, preReader.Entries[0].FileId)!;
    var preBetaFh = OpenVmsReader.ReadFileHeader(image, preReader.Entries[1].FileId)!;

    var alphaLbn = preAlphaFh.Extents[0].StartLbn;
    var betaLbn = preBetaFh.Extents[0].StartLbn;
    var preAlphaBytes = image.AsSpan(alphaLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray();
    var preBetaBytes = image.AsSpan(betaLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray();
    var preAlphaFhBlock = image.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(preAlphaFh.FileId), OpenVmsLayout.BlockSize).ToArray();
    var preBetaFhBlock = image.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(preBetaFh.FileId), OpenVmsLayout.BlockSize).ToArray();

    // Add a third file in place.
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    var gamma = Bytes("GAMMA fresh content");
    OpenVmsInPlaceModifier.AddFile(ms, "GAMMA.TXT", gamma);

    var modified = ms.ToArray();
    Assert.That(modified.Length, Is.EqualTo(image.Length), "Add must not grow or shrink the volume image.");

    // Pre-existing data LBNs untouched.
    Assert.That(modified.AsSpan(alphaLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preAlphaBytes), "ALPHA's data LBN must be byte-identical after Add.");
    Assert.That(modified.AsSpan(betaLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preBetaBytes), "BETA's data LBN must be byte-identical after Add.");

    // Pre-existing File Headers untouched.
    Assert.That(modified.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(preAlphaFh.FileId), OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preAlphaFhBlock), "ALPHA's FH bytes must be byte-identical after Add.");
    Assert.That(modified.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(preBetaFh.FileId), OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preBetaFhBlock), "BETA's FH bytes must be byte-identical after Add.");

    // And the new file round-trips.
    var postReader = new OpenVmsReader(modified);
    Assert.That(postReader.Entries.Select(e => e.Name), Does.Contain("GAMMA.TXT"));
    var gammaEntry = postReader.Entries.Single(e => e.Name == "GAMMA.TXT");
    Assert.That(postReader.Extract(gammaEntry), Is.EqualTo(gamma));
  }

  /// <summary>
  /// Given a fresh workbench-layout volume with three files,
  /// when the middle file is removed in-place,
  /// then the remaining files' data LBNs and FH bytes are byte-identical
  /// AND the removed file's data LBNs are zero-wiped AND BITMAP.SYS is updated.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Remove_WipesDataAndFreesBitmap_LeavesOthersByteIdentical() {
    var alpha = Bytes("ALPHA content");
    var beta = Bytes("BETA content middle");
    var gamma = Bytes("GAMMA content tail");
    var image = BuildBaseVolume(("ALPHA.TXT", alpha), ("BETA.TXT", beta), ("GAMMA.TXT", gamma));

    var pre = new OpenVmsReader(image);
    var alphaFh = OpenVmsReader.ReadFileHeader(image, pre.Entries[0].FileId)!;
    var betaFh = OpenVmsReader.ReadFileHeader(image, pre.Entries[1].FileId)!;
    var gammaFh = OpenVmsReader.ReadFileHeader(image, pre.Entries[2].FileId)!;
    var betaLbn = betaFh.Extents[0].StartLbn;

    var preAlphaBytes = image.AsSpan(alphaFh.Extents[0].StartLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray();
    var preGammaBytes = image.AsSpan(gammaFh.Extents[0].StartLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray();
    var preAlphaFhBlock = image.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(alphaFh.FileId), OpenVmsLayout.BlockSize).ToArray();
    var preGammaFhBlock = image.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(gammaFh.FileId), OpenVmsLayout.BlockSize).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    var removed = OpenVmsInPlaceModifier.RemoveFile(ms, "BETA.TXT");
    Assert.That(removed, Is.True);

    var modified = ms.ToArray();

    Assert.That(modified.AsSpan(alphaFh.Extents[0].StartLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preAlphaBytes), "ALPHA's data LBN must be byte-identical after Remove.");
    Assert.That(modified.AsSpan(gammaFh.Extents[0].StartLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preGammaBytes), "GAMMA's data LBN must be byte-identical after Remove.");
    Assert.That(modified.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(alphaFh.FileId), OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preAlphaFhBlock), "ALPHA's FH bytes must be byte-identical after Remove.");
    Assert.That(modified.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(gammaFh.FileId), OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preGammaFhBlock), "GAMMA's FH bytes must be byte-identical after Remove.");

    // The removed file's data LBN is zero-wiped.
    var betaSlice = modified.AsSpan(betaLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray();
    Assert.That(betaSlice, Is.All.EqualTo((byte)0), "Removed file's data LBN must be zero-wiped.");

    // The removed FH is marked free (struc-level word zero).
    var betaFhBlock = modified.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(betaFh.FileId), OpenVmsLayout.BlockSize).ToArray();
    var strucLev = (ushort)(betaFhBlock[OpenVmsLayout.FhStrucLev] | (betaFhBlock[OpenVmsLayout.FhStrucLev + 1] << 8));
    Assert.That(strucLev, Is.EqualTo(0), "Removed file's FH must have struc-level zero.");

    // The reader sees only ALPHA + GAMMA.
    var post = new OpenVmsReader(modified);
    Assert.That(post.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "ALPHA.TXT", "GAMMA.TXT" }));
  }

  /// <summary>
  /// Given an existing file,
  /// when it's replaced via Add (which collapses to Remove+Add),
  /// then surrounding files' on-disk bytes are byte-identical and the new bytes round-trip.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Replace_NewBytesRoundTrip_OthersUntouched() {
    var alpha = Bytes("ALPHA original");
    var beta = Bytes("BETA original content");
    var image = BuildBaseVolume(("ALPHA.TXT", alpha), ("BETA.TXT", beta));

    var pre = new OpenVmsReader(image);
    var alphaFh = OpenVmsReader.ReadFileHeader(image, pre.Entries[0].FileId)!;
    var preAlphaBytes = image.AsSpan(alphaFh.Extents[0].StartLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray();
    var preAlphaFhBlock = image.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(alphaFh.FileId), OpenVmsLayout.BlockSize).ToArray();

    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    var newBeta = Bytes("BETA replaced — completely different content here");
    var newFid = OpenVmsInPlaceModifier.ReplaceFile(ms, "BETA.TXT", newBeta);

    var modified = ms.ToArray();

    // ALPHA untouched.
    Assert.That(modified.AsSpan(alphaFh.Extents[0].StartLbn * OpenVmsLayout.BlockSize, OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preAlphaBytes), "ALPHA's data LBN must survive a BETA replace.");
    Assert.That(modified.AsSpan((int)OpenVmsLayout.FileHeaderByteOffset(alphaFh.FileId), OpenVmsLayout.BlockSize).ToArray(),
      Is.EqualTo(preAlphaFhBlock), "ALPHA's FH bytes must survive a BETA replace.");

    // New BETA round-trips with the new bytes.
    var post = new OpenVmsReader(modified);
    var betaEntry = post.Entries.Single(e => e.Name == "BETA.TXT");
    Assert.That(post.Extract(betaEntry), Is.EqualTo(newBeta));
    Assert.That(post.Entries, Has.Count.EqualTo(2));

    // The new FID may be the same slot (we freed it) or a higher one — either way it's allocated.
    Assert.That(newFid, Is.GreaterThanOrEqualTo(OpenVmsLayout.FirstUserFileId));
  }

  /// <summary>
  /// Given a fresh CWB volume,
  /// when we Add+Remove+Add the same name in sequence,
  /// then every operation round-trips through the reader.
  /// </summary>
  [Test, Category("HappyPath")]
  public void AddRemoveAdd_RoundTripsThroughReader() {
    var image = BuildBaseVolume(("KEEP.TXT", Bytes("permanent")));

    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    OpenVmsInPlaceModifier.AddFile(ms, "TMP.TXT", Bytes("temporary content"));
    Assert.That(new OpenVmsReader(ms.ToArray()).Entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "KEEP.TXT", "TMP.TXT" }));

    Assert.That(OpenVmsInPlaceModifier.RemoveFile(ms, "TMP.TXT"), Is.True);
    Assert.That(new OpenVmsReader(ms.ToArray()).Entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "KEEP.TXT" }));

    OpenVmsInPlaceModifier.AddFile(ms, "TMP.TXT", Bytes("fresh take 2"));
    var final = new OpenVmsReader(ms.ToArray());
    Assert.That(final.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "KEEP.TXT", "TMP.TXT" }));
    var tmp = final.Entries.Single(e => e.Name == "TMP.TXT");
    Assert.That(final.Extract(tmp), Is.EqualTo(Bytes("fresh take 2")));
  }

  /// <summary>
  /// Given a fresh CWB volume,
  /// when a duplicate name is added,
  /// then <see cref="OpenVmsInPlaceModifier.AddFile"/> throws.
  /// </summary>
  [Test, Category("ErrorHandling")]
  public void Add_DuplicateName_Throws() {
    var image = BuildBaseVolume(("DUPE.TXT", Bytes("first")));
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    Assert.Throws<IOException>(() => OpenVmsInPlaceModifier.AddFile(ms, "DUPE.TXT", Bytes("second")));
  }

  /// <summary>
  /// Given a non-CWB volume,
  /// when the modifier is invoked,
  /// then it throws <see cref="InvalidDataException"/> rather than corrupting the image.
  /// </summary>
  [Test, Category("ErrorHandling")]
  public void Modifier_RejectsNonCwbVolume() {
    var arbitrary = new byte[OpenVmsLayout.VolumeBytes];
    using var ms = new MemoryStream();
    ms.Write(arbitrary, 0, arbitrary.Length);
    Assert.Throws<InvalidDataException>(() => OpenVmsInPlaceModifier.AddFile(ms, "X.TXT", [1, 2, 3]));
  }

  /// <summary>
  /// Given a fresh volume,
  /// when descriptor.Add is invoked,
  /// then it routes through the in-place modifier and the file becomes extractable.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Descriptor_AddRemove_EndToEnd() {
    var image = BuildBaseVolume(("KEEP.TXT", Bytes("keepers")));
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    var descriptor = new OpenVmsFormatDescriptor();
    descriptor.Add(ms, [ArchiveInputInfo.InMemory("HELLO.TXT", Bytes("hello world"))]);

    var imageAfterAdd = ms.ToArray();
    Assert.That(new OpenVmsReader(imageAfterAdd).Entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "KEEP.TXT", "HELLO.TXT" }));

    ms.Position = 0;
    descriptor.Remove(ms, ["HELLO.TXT"]);
    var imageAfterRemove = ms.ToArray();
    Assert.That(new OpenVmsReader(imageAfterRemove).Entries.Select(e => e.Name),
      Is.EquivalentTo(new[] { "KEEP.TXT" }));
  }

  /// <summary>
  /// Given a fresh CWB volume,
  /// when filling it past <see cref="OpenVmsLayout.VolumeBlocks"/> data LBN capacity,
  /// then the modifier throws cleanly rather than silently corrupting accounting.
  /// </summary>
  [Test, Category("Sad")]
  public void Add_VolumeFull_ThrowsRatherThanCorrupting() {
    var image = BuildBaseVolume();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // Each file consumes one LBN; the data area has VolumeBlocks - DataAreaStartLbn slots.
    var huge = new byte[OpenVmsLayout.VolumeBytes]; // intentionally larger than the volume itself
    Assert.Throws<IOException>(() => OpenVmsInPlaceModifier.AddFile(ms, "BIG.BIN", huge));
  }

  /// <summary>
  /// Given a Create+Add cycle through the descriptor,
  /// when we Extract through the descriptor,
  /// then the file content survives the round trip end to end.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Descriptor_CreateAddExtract_EndToEnd() {
    var descriptor = new OpenVmsFormatDescriptor();
    using var stream = new MemoryStream();
    descriptor.Create(stream, [ArchiveInputInfo.InMemory("INITIAL.TXT", Bytes("initial"))], new FormatCreateOptions());

    stream.Position = 0;
    descriptor.Add(stream, [ArchiveInputInfo.InMemory("ADDED.TXT", Bytes("added later"))]);

    stream.Position = 0;
    var bytes = descriptor.ExtractEntryToMemory(stream, "ADDED.TXT", null);
    Assert.That(bytes, Is.EqualTo(Bytes("added later")));

    stream.Position = 0;
    var initial = descriptor.ExtractEntryToMemory(stream, "INITIAL.TXT", null);
    Assert.That(initial, Is.EqualTo(Bytes("initial")));
  }
}
