namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter2;

/// <summary>
/// Represents a page 8 0.
/// </summary>
public class Page8_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         1, 4, 4, 6, 6, 7, 7, 7, 7, 8, 8, 9, 9, 6, 5, 5,
         7, 7, 8, 8, 8, 8, 9, 9,10,10, 7, 6, 5, 7, 7, 8,
         8, 8, 8, 9, 9,10,10, 0, 8, 8, 8, 8, 9, 9, 9, 9,
        10,10,11,11, 0, 8, 8, 8, 8, 9, 9, 9, 9,10,10,11,
        11, 0,12,12, 9, 9,10,10,10,10,11,11,11,11, 0,13,
        13, 9, 9,10,10,10,10,11,11,12,12, 0, 0, 0,10,10,
        10,10,11,11,12,12,12,13, 0, 0, 0,10,10,10,10,11,
        11,12,12,12,12, 0, 0, 0,14,14,10,11,11,11,12,12,
        13,13, 0, 0, 0,14,14,11,10,11,11,13,12,13,13, 0,
         0, 0, 0, 0,12,12,11,12,13,12,14,14, 0, 0, 0, 0,
         0,12,12,12,12,13,12,14,14,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = -526516224;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 1616117760;
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
