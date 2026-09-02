namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.ManagedChapterNeg1;

/// <summary>
/// Represents a page 8 1.
/// </summary>
public class Page8_1 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         1, 4, 4, 6, 6, 8, 8, 9, 9,10,11,11,11, 6, 5, 5,
         7, 7, 8, 8,10,10,10,11,11,11, 6, 5, 5, 7, 7, 8,
         8,10,10,11,12,12,12,14, 7, 7, 7, 8, 9, 9,11,11,
        11,12,11,12,17, 7, 7, 8, 7, 9, 9,11,11,12,12,12,
        12,14,11,11, 8, 8,10,10,11,12,12,13,11,12,14,11,
        11, 8, 8,10,10,11,12,12,13,13,12,14,15,14,10,10,
        10,10,11,12,12,12,12,11,14,13,16,10,10,10, 9,12,
        11,12,12,13,14,14,15,14,14,13,10,10,11,11,12,11,
        13,11,14,12,15,13,14,11,10,12,10,12,12,13,13,13,
        13,14,15,15,12,12,11,11,12,11,13,12,14,14,14,14,
        17,12,12,11,10,13,11,13,13,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = -522616832;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 1620115456;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 4;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = {
        6,
        5,
        7,
        4,
        8,
        3,
        9,
        2,
        10,
        1,
        11,
        0,
        12,
    };
}
