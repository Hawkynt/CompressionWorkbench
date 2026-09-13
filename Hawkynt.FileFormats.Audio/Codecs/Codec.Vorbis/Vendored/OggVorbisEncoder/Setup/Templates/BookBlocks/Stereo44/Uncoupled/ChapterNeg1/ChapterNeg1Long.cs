namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.ChapterNeg1;

/// <summary>
/// Represents a chapter neg 1 long.
/// </summary>
public class ChapterNeg1Long : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         5, 6,12, 9,14, 9, 9,19, 6, 1, 5, 5, 8, 7, 9,19,
        12, 4, 4, 7, 7, 9,11,18, 9, 5, 6, 6, 8, 7, 8,17,
        14, 8, 7, 8, 8,10,12,18, 9, 6, 8, 6, 8, 6, 8,18,
         9, 8,11, 8,11, 7, 5,15,16,18,18,18,17,15,11,18,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = null;
}
