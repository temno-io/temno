using Temno.Core.Models;

namespace Temno.Media.Effects;

/// <summary>
/// Applies a clip's ordered effect stack to one decoded source frame.
/// Implementations receive a clip-relative frame offset so animated parameters
/// use the same coordinate system as the project package.
/// </summary>
public interface IFrameEffectsRenderer
{
    VideoFrame Apply(VideoFrame source, IReadOnlyList<Effect>? effects, int clipRelativeFrame);
}
