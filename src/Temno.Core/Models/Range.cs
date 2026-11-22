namespace Temno.Core.Models;

/// <summary>
/// Generic Range structure to satisfy compilation of legacy references.
/// </summary>
public struct Range<T>
{
    public T Start { get; set; }
    public T End { get; set; }

    public Range(T start, T end)
    {
        Start = start;
        End = end;
    }
}
