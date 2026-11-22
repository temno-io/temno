namespace Temno.Core.Generation;

/// <summary>
/// One process per environment. Temno and Temno-Dev use different names, so they still run together.
/// </summary>
public static class SingleInstanceNames
{
    public static string Mutex(string environment)
        => @"Local\Temno.Instance." + environment;

    public static string Activate(string environment)
        => @"Local\Temno.Instance." + environment + ".activate";
}
