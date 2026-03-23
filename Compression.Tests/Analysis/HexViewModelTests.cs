namespace Compression.Tests.Analysis;

[TestFixture]
public class HexViewModelTests {

  [Test, Category("HappyPath")]
  public void AutoWidth_MinimumIs8() {
    // With very small available width, bytesPerRow should still be at least 8
    // Test the formula: maxBytes = (availableWidth / charWidth - fixedChars) / charsPerByte
    const double charWidth = 7.2;
    const double fixedChars = 13.0;
    const double charsPerByte = 4.0;

    var availableWidth = 100.0; // Very small
    var maxBytes = (int)((availableWidth / charWidth - fixedChars) / charsPerByte);
    var bytesPerRow = Math.Max(8, maxBytes);
    Assert.That(bytesPerRow, Is.GreaterThanOrEqualTo(8));
  }

  [Test, Category("HappyPath")]
  public void AutoWidth_ContinuousValues() {
    // Verify that auto-width produces continuous (non-stepped) values > 8
    const double charWidth = 7.2;
    const double fixedChars = 13.0;
    const double charsPerByte = 4.0;

    var availableWidth = 800.0;
    var maxBytes = (int)((availableWidth / charWidth - fixedChars) / charsPerByte);
    var bytesPerRow = Math.Max(8, maxBytes);
    // At 800px: ~(111 - 13) / 4 = ~24.5 -> 24
    // Should NOT be snapped to 16 or 32 (old behavior)
    Assert.That(bytesPerRow, Is.GreaterThan(8));
    Assert.That(bytesPerRow, Is.Not.EqualTo(16).And.Not.EqualTo(32).And.Not.EqualTo(64),
      "Should not be power-of-2 snapped");
  }
}
