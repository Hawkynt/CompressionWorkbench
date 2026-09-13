namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.ManagedChapter1;

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
         1, 4, 4, 6, 6, 7, 7, 9, 9,10,11,12,12, 6, 5, 5,
         7, 7, 8, 7,10,10,11,11,12,12, 6, 5, 5, 7, 7, 8,
         8,10,10,11,11,12,12,16, 7, 7, 8, 8, 9, 9,11,11,
        12,12,13,13,17, 7, 7, 8, 7, 9, 9,11,10,12,12,13,
        13,19,11,10, 8, 8,10,10,11,11,12,12,13,13,19,11,
        11, 9, 7,11,10,11,11,12,12,13,12,19,19,19,10,10,
        10,10,11,12,12,12,13,14,18,19,19,11, 9,11, 9,13,
        12,12,12,13,13,19,20,19,13,15,11,11,12,12,13,13,
        14,13,18,19,20,15,13,12,10,13,10,13,13,13,14,20,
        20,20,20,20,13,14,12,12,13,12,13,13,20,20,20,20,
        20,13,12,12,12,14,12,14,13,
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
