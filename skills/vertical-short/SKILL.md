---
name: vertical-short
description: Cut a vertical Short or Reel (9:16, under 60 seconds) from a long video such as a stream VOD, a match, a talk or a podcast episode, on a new timeline, reframed and captioned. Use when the user asks for a Short, a Reel, a TikTok, a vertical clip or a highlight from a longer recording.
metadata:
  author: Temno
  version: "1.0"
---

# Vertical Short from a long video

The long video stays as it is. The Short is built on a timeline of its own.

## 1. Find the moment

1. Call get_media and pick the source video. If there are several, ask which one.
2. If the user named the moment ("the comeback in round three", "where she talks about pricing"), find it with search_media: scope "spoken" for something said, "visual" for something seen, "both" when unsure. Spoken search needs a transcript, so if it finds nothing spoken, call inspect_media on the source with wordTimestamps true, then search again.
3. If the user did not name one, call inspect_media on the source with overview true and read its transcript. Propose two or three moments of 15 to 60 seconds, each with its start and end in source seconds and one line on why it would hold a viewer. Wait for the user to choose.

## 2. Build the Short

4. Call create_timeline with a name such as "Short - <the moment>". It becomes the active timeline.
5. Call set_project_settings with aspectRatio "9:16". This sets the new timeline only.
6. Place the moment with add_clips: the source's mediaRef, source [startSeconds, endSeconds] and startFrame 0.
7. If the moment has pauses, call remove_silence now. It splits the clip, so it comes before reframing.
8. Reframe each video clip of the new timeline. Call suggest_crop with targetAspect 0.5625 and apply true, which trims the sides. Then call set_clip_properties on the same clips with transform centerX 0.5, centerY 0.5, height 1 and width equal to the source's width divided by its height, times 1.778 (3.16 for a 16:9 source), so the trimmed picture fills the frame without stretching. Check one frame with inspect_timeline.
9. Most Shorts are watched muted, so call add_captions.
10. Offer a short hook title for the first two seconds. If the user wants one, place it with add_texts.

## 3. Export

11. Export if the user asked for an export; otherwise ask first. Call export_project with mode "video", codec "H.264", resolution "Match Timeline" (the timeline is already 1080 by 1920) and the new timeline's timelineId.

## 4. Report

12. Say in one or two sentences which moment was used, how long the Short runs, and where the export went.
