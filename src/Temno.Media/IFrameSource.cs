using System.Threading;
using System.Threading.Tasks;

namespace Temno.Media;

/// <summary>
/// Decodes a frame at a specific source timestamp.
/// </summary>
public interface IFrameSource
{
    /// <summary>
    /// Retrieves a video frame at the specified time offset in seconds. The caller owns and must
    /// dispose the returned frame after it is no longer needed.
    /// </summary>
    Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default);
}
