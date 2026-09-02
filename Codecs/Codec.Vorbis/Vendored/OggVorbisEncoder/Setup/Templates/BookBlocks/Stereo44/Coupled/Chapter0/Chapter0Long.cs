namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter0;

/// <summary>
/// Represents a chapter 0 long.
/// </summary>
public class Chapter0Long : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         5, 4, 8, 9, 8, 9,10,12,15, 4, 1, 5, 5, 6, 8,11,
        12,12, 8, 5, 8, 9, 9,11,13,12,12, 9, 5, 8, 5, 7,
         9,12,13,13, 8, 6, 8, 7, 7, 9,11,11,11, 9, 7, 9,
         7, 7, 7, 7,10,12,10,10,11, 9, 8, 7, 7, 9,11,11,
        12,13,12,11, 9, 8, 9,11,13,16,16,15,15,12,10,11,
        12,
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
