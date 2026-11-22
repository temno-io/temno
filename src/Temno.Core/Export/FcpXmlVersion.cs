namespace Temno.Core.Export;

public enum FcpXmlVersion
{
    V1_10,
    V1_11,
    V1_12,
    V1_13,
    V1_14
}

public static class FcpXmlVersionExtensions
{
    public static string ToRawValue(this FcpXmlVersion version)
    {
        return version switch
        {
            FcpXmlVersion.V1_10 => "1.10",
            FcpXmlVersion.V1_11 => "1.11",
            FcpXmlVersion.V1_12 => "1.12",
            FcpXmlVersion.V1_13 => "1.13",
            FcpXmlVersion.V1_14 => "1.14",
            _ => "1.10"
        };
    }
}
