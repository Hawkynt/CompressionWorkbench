namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter4;

/// <summary>
/// Represents a page 9 1.
/// </summary>
public class Page9_1 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 4, 5, 5, 7, 7, 9, 8,10, 9,10,10,10,10, 6,
         5, 5, 7, 7, 9, 8,10, 9,11,10,12,12,13,13, 6, 5,
         5, 7, 7, 9, 9,10,10,11,11,12,12,12,13,19, 8, 8,
         8, 8, 9, 9,10,10,12,11,12,12,13,13,19, 8, 8, 8,
         8, 9, 9,11,11,12,12,13,13,13,13,19,12,12, 9, 9,
        11,11,11,11,12,11,13,12,13,13,18,12,12, 9, 9,11,
        10,11,11,12,12,12,13,13,14,19,18,18,11,11,11,11,
        12,12,13,12,13,13,14,14,16,18,18,11,11,11,10,12,
        11,13,13,13,13,13,14,17,18,18,14,15,11,12,12,13,
        13,13,13,14,14,14,18,18,18,15,15,12,10,13,10,13,
        13,13,13,13,14,18,17,18,17,18,12,13,12,13,13,13,
        14,14,16,14,18,17,18,18,17,13,12,13,10,12,12,14,
        14,14,14,17,18,18,18,18,14,15,12,12,13,12,14,14,
        15,15,18,18,18,17,18,15,14,12,11,12,12,14,14,14,
        15,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -520986624;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1620377600;
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
        7,
        6,
        8,
        5,
        9,
        4,
        10,
        3,
        11,
        2,
        12,
        1,
        13,
        0,
        14,
    };
}
