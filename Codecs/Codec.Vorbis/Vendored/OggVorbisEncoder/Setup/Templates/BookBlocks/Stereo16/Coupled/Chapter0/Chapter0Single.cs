namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo16.Coupled.Chapter0;

/// <summary>
/// Represents a chapter 0 single.
/// </summary>
public class Chapter0Single : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         3, 4,19, 7, 9, 7, 8,11, 9,12, 4, 1,19, 6, 7, 7,
         8,10,11,13,18,18,18,18,18,18,18,18,18,18, 8, 6,
        18, 8, 9, 9,11,12,14,18, 9, 6,18, 9, 7, 8, 9,11,
        12,18, 7, 6,18, 8, 7, 7, 7, 9,11,17, 8, 8,18, 9,
         7, 6, 6, 8,11,17,10,10,18,12, 9, 8, 7, 9,12,18,
        13,15,18,15,13,11,10,11,15,18,14,18,18,18,18,18,
        16,16,18,18,
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
