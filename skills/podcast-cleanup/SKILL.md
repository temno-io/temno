---
name: podcast-cleanup
description: Clean up a recorded conversation, podcast or interview by cutting dead air and filler words, reducing steady background noise, ducking music under the voices and adding captions. Use when the user asks to tighten, clean up or polish spoken audio or a talking-head recording.
metadata:
  author: Temno
  version: "1.0"
---

# Podcast cleanup

Work on the active timeline. Edits are undoable, so make them and say what changed. Ask only where a step says to.

## 1. Look first

1. Call get_timeline, then get_media.
2. Tell the voice recordings from any music bed with inspect_media, never by file name.
3. If the timeline is empty, ask which recordings to use, then place them with add_clips.
4. Local transcription understands English only. If the speech is in another language, say so and stop after step 5.

## 2. Remove dead air

5. Call remove_silence once. It cuts quiet, speech-free stretches from the timeline's audio.

## 3. Remove fillers and flubs

6. Call get_transcript with granularity "words" and read it as prose once.
7. Collect the indices of filler words ("um", "uh", "er", and "you know" or "like" only where they carry no meaning) and of false starts the speaker repeats at once.
8. Call remove_words once with those indices and cutAggressiveness "balanced".
9. Indices shift after a cut. Read get_transcript again before any further remove_words.

## 4. Sound

10. If a voice carries steady noise (hum, hiss, room tone), call denoise_audio on those clips with strength 0.5. Leave clean recordings alone.
11. If there is a music bed, call duck_audio with the voice track as voiceTrack, the music track as bgTrack and reductionDb 12.

## 5. Captions

12. Ask whether the user wants captions. If they do, call add_captions.

## 6. Report

13. In two or three sentences, say what changed: about how much dead air came out, how many words were cut, and what else was applied. Offer to undo any step.
