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

Select your microphone and meeting output, configure your own provider credentials through the app, then choose **Start listening**. Microphone and computer audio are transcribed using Deepgram; questions and selected context are sent to OpenAI. The app never speaks or sends an answer into the meeting. Floating readers remain interactive while staying out of the taskbar.

Credentials use Windows account-bound protection. Recordings, knowledge, personal context, development notes and private evidence are not part of this public repository. Keep your own data backups separately.

## Layout

- `src/SecondBrain.App`: WPF interface and Windows/provider adapters.
- `src/SecondBrain.Core`: reader, transcript, knowledge and storage logic.
- `tests`: isolated core regression checks.
- `scripts`: builds, packaged checks and opt-in provider checks.

Live-provider scripts require your configured credentials and may incur provider usage charges. Run them intentionally; ordinary build and smoke checks do not require meeting capture.
