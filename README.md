# Second Brain

A Windows desktop meeting companion built with .NET 10 and WPF. It captures the selected microphone and computer audio, transcribes the conversation, retrieves context from a local Markdown vault, and presents streamed answers in voice-following reader windows.

## Build

Requires Windows x64, the .NET 10 SDK and Git for Windows. From the repository root:

```powershell
./scripts/build.ps1
```

The script restores packages, builds with warnings treated as errors, runs the core tests and publishes a self-contained executable to `dist/single-file/SecondBrain.exe`.

For an SDK or package feed outside the default installation:

```powershell
./scripts/build.ps1 -Dotnet 'path/to/dotnet.exe' -PackageSource 'path/to/package-feed'
```

Run isolated packaged checks after building:

```powershell
./scripts/smoke.ps1
```

Packaged checks use generated fixtures. They do not establish real meeting, microphone, display or screen-sharing compatibility. `scripts/performance.ps1` measures rendering for twenty minutes; it is not a two-hour meeting test.

## Run

Place the executable in a writable folder. Its `data` and `Vault` folders stay beside it by default. Launching places the controls in the system tray. Click the tray icon to open controls; closing controls keeps the app running. Use **Exit Second Brain** to stop and save the session and exit.

With your provider credentials configured, select your microphone and meeting output, then choose **Start listening**. The current build expects account-protected provider key files in its data folder; it does not yet provide a credential setup screen. Microphone and computer audio are transcribed using Deepgram; questions and selected context are sent to OpenAI. The app never speaks or sends an answer into the meeting. Floating readers remain interactive while staying out of the taskbar.

Credentials use Windows account-bound protection. Recordings, knowledge, personal context, development notes and private evidence are not part of this public repository. Keep your own data backups separately.

Expand **Answer timing** on Live to see transcription, detection, queue, retrieval and generation timing, plus first/later request percentiles. Export a JSON report, or find `latency.json` in a saved meeting folder after stopping. First/later requests are cold/warm session proxies; provider cache state is unknown. Speech-end timing requires provider word timestamps. These diagnostics measure readable text readiness, not physical screen presentation or verified live performance targets.

## Layout

- `src/SecondBrain.App`: WPF interface and Windows/provider adapters.
- `src/SecondBrain.Core`: reader, transcript, knowledge and storage logic.
- `tests`: isolated core regression checks.
- `scripts`: builds, packaged checks and opt-in provider checks.

Live-provider scripts require your configured credentials and may incur provider usage charges. Run them intentionally; ordinary build and smoke checks do not require meeting capture.

The Live companion can keep an answer flowing: voice, timed reading or forward taps within the last 40 words request the next grounded section and append it to the same reader answer. Tapping or scrolling moves active voice following to your chosen word; read from there without pressing Resume. Manual navigation clears old motion and rejects revisions of the phrase recognized before the move. Explicitly paused readers stay paused, and manual navigation still pauses timed scrolling. Backward navigation and older answers do not request more text. Disable **Keep this answer flowing as I read** to stop future automatic extensions. It stops on missing further supported detail, cancellation, a newer question, or the existing bounded answer size. Opening retrieval uses a 250 ms keyword budget; deeper retrieval can add evidence afterward.

Recorded transcripts retain optional word timestamps and source metadata in a versioned `transcript-details.jsonl` sidecar. Original transcript journals remain unchanged. Choose **Meetings > View selected transcript** to inspect finalized text and word times; older recordings remain readable without invented timestamps. Backups include these sidecars.

**Settings > Audio & reader > Audio speaker labels** configures provisional remote speaker separation and an optional microphone participant name. Streaming diarization pins `diarize_model=v1`; remote IDs are session-local and new connections receive new labels. Missing, weak or detectably overlapping word metadata stays Unknown. Streaming speaker confidence is not invented. Meeting vocabulary is sent as bounded Nova-3 keyterm hints; the Live details show when the hint budget reduces the list. Physical meeting speaker accuracy remains to be qualified.

Saved meeting transcripts group speaker turns and support text/speaker search, bookmarks and replay from the selected timestamp. Name a turn, rename a speaker, split selected words or merge speakers; Undo reverses the last change. Corrections live in a source-bound local journal and backups include them. Original transcript and timing files remain unchanged. Optional general-guidance permission in Settings lets continuation add explanations and hypothetical examples without inventing client facts.
