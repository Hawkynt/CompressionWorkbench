namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter7;

/// <summary>
/// Represents a page 9 2.
/// </summary>
public class Page9_2 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 1;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         2, 4, 3, 4, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6,
         6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
         7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
         7,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = -526909440;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 1611661312;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 6;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = {
        24,
        23,
        25,
        22,
        26,
        21,
        27,
        20,
        28,
        19,
        29,
        18,
        30,
        17,
        31,
        16,
        32,
        15,
        33,
        14,
        34,
        13,
        35,
        12,
        36,
        11,
        37,
        10,
        38,
        9,
        39,
        8,
        40,
        7,
        41,
        6,
        42,
        5,
        43,
        4,
        44,
        3,
        45,
        2,
        46,
        1,
        47,
        0,
        48,
    };
}
