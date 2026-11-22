using System.Text.Json.Nodes;

namespace Temno.Core.Account;

/// <summary>Display-only provider metadata. Never used to authorize an account.</summary>
public sealed record AccountProfile(string UserId, string? DisplayName, string? AvatarUrl)
{
    public static AccountProfile? FromUser(JsonObject? user)
    {
        var userId = ReadString(user, "id");
        if (userId is null)
            return null;

        var metadata = user?["user_metadata"] as JsonObject;
        var google = (user?["identities"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(identity => ReadString(identity, "provider") == "google")?
            ["identity_data"] as JsonObject;
        var name = ReadString(google, "full_name") ?? ReadString(google, "name")
            ?? ReadString(metadata, "full_name") ?? ReadString(metadata, "name");
        if (name is not null)
            name = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var avatar = GoogleAvatarUrl(ReadString(google, "avatar_url"))
            ?? GoogleAvatarUrl(ReadString(google, "picture"))
            ?? GoogleAvatarUrl(ReadString(metadata, "avatar_url"))
            ?? GoogleAvatarUrl(ReadString(metadata, "picture"));
        return new AccountProfile(userId, name, avatar);
    }

    public static string? GoogleAvatarUrl(string? value)
    {
        // Metadata is user-editable. Only fetch HTTPS Google images, never local files
        // or arbitrary hosts, and never attach the account bearer token to the image.
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0)
            return null;

        string host;
        try { host = uri.IdnHost; }
        catch (UriFormatException) { return null; }
        return host.Equals("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("ggpht.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".ggpht.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri : null;
    }

    private static string? ReadString(JsonObject? obj, string key)
        => obj?[key] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
