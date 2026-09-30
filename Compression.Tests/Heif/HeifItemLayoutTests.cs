#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Heif;

namespace Compression.Tests.Heif;

/// <summary>
/// The owning <see cref="HeifReader"/> and the zero-copy span path share one
/// <c>iloc</c> parser. These cases pin the item-data rules of ISO/IEC 14496-12
/// §8.11.3 (an item is the concatenation of its extents, in order) and the
/// defensive boundaries a malformed file must not turn into an exception.
/// </summary>
[TestFixture]
public class HeifItemLayoutTests {

  private static readonly byte[] Tail = Enumerable.Range(0, 64).Select(static i => (byte)(0xA0 + i)).ToArray();

  private static byte[] Box(string type, byte[] body) {
    var box = new byte[8 + body.Length];
    BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
    Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
    body.CopyTo(box, 8);
    return box;
  }

  /// <summary>
  /// ftyp + meta(iinf, iloc v1) + an unboxed tail. Extent offsets are relative
  /// to the tail unless <paramref name="absolute"/> is set.
  /// </summary>
  private static byte[] Build(
      ushort constructionMethod,
      IReadOnlyList<(ulong Offset, ulong Length)> extents,
      int offsetSize = 4,
      bool absolute = false,
      byte[]? tail = null) {
    tail ??= Tail;
    var ftyp = Box("ftyp", [.. "heic"u8, 0, 0, 0, 0, .. "mif1"u8]);
    var infe = Box("infe", [2, 0, 0, 0, 0, 1, 0, 0, .. "hvc1"u8, 0]);
    var iinf = Box("iinf", [0, 0, 0, 0, 0, 1, .. infe]);

    byte[] BuildIloc(long tailStart) {
      var body = new List<byte> { 1, 0, 0, 0, (byte)((offsetSize << 4) | 4), 0, 0, 1, 0, 1 };
      body.AddRange([(byte)(constructionMethod >> 8), (byte)constructionMethod, 0, 0]);
      body.AddRange([(byte)(extents.Count >> 8), (byte)extents.Count]);
      Span<byte> scratch = stackalloc byte[8];
      foreach (var (offset, length) in extents) {
        var value = absolute ? offset : offset + (ulong)tailStart;
        BinaryPrimitives.WriteUInt64BigEndian(scratch, value);
        body.AddRange(scratch[(8 - offsetSize)..].ToArray());
        BinaryPrimitives.WriteUInt32BigEndian(scratch, (uint)length);
        body.AddRange(scratch[..4].ToArray());
      }
      return Box("iloc", [.. body]);
    }

    // The iloc size does not depend on the offset values, so a first pass
    // fixes where the tail lands.
    var metaLength = 8 + 4 + iinf.Length + BuildIloc(0).Length;
    var tailStart = ftyp.Length + metaLength;
    var meta = Box("meta", [0, 0, 0, 0, .. iinf, .. BuildIloc(tailStart)]);
    return [.. ftyp, .. meta, .. tail];
  }

  [Test, Category("HappyPath")]
  public void GivenItemSplitAcrossExtents_WhenRead_ThenBytesFollowExtentOrderNotFileOrder() {
    var file = Build(0, [(40, 5), (3, 4)]);

    var item = new HeifReader(file).ReadItem(1);

    Assert.That(item, Is.EqualTo(Tail[40..45].Concat(Tail[3..7]).ToArray()));
  }

  [Test, Category("HappyPath")]
  public void GivenItemSplitAcrossExtents_WhenListedAsSpan_ThenSizeMatchesOwningRead() {
    var file = Build(0, [(40, 5), (3, 4)]);
    var ops = (IArchiveFormatOperations)new HeifFormatDescriptor();

    var entries = ops.ListSpan(file, null);

    var item = entries.Single(e => e.Name.Contains("hvc1", StringComparison.Ordinal));
    Assert.That(item.OriginalSize, Is.EqualTo(9));
  }

  [TestCase((ushort)1, TestName = "GivenIdatConstructionMethod_WhenRead_ThenItemIsEmpty")]
  [TestCase((ushort)2, TestName = "GivenItemReferenceConstructionMethod_WhenRead_ThenItemIsEmpty")]
  [Category("EdgeCase")]
  public void GivenUnsupportedConstructionMethod_WhenRead_ThenItemIsEmpty(ushort method) {
    var file = Build(method, [(0, 8)]);

    Assert.That(new HeifReader(file).ReadItem(1), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void GivenExtentEndingExactlyAtEndOfFile_WhenRead_ThenItIsIncluded() {
    var file = Build(0, [(60, 4)]);

    Assert.That(new HeifReader(file).ReadItem(1), Is.EqualTo(Tail[60..64]));
  }

  [Test, Category("Boundary")]
  public void GivenExtentOneBytePastEndOfFile_WhenRead_ThenOnlyTheValidExtentRemains() {
    var file = Build(0, [(0, 2), (61, 4)]);

    Assert.That(new HeifReader(file).ReadItem(1), Is.EqualTo(Tail[0..2]));
  }

  [Test, Category("ErrorHandling")]
  public void GivenEightByteOffsetAboveLongMaxValue_WhenOpened_ThenExtentIsSkippedWithoutThrowing() {
    var file = Build(0, [(ulong.MaxValue, 4), (0, 3)], offsetSize: 8, absolute: true);

    byte[] item = [];
    Assert.DoesNotThrow(() => item = new HeifReader(file).ReadItem(1));
    Assert.That(item, Is.EqualTo(file[..3]), "only the in-range extent (absolute offset 0) contributes");
  }

  [Test, Category("ErrorHandling")]
  public void GivenExtentsRepeatingSourceBeyondArrayLimit_WhenListed_ThenItemIsDroppedInsteadOfThrowing() {
    // 65 535 extents of the same 40 000 bytes add up to ~2.6 GB, more than one
    // array can hold; the item is unreadable, the listing still succeeds.
    var tail = new byte[40_000];
    var extents = Enumerable.Repeat((0UL, 40_000UL), ushort.MaxValue).ToArray();
    var file = Build(0, extents, tail: tail);
    var ops = (IArchiveFormatOperations)new HeifFormatDescriptor();

    List<ArchiveEntryInfo> entries = [];
    Assert.DoesNotThrow(() => entries = ops.ListSpan(file, null));
    Assert.Multiple(() => {
      Assert.That(entries.Select(e => e.Name), Has.None.Contains("hvc1"));
      Assert.That(new HeifReader(file).ReadItem(1), Is.Empty);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenPrefixSpanningTwoRanges_WhenSkipped_ThenRemainderStartsInsideTheSecond() {
    IReadOnlyList<HeifReader.SourceRange> ranges = [new(10, 3), new(20, 5)];

    var skipped = HeifReader.SkipPrefix(ranges, 4);

    Assert.That(skipped, Is.EqualTo(new HeifReader.SourceRange[] { new(21, 4) }));
  }

  [Test, Category("Boundary")]
  public void GivenPrefixEqualToFirstRange_WhenSkipped_ThenFirstRangeDisappears() {
    IReadOnlyList<HeifReader.SourceRange> ranges = [new(10, 4), new(20, 5)];

    Assert.That(HeifReader.SkipPrefix(ranges, 4), Is.EqualTo(new HeifReader.SourceRange[] { new(20, 5) }));
  }

  [TestCase(0, TestName = "GivenZeroPrefix_WhenSkipped_ThenRangesAreUnchanged")]
  [TestCase(-1, TestName = "GivenNegativePrefix_WhenSkipped_ThenRangesAreUnchanged")]
  [TestCase(8, TestName = "GivenPrefixCoveringEverything_WhenSkipped_ThenRangesAreUnchanged")]
  [Category("Boundary")]
  public void GivenDegeneratePrefix_WhenSkipped_ThenRangesAreUnchanged(int prefix) {
    IReadOnlyList<HeifReader.SourceRange> ranges = [new(10, 3), new(20, 5)];

    Assert.That(HeifReader.SkipPrefix(ranges, prefix), Is.EqualTo(ranges));
  }
}
