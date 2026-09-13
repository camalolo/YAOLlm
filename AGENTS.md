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
- `ExecuteWebSearchToolAsync` / `ExecuteWebFetchToolAsync` — shared tool executors that also update `CompletedSearchCount`/`CompletedFetchCount`/`CompletedSearchSummaries`.
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
PROXY_API_KEY=ik-...             # key for the user's own proxy (search + LLM presets)
PROXY_SEARCH_URL=https://inference.camalolo.com/api/search

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
- `PRESET_N_REASONING` / `REASONING` — `off` or `low` (case-insensitive; also accepts `disabled`/`none`/`0`/`minimal`/`min`). Unset = provider default, nothing is sent. Verified per-profile mapping:

| profile | `off` | `low` |
|---|---|---|
| `deepseek` | `thinking: {"type":"disabled"}` (V3.2+ hybrid toggle) | `thinking enabled` + `reasoning_effort: "low"` |
| `zai` (GLM) | `thinking: {"type":"disabled"}` | `thinking: {"type":"enabled"}` (no effort param exists) |
| `openrouter` | `reasoning: {"enabled": false}` | `reasoning: {"effort": "low"}` |
| `gemini` | `thinkingConfig.thinkingBudget: 0` | `thinkingConfig.thinkingBudget: 1024` |
| `ollama` | `think: false` | `think: false` (boolean-only API) |
| `openai` / `openai-compatible` | `reasoning_effort: "low"` (most portable floor) | `reasoning_effort: "low"` |

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

### Search services

`ProxySearchService` is the only implementation: a thin client for `GET {PROXY_SEARCH_URL}?q=<encoded>&limit=N` with Bearer auth. Per the endpoint contract it retries 429 (honoring `Retry-After`), 5xx, and network errors (max 3 attempts, 1s → 4s backoff, 45s per-attempt timeout), never retries 400/401 or empty result sets, and treats `results: []` with HTTP 200 as a valid answer. It never parses the informational `provider` field. Results are formatted as markdown (`**title**` / `URL:` / `Content:` blocks). Failures return strings starting with `"Error:"`, which is the convention `SearchServiceAggregator` uses to fall through to the next service.

## UI Bridge Protocol

All messages are JSON. C# → JS via `CoreWebView2.PostWebMessageAsJson`. JS → C# via `window.chrome.webview.postMessage`.

### C# → JS messages

| type | fields | description |
|---|---|---|
| `chat_message` | `role`, `html` | Complete message (user/model/system/error) |
| `chat_queued` | `html` | User message queued behind an in-flight request |
| `chat_stream` | `html` | Streaming chunk |
| `status` | `status` | Idle/Sending/Receiving/Searching/Fetching |
| `provider` | `name` | Active provider display name |
| `history` | `chars` | Conversation char count |
| `warning` | `message` | System-styled warning message |
| `error` | `message` | Error-styled message |
| `reset` | — | Clear all messages |
| `focus_input` | — | Focus textarea |
| `no_provider` | — | Disable chat UI |

### JS → C# messages

`send`, `capture_send`, `load_image`, `proceed`, `clear`, `compact`, `hide`, `exit`, `cycle_provider`, `stop`, `_console` (JS console forwarding — logged by `WebViewBridge`, not dispatched)

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
- **Ollama does not support web search tools** — `SupportsWebSearch = false`. Only `tts_summary` is advertised, and it is handled as a terminal spoken summary (no tool-result round-trip).
- **Provider creation throws at runtime** — if the required API key env var is missing, `CreateProvider()` throws `InvalidOperationException`/`NotSupportedException`, not at startup. The app starts but `HasProvider` will be false until a valid preset is configured.
- **Tab key cycles providers** — `ProcessCmdKey` intercepts `Tab` when the overlay is visible to call `CyclePreset()`. Switching mid-request defers the actual provider swap to the next send (`_pendingPresetSwitch`).
- **Error/empty exchanges are not persisted** — failed requests and empty responses don't add turns to `ConversationManager`; the user can simply retry. Cancelled (stopped) responses *are* persisted with a stop marker.
- **History trim**: `ConversationManager` caps at 32 entries (system + 31 conversation turns). Trimming removes oldest user/model pairs, keeping the system message.
- **Compaction (🗜 Compact button)**: `MainForm.CompactConversationAsync` streams a summarization request (`BuildCompactionSystemPrompt` + `BuildCompactionTranscript`) through the current provider with no tools, then `ConversationManager.Compact(summary)` replaces the whole history with system prompt + a single summary-bearing user message. Takes `_sendLock` (busy → warning, no queueing); Stop cancels it via the shared `_cancellationTokenSource`; failures/cancellations leave history untouched. Attached images are noted in the transcript, not embedded. Re-compacting works — the previous summary becomes part of the transcript.
- **Session persistence**: every structural history mutation (exchange/compact/clear via `ConversationManager.OnHistoryChanged`) atomically rewrites `~/.yaollm.session.json`. On startup `MainForm.RestoreSession()` loads it *before* the hook is assigned (assigning earlier would overwrite the file with the empty startup state), discards the persisted system prompt, and re-appends the turns via `RestoreTurns` — so the system prompt is rebuilt fresh (current date/TTS/window state). UI replay happens in the `NavigationCompleted` handler, not earlier (messages posted before the JS bridge listener attaches are dropped). Corrupt/missing/empty file → fresh session; Clear starts a new one.
- **Tool arguments box as `JsonElement`** — `DeserializeArguments` values (including numbers/bools) come back as `JsonElement`; use `GetIntArg`/`.ToString()` rather than `is long`/`is bool` checks.
