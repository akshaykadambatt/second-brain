# Second Brain

A Windows desktop meeting companion built with .NET 10 and WPF. It captures the selected microphone and computer audio, transcribes the conversation, retrieves context from a local Markdown vault, and presents streamed answers in voice-following reader windows.

## Build

Requires Windows 10 version 2004 or later (x64), the .NET 10 SDK and Git for Windows. From the repository root:

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

Optional meeting-window capture is off by default. In **Settings > Audio & reader**, enable it and explicitly choose one visible window. Selection does not start listening. While listening, Windows Graphics Capture processes frames locally in memory without saving or sending images. Clearing selection, stopping listening or exiting stops capture. Closed, minimized, protected or unavailable windows fall back to audio-only listening. Permission and selection reset on app exit. The `capture` packaged check uses a visible synthetic window and the real native capture API.

Experimental Chrome Teams name hints use only the explicitly selected, freshly captured window. Add exact participant names to the meeting brief, enable optional window capture and start listening. The adapter requires a visible Teams document on `teams.microsoft.com`, `teams.live.com` or `teams.cloud.microsoft` that exposes its URL through Windows accessibility. It recognizes English tile labels `Name is speaking`, `Name, speaking` or `Speaking: Name` (or an exact name with Speaking help text). Other layouts/languages, missing labels, duplicate names, overlap and stale observations retain the audio speaker label. Chrome accessibility inspection is bounded and runs away from the audio/UI work. No images are saved or uploaded.

Matched names appear as **screen hint** in saved transcript review and remain provisional. The versioned `speaker-name-hints.jsonl` sidecar binds each hint to the original turn; manual corrections take priority and backup/restore includes hints. This adapter has synthetic rule/lifecycle tests and a native accessibility rejection fixture. Real Chrome Teams meeting layouts, named-turn precision and eligible-turn coverage have **not** been qualified. Chrome Meet has an independent experimental adapter for `meet.google.com/abc-defg-hij` meeting URLs. It accepts exact roster labels `Name is speaking`, `Name (speaking)` or `Speaking: Name`, with the same conservative timing and overlap rules. The `meet` packaged phase independently exercises provenance, lifecycle, timeout and persistence. Real Meet layouts, precision and coverage also remain unqualified; inaccessible or unsupported layouts keep audio labels.

Version 0.27.0 includes independently tracked Teams and Meet hint adapters; real-meeting name accuracy remains an open acceptance gate.

**Knowledge > Import documents** accepts Markdown and text files, up to 20 per batch. Choose an optional project explicitly; this does not change the current meeting or start capture. Each success preserves an exact original under `Imports/.../Attachments` and creates an editable `Content.md` with original line references. Identical bytes in the same project are kept once, even after manual note edits; changed bytes create a new import. Saved imports shows source/note links after restart. Failures are shown per file. UTF-8 and BOM-marked UTF-16 are supported, with 2 MB / 500,000-character input and 1.9 MB extracted-note limits. Unsupported encodings need conversion before import. Notes are indexed using your current keyword/semantic settings, and backup/restore includes original copies and import metadata. Imported sources are excluded from automatic meeting-note edits.

Version 0.28.0 adds the document-import workflow; existing recordings, manual vault edits and independent knowledge history remain compatible.

Version 0.29.0 adds **PDF imports** to the same Knowledge workflow, placed at the top of that page. Text-bearing PDFs retain their exact original and provide page references plus original-page links in the extracted note. Mixed PDFs list pages without text; image-only/scanned or blank files are explicitly rejected because OCR is not included. Damaged and encrypted PDFs fail separately. Limits: 20 MB, 250 pages, 500,000 extracted characters, 1.9 MB extracted notes and 30 seconds per PDF. Parsing runs in a separate local process with a 512 MB memory limit; timeout, cancellation and parent exit terminate the worker. Source images/documents are not sent to the parser service or a network service; subsequent knowledge indexing uses your existing keyword/semantic preference. Columns and tables can have imperfect text order: verify the preserved original. PdfPig 0.1.16 is bundled under Apache 2.0; **PDF library notices** displays its embedded license and upstream notices.

PPTX imports preserve presentations and extract shape/table text with slide references in presentation order, including marked hidden slides. Blank slides are listed. Notes, masters, charts and image text are excluded. Presentations are limited to 500 slides; other Office limits below apply.

DOCX imports preserve the original and extract body paragraphs and tables with document-section and paragraph references. Headers, footnotes, comments, images and field instructions are excluded and disclosed. Encrypted, malformed, ambiguous or oversized packages fail per file; no Office installation or external link access is needed. Office sources are limited to 20 MB compressed / 64 MB expanded, 4,096 package entries and 500,000 extracted characters.

**Clients, people, projects and facts** on Knowledge opens a client-scoped Markdown catalog. Save a client brief on Live first. Records support aliases, observations, explicit confirmation and dated source quotes. Decisions and commitments require a source; owner/due wording must occur in that quote. Confirmation records your review, not an automated truth assessment. **Save observation** reverses confirmation. Manual edits remain supported, stale saves fail without overwriting, and local vault history retains changes. Automatic note maintenance excludes these curated records.

Knowledge's client selector controls document imports, search and **Answer from this client’s sources**. Answers quote retrieved passages with file/line references, source dates and transcript timestamps where available; missing evidence produces an abstention. Differing records for a named decision or commitment are flagged for review, without choosing a current value automatically. This conservative conflict check does not detect every semantic contradiction. Live retrieval and automatic note updates use the saved session client. **General** uses unassigned notes; old notes are not assigned by matching a project name. Use **Assign an existing vault note** for explicit, reversible assignment, or add `client_id: <saved profile GUID>` to Markdown frontmatter. Each note is assigned independently; original recordings are unchanged.
