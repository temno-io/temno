namespace Temno.Core.Session;

/// <summary>Marshals project-session access to its owning thread.</summary>
public interface IProjectSessionDispatcher
{
    bool HasThreadAccess { get; }

    T Invoke<T>(Func<T> operation);
}
