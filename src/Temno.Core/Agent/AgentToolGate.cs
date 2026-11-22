using System.Text.Json.Nodes;

namespace Temno.Core.Agent;

/// <summary>What a consent request asks the user to allow (WP-400).</summary>
public enum AgentToolConsentKind
{
    Export,
    OutsideFile,
    Download,
    Spend,
}

/// <summary>
/// One model tool call that waits for the user's yes before it runs (WP-400). It carries the exact
/// arguments that run on approval, so the yes cannot be spent on a different call, and it can be
/// approved once.
/// </summary>
public sealed class AgentToolConsentRequest
{
    private int _used;

    internal AgentToolConsentRequest(string toolName, string inputJson, AgentToolConsentKind kind, string title,
        string action, string summary, IReadOnlyList<KeyValuePair<string, string>> details, string? note)
    {
        ToolName = toolName;
        InputJson = inputJson;
        Kind = kind;
        Title = title;
        Action = action;
        Summary = summary;
        Details = details;
        Note = note;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string ToolName { get; }
    /// <summary>The arguments that run if the user allows it, normalized from the model's call.</summary>
    public string InputJson { get; }
    public AgentToolConsentKind Kind { get; }
    /// <summary>The question, for example "Export the project?".</summary>
    public string Title { get; }
    /// <summary>The verb on the button that allows it, for example "Export".</summary>
    public string Action { get; }
    /// <summary>One line naming the action and its target, used in the tool result when it does not run.</summary>
    public string Summary { get; }
    /// <summary>Label and value pairs: the exact file, folder, address, model or limit.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Details { get; }
    public string? Note { get; }

    internal bool TryConsume() => Interlocked.Exchange(ref _used, 1) == 0;
}

/// <summary>The user's yes to one request, named by its id.</summary>
public sealed record AgentToolApproval(Guid RequestId);

/// <summary>Asks the user about one request. A yes returns an approval for its id; anything else returns null.</summary>
public interface IAgentToolConsent
{
    Task<AgentToolApproval?> AskAsync(AgentToolConsentRequest request, CancellationToken cancellationToken);
}

/// <summary>A call the gate let through, with the arguments to run, or the reason it did not run.</summary>
public readonly record struct AgentToolAdmission(bool Allowed, string Name, string InputJson, string? Refusal)
{
    internal static AgentToolAdmission Run(string name, string input) => new(true, name, input, null);
    internal static AgentToolAdmission Refuse(string name, string reason) => new(false, name, "{}", reason);
}

/// <summary>
/// WP-400: the in-app agent's execution point. A model's tool call runs only when this chat offered the tool, and
/// an export, a file outside the project, a download or a paid generation runs only after the user allows that
/// exact call in Temno. Model instructions are not the boundary; this is. The direct MCP server does not pass
/// through here (its caller holds the token and is governed by its own contract).
/// </summary>
public sealed class AgentToolGate
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Project switching tools. The in-app chat works on the open project and never offers or runs them.</summary>
    public static readonly IReadOnlySet<string> ProjectTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "get_projects", "open_project", "new_project", "close_project", "manage_project",
    };

    /// <summary>Tools that submit a generation that costs credits or bills the user's own provider key.</summary>
    public static readonly IReadOnlySet<string> SpendTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "generate_video", "generate_image", "edit_image", "generate_audio", "upscale_media", "generate_google",
    };

    private readonly IAgentToolConsent? _consent;
    private readonly TimeSpan _timeout;

    public AgentToolGate(IEnumerable<string> localTools, IAgentToolConsent? consent = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(localTools);
        LocalTools = new HashSet<string>(localTools.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.Ordinal);
        _consent = consent;
        _timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
    }

    /// <summary>The tools this install lets the in-app agent run at all.</summary>
    public IReadOnlySet<string> LocalTools { get; }

    /// <summary>
    /// Decides one model tool call. <paramref name="offered"/> is what this turn offered the model (null when the
    /// turn has no record of it). Throws <see cref="OperationCanceledException"/> only when
    /// <paramref name="cancellationToken"/> is cancelled; a prompt that times out is a refusal.
    /// </summary>
    public async Task<AgentToolAdmission> AdmitAsync(string? name, string? inputJson, IEnumerable<string>? offered,
        CancellationToken cancellationToken)
    {
        name ??= "";
        cancellationToken.ThrowIfCancellationRequested();
        if (name.Length == 0 || ProjectTools.Contains(name) || !LocalTools.Contains(name)
            || (offered is not null && !offered.Contains(name, StringComparer.Ordinal)))
            return AgentToolAdmission.Refuse(name,
                $"Not run: {(name.Length == 0 ? "a tool with no name" : name)} is not available in this chat. Use only the tools this chat offers.");

        AgentToolConsentRequest? request;
        try
        {
            request = ConsentFor(name, inputJson);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return AgentToolAdmission.Refuse(name, "Not run: Temno could not read this call's arguments, so it could not ask the user about it.");
        }
        if (request is null)
            return AgentToolAdmission.Run(name, string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
        if (_consent is null)
            return AgentToolAdmission.Refuse(name,
                $"Not run: {request.Summary} needs the user's approval in Temno, and none can be asked here.");

        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        AgentToolApproval? approval;
        try
        {
            approval = await _consent.AskAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            approval = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentToolAdmission.Refuse(name,
                $"Not run: Temno could not ask the user about {request.Summary} ({ex.Message}). Ask the user in this conversation before trying again.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (approval is not null && approval.RequestId == request.Id && !timeout.IsCancellationRequested && request.TryConsume())
            return AgentToolAdmission.Run(request.ToolName, request.InputJson);
        return AgentToolAdmission.Refuse(name, timeout.IsCancellationRequested
            ? $"Not run: the user did not answer Temno's approval prompt for {request.Summary} in time. Ask the user before trying again."
            : $"Not run: the user declined {request.Summary} in Temno. Do not try it again unless the user asks you to in this conversation.");
    }

    /// <summary>
    /// The consent a call needs, or null when it needs none. The arguments are read as the in-process runner reads
    /// them (anything but a JSON object is empty), and the request carries them back out in that form.
    /// </summary>
    public static AgentToolConsentRequest? ConsentFor(string name, string? inputJson)
    {
        ArgumentNullException.ThrowIfNull(name);
        var needs = name is "export_project" or "import_media" or "apply_color" or "apply_effect" || SpendTools.Contains(name);
        if (!needs)
            return null;
        var args = Parse(inputJson);
        // Built now, so an argument object that cannot be written back (a duplicate key) refuses before asking.
        var normalized = args.ToJsonString();
        return name switch
        {
            "export_project" => Export(name, normalized, args),
            "import_media" => Import(name, normalized, args),
            "apply_color" or "apply_effect" => OutsideFile(name, normalized, args),
            _ => Spend(name, normalized, args),
        };
    }

    private static JsonObject Parse(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
            return [];
        try
        {
            return JsonNode.Parse(inputJson) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static AgentToolConsentRequest Export(string name, string normalized, JsonObject args)
    {
        var mode = Text(args, "mode") ?? "video";
        var what = mode switch
        {
            "video" => "a video file",
            "xml" => "a Premiere Pro XML file",
            "fcpxml" => "a Final Cut Pro XML file",
            "palmier" => "a self-contained project package",
            _ => "an export (" + mode + ")",
        };
        var details = new List<KeyValuePair<string, string>> { new("Export as", Capitalize(what)) };
        string where;
        if (Text(args, "outputPath") is { Length: > 0 } output)
        {
            AddLocation(details, output);
            where = output;
            if (args["overwrite"] is not JsonValue overwrite || !overwrite.TryGetValue<bool>(out var ow) || ow)
                details.Add(new("If it exists", "Replaced"));
        }
        else
        {
            details.Add(new("Folder", "Downloads, under a name not yet taken"));
            where = "the Downloads folder";
        }
        return new AgentToolConsentRequest(name, normalized, AgentToolConsentKind.Export, "Export the project?", "Export",
            $"exporting {what} to {where}", details, null);
    }

    private static AgentToolConsentRequest? Import(string name, string normalized, JsonObject args)
    {
        var source = args["source"] as JsonObject;
        var path = (source is null ? null : Text(source, "path")) ?? Text(args, "path");
        var url = source is null ? null : Text(source, "url");
        var reference = source?["reference"] is JsonValue r && r.TryGetValue<bool>(out var linked) && linked;
        if (path is null && url is null)
            return null; // inline bytes or a solid matte stay inside the package
        var details = new List<KeyValuePair<string, string>>();
        if (path is not null)
            AddLocation(details, path);
        if (url is not null)
            details.Add(new("Address", url));
        if (path is not null && reference)
            return new AgentToolConsentRequest(name, normalized, AgentToolConsentKind.OutsideFile,
                "Link a file outside the project?", "Link", $"linking the file {path} into the project", details,
                "The project will read this file where it is, and Temno will allow it for this project.");
        if (path is not null)
            return new AgentToolConsentRequest(name, normalized, AgentToolConsentKind.OutsideFile,
                "Copy a file into the project?", "Copy in", $"copying the file {path} into the project", details,
                "Temno copies the file into the project's media folder.");
        return new AgentToolConsentRequest(name, normalized, AgentToolConsentKind.Download,
            "Download into the project?", "Download", $"downloading {url} into the project", details,
            "Temno downloads this address into the project's media folder.");
    }

    private static AgentToolConsentRequest? OutsideFile(string name, string normalized, JsonObject args)
    {
        var paths = new List<string>();
        CollectPaths(args, paths);
        if (paths.Count == 0)
            return null;
        var details = new List<KeyValuePair<string, string>>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
            AddLocation(details, path);
        return new AgentToolConsentRequest(name, normalized, AgentToolConsentKind.OutsideFile,
            "Use a file outside the project?", "Use file",
            $"using the file {string.Join(", ", paths.Distinct(StringComparer.Ordinal))} for a color look", details,
            "Preview and export will read this file, a color look-up table, from where it is.");
    }

    private static AgentToolConsentRequest Spend(string name, string normalized, JsonObject args)
    {
        var what = name switch
        {
            "generate_video" => "a video",
            "generate_image" => "an image",
            "edit_image" => "an image edit",
            "generate_audio" => "audio",
            "upscale_media" => "an upscale",
            _ => "an image or video",
        };
        var details = new List<KeyValuePair<string, string>> { new("Generate", Capitalize(what)) };
        if (Text(args, "model") is { Length: > 0 } model)
            details.Add(new("Model", model));
        if (Text(args, "prompt") is { Length: > 0 } prompt)
            details.Add(new("Prompt", prompt.Length <= 160 ? prompt : prompt[..157] + "…"));
        if (args["maxCredits"] is JsonValue max && max.TryGetValue<double>(out var credits))
            details.Add(new("Credit limit", credits.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " credits"));
        else if (Text(args, "quoteId") is { Length: > 0 })
            details.Add(new("Credits", "As quoted in this chat"));
        return new AgentToolConsentRequest(name, normalized, AgentToolConsentKind.Spend, "Start a paid generation?", "Start",
            $"starting {what}" + (Text(args, "model") is { Length: > 0 } m ? " with " + m : "") + ", which costs credits or bills your key",
            details, "Hosted models spend your Temno credits; your own key bills your provider.");
    }

    private static void CollectPaths(JsonNode? node, List<string> paths)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key is "path" or "lutPath" && value is JsonValue v && v.TryGetValue<string>(out var text)
                        && !string.IsNullOrWhiteSpace(text))
                        paths.Add(text);
                    else
                        CollectPaths(value, paths);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    CollectPaths(item, paths);
                break;
        }
    }

    private static void AddLocation(List<KeyValuePair<string, string>> details, string path)
    {
        string? file = null, folder = null;
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            file = Path.GetFileName(trimmed);
            folder = Path.GetDirectoryName(trimmed);
        }
        catch (ArgumentException)
        {
        }
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(folder))
        {
            details.Add(new("Path", path));
            return;
        }
        details.Add(new("File", file));
        details.Add(new("Folder", folder));
    }

    private static string? Text(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
