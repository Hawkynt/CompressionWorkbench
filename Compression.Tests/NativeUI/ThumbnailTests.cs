using System.Linq;
using Compression.NativeUI.Controls;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>Shrinking pictures into the thumbnail square.</summary>
[TestFixture]
public sealed class ThumbnailTests {
  private const int Red = unchecked((int)0xFFFF0000);
  private const int Blue = unchecked((int)0xFF0000FF);

  private static int[] Solid(int width, int height, int color) => Enumerable.Repeat(color, width * height).ToArray();

  private static (int Left, int Top, int Right, int Bottom) Painted(int[] square, int size) {
    int left = size, top = size, right = -1, bottom = -1;
    for (var y = 0; y < size; ++y)
      for (var x = 0; x < size; ++x)
        if (square[y * size + x] != 0) {
          left = System.Math.Min(left, x);
          top = System.Math.Min(top, y);
          right = System.Math.Max(right, x);
          bottom = System.Math.Max(bottom, y);
        }

    return (left, top, right, bottom);
  }

  [Test]
  public void GivenAPictureAsLargeAsTheSquare_WhenFitted_ThenItFillsItExactly() {
    var square = Thumbnail.Fit(Solid(8, 8, Red), 8, 8, size: 8);

    Assert.That(square, Is.All.EqualTo(Red));
  }

  [Test]
  public void GivenASmallerPicture_WhenFitted_ThenItKeepsItsSizeAndIsCentred() {
    var square = Thumbnail.Fit(Solid(2, 2, Red), 2, 2, size: 8);

    Assert.That(Painted(square, 8), Is.EqualTo((3, 3, 4, 4)), "a 2x2 icon is not blown up into blocks");
  }

  [Test]
  public void GivenAWidePicture_WhenFitted_ThenItsAspectIsKeptWithBandsAboveAndBelow() {
    var square = Thumbnail.Fit(Solid(40, 10, Red), 40, 10, size: 8);

    Assert.That(Painted(square, 8), Is.EqualTo((0, 3, 7, 4)));
  }

  [Test]
  public void GivenATallPicture_WhenFitted_ThenItsAspectIsKeptWithBandsLeftAndRight() {
    var square = Thumbnail.Fit(Solid(10, 40, Red), 10, 40, size: 8);

    Assert.That(Painted(square, 8), Is.EqualTo((3, 0, 4, 7)));
  }

  [Test]
  public void GivenFineDetail_WhenShrunk_ThenItAveragesIntoOneTone() {
    // A 2x2 checkerboard of red and blue shrunk to one pixel is the mean of the four.
    int[] checker = [Red, Blue, Blue, Red];

    var square = Thumbnail.Fit(checker, 2, 2, size: 1);

    Assert.That((uint)square[0], Is.EqualTo(0xFF7F007Fu));
  }

  [Test]
  public void GivenTransparency_WhenShrunk_ThenAlphaIsAveragedToo() {
    int[] half = [Red, 0, Red, 0];

    var square = Thumbnail.Fit(half, 2, 2, size: 1);

    Assert.That((uint)square[0] >> 24, Is.EqualTo(0x7Fu));
  }

  [TestCase(0, 5)]
  [TestCase(5, 0)]
  [TestCase(-1, 5)]
  public void GivenAPictureWithNoArea_WhenFitted_ThenTheSquareIsEmpty(int width, int height)
    => Assert.That(Thumbnail.Fit([Red], width, height, size: 4), Is.All.Zero);

  [Test]
  public void GivenFewerPixelsThanTheSizeClaims_WhenFitted_ThenTheSquareIsEmptyRatherThanReadingPastTheEnd()
    => Assert.That(Thumbnail.Fit(Solid(3, 3, Red), 4, 4, size: 4), Is.All.Zero);

  [Test]
  public void GivenTheDefaultSize_WhenFitted_ThenTheSquareIsNinetySixPixelsASide()
    => Assert.That(Thumbnail.Fit(Solid(1, 1, Red), 1, 1), Has.Length.EqualTo(96 * 96));
}
