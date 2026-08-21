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
                ├─ PresetManager        → ILLMProvider (6 implementations), owns shared HttpClient
                ├─ ConversationManager  → ChatMessage history
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
| `GeminiProvider` | `BaseLLMProvider` | Gemini SSE (`/v1beta/models/{m}:streamGenerateContent?alt=sse`) | Yes (`web_search` tool) |
| `OpenRouterProvider` | `OpenAIStyleProvider` | OpenAI-compatible SSE | Yes (`web_search` tool) |
| `OllamaProvider` | `BaseLLMProvider` | Ollama JSON streaming (`/api/chat`) | No (`SupportsWebSearch = false`) |
| `OpenAICompatibleProvider` | `OpenAIStyleProvider` | OpenAI-compatible SSE (`/v1/chat/completions`) | Yes |
| `DeepSeekProvider` | `OpenAICompatibleProvider` | DeepSeek SSE | Yes |
| `ZaiProvider` | `OpenAICompatibleProvider` | Z.ai SSE (`/chat/completions`) | Yes |

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

```
GEMINI_API_KEY=...
OPENROUTER_API_KEY=...
DEEPSEEK_API_KEY=...
ZAI_API_KEY=...
OLLAMA_BASE_URL=http://localhost:11434
OPENAI_COMPATIBLE_BASE_URL=http://localhost:11434
TAVILY_API_KEY=...
EXA_API_KEY=...
SERPER_API_KEY=...
TINYFISH_API_KEY=...

SEARCH_SERVICES=exa,tavily,tinyfish,serper

PRESET_1=gemini:gemini-2.0-flash:My Gemini
PRESET_2=openrouter:openrouter/...:OpenRouter
ACTIVE_PRESET=1
```

`SEARCH_SERVICES` — comma-separated list of search service names in priority order. Supported names: `exa`, `tavily`, `tinyfish`, `serper`. Each name maps to its API key env var:
- `exa` → `EXA_API_KEY`
- `serper` → `SERPER_API_KEY`
- `tavily` → `TAVILY_API_KEY`
- `tinyfish` → `TINYFISH_API_KEY`

Services are tried in the order listed. If `SEARCH_SERVICES` is not set, falls back to `tinyfish` first, then `tavily`. Tavily falls back to a DuckDuckGo HTML scrape on quota errors (432/433).

Preset format: `provider:model[:display_name]`. Provider names are case-insensitive. With 3+ colon-separated segments, the **last** segment is always the display name.

### Search services

All extend `SearchServiceBase` — a template that owns the HTTP call, error mapping, and markdown formatting. Subclasses implement `CreateRequest` (endpoint/auth/body) and `ParseResults` (JSON → `(title, url, content)` triples), plus an optional `OnRequestFailedAsync` quota fallback. Failures return strings starting with `"Error:"`, which is the convention `SearchServiceAggregator` uses to fall through to the next service.

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

`send`, `capture_send`, `load_image`, `proceed`, `clear`, `hide`, `exit`, `cycle_provider`, `stop`, `_console` (JS console forwarding — logged by `WebViewBridge`, not dispatched)

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
- **Tool arguments box as `JsonElement`** — `DeserializeArguments` values (including numbers/bools) come back as `JsonElement`; use `GetIntArg`/`.ToString()` rather than `is long`/`is bool` checks.
