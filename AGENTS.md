# YAOLlm — Agent Guide

## Overview

YAOLlm is a Windows desktop overlay application that provides an LLM-powered chat interface accessible via a global hotkey. The app runs as a system tray icon, renders its UI in a WebView2 control backed by Tailwind CSS + Alpine.js, and streams responses from multiple LLM providers.

## Build & Run

```bash
dotnet build          # Debug build
dotnet run            # Run in debug
dotnet test           # Run the xunit test suite (tests/YAOLlm.Tests)
dotnet publish -p:PublishSingleFile=true -c Release -r win-x64 --self-contained true YAOLlm.csproj  # Release build
```

- `build.bat` publishes to `bin\Release\net8.0-windows\win-x64\publish\YAOLlm.exe` and copies to `E:\Apps\`.
- **Restarting the app after a deploy: NEVER `Start-Process` it from the agent shell.** The agent shell can run in **session 0** (services session — verify with `(Get-Process -Id $PID).SessionId`): any process it launches inherits session 0, where GUI cannot reach the user's desktop — tray icon invisible, `RegisterHotKey` fails, and WebView2 controller creation fails with `0x80070578 Invalid window handle` (misdiagnosed for hours on 2026-09-15 as a broken WebView2 runtime; the runtime was fine). The correct restart from any session is the interactive scheduled task: `schtasks /run /tn "YAOLlm-Restart"` (created once with `schtasks /create /tn "YAOLlm-Restart" /tr "E:\Apps\YAOLlm.exe" /sc once /st 23:59 /it /f`), which starts the exe in the user's interactive session. Deploy flow: `taskkill /IM YAOLlm.exe /T /F`, wait until no `YAOLlm` processes remain, `build.bat`, then `schtasks /run /tn "YAOLlm-Restart"`, then check `E:\Apps\yaollm.log` for `Navigation completed` to confirm the UI actually came up.
- Runs on .NET 8 (`net8.0-windows`), WinForms + WebView2.
- Single instance is enforced with a named mutex (`YAOLlm_SingleInstance`) in `Program.Main`.
- The root csproj excludes `tests\**` from compilation — the test project compiles separately.

## Architecture

```
Program.cs → MainForm (WinForms shell + WebView2 host)
                ├─ PresetManager        → ILLMProvider (4 implementations), owns shared HttpClient
                ├─ ConversationManager  → ChatMessage history
                ├─ SessionStore         → ~/.yaollm.session.json (session resume across restarts)
                ├─ StatusManager        → Status enum (Idle/Sending/Receiving/Searching/Fetching)
                ├─ WebViewBridge        → C# ↔ JS messaging (JSON over postMessage)
                ├─ TrayIconManager      → System tray
                └─ ui/index.html        → Embedded resource, served from temp file
                                              └─ Alpine.js + Tailwind + highlight.js (all vendored)
```

### Control Flow

1. User types in WebView2 textarea → JS sends `{type: "send", text}` via `window.chrome.webview.postMessage`
2. `WebViewBridge.OnWebMessageReceived` dispatches to `MainForm.OnSendMessage`
3. `MainForm.SendMessage` acquires `_sendLock`, adds user message to `ConversationManager`, calls `provider.StreamAsync()`
4. Each `yield return` chunk goes to `_bridge.ChatStream(...)` → JS appends to streaming HTML (adaptive throttle: 50ms → 100ms > 5k chars → 250ms > 20k chars)
5. On completion, full response added to conversation, displayed as `chat_message`

### Key Constraints

- **`_sendLock` (SemaphoreSlim)**: Only one LLM request at a time. Busy sends are queued (`_messageQueue`) and displayed as pending bubbles.
- **Provider disposal on preset switch**: `ProcessCmdKey` and `ApplyPendingPresetSwitch` unsubscribe from the old provider's `OnStatusChange` and `Dispose()` it before creating the new one.
- **`_isDisposed` flag**: All providers check `ThrowIfDisposed()` inside async streams before making HTTP calls, preventing use-after-dispose.
- **Shared HttpClient**: `PresetManager` owns one `HttpClient` (5-minute timeout for long streams) passed to every provider. Auth headers are set per-request (`CustomizeRequest`), so the client is safe to reuse across preset switches. Providers only dispose a client they created themselves.
- **HTML served from temp file**: `WriteHtmlToTempFile()` extracts `ui/index.html` + vendored JS/CSS from embedded resources to `%TEMP%\YAOLlm\`, skipping writes when the file on disk is already identical, then navigates via `file://` URI.
- **WebView2 user data folder**: Set to `%TEMP%\YAOLlm\WebView2` to avoid profile lock conflicts.
- **WebView2 init retry**: `MainForm.InitializeOverlayAsync` retries `EnsureCoreWebView2Async` up to 10× (3s apart, ~30s window) — a hard-killed instance's teardown (hotkey, browser process, profile) can briefly make controller creation fail with odd HRESULTs. The app stays in the tray while retrying and never ends up UI-less after a normal restart.
- **No WebView2 page transparency**: `DefaultBackgroundColor` must stay opaque black and `html/body` must have an opaque `background` — do not reintroduce `transparent`. WebView2 page transparency triggers a compositing regression in the Evergreen runtime (~152, Sep 2026): partial repaints (text selection, scrolling) composite regions against white, flashing white boxes / a white chat background. The overlay's translucency comes from `FormLayout`'s form-level `Opacity = 0.9`, which is unaffected.

## Providers

All providers implement `IAsyncEnumerable<string> StreamAsync(...)` — streaming is mandatory.

| Provider | Base Class | API Style | Web Search |
|---|---|---|---|
| `GeminiProvider` | `BaseLLMProvider` | Gemini SSE (`{BASE_URL}/models/{m}:streamGenerateContent?alt=sse`) | Yes (`web_search` tool) |
| `OllamaProvider` | `BaseLLMProvider` | Ollama JSON streaming (`{BASE_URL}/api/chat`) | No (`SupportsWebSearch = false`) |
| `OpenAICompatibleProvider` | `OpenAIStyleProvider` | OpenAI-compatible SSE (`{BASE_URL}/chat/completions`) | Yes |
| `OpenRouterProvider` | `OpenAIStyleProvider` | OpenAI-compatible SSE + `HTTP-Referer`/`X-Title` headers | Yes (`web_search` tool) |

All provider base URLs come from config (`PRESET_N_BASE_URL`) — see [Configuration](#configuration). Providers hardcode only the protocol resource path (`/chat/completions`, `/api/chat`, `/models/...`).

There are only **two real protocol implementations** per family: `OpenAICompatibleProvider` serves every OpenAI-style config profile (`openai`, `openai-compatible`, `deepseek`, `zai` — the profile name is just a ctor param used in logs/errors), and `OpenRouterProvider` adds attribution headers. Don't create new per-vendor subclasses for OpenAI-style APIs — add a profile name in `PresetManager` instead.

### `BaseLLMProvider` — shared transport & tooling

- `PostWithRetryAsync(url, jsonPayload, ct)` — POST with exponential backoff on 429/503/network errors; throws `LLMException` on final failure.
- `CustomizeRequest(HttpRequestMessage)` — virtual hook for per-provider auth headers.
- `ReadSseDataLinesAsync(response, ct)` — shared SSE reader yielding `data:` payloads.
- `ExecuteWebSearchToolAsync` / `ExecuteWebScrapeToolAsync` / `ExecuteFileReadToolAsync` — shared tool executors that also update `CompletedSearchCount`/`CompletedScrapeCount`/`CompletedFileReadCount`/`CompletedSearchSummaries`.
- `ExtractTtsText(ToolCall)`, `GetIntArg(...)` — TTS/tool argument helpers. Note: JSON scalars from `DeserializeArguments` box as `JsonElement`, so numeric args must go through `GetIntArg`.

### `OpenAIStyleProvider` — full OpenAI-style template

Implements the entire streaming loop: SSE parsing (`TryParseStreamChunk`), DSML tag filtering, tool-call delta buffering (`ToolCallBuilder`), TTS separation, tool execution via the shared executors, and follow-up request re-issuing (including `reasoning_content` when the model emitted one). Subclasses only supply `StreamUrl` and optionally `CustomizeRequest`.

`OllamaProvider` extends `BaseLLMProvider` directly (JSON-per-line, not SSE). It does **not** support web search; the `tts_summary` tool call is handled as a terminal spoken summary (raised as a TTS `ProviderStatus`, no tool-result round-trip).

`GeminiProvider` builds contents with `System.Text.Json.Nodes` (no reflection), authenticates via the `x-goog-api-key` header (never in the URL), and runs a multi-round tool loop: model text + `functionCall` parts are appended as `"model"` role, tool results as `"function"` role, then the request is re-issued.

### Status events

Providers raise structured `ProviderStatus` records (`StatusManager.cs`) — not strings:

- `Sending` — provider is (back to) sending
- `Searching` — `Detail` = query, `ServiceName` = search backend
- `Fetching` — `Detail` = URL
- `ReadingFile` — `Detail` = file path (file_read / list_files)
- `Browsing` — `Detail` = URL for navigations, else the action label; MainForm only renders an in-chat line when the detail contains `://` (per-click lines would flood the transcript)
- `Captions` — `Detail` = video URL; MainForm renders a `💬 Extracting captions:` line
- `WritingFile` — `Detail` = target path; MainForm renders a `✍️ Writing:` line
- `Tts` — `Detail` = text to speak

### Role mapping

- `ChatRole.Model` → `"assistant"` (OpenAI) / `"model"` (Gemini API)
- `ChatRole.System` → `"user"` in Gemini (Gemini doesn't support system messages directly)

## Configuration

Config file: `~/.yaollm.conf` — loaded **once** by `Program.Main` via `DotEnv.Load` into the process environment. `PresetManager.LoadConfig()` reads `PRESET_*`/`ACTIVE_PRESET` from environment variables (no second dotenv parse).

**No LLM endpoint is hardcoded in code.** Every preset gets its endpoint from config:

```
GEMINI_API_KEY=...        # legacy per-vendor keys, still honored as fallback
OPENROUTER_API_KEY=...
DEEPSEEK_API_KEY=...
ZAI_API_KEY=...
OLLAMA_BASE_URL=http://localhost:11434           # legacy fallback for ollama presets
OPENAI_COMPATIBLE_BASE_URL=http://localhost:11434 # legacy fallback for openai presets
TTS_VOICE=en-US-GuyNeural

SEARCH_SERVICES=proxy            # on/off switch: must contain "proxy" to enable search
BROWSER_SERVICES=playwright      # opt-in switch: must contain "playwright" to enable browse_* tools
BROWSER_HEADLESS=on              # off = visible Chrome window
# BROWSER_CHANNEL=chrome         # optional: chrome | msedge | chromium (MCP server default otherwise)
# BROWSER_CDP_ENDPOINT=...       # optional: attach to an already-running browser
# BROWSER_MCP_CLI=...            # optional: full path to @playwright/mcp cli.js
# YOUTUBE_CAPTIONS=off           # optional: disable youtube_captions (auto-on when yt-dlp is on PATH)
# YTDLP_PATH=...                 # optional: full path to yt-dlp when it is not on PATH
# FILE_WRITE=off                 # optional: disable file_write/memory_write (writes are temp-area only)
# MEMORY=off                     # optional: disable the memory file (file_write stays)
# MEMORY_DIR=...                 # optional: move the memory dir (default %TEMP%\YAOLlm\memory)
PROXY_API_KEY=ik-...             # key for the user's own proxy (search + scrape + LLM presets)
PROXY_SEARCH_URL=https://inference.camalolo.com/api/search
PROXY_SCRAPE_URL=...             # optional; defaults to PROXY_SEARCH_URL with /search → /scrape

PRESET_1=openai:glm-5.3-flash:My Proxy
PRESET_1_BASE_URL=http://127.0.0.1:3003/api/v1
PRESET_1_API_KEY=ik-XXXXX

PRESET_2=gemini:gemini-2.5-flash-lite:Gemini-2.5
PRESET_2_BASE_URL=https://generativelanguage.googleapis.com/v1beta
ACTIVE_PRESET=1

MAX_TOKENS=2048          # global completion cap (all presets)
REASONING=off            # global reasoning preference (all presets)
PRESET_2_MAX_TOKENS=1024 # per-preset override (wins over the global)
PRESET_2_REASONING=off   # per-preset override (wins over the global)
```

Token-saving knobs (both optional, per-preset with global fallback):

- `PRESET_N_MAX_TOKENS` / `MAX_TOKENS` — completion-token cap. Mapped per protocol: `max_tokens` (OpenAI-style), `generationConfig.maxOutputTokens` (Gemini), `options.num_predict` (Ollama).
- `PRESET_N_REASONING` / `REASONING` — `off`, `low`, `medium`, or `high` (case-insensitive; also accepts `disabled`/`none`/`0`, `minimal`/`min`, `med`, `max`/`maximum`). Unset = provider default, nothing is sent. Verified per-profile mapping (levels are monotone per provider; where a provider's scale has gaps, the nearest higher native value is used):

| profile | `off` | `low` | `medium` | `high` |
|---|---|---|---|---|
| `deepseek` | `thinking: {"type":"disabled"}` (V3.2+/V4 hybrid toggle) | enabled + `reasoning_effort: "low"` | enabled + `reasoning_effort: "high"` (no native medium) | enabled + `reasoning_effort: "max"` |
| `zai` (GLM) | `thinking: {"type":"disabled"}` | `thinking: {"type":"enabled"}` | `thinking: {"type":"enabled"}` | `thinking: {"type":"enabled"}` (no effort param exists) |
| `openrouter` | `reasoning: {"enabled": false}` | `reasoning: {"effort": "low"}` | `reasoning: {"effort": "medium"}` | `reasoning: {"effort": "high"}` |
| `gemini` | `thinkingConfig.thinkingBudget: 0` | `thinkingBudget: 1024` | `thinkingBudget: 8192` | `thinkingBudget: -1` (dynamic) |
| `ollama` | `think: false` | `think: false` (boolean-only API) | `think: true` | `think: true` |
| `openai` / `openai-compatible` | `reasoning_effort: "low"` (most portable floor) | `reasoning_effort: "low"` | `reasoning_effort: "medium"` | `reasoning_effort: "high"` |

Note: a preset using the generic `openai` profile against a DeepSeek hybrid model only gets `reasoning_effort` (no thinking toggle) — use the `deepseek` profile for the same endpoint to get the `thinking` on/off switch and DeepSeek's full effort scale.

Caveats (both knobs are opt-in; defaults send nothing): Gemini 2.5 **Pro** cannot disable thinking (budget 0 is rejected — only set REASONING on Flash/Flash-Lite); OpenAI's `reasoning_effort` is only honored by reasoning-capable models and `low` is the lowest universally-accepted value there. `PRESET_N_MAX_TOKENS`/`PRESET_N_REASONING` lines are persisted by `SaveConfig` like the other extras.

Per-preset keys:

- `PRESET_N=provider:model[:display_name]` — `provider` selects the protocol profile: `gemini`, `openai`/`openai-compatible`, `openrouter`, `deepseek`, `zai`, `ollama` (case-insensitive).
- `PRESET_N_BASE_URL` — endpoint root. `PRESET_N_BASE_URL` wins; if absent, legacy env fallbacks apply (`OLLAMA_BASE_URL` for ollama, `OPENAI_COMPATIBLE_BASE_URL` for openai/openai-compatible). If neither is set, `CreateProvider()` throws `InvalidOperationException`.
- `PRESET_N_API_KEY` — optional. Falls back to the legacy vendor env vars (`GEMINI_API_KEY`, `OPENROUTER_API_KEY`, `DEEPSEEK_API_KEY`, `ZAI_API_KEY`). Generic `openai`/`ollama` presets need no key; omitting `PRESET_N_API_KEY` means `SaveConfig` won't write a key line (avoids duplicating secrets into the preset block).

Providers append only their protocol resource path to `BASE_URL` — the host/version segment comes entirely from config:

| provider | resulting URL | auth |
|---|---|---|
| `gemini` | `{BASE_URL}/models/{model}:streamGenerateContent?alt=sse` | `x-goog-api-key` header |
| `openai` / `openai-compatible` | `{BASE_URL}/chat/completions` | Bearer if key set |
| `openrouter` | `{BASE_URL}/chat/completions` | Bearer + `HTTP-Referer`/`X-Title` |
| `deepseek` | `{BASE_URL}/chat/completions` | Bearer (BASE_URL e.g. `https://api.deepseek.com/v1`) |
| `zai` | `{BASE_URL}/chat/completions` | Bearer (BASE_URL e.g. `https://api.z.ai/api/coding/paas/v4`) |
| `ollama` | `{BASE_URL}/api/chat` | none |

Preset line format: `provider:model[:display_name]`. Provider names are case-insensitive. With 3+ colon-separated segments, the **last** segment is always the display name (so model IDs containing colons work).

`SEARCH_SERVICES` — on/off switch for web search. There is exactly one search backend: the user's own proxy endpoint (`PROXY_SEARCH_URL` + `PROXY_API_KEY`), which does provider failover server-side. If `SEARCH_SERVICES` is set, it must contain `proxy` (e.g. `SEARCH_SERVICES=proxy`) or search is disabled; if unset, search is enabled when both `PROXY_API_KEY` and `PROXY_SEARCH_URL` are present. The local per-provider search services (Exa/Tavily/TinyFish/Serper) were removed — don't reintroduce them; add upstreams to the proxy's server-side chain instead.

`BROWSER_SERVICES` — opt-in switch for the `browse_*` tools (real browser via Playwright): must contain `playwright` to enable; unset = disabled (spawning Chrome unprompted is heavy, so unlike search this never auto-enables). Backed by `PlaywrightBrowserService`: a lazy child process of the globally-installed `@playwright/mcp` (MCP JSON-RPC over stdio, `BROWSER_MCP_CLI` overrides the default `%APPDATA%\npm\node_modules\@playwright\mcp\cli.js` path), driven end-to-end from YAOLlm — initialize handshake on spawn, then `tools/call` per invocation. The instance is app-scoped (created in `Program.Main`, passed to `PresetManager` and disposed via `MainForm`), so the browser session survives preset/provider switches. `BROWSER_HEADLESS=off` shows the Chrome window; `BROWSER_CHANNEL` picks chrome/msedge/chromium; `BROWSER_CDP_ENDPOINT` attaches to an already-running browser instead of launching one. Exactly thirteen curated tools are advertised (navigate/snapshot/find/click/type/press_key/tabs/wait_for/back/close + evaluate/network/network_request for canvas/JS-rendered pages whose content never reaches the DOM); `browser_run_code_unsafe` is never exposed. The tool map is `ToolMap` in `PlaywrightBrowserService`; add a tool by defining it in `ToolDefinitions` + one `ToolMap` entry + dispatch already covers any `browse_*` prefix. Available on OpenAI-style and Gemini providers only (Ollama has no tool loop).

`YOUTUBE_CAPTIONS` — switch for the `youtube_captions` tool (YouTube transcript extraction). Auto-enabled when `yt-dlp` is found on PATH (or `YTDLP_PATH` points at it); `YOUTUBE_CAPTIONS=off` (also `no`/`false`/`0`) forces it off. `YouTubeCaptionService` runs yt-dlp twice: a `-J --skip-download` metadata probe (title/duration + `subtitles`/`automatic_captions` language keys, in `Program.Main`'s process env), then a single-track download (`--write-subs --write-auto-subs --sub-langs <key> --sub-format vtt/best`) into a private temp dir that is deleted afterwards. `SelectLanguage` prefers an explicitly requested language (exact then prefix match, manual before auto), else English → the video's own `language` → first manual → first auto; missing tracks return an `Error:` listing what exists. The VTT/SRT parser (`ParseCaptionFile`) joins lines wrapped inside a cue, then reconciles consecutive cues against the accumulated utterance: exact repeats are dropped, growing cues replace the previous segment, and rolling continuations contribute only their new part — overlap matching is **word-based and punctuation-insensitive** (`OverlapChars`), because auto-captions restate text with differing punctuation (`have really...` then `really really long trunks`); overlaps shorter than 4 chars are treated as coincidences. Transcripts are capped at 60k chars (marker appended), yt-dlp stdout/stderr are read with hard caps so a huge metadata dump can't balloon memory, and the tool returns a small header (title/channel/duration/language) plus the transcript, with `timestamps=true` giving `[h:mm:ss]`-prefixed lines. Available on OpenAI-style and Gemini providers only.

`FILE_WRITE` — switch for the write tools (`file_write` + `memory_write`), on by default when FILE_READ is on; `FILE_WRITE=off` (also `no`/`false`/`0`) keeps them unadvertised. `FileWriteService` restricts every write to the YAOLlm-owned subtree under the system temp dir: `WriteRoot` = `%TEMP%\YAOLlm\files` (scratch) and `MemoryDir` = `%TEMP%\YAOLlm\memory` (default; `MEMORY_DIR` overrides, `MEMORY=off` drops the memory file while keeping `file_write`). Enforcement: `FileReadService.ResolvePath` now collapses `..` via `GetFullPath` (closing traversal for reads too), then a separator-bounded prefix check rejects anything outside the root (the root itself is not writable); symlinked targets are refused (`File.ResolveLinkTarget`); writes are UTF-8 no-BOM, parent dirs auto-created, 200k-char per-write cap, serialized behind `_ioLock`.

`memory_write` — the memory file: a single fixed `memory.md` under the memory dir, independent of the active app (the user clears it manually when wanted). Ops: `append` (default; inserts a newline separator when needed), `replace` (exact `find` snippet → `content`, must match exactly once — 0 matches → "read it first", >1 → "include more surrounding text"), `overwrite` (whole-file rewrite for reorganizing; past `MemoryWarnChars` (50k) the result nudges compaction). The memory path is ALWAYS in the system prompt (with "for writing memories — read it before searching"), and `MemoryFilePath`/`WritableRoot` on `ConversationManager` keep it rebuilt in place. Reading needs no new tool: `file_read` reaches the memory dir and write root via `ImplicitReadRoots` even with an empty user allowlist (anything else stays denied), and the file tools are advertised whenever memory/write is enabled even if the allowlist is empty. The UI file list shows the memory file as a special amber 🧠 row (via the `memory_file` bridge message, `{enabled, path, exists}`); its `[x]` arms on first click and a second click within 1s sends `delete_memory_file` — `DeleteCurrentMemoryFile` re-validates the path against the memory dir before deleting, so **deletion can never apply to a user-added allowlist entry** (those keep remove-from-list semantics). Memory lives in %TEMP% — temp-cleanup tools can wipe it.

`FILE_READ` — master switch for the local file tools (`file_read` + `list_files`). On by default; set `FILE_READ=off` (also `no`/`false`/`0`) to keep the tools unadvertised and unwired. When enabled, access is **allowlist-gated**: `Program.Main` creates one shared `FileAllowlist` and injects it (wrapped in `FileReadService`) into `PresetManager` *and* `MainForm`. The overlay's `📄 Files` / `📁 Folder` buttons open native pickers (multi-select files / folder browser) and the chosen paths appear in the bottom-right panel (full path + `[x]` remove button). The allowlist is persisted in `~/.yaollm.session.json` (`allowedPaths`) and restored on startup even when the chat was empty; `FileAllowlist.Changed` → `MainForm.OnAllowlistChanged` updates the system prompt, pushes `allowed_paths` to the UI, and re-saves the session. Tool advertising requires BOTH the master switch and a non-empty allowlist (`MainForm.ProcessLLMRequestAsync`), and the approved paths are listed in the system prompt so the model knows what it may touch. The service opens files with `FileAccess.Read` only, refuses binary content (NUL-byte sniff), rejects relative paths (models must send absolute paths; `~` and `%ENV%` are expanded, wrapping quotes stripped), and truncates reads at 15k chars with a marker telling the model to re-read via `start_line`/`end_line`. `list_files` lists immediate children (dirs first with trailing `/`, files with byte size, capped at 500 entries) of an allowlisted directory or one inside it — a file entry grants reading that exact file but no listing rights over anything. Denials return `Error: Access denied — ...` strings so the model can tell the user. Passing a null allowlist to `FileReadService` means unrestricted access (tests only).

### Search & scrape services

`ProxySearchService` is the only search implementation: a thin client for `GET {PROXY_SEARCH_URL}?q=<encoded>&limit=N` with Bearer auth. Per the endpoint contract it retries 429 (honoring `Retry-After`), 5xx, and network errors (max 3 attempts, 1s → 4s backoff, 45s per-attempt timeout), never retries 400/401 or empty result sets, and treats `results: []` with HTTP 200 as a valid answer. It never parses the informational `provider` field. Results are formatted as markdown (`**title**` / `URL:` / `Content:` blocks). Failures return strings starting with `"Error:"`, which is the convention `SearchServiceAggregator` uses to fall through to the next service.

`web_scrape` follows the same pattern: `ProxyScrapeService` is a thin client for `GET {scrapeUrl}?url=<encoded>&format=json` with the same Bearer auth and identical retry contract, parsing `{"url","title","content","provider"}` (provider never used). The scrape URL is `PROXY_SCRAPE_URL` if set, otherwise derived from `PROXY_SEARCH_URL` by replacing the trailing `/search` path segment with `/scrape` (case-insensitive, trailing slash preserved). The result is formatted as a bold `**title**` heading + full page content — there is **no length cap** (a 15k cap used to amputate exactly the sections the model needed; don't reintroduce one). Because scrape providers return HTTP 200 even for dead links, `DetectErrorPage` screens every success: error-page titles (`404: Not Found`, `Page not found`, `... :: Error`) on bodies < 10k chars, and near-empty shell pages (< 250 chars, JS-rendered apps) are converted into `Error:` failures instead of reaching the model as fake content. Empty content is a failure, unlike search's empty results. Scrape results are cached in-process for 24 h (`ScrapeCache`, static by design so it survives preset/provider switches; key = normalized URL — host case-folded, fragment dropped, query significant, 64-entry LRU-ish eviction, cache hits logged as `[scrape-cache] hit`). Only successes are cached — `Error:` results always retry live so a recovered backend is used immediately. When the proxy scrape isn't configured (no key or no derivable URL), `Program.Main` falls back to the direct `WebFetchService` (browser-mimicking headers, regex HTML-to-text pipeline, 2 MB download cap — see `WebFetchService.cs`/`WebFetchServiceTests.cs`); keep that class as the no-proxy fallback.

## UI Bridge Protocol

All messages are JSON. C# → JS via `CoreWebView2.PostWebMessageAsJson`. JS → C# via `window.chrome.webview.postMessage`.

### C# → JS messages

| type | fields | description |
|---|---|---|
| `chat_message` | `role`, `html` | Complete message (user/model/system/error) |
| `chat_queued` | `html` | User message queued behind an in-flight request |
| `chat_dequeued` | `html` | Queued message is being answered — JS moves its pending bubble into the main transcript (order stays correct; done at dequeue, not at response completion) |
| `chat_stream` | `html` | Streaming chunk |
| `status` | `status` | Idle/Sending/Receiving/Searching/Fetching |
| `provider` | `name` | Active provider display name |
| `history` | `chars` | Conversation char count |
| `warning` | `message` | System-styled warning message |
| `error` | `message` | Error-styled message |
| `reset` | — | Clear all messages |
| `focus_input` | — | Focus textarea |
| `no_provider` | — | Disable chat UI |
| `tts_state` | `enabled` | TTS on/off (button styling) |
| `file_access` | `enabled` | FILE_READ master switch (hides picker buttons + panel) |
| `allowed_paths` | `paths` | Full allowlist replace — bottom-right panel entries |

### JS → C# messages

`send`, `capture_send`, `load_image`, `proceed`, `clear`, `compact`, `hide`, `exit`, `cycle_provider`, `stop`, `toggle_tts`, `add_files` / `add_folder` (open the allowlist file/folder picker), `remove_allowed_path` (`path` — remove one allowlist entry), `_console` (JS console forwarding — logged by `WebViewBridge`, not dispatched)

Syntax highlighting uses `highlightNew()` — only `pre code` blocks lacking the `hljs` class are highlighted; streaming chunks are not highlighted at all (the final `chat_message` handles it).

## Code Conventions

- **Namespace**: Root `YAOLlm`; providers in `YAOLlm.Providers`.
- **Nullable**: Enabled project-wide (`<Nullable>enable</Nullable>`).
- **Async streams**: `IAsyncEnumerable<T>` with `[EnumeratorCancellation]` for cancellable `StreamAsync` implementations.
- **Logging**: `Logger.Log(string)` — suppresses consecutive duplicate messages (used heavily in streaming). Logs to both console and `yaollm.log` in the app directory.
- **JSON**: Uses `System.Text.Json` with `JsonNamingPolicy.CamelCase` + `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` for bridge messages.
- **Markdown rendering**: `MarkdownHelper.ToHtml` — single shared Markdig pipeline (`UseAdvancedExtensions`), used by both `MainForm` and `WebViewBridge`.
- **Image handling**: `BaseLLMProvider.DetectImageMimeType` reads magic bytes. Supported: PNG, JPEG, GIF, WebP. Screen capture uses the full virtual screen (all monitors); `ImageService.ResizeImageBase64` only downscales (≤640px width, bicubic).

## Gotchas

- **No `.editorconfig` or style checker** — code style is inconsistent (braces on same/new line, field underscore prefixes vary).
- **`GeminiProvider` maps system role to `"user"`** — Gemini API has no system role, so the system prompt is injected as a user message. This means it appears as a user message in the API history.
- **Ollama does not support web search tools** — `SupportsWebSearch = false`, gets no `IFileReadService` either (no tool-result loop to consume results). Only `tts_summary` is advertised, and it is handled as a terminal spoken summary (no tool-result round-trip).
- **Provider creation throws at runtime** — if the required API key env var is missing, `CreateProvider()` throws `InvalidOperationException`/`NotSupportedException`, not at startup. The app starts but `HasProvider` will be false until a valid preset is configured.
- **Tab key cycles providers** — `ProcessCmdKey` intercepts `Tab` when the overlay is visible to call `CyclePreset()`. Switching mid-request defers the actual provider swap to the next send (`_pendingPresetSwitch`).
- **Error/empty exchanges are not persisted** — failed requests and empty responses don't add turns to `ConversationManager`; the user can simply retry. Cancelled (stopped) responses *are* persisted with a stop marker.
- **History trim**: `ConversationManager` caps at 32 entries (system + 31 conversation turns). Trimming removes oldest user/model pairs, keeping the system message.
- **Compaction (🗜 Compact button)**: `MainForm.CompactConversationAsync` streams a summarization request (`BuildCompactionSystemPrompt` + `BuildCompactionTranscript`) through the current provider with no tools, then `ConversationManager.Compact(summary)` replaces the whole history with system prompt + a single summary-bearing user message. Takes `_sendLock` (busy → warning, no queueing); Stop cancels it via the shared `_cancellationTokenSource`; failures/cancellations leave history untouched. Attached images are noted in the transcript, not embedded. Re-compacting works — the previous summary becomes part of the transcript.
- **Session persistence**: every structural history mutation (exchange/compact/clear via `ConversationManager.OnHistoryChanged`) atomically rewrites `~/.yaollm.session.json`. On startup `MainForm.RestoreSession()` loads it *before* the hook is assigned (assigning earlier would overwrite the file with the empty startup state), discards the persisted system prompt, and re-appends the turns via `RestoreTurns` — so the system prompt is rebuilt fresh (current date/TTS/window state). UI replay happens in the `NavigationCompleted` handler, not earlier (messages posted before the JS bridge listener attaches are dropped). Corrupt/missing/empty file → fresh session; Clear starts a new one.
- **Tool arguments box as `JsonElement`** — `DeserializeArguments` values (including numbers/bools) come back as `JsonElement`; use `GetIntArg`/`.ToString()` rather than `is long`/`is bool` checks.
