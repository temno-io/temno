namespace Temno.App.Controls;

/// <summary>Normalised sample-caption centre after a live drag or a pointer-up commit.</summary>
public sealed class CaptionOverlayPositionEventArgs : EventArgs
{
    public CaptionOverlayPositionEventArgs(double centerX, double centerY, bool isFinal)
    {
        CenterX = centerX;
        CenterY = centerY;
        IsFinal = isFinal;
    }

    public double CenterX { get; }
    public double CenterY { get; }
    public bool IsFinal { get; }
}
