using Compression.Core.Dictionary.Crush;
using Compression.Tests.CodecProof;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Third-party proof for <c>BB_Crush</c>. The files below were written by Ilya Muravyov's
/// CRUSH 1.00 (<c>crush100.zip</c> from http://sourceforge.net/projects/crush/, the author's
/// Windows build) with <c>cf</c>, <c>c</c> and <c>cx</c>; the public-domain <c>crush.cpp</c> from
/// <c>crush100src.zip</c>, built with g++ 16.2, writes the same bytes. Our encoder reproduces
/// each level byte for byte, our decoder restores them, and <c>crush d</c> restored every file we
/// wrote for the same inputs.
/// </summary>
[TestFixture]
public sealed class CrushReferenceTests {

  private const string CrushFast =
    "AAQAAMrMGRMGThkQb+SQKSMHBB00ZUDICePmTBkQZN6cFZgOEnPytBHzhg2IOHXSjFkDAobCw0FNnTZw5gqQ2ITRkwfEjRwxcih4YzfglKC0eU" +
    "OmDFsD5sEVowaOGTgUmHmDB8SYN27olMFDB2QB/og+loe6C2BjewCLHA78g48YOW/uuAEhY8aNGjcUWjgP/UGYxn3gHlBmxLBxw4bCB/YxJeGt" +
    "KD4oM3LUyFFDYWH7iB4KNG7UiEFDoUj+oP1IWvA3Pw2Wj+OheCgf7JNCBpUaNWjMmCkjdfB/oGzMmFFDZuckj/BjKOJayH5e3Ygh40ZMEUyT9c" +
    "HxkJa5WYm1SG5awVB46A/aAmMBo0H50ihAwXEjBowcCgWYj3UnpAF7YTzAG2kBNBOWHDVgyMCh0AT0wW8IyaS1aMjIQdNG7QH9QBbSG5Qy3z9L" +
    "F5IYMHDYmKFwoC2MB1IRQQG1gHGhr4AOwgNXDBw3cMhQSOP5WBfSFdzL4QV0UGVyIcPGDRgxZZIeigssG4GP6WH7eEqwC+QCUUjCwrMQmkHDhg" +
    "wYCg/Wx/0y+cgX0BXzR3UyyWJK5gkNGTVm5BQC81AUqeygAQ==";

  private const string CrushNormal =
    "AAQAAMrMGRMGThkQb+SQKSMHBB00ZUDICePmTBkQZN6cFZgOEnPytBHzhg2IOHXSjFkDAobCw0FNnTZw5gqQ2ITRkwfEjRwxcih4YzfglKC0eU" +
    "OmDFsD5sEVowaOGTgUmHmDB8SYN27olMFDB2QB/og+loe6C2BjewCLHA78g48YOW/uuAEhY8aNGjcUWjgP/UGYxn3gHlBmxLBxw4bCB/YxJeGt" +
    "KD4oM3LUyFFDYWH7iB4KNG7UiEFDoUj+oP1IWvA3Pw2Wj+OheCgf7JNCBpUaNWjMmCkjdfB/oGzMmFFDZuckj/BjyMbl59WNGDJuxBTBNFkfHO" +
    "ncrMRaJDetYCg89AdtgbGAkY9TAQqOGzFg5FAowHysOyEN2AvjAd5IU6rxwpKjBgwZOBSagD74DSGZtBYNGTlo2qg9oMm83qCU+f5ZupDEgIHD" +
    "xgyFA21hTAhXAbWAsVN1AjoID1wxcNzAIUMhjedj7fP3cujEJZMLGTZuwIgpk5RQz2QEPqaH7eNpZHdpSCc7CU0xg4YNGTAUHqyPOyW7B+iKuU" +
    "9wFlMyT2jIqDEjpxCYlBzsoAE=";

  private const string CrushMaximum =
    "AAQAAMrMGRMGThkQb+SQKSMHBB00ZUDICePmTBkQZN6cFZgOEnPytBHzhg2IOHXSjFkDAobCw0FNnTZw5gqQ2ITRkwfEjRwxcih4YzfglKC0eU" +
    "OmDFsD5sEVowaOGTgUmHmDB8SYN27olMFDB2QB/og+loe6C2BjewCLHA78g48YOW/uuAEhY8aNGjcUWjgP/UGYxn3gHlBmxLBxw4bCB/YxJeGt" +
    "KD4oM3LUyFFDYWH7iB4KNG7UiEFDoUj+oP1IWvA3Pw2Wj+OheCgf7JNCBpUaNWjMmCkjdfB/oGzMmFFDZuckj/BjyMbl59WNGDJuxFDIZX1wpH" +
    "OzEmuR3LSCofDQH7QFxgJGPk4FKDhuxICRQ6EA87HuhDRgL4wHeCNNqcYLS44aMGTgUGgC+uA3hGTSWjRk5KBpo/aAJvN6g1Lm+2fpQhIDBg4b" +
    "MxQOtIUxIVwF1ALGThW7QtoOXDFw3MAhQyGN52Pt8/dy6MQlkwsZNm7AiCmTlFDPZAQ+poft42lkd2lIJzsJTTGDhg0ZMBQerI87JbvsGmXsE5" +
    "zFlMwTGjJqzMgpBCYlBzto";

  [TestCase(0, CrushFast, TestName = "GivenTheSampleText_WhenCompressedFast_ThenItIsWhatCrushCfWrites")]
  [TestCase(1, CrushNormal, TestName = "GivenTheSampleText_WhenCompressed_ThenItIsWhatCrushCWrites")]
  [TestCase(2, CrushMaximum, TestName = "GivenTheSampleText_WhenCompressedMaximum_ThenItIsWhatCrushCxWrites")]
  [Category("HappyPath")]
  public void EncoderMatchesTheReference(int level, string expected)
    => Assert.That(CrushCodec.Compress(CodecProofData.Text(1024), level), Is.EqualTo(Convert.FromBase64String(expected)));

  [TestCase(CrushFast, TestName = "GivenACrushCfFile_WhenDecoded_ThenItIsTheSample")]
  [TestCase(CrushNormal, TestName = "GivenACrushCFile_WhenDecoded_ThenItIsTheSample")]
  [TestCase(CrushMaximum, TestName = "GivenACrushCxFile_WhenDecoded_ThenItIsTheSample")]
  [Category("HappyPath")]
  public void DecoderRestoresTheReference(string file)
    => Assert.That(CrushCodec.Decompress(Convert.FromBase64String(file)), Is.EqualTo(CodecProofData.Text(1024)));

  [Test, Category("HappyPath")]
  public void GivenTheBuildingBlock_WhenCompressing_ThenItWritesTheNormalLevel()
    => Assert.That(new CrushBuildingBlock().Compress(CodecProofData.Text(1024)), Is.EqualTo(Convert.FromBase64String(CrushNormal)));

  [Test, Category("BoundaryCase")]
  public void GivenNoBytes_WhenCompressed_ThenTheFileIsEmptyLikeTheReference() {
    Assert.Multiple(() => {
      Assert.That(CrushCodec.Compress([]), Is.Empty);
      Assert.That(CrushCodec.Decompress([]), Is.Empty);
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenTwoFilesConcatenated_WhenDecoded_ThenBothBlocksAreRestored() {
    var file = Convert.FromBase64String(CrushNormal);
    Assert.That(CrushCodec.Decompress([.. file, .. file]), Is.EqualTo(CodecProofData.Text(1024).Concat(CodecProofData.Text(1024)).ToArray()));
  }

  [Test, Category("BoundaryCase")]
  public void GivenAMatchAtTheFarEndOfTheWindow_WhenRoundTripped_ThenItIsRestored() {
    var head = CodecProofData.Random(70_000);
    byte[] data = [.. head, .. head.AsSpan(0, 600)];
    Assert.That(CrushCodec.Decompress(CrushCodec.Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnOutOfRangeLevel_WhenCompressing_ThenItIsRejected()
    => Assert.Throws<ArgumentOutOfRangeException>(() => CrushCodec.Compress("x"u8, 3));

  [Test, Category("ExceptionalCase")]
  public void GivenAZeroBlockSize_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => CrushCodec.Decompress([0, 0, 0, 0, 0]));

  [Test, Category("ExceptionalCase")]
  public void GivenATruncatedFile_WhenDecoded_ThenItIsRefused() {
    var file = Convert.FromBase64String(CrushNormal);
    Assert.Throws<InvalidDataException>(() => CrushCodec.Decompress(file.AsSpan(0, file.Length - 10)));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAMatchBeforeTheStart_WhenDecoded_ThenItIsRefused()
    // size 8, first token: match bit 1, length class "1 00", slot 0, offset 0 -> reaches position -1.
    => Assert.Throws<InvalidDataException>(() => CrushCodec.Decompress([8, 0, 0, 0, 0b0000_0011, 0, 0, 0]));
}
