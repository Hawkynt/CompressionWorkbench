using System.IO;

namespace GroovyCodecs.Mp3
{
    /// <summary>
    /// Decodes i mp 3 data.
    /// </summary>
public interface IMp3Decoder
    {
        void close();

        void decode(MemoryStream sampleBuffer, bool playOriginal);
    }
}