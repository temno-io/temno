using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Temno.Mcp;

public static class McpToolRegistry
{
    private static readonly List<McpTool> _tools = [];

    public static IReadOnlyList<McpTool> Tools => _tools;

    // WP-337: every tool that spends says so where an MCP client reads it, since a client may keep only the start of
    // the server instructions.
    private const string SpendsMoney =
        "It costs the user real money with a real provider (Temno credits when hosted, their own provider's bill on their key), so call it only after the user says yes to your proposal.";

    static McpToolRegistry()
    {
        InitializeTools();
    }

    private static void Register(string name, string description, JsonObject inputSchema)
    {
        _tools.Add(new McpTool
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema
        });
    }

    private static JsonObject Obj(params (string Key, JsonNode? Value)[] properties)
    {
        var obj = new JsonObject();
        foreach (var p in properties)
        {
            obj[p.Key] = p.Value;
        }
        return obj;
    }

    private static JsonArray Arr(params string[] items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
        {
            arr.Add(item);
        }
        return arr;
    }

    private static JsonObject Prop(string type, string description)
    {
        return Obj(
            ("type", (JsonNode)type),
            ("description", (JsonNode)description)
        );
    }

    private static JsonObject Prop(string type, string description, string[] enums)
    {
        var enumArr = new JsonArray();
        foreach (var e in enums) enumArr.Add(e);
        return Obj(
            ("type", (JsonNode)type),
            ("description", (JsonNode)description),
            ("enum", enumArr)
        );
    }

    private static JsonObject ObjectSchema(JsonObject? properties = null, string[]? required = null)
    {
        var schema = new JsonObject { ["type"] = "object" };
        if (properties != null)
        {
            schema["properties"] = properties;
        }
        if (required != null && required.Length > 0)
        {
            schema["required"] = Arr(required);
        }
        return schema;
    }

    private static JsonObject TextBoxTransformProperties()
    {
        return Obj(
            ("centerX", Prop("number", "0-1 horizontal center.")),
            ("centerY", Prop("number", "0-1 vertical center.")),
            ("width", Prop("number", "0-1 width.")),
            ("height", Prop("number", "0-1 height."))
        );
    }

    private static JsonObject TextStyleProperties()
    {
        return Obj(
            ("fontName", Prop("string", "Font name.")),
            ("fontSize", Prop("number", "Canvas points.")),
            ("isBold", Prop("boolean", "Bold.")),
            ("isItalic", Prop("boolean", "Italic.")),
            ("color", Prop("string", "Text color hex.")),
            ("alignment", Prop("string", "Text alignment.", ["left", "center", "right"])),
            ("borderColor", Prop("string", "Text outline hex; enables outline.")),
            ("backgroundColor", Prop("string", "Text box fill hex; enables fill."))
        );
    }

    private static JsonObject Merge(params JsonObject[] objects)
    {
        var merged = new JsonObject();
        foreach (var obj in objects)
        {
            foreach (var kvp in obj)
            {
                if (kvp.Value != null)
                {
                    merged[kvp.Key] = kvp.Value.DeepClone();
                }
            }
        }
        return merged;
    }

    private static JsonObject CurvePoints() => Obj(
        ("type", (JsonNode)"array"),
        ("items", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"number")))))
    );

    private static JsonObject MediaInputs(string description) => Obj(
        ("type", (JsonNode)"array"),
        ("description", (JsonNode)description),
        ("items", Obj(
            ("type", (JsonNode)"object"),
            ("properties", Obj(
                ("role", Prop("string", "Input role.")),
                ("mediaRef", Prop("string", "Asset ID."))
            )),
            ("required", Arr("role", "mediaRef"))
        ))
    );

    private static void InitializeTools()
    {
        // --- Projects ---
        Register("get_projects",
            "List the user's known projects, most recently opened first. Returns projects, active project.",
            ObjectSchema());

        Register("open_project",
            "Open a project and make it the active one. Identify it by name, id, or path.",
            ObjectSchema(Obj(
                ("name", Prop("string", "Project name, matched case-insensitively.")),
                ("id", Prop("string", "Project id from get_projects.")),
                ("path", Prop("string", "Filesystem path to a .temno or .palmier package."))
            )));

        Register("new_project",
            "Create a new project in the user's Temno folder and make it active.",
            ObjectSchema(Obj(
                ("name", Prop("string", "Project name. Defaults to 'Untitled Project'.")),
                ("template", Prop("string", "Optional saved project template name. A name that is not a saved template fails without creating a project.")),
                ("fps", Prop("integer", "Optional timeline frame rate (1-120).")),
                ("aspectRatio", Prop("string", "Optional canvas aspect ratio.", ["16:9", "9:16", "1:1", "4:3", "2.4:1", "9:14"])),
                ("quality", Prop("string", "Optional resolution preset.", ["720p", "1080p", "2K", "4K"]))
            )));

        Register("close_project",
            "Save and close an open project. Omit all arguments to close the active project.",
            ObjectSchema(Obj(
                ("name", Prop("string", "Project name.")),
                ("id", Prop("string", "Project id from get_projects.")),
                ("path", Prop("string", "Filesystem path to a .temno or .palmier package."))
            )));

        Register("manage_project",
            "List, open, create, or close Temno projects for this MCP session. Set `action` to: `list` for known projects plus session-active and visible state; `open` with a name, id from list, or .temno/.palmier path; `create` with an optional name and initial fps/aspectRatio/quality; or `close` to save and close the session project, optionally targeting another open project by name/id/path. Opening or creating changes only this session's target. Closing always completes a final save first. This tool never deletes projects or files.",
            ObjectSchema(Obj(
                ("action", Prop("string", "Project operation.", ["list", "open", "create", "close"])),
                ("name", Prop("string", "Project name. For open/close, matched case-insensitively; for create, defaults to 'Untitled Project'.")),
                ("id", Prop("string", "Project id returned by action='list'. Used by open or close.")),
                ("path", Prop("string", "Filesystem path to a .temno or .palmier package. Used by open or close.")),
                ("fps", Prop("integer", "Create only. Optional timeline frame rate (1-120).")),
                ("aspectRatio", Prop("string", "Create only. Optional canvas aspect ratio.", ["16:9", "9:16", "1:1", "4:3", "2.4:1", "9:14"])),
                ("quality", Prop("string", "Create only. Optional resolution preset applied to the aspect ratio.", ["720p", "1080p", "2K", "4K"])),
                ("template", Prop("string", "Create only. Optional saved project template name. A name that is not a saved template fails without creating a project."))
            ), ["action"]));

        // --- Timelines ---
        Register("get_timeline",
            "Always call at the start of a session. Returns project settings, tracks and clips.",
            ObjectSchema(Obj(
                ("startFrame", Prop("integer", "Optional window start frame.")),
                ("endFrame", Prop("integer", "Optional window end frame.")),
                ("captionDetail", Prop("boolean", "Optional. True expands captionGroups into per-clip rows."))
            )));

        Register("inspect_timeline",
            "See the composited timeline - what the user actually sees in the preview. With no frame given, it renders the playhead frame.",
            ObjectSchema(Obj(
                ("startFrame", Prop("integer", "Project frame to render. Omit it and endFrame to render the playhead frame, what the user sees now.")),
                ("endFrame", Prop("integer", "Optional sample end frame.")),
                ("maxFrames", Prop("integer", "Frames to sample when endFrame is set."))
            )));

        Register("validate_timeline",
            "Pre-export safety scan of the active timeline - pure read-only inspection.",
            ObjectSchema(Obj(
                ("exportPresetId", Prop("string", "Optional saved export preset id or name."))
            )));

        Register("create_timeline",
            "Creates a timeline and switches to it.",
            ObjectSchema(Obj(
                ("name", Prop("string", "Optional display name.")),
                ("from", Prop("string", "Optional timelineId to duplicate."))
            )));

        Register("set_active_timeline",
            "Switches the active timeline.",
            ObjectSchema(Obj(
                ("timelineId", Prop("string", "Timeline id from get_media's timelines list."))
            ), ["timelineId"]));

        Register("set_project_settings",
            "Change the project's frame rate, resolution, or aspect ratio.",
            ObjectSchema(Obj(
                ("fps", Prop("integer", "Frame rate in frames per second.")),
                ("width", Prop("integer", "Canvas width in pixels.")),
                ("height", Prop("integer", "Canvas height in pixels.")),
                ("aspectRatio", Prop("string", "Preset aspect ratio.", ["16:9", "9:16", "1:1", "4:3", "2.4:1", "9:14"])),
                ("quality", Prop("string", "Resolution quality preset.", ["720p", "1080p", "2K", "4K"]))
            )));

        Register("export_project",
            "Exports from the current project: video (H.264/H.265/ProRes/HEVC HDR), FCPXML, XMEML (xml), or self-contained temno package (mode name `palmier` reads both, writes .temno).",
            ObjectSchema(Obj(
                ("mode", Prop("string", "Export mode.", ["video", "xml", "fcpxml", "palmier"])),
                ("codec", Prop("string", "video mode codec.", ["H.264", "H.265", "ProRes", "HEVC 10-bit HDR"])),
                ("resolution", Prop("string", "Resolution.", ["720p", "1080p", "2K", "4K", "Match Timeline"])),
                ("outputPath", Prop("string", "Optional destination. When omitted, a name not yet taken in ~/Downloads (<project>.<ext>, then <project> 2.<ext>, …). palmier mode adds .temno to a new package's path (a .palmier suffix becomes .temno) and replaces only an existing .temno or .palmier package that holds project.json: any other folder, a file or a link is refused. The other modes refuse a folder or a link. No mode writes inside a .temno or .palmier folder or the open project.")),
                ("overwrite", Prop("boolean", "Default true. false refuses when the destination already exists, judged after palmier mode adds .temno.")),
                ("fcpxmlTarget", Prop("string", "fcpxml mode only.", ["resolve", "fcp"])),
                ("timelineId", Prop("string", "Timeline to export (video/xml/fcpxml).")),
                ("preset", Prop("string", "Export preset id or name.")),
                ("maxRenderQuality", Prop("boolean", "video mode only. true sets a higher quality target on the software H.264 and H.265 encoders (x264, x265): CRF 18 instead of 23. No effect on hardware encoders (NVENC, QSV, AMF), OpenH264 or ProRes. By default a hardware encoder is used where one exists; pass preferHardware false for this to apply.")),
                ("maxBitDepth", Prop("boolean", "video mode only. true exports HEVC 10-bit tagged HLG, whatever codec says. The picture is rendered in 8-bit SDR and converted, not processed at higher precision.")),
                ("preferHardware", Prop("boolean", "video mode only. Overrides the saved prefer-hardware-encode preference; NVENC/QSV/AMF when true, software encoders when false.")),
                ("background", Prop("boolean", "video mode only. When true, queues the render and returns a jobId immediately; poll with manage_exports.")),
                ("allowOfflineMedia", Prop("boolean", "video mode only. Default false: a timeline whose picture would be missing clips because their media is offline is refused, and the error names them. true exports anyway, like the editor's Export anyway: those parts of the picture are left blank, offlineMedia lists each offline file with the frame ranges (end exclusive) that use it, and missingPicture lists the clips the render dropped. A timeline with no picture left is still refused."))
            )));

        Register("manage_exports",
            "List queued/running exports and their progress, or cancel one by jobId. An export queued with allowOfflineMedia also lists warnings, and missingPicture once it completes.",
            ObjectSchema(Obj(
                ("action", Prop("string", "list or cancel.", ["list", "cancel"])),
                ("jobId", Prop("string", "Required for cancel. A jobId from export_project (background) or manage_exports list."))
            ), ["action"]));

        // --- Markers ---
        Register("add_marker",
            "Adds a named marker to the active timeline at a given frame.",
            ObjectSchema(Obj(
                ("frame", Prop("integer", "Project frame to place the marker at.")),
                ("name", Prop("string", "Optional display name.")),
                ("color", Prop("string", "Optional color.", ["red", "orange", "yellow", "green", "blue", "purple"])),
                ("comment", Prop("string", "Optional longer note."))
            ), ["frame"]));

        Register("list_markers",
            "Lists every marker on the active timeline, sorted by frame.",
            ObjectSchema());

        Register("update_marker",
            "Updates one or more fields of an existing marker on the active timeline.",
            ObjectSchema(Obj(
                ("markerId", Prop("string", "Marker id.")),
                ("frame", Prop("integer", "Optional new frame.")),
                ("name", Prop("string", "Optional new name.")),
                ("color", Prop("string", "Optional color.", ["red", "orange", "yellow", "green", "blue", "purple"])),
                ("comment", Prop("string", "Optional comment."))
            ), ["markerId"]));

        Register("remove_marker",
            "Deletes a marker from the active timeline.",
            ObjectSchema(Obj(
                ("markerId", Prop("string", "Marker id."))
            ), ["markerId"]));

        // --- Transitions ---
        Register("add_transition",
            "Adds a transition spanning the boundary of two adjacent clips on the same video track.",
            ObjectSchema(Obj(
                ("clipIdA", Prop("string", "The outgoing clip.")),
                ("clipIdB", Prop("string", "The incoming clip.")),
                ("type", Prop("string", "Optional transition type.", [
                    "crossDissolve", "fadeToBlack", "fadeToWhite",
                    "slideLeft", "slideRight", "slideUp", "slideDown", "wipe"
                ])),
                ("durationFrames", Prop("integer", "Length of the transition in project frames."))
            ), ["clipIdA", "clipIdB", "durationFrames"]));

        Register("list_transitions",
            "Lists every transition on the active timeline.",
            ObjectSchema());

        Register("remove_transition",
            "Deletes a transition from the active timeline.",
            ObjectSchema(Obj(
                ("transitionId", Prop("string", "Transition id."))
            ), ["transitionId"]));

        // --- Media library ---
        Register("get_media",
            "The library inventory: media assets, folders, and timelines.",
            ObjectSchema(Obj(
                ("ids", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))), ("description", (JsonNode)"Optional asset ids filter."))),
                ("folder", Prop("string", "Optional folder path filter.")),
                ("pending", Prop("boolean", "Optional. True returns only pending assets."))
            )));

        Register("inspect_media",
            "Look at a media asset before referencing or editing it.",
            ObjectSchema(Obj(
                ("mediaRef", Prop("string", "Asset ID.")),
                ("clipId", Prop("string", "Optional clip ID.")),
                ("maxFrames", Prop("integer", "Sample frame count.")),
                ("startSeconds", Prop("number", "Source-time window start.")),
                ("endSeconds", Prop("number", "Window end.")),
                ("wordTimestamps", Prop("boolean", "Add word-level timestamps.")),
                ("overview", Prop("boolean", "One storyboard grid of distinct moments.")),
                ("language", Prop("string", "Optional language tag."))
            ), ["mediaRef"]));

        Register("search_media",
            "Search the media library by content: what's on screen (visual) and what's said (spoken).",
            ObjectSchema(Obj(
                ("query", Prop("string", "Search query.")),
                ("scope", Prop("string", "Search scope.", ["visual", "spoken", "both"])),
                ("mediaRef", Prop("string", "Optional media ref filter.")),
                ("limit", Prop("integer", "Optional limit."))
            ), ["query"]));

        Register("import_media",
            "Imports external media into the project's library. A local path is copied into the "
            + "project by default, so the package stays self-contained; set source.reference true to "
            + "record the file where it already is instead, which matters for very large sources. "
            + "A url import, and the copy of a local file of 64 MB or more, run in the background and "
            + "return {mediaRef, status:'downloading'}: poll get_media with ids:[mediaRef] until "
            + "generationStatus clears ('failed: <reason>' means it did not land). Every other import "
            + "finishes in the call and returns status:'ready'.",
            ObjectSchema(Obj(
                ("source", Obj(
                    ("type", (JsonNode)"object"),
                    ("description", (JsonNode)"Source details."),
                    ("properties", Obj(
                        ("url", Prop("string",
                            "HTTPS URL. The type comes from mimeType when set, else from the URL path's "
                            + "extension; with neither, the download runs inside the call.")),
                        ("path", Prop("string", "Absolute local path.")),
                        ("bytes", Prop("string", "Base64 encoded bytes.")),
                        ("matte", Obj(
                            ("type", (JsonNode)"object"),
                            ("description", (JsonNode)"Solid color PNG matte generation."),
                            ("properties", Obj(
                                ("hex", Prop("string", "Hex color.")),
                                ("aspectRatio", Prop("string", "Aspect ratio.", ["Project", "16:9", "9:16", "1:1", "4:3", "9:14", "2.4:1"]))
                            )),
                            ("required", Arr("hex"))
                        )),
                        ("mimeType", Prop("string", "Mime type.")),
                        ("reference", Prop("boolean",
                            "Local path only. True records the file where it already is "
                            + "(media.json external.absolutePath) instead of copying it into the "
                            + "project. The project stays small and the import is instant, but it "
                            + "is no longer self-contained: move or delete the original and the "
                            + "asset goes offline. Defaults to false (copy)."))
                    ))
                )),
                ("path", Prop("string", "Legacy absolute local path. Prefer source.path. Do not combine with source.")),
                ("name", Prop("string", "Optional display name.")),
                ("targetName", Prop("string", "Legacy alias of name.")),
                ("folder", Prop("string", "Optional destination folder path.")),
                ("folderId", Prop("string", "Legacy destination folder id. Prefer folder."))
            )));

        Register("organize_media",
            "Reorganizes the library in one undoable action.",
            ObjectSchema(Obj(
                ("createFolders", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("moves", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("items", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                            ("into", Prop("string", "Destination folder path."))
                        )),
                        ("required", Arr("items"))
                    ))
                )),
                ("renames", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("item", Prop("string", "Asset, timeline, or folder.")),
                            ("name", Prop("string", "New name."))
                        )),
                        ("required", Arr("item", "name"))
                    ))
                )),
                ("deletes", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string")))))
            )));

        // --- Clips ---
        Register("manage_tracks",
            "Track-level operations in one undoable action: reorder, set flags, and remove.",
            ObjectSchema(Obj(
                ("reorder", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("index", Prop("integer", "Track to move.")),
                            ("trackId", Prop("string", "Stable track id from get_timeline.")),
                            ("to", Prop("integer", "Destination index."))
                        )),
                        ("required", Arr("to"))
                    ))
                )),
                ("set", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("index", Prop("integer", "Track to change.")),
                            ("trackId", Prop("string", "Stable track id from get_timeline.")),
                            ("muted", Prop("boolean", "Mute/unmute.")),
                            ("hidden", Prop("boolean", "Hide/unhide.")),
                            ("syncLocked", Prop("boolean", "Sync lock."))
                        ))
                    ))
                )),
                ("remove", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("index", Prop("integer", "Track index to remove.")),
                            ("trackId", Prop("string", "Stable track id from get_timeline."))
                        ))
                    ))
                )),
                ("action", Prop("string", "Legacy operation. Prefer reorder, set, and remove.", ["add", "remove"])),
                ("type", Prop("string", "Legacy track type for action=add.")),
                ("trackIndex", Prop("integer", "Legacy track index for action=remove."))
            )));

        Register("add_clips",
            "Places one or more media assets on the timeline as a single undoable action. "
            + "When a video asset with audio is placed on a video track, a linked audio clip is "
            + "automatically created on an audio track (an existing one if available, otherwise a "
            + "new one); the pair shares a link group and one undo removes both.",
            ObjectSchema(Obj(
                ("entries", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("mediaRef", Prop("string", "ID of media asset.")),
                            ("trackIndex", Prop("integer", "Optional track index.")),
                            ("startFrame", Prop("integer", "Timeline frame position.")),
                            ("endFrame", Prop("integer", "Optional end frame.")),
                            ("source", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"number")))))
                        )),
                        ("required", Arr("mediaRef", "startFrame"))
                    ))
                ))
            ), ["entries"]));

        Register("insert_clips",
            "Inserts one or more media assets at a single point and RIPPLES. As in add_clips, a video "
            + "asset with audio spawns a linked audio clip, and the push is applied to the audio "
            + "track that clip lands on so the pair stays aligned.",
            ObjectSchema(Obj(
                ("trackIndex", Prop("integer", "Track to insert into.")),
                ("atFrame", Prop("integer", "Timeline frame where insertion begins.")),
                ("entries", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("mediaRef", Prop("string", "ID of media asset.")),
                            ("source", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"number"))))),
                            ("durationFrames", Prop("integer", "Duration in frames."))
                        )),
                        ("required", Arr("mediaRef"))
                    ))
                ))
            ), ["trackIndex", "atFrame", "entries"]));

        Register("move_clips",
            "Moves one or more clips to a new track and/or frame position.",
            ObjectSchema(Obj(
                ("moves", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("clipId", Prop("string", "Clip to move.")),
                            ("toTrack", Prop("integer", "Destination track index.")),
                            ("toFrame", Prop("integer", "Destination start frame."))
                        )),
                        ("required", Arr("clipId"))
                    ))
                ))
            ), ["moves"]));

        Register("remove_clips",
            "Removes one or more clips by ID.",
            ObjectSchema(Obj(
                ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string")))))
            ), ["clipIds"]));

        Register("restore_linked_audio",
            "Repairs timelines built before linked-audio partners existed: for every listed video "
            + "clip whose media has audio and which has no linked audio clip yet, creates the audio "
            + "partner add_clips would have made (same occupancy rule, never overwrites, one undo). "
            + "Clips that need nothing are reported as skipped with a reason.",
            ObjectSchema(Obj(
                ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string")))))
            ), ["clipIds"]));

        Register("split_clips",
            "Splits clips into two at one or more cut points.",
            ObjectSchema(Obj(
                ("splits", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("clipId", Prop("string", "Clip ID.")),
                            ("atFrame", Prop("integer", "Split frame."))
                        )),
                        ("required", Arr("clipId", "atFrame"))
                    ))
                )),
                ("trackIndex", Prop("integer", "Track to cut.")),
                ("frames", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"integer")))))
            )));

        Register("ripple_delete_ranges",
            "Cuts one or more ranges out and closes the gaps in one undoable action. Give exactly one of clipId or trackIndex. "
            + "Linked clips stay together: every clip the cut trims or moves takes its partners (a video clip's linked audio, a multicam group's other clips) with it, cut by the same frames on their tracks. "
            + "If a partner's track holds an unrelated clip inside a range, nothing is cut and the error names that clip. Answers removedFrames.",
            ObjectSchema(Obj(
                ("trackIndex", Prop("integer", "Track index; ranges are project frames.")),
                ("clipId", Prop("string", "Clip ID; ranges are clamped to the clip.")),
                ("ranges", Obj(
                    ("type", (JsonNode)"array"),
                    ("description", (JsonNode)"Ranges to remove, each a [start, end] pair (end > start)."),
                    ("items", Obj(
                        ("type", (JsonNode)"array"),
                        ("items", Obj(("type", (JsonNode)"number"))),
                        ("minItems", (JsonNode)2),
                        ("maxItems", (JsonNode)2)
                    ))
                )),
                ("units", Prop("string", "Ranges unit, default frames (project frames). seconds are the clip's source seconds and need clipId.", ["seconds", "frames"])),
                ("ignoreSyncLockedTracks", Obj(
                    ("type", (JsonNode)"array"),
                    ("description", (JsonNode)"Accepted for Mac parity. Windows has no sync lock, so it has no effect."),
                    ("items", Obj(("type", (JsonNode)"integer")))
                ))
            ), ["ranges"]));

        Register("set_clip_properties",
            "Apply the same generic clip property values to one or more clips.",
            ObjectSchema(Obj(
                ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("durationFrames", Prop("integer", "New duration.")),
                ("trimStartFrame", Prop("integer", "Trim start frame.")),
                ("trimEndFrame", Prop("integer", "Trim end frame.")),
                ("speed", Prop("number", "Playback speed multiplier.")),
                ("volume", Prop("number", "Volume 0-1.")),
                ("opacity", Prop("number", "Opacity 0-1.")),
                ("transform", Obj(
                    ("type", (JsonNode)"object"),
                    ("properties", Obj(
                        ("centerX", Prop("number", "centerX")),
                        ("centerY", Prop("number", "centerY")),
                        ("width", Prop("number", "width")),
                        ("height", Prop("number", "height")),
                        ("flipHorizontal", Prop("boolean", "flipHorizontal")),
                        ("flipVertical", Prop("boolean", "flipVertical"))
                    ))
                )),
                ("blendMode", Prop("string", "Blend mode.", [
                    "normal", "multiply", "screen", "overlay", "darken", "lighten",
                    "colorDodge", "colorBurn", "hardLight", "softLight", "difference",
                    "exclusion", "hue", "saturation", "color", "luminosity"
                ]))
            ), ["clipIds"]));

        Register("set_keyframes",
            "Set animated keyframes on one property of one clip.",
            ObjectSchema(Obj(
                ("clipId", Prop("string", "Clip ID.")),
                ("property", Prop("string", "Property.", ["volume", "opacity", "rotation", "position", "scale", "crop"])),
                ("keyframes", Obj(
                    ("type", (JsonNode)"array"),
                    ("description", (JsonNode)"Rows of [frame, ...values, interpolation?]; interpolation is linear, hold or smooth. An empty array clears the track."),
                    ("items", Obj(
                        ("type", (JsonNode)"array"),
                        ("items", Obj(("anyOf", new JsonArray(
                            Obj(("type", (JsonNode)"number")),
                            Obj(("type", (JsonNode)"string"))))))
                    ))
                ))
            ), ["clipId", "property", "keyframes"]));

        Register("apply_layout",
            "Arrange multiple clips into a common multi-video layout (split screen, picture-in-picture, grid).",
            ObjectSchema(Obj(
                ("layout", Prop("string", "Layout.", [
                    "full", "side_by_side", "top_bottom", "pip_bottom_right",
                    "pip_bottom_left", "pip_top_right", "pip_top_left",
                    "grid_2x2", "main_sidebar", "three_up"
                ])),
                ("slots", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("slot", Prop("string", "Slot name.")),
                            ("mediaRef", Prop("string", "Asset ID.")),
                            ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                            ("anchor", Prop("string", "Coarse anchor.", ["center", "top", "bottom", "left", "right", "top_left", "top_right", "bottom_left", "bottom_right"])),
                            ("anchorX", Prop("number", "Fine horizontal framing.")),
                            ("anchorY", Prop("number", "Fine vertical framing."))
                        )),
                        ("required", Arr("slot"))
                    ))
                )),
                ("startFrame", Prop("integer", "Start frame.")),
                ("endFrame", Prop("integer", "End frame.")),
                ("fit", Prop("string", "Fit mode.", ["fill", "fit"]))
            ), ["layout", "slots"]));

        Register("suggest_crop",
            "Auto-reframe one clip for a different aspect ratio.",
            ObjectSchema(Obj(
                ("clipId", Prop("string", "Clip ID.")),
                ("targetAspect", Prop("number", "Target aspect ratio.")),
                ("apply", Prop("boolean", "Apply automatically."))
            ), ["clipId"]));

        Register("sync_clips",
            "Align one or more clips to a reference clip by shifting targets on the timeline.",
            ObjectSchema(Obj(
                ("referenceClipId", Prop("string", "Reference clip ID.")),
                ("targetClipId", Prop("string", "Single clip to align.")),
                ("targetClipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("mode", Prop("string", "Sync mode.", ["auto", "audio", "timecode"])),
                ("searchWindowSeconds", Prop("number", "Search window size.")),
                ("minConfidence", Prop("number", "Minimum confidence."))
            ), ["referenceClipId"]));

        Register("manage_multicam",
            "Create or ungroup a multicam group. create syncs session media into stamped timeline clips; ungroup strips multicam stamps.",
            ObjectSchema(Obj(
                ("create", Obj(
                    ("type", (JsonNode)"object"),
                    ("properties", Obj(
                        ("name", Prop("string", "Group display name.")),
                        ("master", Prop("string", "Master mediaRef (default first camera).")),
                        ("startFrame", Prop("integer", "Timeline start for placement.")),
                        ("searchWindowSeconds", Prop("number", "Audio sync window.")),
                        ("members", Obj(
                            ("type", (JsonNode)"array"),
                            ("items", Obj(
                                ("type", (JsonNode)"object"),
                                ("properties", Obj(
                                    ("mediaRef", Prop("string", "Asset ID.")),
                                    ("kind", Prop("string", "Member kind.", ["angle", "mic", "both"])),
                                    ("angleLabel", Prop("string", "Label for change_cam.")),
                                    ("offsetSeconds", Prop("number", "Pinned sync offset."))
                                )),
                                ("required", Arr("mediaRef", "kind"))
                            ))
                        ))
                    ))
                )),
                ("ungroup", Obj(
                    ("type", (JsonNode)"object"),
                    ("properties", Obj(
                        ("groupId", Prop("string", "Multicam group id."))
                    )),
                    ("required", Arr("groupId"))
                ))
            )));

        Register("change_cam",
            "Switch a multicam group's camera angle over timeline frame ranges. Full-frame: {range, angle}. Multi-angle layout: {range, layout, angles} places primary in the main slot and extras as overlays (side_by_side, pip_*, grid_2x2, …).",
            ObjectSchema(Obj(
                ("groupId", Prop("string", "Multicam group id.")),
                ("clipId", Prop("string", "Any clip in the group (alternative to groupId).")),
                ("entries", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", Obj(
                        ("type", (JsonNode)"object"),
                        ("properties", Obj(
                            ("range", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"integer"))))),
                            ("angle", Prop("string", "angleLabel for full-frame switch (from get_multicam).")),
                            ("layout", Prop("string", "Multi-angle layout (not full). angleLabels fill slots in order.", [
                                "side_by_side", "top_bottom", "pip_bottom_right", "pip_bottom_left",
                                "pip_top_right", "pip_top_left", "grid_2x2", "main_sidebar", "three_up"
                            ])),
                            ("angles", Obj(
                                ("type", (JsonNode)"array"),
                                ("items", Prop("string", "angleLabel in slot order; first is program/main.")),
                                ("description", (JsonNode)"Required with layout. Fewer than slots leaves cells empty.")
                            ))
                        )),
                        ("required", Arr("range"))
                    ))
                ))
            ), ["entries"]));

        Register("get_multicam",
            "Read a multicam group: members, program cut rows, and track indexes.",
            ObjectSchema(Obj(
                ("groupId", Prop("string", "Multicam group id.")),
                ("clipId", Prop("string", "Any clip in the group.")),
                ("startFrame", Prop("integer", "Optional program window start.")),
                ("endFrame", Prop("integer", "Optional program window end."))
            )));

        Register("undo",
            "Reverts the assistant's most recent timeline edit.",
            ObjectSchema());

        Register("redo",
            "Re-applies the assistant's most recently undone timeline edit.",
            ObjectSchema());

        // --- Transcript ---
        Register("get_transcript",
            "Returns the spoken transcript of the CURRENT timeline in project frames (on-device Whisper; cached).",
            ObjectSchema(Obj(
                ("startFrame", Prop("integer", "Start frame.")),
                ("endFrame", Prop("integer", "End frame.")),
                ("clipId", Prop("string", "Clip ID.")),
                ("trackIndex", Prop("integer", "Transcribe only this track index.")),
                ("granularity", Prop("string", "Words or segments.", ["words", "segments"])),
                ("language", Prop("string", "Language tag."))
            )));

        Register("remove_words",
            "Cut speech by the word, Descript-style.",
            ObjectSchema(Obj(
                ("words", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"integer"))))),
                ("matches", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("cutAggressiveness", Prop("string", "Cut aggressiveness.", ["tight", "balanced", "loose"])),
                ("language", Prop("string", "Language tag."))
            )));

        Register("remove_silence",
            "Remove dead air - quiet, speech-free sections - from the timeline's audio.",
            ObjectSchema());

        Register("detect_beats",
            "Detect musical beats and downbeats in a media asset's audio, on-device.",
            ObjectSchema(Obj(
                ("mediaRef", Prop("string", "Audio or video asset ID.")),
                ("startSeconds", Prop("number", "Start time.")),
                ("endSeconds", Prop("number", "End time."))
            ), ["mediaRef"]));

        // --- Text & captions ---
        Register("add_texts",
            "Adds text clips as timeline layers.",
            ObjectSchema(Obj(
                ("entries", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", ObjectSchema(
                        Merge(
                            Obj(
                                ("trackIndex", Prop("integer", "Track index.")),
                                ("startFrame", Prop("integer", "Start frame.")),
                                ("endFrame", Prop("integer", "End frame.")),
                                ("content", Prop("string", "Text content.")),
                                ("transform", Obj(
                                    ("type", (JsonNode)"object"),
                                    ("properties", TextBoxTransformProperties())
                                ))
                            ),
                            TextStyleProperties(),
                            Obj(
                                ("animation", Prop("string", "Animation preset; off clears.", ["off", "fadeIn", "popIn", "slideUp", "typewriter", "wordReveal", "wordSlide", "wordPop", "wordCycle", "highlightPop", "highlightBlock"])),
                                ("highlightColor", Prop("string", "Highlight color hex."))
                            )
                        ),
                        ["startFrame", "endFrame", "content"]
                    ))
                ))
            ), ["entries"]));

        Register("update_text",
            "Updates text clips or a captionGroupId.",
            ObjectSchema(
                Merge(
                    Obj(
                        ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                        ("captionGroupId", Prop("string", "Caption group ID.")),
                        ("content", Prop("string", "Replacement text.")),
                        ("transform", Obj(
                            ("type", (JsonNode)"object"),
                            ("properties", TextBoxTransformProperties())
                        ))
                    ),
                    TextStyleProperties(),
                    Obj(
                        ("animation", Prop("string", "Animation preset; off clears.", ["off", "fadeIn", "popIn", "slideUp", "typewriter", "wordReveal", "wordSlide", "wordPop", "wordCycle", "highlightPop", "highlightBlock"])),
                        ("highlightColor", Prop("string", "Highlight color hex."))
                    )
                )
            ));

        Register("add_captions",
            "Transcribes spoken audio with on-device Whisper and creates styled caption text clips.",
            ObjectSchema(
                Merge(
                    Obj(
                        ("language", Prop("string", "Language tag.")),
                        ("transform", Obj(
                            ("type", (JsonNode)"object"),
                            ("properties", Obj(
                                ("centerX", Prop("number", "centerX")),
                                ("centerY", Prop("number", "centerY")),
                                ("rotation", Prop("number", "Rotation in degrees, clockwise."))
                            ))
                        )),
                        ("textCase", Prop("string", "Text case.", ["auto", "upper", "lower"])),
                        ("censorProfanity", Prop("boolean", "Rejected on Windows Whisper (Apple Speech only).")),
                        ("maxWords", Prop("integer", "Max words per caption.")),
                        ("maxCharacters", Prop("integer", "Max characters per caption.")),
                        ("maximumGapSeconds", Prop("number", "Close gaps up to this many seconds (0-2, default 0.5).")),
                        ("trackIndex", Prop("integer", "Caption only this track index instead of auto-detecting.")),
                        ("subtitleMediaRef", Prop("string", "SRT/VTT library asset to place as captions. Cannot be combined with any other argument."))
                    ),
                    TextStyleProperties(),
                    Obj(
                        ("animation", Prop("string", "Animation preset; off clears.", ["off", "fadeIn", "popIn", "slideUp", "typewriter", "wordReveal", "wordSlide", "wordPop", "wordCycle", "highlightPop", "highlightBlock"])),
                        ("highlightColor", Prop("string", "Highlight color hex."))
                    )
                )
            ));

        // --- Color & effects ---
        Register("apply_color",
            "Author/refine a color grade on video/image clips with named controls.",
            ObjectSchema(Obj(
                ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("reset", Prop("boolean", "Reset.")),
                ("color", Obj(("type", (JsonNode)"object"))),
                ("exposure", Prop("number", "exposure")),
                ("contrast", Prop("number", "contrast")),
                ("saturation", Prop("number", "saturation")),
                ("vibrance", Prop("number", "vibrance")),
                ("temperature", Prop("number", "temperature")),
                ("tint", Prop("number", "tint")),
                ("highlights", Prop("number", "highlights")),
                ("shadows", Prop("number", "shadows")),
                ("blacks", Prop("number", "blacks")),
                ("whites", Prop("number", "whites")),
                ("shadowsHue", Prop("number", "shadowsHue")),
                ("shadowsAmount", Prop("number", "shadowsAmount")),
                ("shadowsLum", Prop("number", "shadowsLum")),
                ("midsHue", Prop("number", "midsHue")),
                ("midsAmount", Prop("number", "midsAmount")),
                ("midsGamma", Prop("number", "midsGamma")),
                ("highsHue", Prop("number", "highsHue")),
                ("highsAmount", Prop("number", "highsAmount")),
                ("highsGain", Prop("number", "highsGain")),
                ("masterCurve", CurvePoints()),
                ("redCurve", CurvePoints()),
                ("greenCurve", CurvePoints()),
                ("blueCurve", CurvePoints()),
                ("hueCurves", Obj(
                    ("type", (JsonNode)"object"),
                    ("properties", Obj(
                        ("targets", Obj(
                            ("type", (JsonNode)"array"),
                            ("items", ObjectSchema(Obj(
                                ("targetHue", Prop("number", "targetHue")),
                                ("hueShift", Prop("number", "hueShift")),
                                ("satScale", Prop("number", "satScale")),
                                ("lumShift", Prop("number", "lumShift"))
                            ), ["targetHue"]))
                        ))
                    ))
                )),
                ("lut", Obj(
                    ("type", (JsonNode)"object"),
                    ("properties", Obj(
                        ("path", Prop("string", "LUT path.")),
                        ("strength", Prop("number", "Strength."))
                    ))
                ))
            ), ["clipIds"]));

        Register("apply_effect",
            "Apply non-color effects to video/image clips. Optional effect position sets the final stack index; omitting it does not reorder an effect already on the clip.",
            ObjectSchema(Obj(
                ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("effects", Obj(
                    ("type", (JsonNode)"array"),
                    ("items", ObjectSchema(Obj(
                        ("type", Prop("string", "Effect type ID.")),
                        ("params", Obj(("type", (JsonNode)"object"))),
                        ("enabled", Prop("boolean", "Enabled.")),
                        ("position", Prop("integer", "Optional zero-based final index. Omit to keep an existing effect where it is, or to insert a new effect at its catalog position."))
                    ), ["type"]))
                )),
                ("remove", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string")))))
            ), ["clipIds"]));

        Register("inspect_color",
            "Measure color scopes of a timeline clip's current graded look.",
            ObjectSchema(Obj(
                ("clipId", Prop("string", "Clip ID.")),
                ("mediaRef", Prop("string", "Media reference ID.")),
                ("atFrame", Prop("integer", "Frame index.")),
                ("reference", Prop("string", "Reference asset ID."))
            )));

        Register("denoise_audio",
            "Apply deterministic Windows denoise to audio clips during shared preview/export decoding.",
            ObjectSchema(Obj(
                ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("strength", Prop("number", "Strength (0-1).")),
                ("enabled", Prop("boolean", "Enabled."))
            ), ["clipIds"]));

        Register("duck_audio",
            "Writes attack/release volume envelopes on a background audio track under voice-clip coverage.",
            ObjectSchema(Obj(
                ("voiceTrack", Prop("integer", "Voice track index.")),
                ("bgTrack", Prop("integer", "Background track index.")),
                ("reductionDb", Prop("number", "Reduction in dB.")),
                ("attack", Prop("number", "Attack time in seconds (default 0.12).")),
                ("release", Prop("number", "Release time in seconds (default 0.30).")),
                ("source", Prop("string", "Only auto (voice-clip coverage) is currently supported.", ["auto", "transcript", "vad"]))
            ), ["voiceTrack", "bgTrack", "reductionDb"]));

        // --- Generation ---
        Register("list_models",
            "Lists AI models with their capabilities.",
            ObjectSchema(Obj(
                ("type", Prop("string", "Model type.", ["video", "image", "audio", "upscale"]))
            )));

        Register("generate_video",
            "Starts an async AI video generation. " + SpendsMoney + " Hosted generation is quote-only: call quote_media first, show its credits in the proposal, and pass its quoteId; the quote binds prompt, settings and inputs. BYOK and demo take the direct arguments.",
            ObjectSchema(Obj(
                ("prompt", Prop("string", "Text description. Required for BYOK and demo; with a quoteId it may be omitted, or must match the quote.")),
                ("name", Prop("string", "Display name.")),
                ("model", Prop("string", "Model ID.")),
                ("duration", Prop("integer", "Duration in seconds (BYOK and demo; hosted durations are quote_media settings).")),
                ("operation", Prop("string", "Must match the quoted operation when given.")),
                ("aspectRatio", Prop("string", "Aspect ratio.")),
                ("resolution", Prop("string", "Resolution.")),
                ("startFrameMediaRef", Prop("string", "Start frame mediaRef.")),
                ("endFrameMediaRef", Prop("string", "End frame mediaRef.")),
                ("sourceVideoMediaRef", Prop("string", "Source video mediaRef.")),
                ("sourceClipId", Prop("string", "Source clip ID.")),
                ("referenceImageMediaRefs", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("referenceVideoMediaRefs", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("referenceAudioMediaRefs", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("quoteId", Prop("string", "Accepted quote_media id. Required for hosted generation.")),
                ("maxCredits", Prop("integer", "Optional accepted spend cap; the submit fails when the quote exceeds it.")),
                ("settings", Obj(("type", (JsonNode)"object"), ("description", (JsonNode)"Must match the quoted settings when given."))),
                ("inputs", MediaInputs("Must match the quoted inputs when given.")),
                ("folder", Prop("string", "Folder path."))
            )));

        Register("quote_media",
            "Quotes a hosted video, audio, or upscale generation without spending. Pass the returned quoteId to generate_video, generate_audio, or upscale_media; any model, operation, prompt, settings, or input change needs a fresh quote. Free to cancel until rendering starts.",
            ObjectSchema(Obj(
                ("model", Prop("string", "Hosted media model ID from list_models.")),
                ("operation", Prop("string", "Media operation.", ["text_to_video", "image_to_video", "reference_to_video", "video_edit", "motion_control", "lip_sync", "text_to_music", "video_to_music", "text_to_sfx", "video_to_sfx", "text_to_speech", "dubbing", "audio_isolation", "text_to_audio", "upscale_video", "upscale_image"])),
                ("prompt", Prop("string", "Text description. May be empty only for input-only operations.")),
                ("settings", Obj(("type", (JsonNode)"object"), ("description", (JsonNode)"Temno option names to values; unknown names are rejected."))),
                ("inputs", MediaInputs("Ordered {role, mediaRef} inputs. Roles: first_frame, last_frame, reference_image, source_video, source_audio."))
            ), ["model", "operation"]));

        Register("generate_image",
            "Starts an async AI image generation. " + SpendsMoney + " Hosted generation is quote-only: call quote_image first, show its credits in the proposal, and pass its quoteId. BYOK and demo take the direct arguments.",
            ObjectSchema(Obj(
                ("prompt", Prop("string", "Text description. Required for BYOK and demo; with a quoteId it may be omitted, or must match the quote.")),
                ("name", Prop("string", "Display name.")),
                ("model", Prop("string", "Model ID.")),
                ("aspectRatio", Prop("string", "Aspect ratio.")),
                ("resolution", Prop("string", "Resolution.")),
                ("quality", Prop("string", "Quality.")),
                ("settings", Obj(("type", (JsonNode)"object"), ("description", (JsonNode)"Hosted Temno option names to values; unknown names are rejected."))),
                ("referenceMediaRefs", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("maskMediaRef", Prop("string", "Mask asset ID (hosted mask-capable operations).")),
                ("quoteId", Prop("string", "Accepted quote_image id. Required for hosted generation.")),
                ("maxCredits", Prop("integer", "Optional accepted spend cap; the submit fails when the quote exceeds it.")),
                ("folder", Prop("string", "Folder path."))
            )));

        Register("quote_image",
            "Quotes a hosted image generation or edit without spending. Pass the returned quoteId to generate_image or edit_image; any prompt, model, operation, settings, or source change needs a fresh quote. Free to cancel until rendering starts.",
            ObjectSchema(Obj(
                ("prompt", Prop("string", "Text description.")),
                ("model", Prop("string", "Hosted image model ID from list_models.")),
                ("operation", Prop("string", "Image operation.", ["generate_image", "edit_image"])),
                ("settings", Obj(("type", (JsonNode)"object"), ("description", (JsonNode)"Temno option names to values; unknown names are rejected."))),
                ("aspectRatio", Prop("string", "Legacy alias for the aspect_ratio setting.")),
                ("resolution", Prop("string", "Legacy alias for the resolution setting.")),
                ("quality", Prop("string", "Legacy alias for the quality setting.")),
                ("referenceMediaRefs", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("maskMediaRef", Prop("string", "Mask asset ID (mask-capable operations)."))
            ), ["prompt", "model"]));

        Register("edit_image",
            "Starts an async hosted AI image edit from a quote_image quote. It spends the user's Temno credits, so call it only after the user says yes to a proposal that showed the quote's credits. Edit needs at least one source image bound in the quote.",
            ObjectSchema(Obj(
                ("quoteId", Prop("string", "Accepted quote_image id.")),
                                ("maxCredits", Prop("integer", "Optional accepted spend cap; the submit fails when the quote exceeds it.")),
                ("prompt", Prop("string", "Must match the quoted prompt when given.")),
                ("model", Prop("string", "Must match the quoted model when given.")),
                ("name", Prop("string", "Display name.")),
                ("settings", Obj(("type", (JsonNode)"object"), ("description", (JsonNode)"Must match the quoted settings when given."))),
                ("referenceMediaRefs", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("maskMediaRef", Prop("string", "Must match the quoted mask when given.")),
                ("folder", Prop("string", "Folder path."))
            ), ["quoteId"]));

        Register("resume_generation",
            "Resumes a hosted generation that is still pending, rendering, or recovering. It does not start a new job or spend again. Pass mediaRef to resume one placeholder; omit it to resume every unfinished generation for this account, including ones whose project is not open.",
            ObjectSchema(Obj(
                ("mediaRef", Prop("string", "Placeholder asset id. Omit to resume every unfinished hosted generation."))
            )));

        Register("generate_audio",
            "Starts an async AI audio generation: text-to-speech, text-to-music, or video-to-music. " + SpendsMoney + " Hosted generation is quote-only: call quote_media first, show its credits in the proposal, and pass its quoteId; the quote binds prompt, settings and inputs. BYOK and demo take the direct arguments.",
            ObjectSchema(Obj(
                ("prompt", Prop("string", "Required for TTS and text-to-music.")),
                ("name", Prop("string", "Display name.")),
                ("model", Prop("string", "Model ID.")),
                ("voice", Prop("string", "TTS only voice name.")),
                ("operation", Prop("string", "Must match the quoted operation when given.")),
                ("lyrics", Prop("string", "MiniMax only lyrics.")),
                ("styleInstructions", Prop("string", "Gemini TTS only delivery instructions.")),
                ("instrumental", Prop("boolean", "Music models only.")),
                ("duration", Prop("integer", "Length in seconds (BYOK and demo; hosted lengths are quote_media settings).")),
                ("videoSourceStartFrame", Prop("integer", "Start frame.")),
                ("videoSourceEndFrame", Prop("integer", "End frame.")),
                ("videoSourceMediaRef", Prop("string", "Score this video asset.")),
                ("quoteId", Prop("string", "Accepted quote_media id. Required for hosted generation.")),
                ("maxCredits", Prop("integer", "Optional accepted spend cap; the submit fails when the quote exceeds it.")),
                ("settings", Obj(("type", (JsonNode)"object"), ("description", (JsonNode)"Must match the quoted settings when given."))),
                ("inputs", MediaInputs("Must match the quoted inputs when given.")),
                ("folder", Prop("string", "Folder path."))
            )));

        Register("upscale_media",
            "Upscales an existing video or image asset. " + SpendsMoney + " Hosted generation is quote-only: call quote_media first, show its credits in the proposal, and pass its quoteId; mediaRef must then be the quoted source. BYOK and demo take the direct arguments.",
            ObjectSchema(Obj(
                ("mediaRef", Prop("string", "Asset ID to upscale. Required for BYOK and demo; with a quoteId it may be omitted, or must be the quoted source.")),
                ("model", Prop("string", "Upscaler model ID.")),
                ("operation", Prop("string", "Must match the quoted operation when given.")),
                ("quoteId", Prop("string", "Accepted quote_media id. Required for hosted generation.")),
                ("maxCredits", Prop("integer", "Optional accepted spend cap; the submit fails when the quote exceeds it.")),
                ("settings", Obj(("type", (JsonNode)"object"), ("description", (JsonNode)"Must match the quoted settings when given."))),
                ("inputs", MediaInputs("Must match the quoted inputs when given.")),
                ("sourceClipId", Prop("string", "Source clip ID."))
            )));

        Register("generate_google",
            "Windows alias for generate_image or generate_video on BYOK (Convex/HTTP) or demo generation; hosted generation uses quote_image or quote_media instead. Not a Google cloud generation API. On BYOK it bills the user's own provider, so call it only after the user says yes to your proposal.",
            ObjectSchema(Obj(
                ("type", Prop("string", "Media type.", ["image", "video"])),
                ("prompt", Prop("string", "Prompt details.")),
                ("model", Prop("string", "Model ID from list_models.")),
                ("aspectRatio", Prop("string", "Aspect ratio.")),
                ("resolution", Prop("string", "Resolution quality preset.", ["720p", "1080p", "2k", "4k"])),
                ("name", Prop("string", "Display name.")),
                ("folder", Prop("string", "Destination folder path."))
            ), ["type", "prompt"]));

        // --- Presets & templates ---
        Register("apply_style",
            "Applies a saved text style preset.",
            ObjectSchema(Obj(
                ("styleId", Prop("string", "Preset name.")),
                ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                ("captionGroupId", Prop("string", "Caption group ID."))
            ), ["styleId"]));

        Register("save_text_style",
            "Saves a named, reusable text style preset.",
            ObjectSchema(
                Merge(
                    Obj(
                        ("name", Prop("string", "Preset name.")),
                        ("clipIds", Obj(("type", (JsonNode)"array"), ("items", Obj(("type", (JsonNode)"string"))))),
                        ("captionGroupId", Prop("string", "Caption group ID."))
                    ),
                    TextStyleProperties()
                ),
                ["name"]
            ));

        Register("list_text_styles",
            "Lists saved text style presets.",
            ObjectSchema());

        Register("remove_text_style",
            "Deletes a saved text style preset by ID or name.",
            ObjectSchema(Obj(
                ("styleId", Prop("string", "Preset name or ID."))
            ), ["styleId"]));

        Register("save_export_preset",
            "Saves a named, reusable export configuration.",
            ObjectSchema(Obj(
                ("name", Prop("string", "Preset name.")),
                ("codec", Prop("string", "Codec.", ["H.264", "H.265", "ProRes"])),
                ("resolution", Prop("string", "Resolution.", ["720p", "1080p", "2K", "4K", "Match Timeline"])),
                ("maxRenderQuality", Prop("boolean", "Higher quality target on the software H.264 and H.265 encoders (x264, x265) when bitrateMbps is not set: CRF 18 instead of 23. No effect on hardware encoders (NVENC, QSV, AMF), OpenH264 or ProRes.")),
                ("maxBitDepth", Prop("boolean", "Exports HEVC 10-bit tagged HLG, whatever codec says. The picture is rendered in 8-bit SDR and converted, not processed at higher precision.")),
                ("bitrateMbps", Prop("number", "Bitrate in Mbps."))
            ), ["name"]));

        Register("list_export_presets",
            "Lists saved export presets.",
            ObjectSchema());

        Register("remove_export_preset",
            "Deletes a saved export preset by ID or name.",
            ObjectSchema(Obj(
                ("presetId", Prop("string", "Preset name or ID."))
            ), ["presetId"]));

        Register("save_project_template",
            "Saves the current project as a named template.",
            ObjectSchema(Obj(
                ("name", Prop("string", "Template name."))
            ), ["name"]));

        Register("list_project_templates",
            "Lists saved project templates.",
            ObjectSchema());

        Register("remove_project_template",
            "Deletes a saved project template by name.",
            ObjectSchema(Obj(
                ("name", Prop("string", "Template name."))
            ), ["name"]));

        // --- Meta & Skill ---
        Register("send_feedback",
            "Report an agent limitation or bug to the Temno team.",
            ObjectSchema(Obj(
                ("category", Prop("string", "Category.", ["missing_capability", "wrong_result", "confusing_ux", "failure", "suggestion"])),
                ("summary", Prop("string", "Summary line.")),
                ("details", Prop("string", "Paraphrased details.")),
                ("severity", Prop("string", "Severity.", ["low", "medium", "high"]))
            ), ["category", "summary"]));

        Register("read_skill",
            "Load the full instructions for one of the skills listed under # Skills in your system prompt.",
            ObjectSchema(Obj(
                ("id", Prop("string", "The skill id."))
            ), ["id"]));
    }
}
