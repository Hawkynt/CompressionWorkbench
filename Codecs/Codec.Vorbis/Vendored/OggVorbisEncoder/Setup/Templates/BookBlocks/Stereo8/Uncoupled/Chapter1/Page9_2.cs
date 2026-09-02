namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo8.Uncoupled.Chapter1;

/// <summary>
/// Represents a page 9 2.
/// </summary>
public class Page9_2 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         2, 5, 4, 6, 6, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9,
         9, 5, 6, 6, 7, 7, 8, 8, 9, 8, 9, 9, 9, 9, 9, 9,
         9, 9, 5, 6, 6, 7, 7, 8, 8, 8, 9, 9, 9, 9, 9, 9,
         9, 9, 9, 7, 7, 7, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9,
         9,10,10, 9, 7, 7, 7, 8, 8, 9, 9, 9, 9, 9, 9, 9,
         9, 9, 9,10,10, 8, 8, 8, 9, 9, 9, 9,10,10,10, 9,
        10,10,10,10,10,10, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9,
        10,10,10,10,10,10,10, 9, 9, 9, 9, 9, 9, 9, 9,10,
        10,10,10,10,10,10,10,10, 9, 9, 9, 9, 9,10,10,10,
        10,10,10,10,10,10,10,10,10, 9, 9, 9, 9, 9, 9,10,
        10,10,10,10,10,10,10,10,10,10, 9, 9, 9, 9, 9,10,
        10,10,10,10,10,10,10,10,10,10,10, 9, 9, 9, 9,10,
        10,10,10,10,10,10,10,10,10,10,10,10, 9, 9, 9, 9,
         9,10,10,10,10,10,10,10,10,10,10,10,10, 9, 9, 9,
        10,10,10,10,10,10,10,10,10,10,10,10,10,10, 9,10,
         9, 9, 9,10,10,10,10,10,10,10,10,10,10,10,10, 9,
        10, 9,10,10,10,10,10,10,10,10,10,10,10,10,10,10,
         9, 9,10,10,10,10,10,10,10,10,10,10,10,10,10,10,
        10,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = -529530880;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 1611661312;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 5;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = {
        8,
        7,
        9,
        6,
        10,
        5,
        11,
        4,
        12,
        3,
        13,
        2,
        14,
        1,
        15,
        0,
        16,
    };
}
