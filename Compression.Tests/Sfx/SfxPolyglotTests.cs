#pragma warning disable CS1591
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Lib;
using Compression.Registry;
using NUnit.Framework;

namespace Compression.Tests.Sfx;

/// <summary>
/// The polyglot container has to satisfy two readers that disagree about what a file is: the
/// Windows loader, which sees a PE, and a POSIX shell, which sees a script. These pin the
/// invariants that keep both true at once, plus the third-party requirement that a native tool can
/// reach the payload without running anything.
/// </summary>
[TestFixture]
public class SfxPolyglotTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-polyglot-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, recursive: true); } catch { }
  }

  /// <summary>
  /// A PE with a DOS stub big enough to hold a bootstrap, built the way the real AOT stubs are:
  /// e_lfanew at 0x110, leaving 208 bytes of DOS stub.
  /// </summary>
  private static byte[] BuildFakePe(int peHeaderOffset = 0x110, int sectionBytes = 512) {
    var size = peHeaderOffset + 24 + 224 + 40 + sectionBytes;
    var pe = new byte[size];

    pe[0] = (byte)'M';
    pe[1] = (byte)'Z';
    // The real DOS stub is a tiny 16-bit program plus "This program cannot be run in DOS mode".
    for (var i = 2; i < peHeaderOffset; ++i) pe[i] = (byte)'.';
    BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3C), peHeaderOffset);

    var p = peHeaderOffset;
    pe[p] = (byte)'P'; pe[p + 1] = (byte)'E';
    BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(p + 6), 1);    // one section
    BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(p + 20), 224); // optional header size

    var section = p + 24 + 224;
    BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(section + 16), (uint)sectionBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(section + 20), (uint)(section + 40));
    return pe;
  }

  /// <summary>Stand-in stub bytes, deliberately free of archive signatures.</summary>
  private static byte[] FakeStub(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);

    // A stub that happens to contain a ZIP local-file-header signature would be found by an overlay
    // scan before the real payload, because the stubs precede it. Real compiled stubs can in
    // principle do this; excluded here so the test measures the layout, not the fixture's luck.
    for (var i = 0; i < bytes.Length - 1; ++i)
      if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B) bytes[i] = 0x51;

    return bytes;
  }

  /// <summary>A payload a third-party scanner can recognise: a ZIP local file header.</summary>
  private static byte[] FakeArchive(int length, int seed) {
    var bytes = FakeStub(length, seed);
    bytes[0] = 0x50; bytes[1] = 0x4B; bytes[2] = 0x03; bytes[3] = 0x04;
    return bytes;
  }

  private string WritePolyglot(out byte[] archive, out IReadOnlyList<SfxPolyglot.TargetStub> posix) {
    archive = FakeArchive(4096, 7);
    posix = [
      new("linux-x64", FakeStub(900, 1)),
      new("linux-arm64", FakeStub(1100, 2)),
      new("osx-arm64", FakeStub(700, 3)),
    ];

    var path = Path.Combine(this._dir, "out.exe");
    SfxPolyglot.Write(path, BuildFakePe(), posix, new MemoryStream(archive));
    return path;
  }

  // ── the Windows half ────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAPolyglot_WhenWindowsLooksAtIt_ThenItIsStillAValidPe() {
    var path = this.WritePolyglot(out _, out _);
    var bytes = File.ReadAllBytes(path);

    Assert.Multiple(() => {
      Assert.That(bytes[0], Is.EqualTo((byte)'M'), "the loader checks these two bytes and nothing else up front");
      Assert.That(bytes[1], Is.EqualTo((byte)'Z'));

      var lfanew = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
      Assert.That(lfanew, Is.EqualTo(0x110), "the PE header must not move; nothing is relocated");
      Assert.That(Encoding.ASCII.GetString(bytes, lfanew, 2), Is.EqualTo("PE"));
    });
  }

  /// <summary>
  /// The high byte of e_lfanew sits at 0x3F. Ending the shell's first line there would overwrite it
  /// and produce a file Windows refuses to load, which is exactly the bug this guards.
  /// </summary>
  [Test]
  public void GivenAPolyglot_WhenTheFirstLineEnds_ThenItEndsAfterTheDosHeaderNotInsideIt() {
    var path = this.WritePolyglot(out _, out _);
    var bytes = File.ReadAllBytes(path);

    var firstNewline = Array.IndexOf(bytes, (byte)'\n');
    Assert.That(firstNewline, Is.EqualTo(0x40),
      "line 1 has to cover the whole DOS header so e_lfanew is inside the shell comment");
  }

  // ── the POSIX half ──────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAPolyglot_WhenAShellReadsLineOne_ThenItIsAnAssignmentFollowedByAComment() {
    var path = this.WritePolyglot(out _, out _);
    var bytes = File.ReadAllBytes(path);
    var line = Encoding.ASCII.GetString(bytes, 0, 0x40);

    Assert.Multiple(() => {
      Assert.That(line, Does.StartWith("MZ=1 #"), "MZ has to stay, so it is made into a shell assignment");
      Assert.That(line, Does.Not.Contain("\n"));

      // Everything the DOS header no longer needs is blanked, but e_lfanew itself cannot be: any PE
      // header offset below 16 MB has zero high bytes, so two NULs inside the comment are
      // unavoidable. Shells skip them in a comment; the POSIX smoke test is what proves it.
      Assert.That(bytes.Take(0x3C), Has.None.EqualTo((byte)0),
        "only e_lfanew may contribute NULs to the comment");
    });
  }

  /// <summary>
  /// Follows the generated script's own offsets and confirms each one lands on the stub it claims.
  /// This is the POSIX behaviour without needing a POSIX machine: if these offsets are wrong, the
  /// bootstrap extracts garbage and the file only fails on someone else's computer.
  /// </summary>
  [Test]
  public void GivenAPolyglot_WhenTheSelectorNamesAStub_ThenItsOffsetsAddressThatStubExactly() {
    var path = this.WritePolyglot(out _, out var posix);
    var bytes = File.ReadAllBytes(path);
    var text = Encoding.ASCII.GetString(bytes);

    var cases = Regex.Matches(text, @"(Linux|Darwin):(\w+)\) O=(\d+);L=(\d+);;");
    Assert.That(cases, Has.Count.EqualTo(posix.Count), "one case arm per bundled runtime");

    Assert.Multiple(() => {
      for (var i = 0; i < cases.Count; ++i) {
        // The script is written for `tail -c +N`, which counts from one.
        var start = long.Parse(cases[i].Groups[3].Value) - 1;
        var length = int.Parse(cases[i].Groups[4].Value);
        var expected = posix[i].Bytes;

        Assert.That(length, Is.EqualTo(expected.Length), $"arm {i} length");
        Assert.That(bytes.AsSpan((int)start, length).ToArray(), Is.EqualTo(expected),
          $"arm {i} offset should address the {posix[i].Rid} stub");
      }
    });
  }

  [Test]
  public void GivenAPolyglot_WhenTheBootstrapLocatesTheSelector_ThenItReadsRunnableShell() {
    var path = this.WritePolyglot(out _, out _);
    var bytes = File.ReadAllBytes(path);
    var bootstrap = Encoding.ASCII.GetString(bytes, 0x41, 0x110 - 0x41);

    var match = Regex.Match(bootstrap, @"tail -c \+(\d+) ""\$0""\|head -c (\d+)");
    Assert.That(match.Success, Is.True, $"bootstrap should copy the selector out of itself: {bootstrap}");

    var selector = Encoding.ASCII.GetString(
      bytes, int.Parse(match.Groups[1].Value) - 1, int.Parse(match.Groups[2].Value));

    Assert.Multiple(() => {
      Assert.That(selector, Does.StartWith("u=`uname -s`"));
      Assert.That(selector, Does.Contain("CWB_SFX_SOURCE=\"$0\""),
        "the stub runs from a temp copy, so it has to be told where the payload really is");
      Assert.That(selector, Does.Contain("esac"));
    });
  }

  [Test]
  public void GivenAStubWithNoRoomInItsDosStub_WhenWritingAPolyglot_ThenItSaysSoInsteadOfCorruptingThePe() {
    // e_lfanew right after the DOS header leaves nowhere for a bootstrap.
    var cramped = BuildFakePe(peHeaderOffset: 0x48);

    var ex = Assert.Throws<ArgumentException>(() => SfxPolyglot.Write(
      Path.Combine(this._dir, "cramped.exe"), cramped, [new("linux-x64", FakeStub(10, 1))], new MemoryStream([1, 2, 3])));
    Assert.That(ex!.Message, Does.Contain("DOS stub"));
  }

  [Test]
  public void GivenSomethingThatIsNotAPe_WhenWritingAPolyglot_ThenItRefuses() {
    var ex = Assert.Throws<ArgumentException>(() => SfxPolyglot.Write(
      Path.Combine(this._dir, "bad.exe"), new byte[512], [], new MemoryStream([1, 2, 3])));
    Assert.That(ex!.Message, Does.Contain("not a PE"));
  }

  // ── what the rest of the toolchain still has to see ─────────────────────────────────────────

  [Test]
  public void GivenAPolyglot_WhenTheTrailerIsRead_ThenItLocatesTheArchiveUnchanged() {
    var path = this.WritePolyglot(out var archive, out _);

    using var fs = File.OpenRead(path);
    Assert.That(SfxTrailer.TryRead(fs, out var location), Is.True);

    var actual = new byte[location.Length];
    fs.Position = location.Offset;
    fs.ReadExactly(actual);

    Assert.Multiple(() => {
      Assert.That(location.Length, Is.EqualTo(archive.Length));
      Assert.That(actual, Is.EqualTo(archive), "the payload is copied verbatim, never reframed");
    });
  }

  /// <summary>
  /// The nice-to-have that constrains the layout: a native tool must be able to find and read the
  /// payload in place, without executing our stub. That only holds while the payload stays a
  /// contiguous, unmodified archive sitting in the PE overlay — so the extra POSIX stubs go
  /// <i>before</i> it, never interleaved with it.
  /// </summary>
  [Test]
  public void GivenAPolyglot_WhenAThirdPartyToolScansThePeOverlay_ThenItFindsTheArchive() {
    var path = this.WritePolyglot(out var archive, out _);

    using var fs = File.OpenRead(path);
    var overlay = PeOverlay.FindOverlayOffset(fs);
    Assert.That(overlay, Is.GreaterThan(0), "the extra stubs and payload all live in the overlay");

    var found = PeOverlay.ScanForArchive(fs, overlay);
    Assert.That(found, Is.Not.Null, "a tool scanning the overlay for its own signature should hit the payload");

    using var trailerRead = File.OpenRead(path);
    Assert.That(SfxTrailer.TryRead(trailerRead, out var location), Is.True);
    Assert.That(found!.Value.Offset, Is.EqualTo(location.Offset),
      "and what it finds should be the payload itself, not a false positive inside a stub");
    Assert.That(archive, Is.Not.Empty);
  }
}
