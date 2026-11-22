using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Models;

/// <summary>
/// Transition blend kinds applied across adjacent clip pairs.
/// Mac currently ships only <see cref="CrossDissolve"/>; Windows preview supports the full set.
/// JSON raw values are camelCase (<c>crossDissolve</c>, <c>fadeToBlack</c>, …).
/// </summary>
public enum TransitionType
{
    CrossDissolve,
    FadeToBlack,
    FadeToWhite,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,
    Wipe,
}

/// <summary>
/// Serializes <see cref="TransitionType"/> as camelCase strings for project interchange.
/// </summary>
public sealed class TransitionTypeJsonConverter : JsonStringEnumConverter<TransitionType>
{
    public TransitionTypeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
