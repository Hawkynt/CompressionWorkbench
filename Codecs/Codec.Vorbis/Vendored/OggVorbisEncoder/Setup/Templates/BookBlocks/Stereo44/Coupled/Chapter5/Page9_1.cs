namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter5;

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
         1, 4, 4, 5, 5, 7, 7, 9, 8,10, 9,10,10,11,10,11,
        11, 6, 5, 5, 7, 7, 8, 9,10,10,11,10,12,11,12,11,
        13,12, 6, 5, 5, 7, 7, 9, 9,10,10,11,11,12,12,13,
        12,13,13,18, 8, 8, 8, 8, 9, 9,10,11,11,11,12,11,
        13,11,13,12,18, 8, 8, 8, 8,10,10,11,11,12,12,13,
        13,13,13,13,14,18,12,12, 9, 9,11,11,11,11,12,12,
        13,12,13,12,13,13,20,13,12, 9, 9,11,11,11,11,12,
        12,13,13,13,14,14,13,20,18,19,11,12,11,11,12,12,
        13,13,13,13,13,13,14,13,18,19,19,12,11,11,11,12,
        12,13,12,13,13,13,14,14,13,18,17,19,14,15,12,12,
        12,13,13,13,14,14,14,14,14,14,19,19,19,16,15,12,
        11,13,12,14,14,14,13,13,14,14,14,19,18,19,18,19,
        13,13,13,13,14,14,14,13,14,14,14,14,18,17,19,19,
        19,13,13,13,11,13,11,13,14,14,14,14,14,19,17,17,
        18,18,16,16,13,13,13,13,14,13,15,15,14,14,19,19,
        17,17,18,16,16,13,11,14,10,13,12,14,14,14,14,19,
        19,19,19,19,18,17,13,14,13,11,14,13,14,14,15,15,
        19,19,19,17,19,18,18,14,13,12,11,14,11,15,15,15,
        15,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -520814592;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1620377600;
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
