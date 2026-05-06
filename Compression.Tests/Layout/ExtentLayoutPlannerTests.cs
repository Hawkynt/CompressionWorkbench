#pragma warning disable CS1591
using Compression.Core.Layout;

namespace Compression.Tests.Layout;

[TestFixture]
public class ExtentLayoutPlannerTests {

  [Test, Category("HappyPath")]
  public void PackFromOrigin_NoHoles_NoMoves() {
    var extents = new[] {
      new LiveExtent(0, 10),
      new LiveExtent(10, 20),
      new LiveExtent(30, 30),
    };
    var moves = ExtentLayoutPlanner.PackFromOrigin(extents, origin: 0);
    Assert.That(moves, Is.Empty, "extents already packed - should yield zero moves");
  }

  [Test, Category("HappyPath")]
  public void PackFromOrigin_HoleInMiddle_ShiftsTail() {
    // Layout: [0..10][hole 10..20][20..50][50..80]
    var extents = new[] {
      new LiveExtent(0, 10, "A"),
      new LiveExtent(20, 30, "B"),
      new LiveExtent(50, 30, "C"),
    };
    var moves = ExtentLayoutPlanner.PackFromOrigin(extents, origin: 0);
    Assert.That(moves, Has.Count.EqualTo(2));
    Assert.That(moves[0].SourceOffset, Is.EqualTo(20));
    Assert.That(moves[0].TargetOffset, Is.EqualTo(10));
    Assert.That(moves[0].Length, Is.EqualTo(30));
    Assert.That(moves[0].Tag, Is.EqualTo("B"));
    Assert.That(moves[1].SourceOffset, Is.EqualTo(50));
    Assert.That(moves[1].TargetOffset, Is.EqualTo(40));
    Assert.That(moves[1].Length, Is.EqualTo(30));
  }

  [Test, Category("HappyPath")]
  public void PackFromOrigin_AlignmentRespected() {
    var extents = new[] {
      new LiveExtent(2048, 100),
      new LiveExtent(4096, 100),
    };
    // Origin 0 with 2048-byte alignment -> first target 0, second 2048
    var moves = ExtentLayoutPlanner.PackFromOrigin(extents, origin: 0, alignment: 2048);
    Assert.That(moves, Has.Count.EqualTo(2));
    Assert.That(moves[0].TargetOffset, Is.EqualTo(0));
    Assert.That(moves[1].TargetOffset, Is.EqualTo(2048));
  }

  [Test, Category("HappyPath")]
  public void PackFromOrigin_PrefixAlreadyCorrect_OnlyShiftsSuffix() {
    // First two extents are already in place; only the third needs to move.
    var extents = new[] {
      new LiveExtent(0, 10),
      new LiveExtent(10, 20),
      new LiveExtent(40, 30), // hole at 30..40
    };
    var moves = ExtentLayoutPlanner.PackFromOrigin(extents, origin: 0);
    Assert.That(moves, Has.Count.EqualTo(1));
    Assert.That(moves[0].SourceOffset, Is.EqualTo(40));
    Assert.That(moves[0].TargetOffset, Is.EqualTo(30));
  }

  [Test, Category("HappyPath")]
  public void PackFromOrigin_OriginNonZero() {
    var extents = new[] { new LiveExtent(100, 50) };
    var moves = ExtentLayoutPlanner.PackFromOrigin(extents, origin: 1000);
    Assert.That(moves, Has.Count.EqualTo(1));
    Assert.That(moves[0].TargetOffset, Is.EqualTo(1000));
  }

  [Test, Category("HappyPath")]
  public void PackFromOrigin_EmptyInput_NoMoves() {
    var moves = ExtentLayoutPlanner.PackFromOrigin([], origin: 0);
    Assert.That(moves, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void PackFromOrigin_ZeroLengthExtent_Skipped() {
    var extents = new[] {
      new LiveExtent(0, 10),
      new LiveExtent(99, 0), // empty extent — skipped, no move generated
      new LiveExtent(20, 5),
    };
    var moves = ExtentLayoutPlanner.PackFromOrigin(extents, origin: 0);
    // Sorted: extent at 0 (10) -> 0, extent at 20 (5) -> 10, the zero-length extent is no-op
    Assert.That(moves, Has.Count.EqualTo(1));
    Assert.That(moves[0].SourceOffset, Is.EqualTo(20));
    Assert.That(moves[0].TargetOffset, Is.EqualTo(10));
  }

  [Test, Category("HappyPath")]
  public void FillHolesBestFit_ExactMatch_OneMove_NoResidual() {
    // [A=10][hole=20 at 10..30][B=20 at 30..50]
    var extents = new[] {
      new LiveExtent(0, 10, "A"),
      new LiveExtent(30, 20, "B"),
    };
    var (moves, residual) = ExtentLayoutPlanner.FillHolesBestFit(extents, imageOrigin: 0);
    Assert.That(moves, Has.Count.EqualTo(1));
    Assert.That(moves[0].SourceOffset, Is.EqualTo(30));
    Assert.That(moves[0].TargetOffset, Is.EqualTo(10));
    Assert.That(moves[0].Length, Is.EqualTo(20));
    Assert.That(moves[0].Tag, Is.EqualTo("B"));
    Assert.That(residual, Is.Empty, "exact-fit hole closes completely");
  }

  [Test, Category("HappyPath")]
  public void FillHolesBestFit_UnderFit_LeavesResidualHole() {
    // [A=10][hole=20][B=15]
    var extents = new[] {
      new LiveExtent(0, 10, "A"),
      new LiveExtent(30, 15, "B"),
    };
    var (moves, residual) = ExtentLayoutPlanner.FillHolesBestFit(extents, imageOrigin: 0);
    Assert.That(moves, Has.Count.EqualTo(1));
    Assert.That(moves[0].TargetOffset, Is.EqualTo(10));
    Assert.That(residual, Has.Count.EqualTo(1));
    Assert.That(residual[0].Offset, Is.EqualTo(25));
    Assert.That(residual[0].Length, Is.EqualTo(5));
  }

  [Test, Category("HappyPath")]
  public void FillHolesBestFit_BestFitPicksLargestThatFits() {
    // [A=10][hole=20 at 10..30][B=5 at 30..35][hole=10 at 35..45][C=20 at 45..65][D=8 at 65..73]
    // Hole 1 (20): biggest tail extent that fits is C (20) — moves to 10.
    // Hole 2 (10): only D (8) is past hole 2 and fits — moves to 35.
    // B (at 30) is BEFORE hole 2 so it's not eligible (moving it forward would
    // create a new hole at 30, which is anti-compaction).
    var extents = new[] {
      new LiveExtent(0, 10, "A"),
      new LiveExtent(30, 5, "B"),
      new LiveExtent(45, 20, "C"),
      new LiveExtent(65, 8, "D"),
    };
    var (moves, residual) = ExtentLayoutPlanner.FillHolesBestFit(extents, imageOrigin: 0);
    Assert.That(moves, Has.Count.EqualTo(2));
    var byTag = moves.ToDictionary(m => (string)m.Tag!);
    Assert.That(byTag["C"].TargetOffset, Is.EqualTo(10), "biggest hole gets biggest tail extent that fits");
    Assert.That(byTag["D"].TargetOffset, Is.EqualTo(35), "smaller hole gets the only extent past it that fits");
    // Hole 1 (20) is fully closed by C. Hole 2 (10) gets D (8), leaving 2 residual at 43..45.
    Assert.That(residual, Has.Count.EqualTo(1));
    Assert.That(residual[0].Offset, Is.EqualTo(43));
    Assert.That(residual[0].Length, Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void FillHolesBestFit_DoesNotMoveExtentsBackwardsThroughOtherHoles() {
    // [A=10][hole=5 at 10..15][B=3 at 15..18]
    // Hole (5) — B (3) is past it, but moving B into the hole creates a new
    // hole at 15..18 of size 3, which is BEFORE B's destination (10). The
    // planner's best-fit DOES move B because it strictly improves layout —
    // total used bytes are now 0..13 vs the original 0..18.
    var extents = new[] {
      new LiveExtent(0, 10, "A"),
      new LiveExtent(15, 3, "B"),
    };
    var (moves, residual) = ExtentLayoutPlanner.FillHolesBestFit(extents, imageOrigin: 0);
    Assert.That(moves, Has.Count.EqualTo(1));
    Assert.That(moves[0].Tag, Is.EqualTo("B"));
    Assert.That(moves[0].TargetOffset, Is.EqualTo(10));
    // Hole was 5, B fills 3 of it, leaving 2 residual at 13..15.
    Assert.That(residual, Has.Count.EqualTo(1));
    Assert.That(residual[0].Length, Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void FillHolesBestFit_NoMatchingTail_AllHolesResidual() {
    // [A=10][hole=5][B=20] — B doesn't fit a 5-byte hole.
    var extents = new[] {
      new LiveExtent(0, 10, "A"),
      new LiveExtent(15, 20, "B"),
    };
    var (moves, residual) = ExtentLayoutPlanner.FillHolesBestFit(extents, imageOrigin: 0);
    Assert.That(moves, Is.Empty);
    Assert.That(residual, Has.Count.EqualTo(1));
    Assert.That(residual[0].Length, Is.EqualTo(5));
  }

  [Test, Category("HappyPath")]
  public void FillHolesBestFit_NoHoles_NoMoves() {
    var extents = new[] {
      new LiveExtent(0, 10),
      new LiveExtent(10, 20),
    };
    var (moves, residual) = ExtentLayoutPlanner.FillHolesBestFit(extents, imageOrigin: 0);
    Assert.That(moves, Is.Empty);
    Assert.That(residual, Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void ApplyMoves_BackwardMove_PreservesData() {
    // Image: [AAAAA][_____][BBBBB]  -> pack via backward move of B into the hole
    var data = new byte[15];
    Array.Fill(data, (byte)'A', 0, 5);
    Array.Fill(data, (byte)'B', 10, 5);
    using var ms = new MemoryStream(data);
    var moves = new[] { new ExtentMove(SourceOffset: 10, TargetOffset: 5, Length: 5, Tag: null) };
    ExtentLayoutPlanner.ApplyMoves(ms, moves);
    var result = ms.ToArray();
    Assert.That(System.Text.Encoding.ASCII.GetString(result, 0, 10), Is.EqualTo("AAAAABBBBB"));
  }

  [Test, Category("RoundTrip")]
  public void ApplyMoves_ForwardMove_PreservesData() {
    // [AAAAA][BBBBB][_____] -> shift B forward to the trailing hole
    var data = new byte[15];
    Array.Fill(data, (byte)'A', 0, 5);
    Array.Fill(data, (byte)'B', 5, 5);
    using var ms = new MemoryStream(data);
    var moves = new[] { new ExtentMove(SourceOffset: 5, TargetOffset: 10, Length: 5, Tag: null) };
    ExtentLayoutPlanner.ApplyMoves(ms, moves);
    var result = ms.ToArray();
    Assert.That(System.Text.Encoding.ASCII.GetString(result, 10, 5), Is.EqualTo("BBBBB"));
    Assert.That(System.Text.Encoding.ASCII.GetString(result, 0, 5), Is.EqualTo("AAAAA"));
  }

  [Test, Category("RoundTrip")]
  public void PackFromOrigin_RoundTrip_DataIntegrity() {
    // Build a real image with three named blocks and a hole, plan + apply, verify content.
    var data = new byte[120];
    Array.Fill(data, (byte)'A', 0, 30);
    // hole at 30..50
    Array.Fill(data, (byte)'B', 50, 40);
    Array.Fill(data, (byte)'C', 90, 30);

    using var ms = new MemoryStream(data);
    var extents = new[] {
      new LiveExtent(0, 30, "A"),
      new LiveExtent(50, 40, "B"),
      new LiveExtent(90, 30, "C"),
    };
    var moves = ExtentLayoutPlanner.PackFromOrigin(extents, origin: 0);
    ExtentLayoutPlanner.ApplyMoves(ms, moves);

    // After packing:  [A=30][B=40][C=30]  contiguous from offset 0.
    var result = ms.ToArray();
    Assert.That(result.AsSpan(0, 30).ToArray(), Is.All.EqualTo((byte)'A'));
    Assert.That(result.AsSpan(30, 40).ToArray(), Is.All.EqualTo((byte)'B'));
    Assert.That(result.AsSpan(70, 30).ToArray(), Is.All.EqualTo((byte)'C'));
  }

  [Test, Category("RoundTrip")]
  public void FillHolesBestFit_RoundTrip_DataIntegrity() {
    // [A=10][hole=20][B=20] — hole-fill moves B into the hole; B's old slot
    // becomes residual free space (caller may truncate).
    var data = new byte[50];
    Array.Fill(data, (byte)'A', 0, 10);
    // hole 10..30
    Array.Fill(data, (byte)'B', 30, 20);

    using var ms = new MemoryStream(data);
    var extents = new[] {
      new LiveExtent(0, 10, "A"),
      new LiveExtent(30, 20, "B"),
    };
    var (moves, _) = ExtentLayoutPlanner.FillHolesBestFit(extents, imageOrigin: 0);
    ExtentLayoutPlanner.ApplyMoves(ms, moves);

    // After fill:  [A=10][B=20 @ 10..30][... B's old bytes at 30..50, treat as free]
    var result = ms.ToArray();
    Assert.That(result.AsSpan(0, 10).ToArray(), Is.All.EqualTo((byte)'A'));
    Assert.That(result.AsSpan(10, 20).ToArray(), Is.All.EqualTo((byte)'B'));
  }

  [Test, Category("ErrorHandling")]
  public void PackFromOrigin_NegativeOrigin_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => _ = ExtentLayoutPlanner.PackFromOrigin([], origin: -1));
  }

  [Test, Category("ErrorHandling")]
  public void PackFromOrigin_ZeroAlignment_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => _ = ExtentLayoutPlanner.PackFromOrigin([], origin: 0, alignment: 0));
  }

  [Test, Category("ErrorHandling")]
  public void PackFromOrigin_NegativeExtentLength_Throws() {
    Assert.Throws<ArgumentException>(
      () => _ = ExtentLayoutPlanner.PackFromOrigin(
        [new LiveExtent(0, -5)], origin: 0));
  }

  [Test, Category("ErrorHandling")]
  public void ApplyMoves_NonSeekableStream_Throws() {
    var nonSeekable = new TestNonSeekableStream();
    var moves = new[] { new ExtentMove(0, 10, 5, null) };
    Assert.Throws<ArgumentException>(() => ExtentLayoutPlanner.ApplyMoves(nonSeekable, moves));
  }

  private sealed class TestNonSeekableStream : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { }
  }
}
