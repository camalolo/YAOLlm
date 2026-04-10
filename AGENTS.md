# YAOLlm — Agent Guide

## Overview

YAOLlm is a Windows desktop overlay application that provides an LLM-powered chat interface accessible via a global hotkey. The app runs as a system tray icon, renders its UI in a WebView2 control backed by Tailwind CSS + Alpine.js, and streams responses from multiple LLM providers.

## Build & Run

```bash
dotnet build          # Debug build
dotnet run            # Run in debug
dotnet publish -p:PublishSingleFile=true -c Release -r win-x64 --self-contained true YAOLlm.csproj  # Release build
```

- `build.bat` publishes to `bin\Release\net8.0-windows\win-x64\publish\YAOLlm.exe` and copies to `E:\Apps\`.
- Runs on .NET 8 (`net8.0-windows`), WinForms + WebView2. No test suite exists.

## Architecture

```
Program.cs → MainForm (WinForms shell + WebView2 host)
                ├─ PresetManager        → ILLMProvider (6 implementations)
                ├─ ConversationManager  → ChatMessage history
                ├─ StatusManager        → Status enum (Idle/Sending/Receiving/Searching)
                ├─ WebViewBridge        → C# ↔ JS messaging (JSON over postMessage)
                ├─ TrayIconManager      → System tray
                └─ ui/index.html        → Embedded resource, served from temp file
                                              └─ Alpine.js + Tailwind CDN + highlight.js
```

### Control Flow

1. User types in WebView2 textarea → JS sends `{type: "send", text}` via `window.chrome.webview.postMessage`
2. `WebViewBridge.OnWebMessageReceived` dispatches to `MainForm.OnSendMessage`
3. `MainForm.SendMessage` acquires `_sendLock`, adds user message to `ConversationManager`, calls `provider.StreamAsync()`
4. Each `yield return` chunk goes to `_bridge.ChatStream(...)` → JS appends to streaming HTML (throttled at 50ms)
5. On completion, full response added to conversation, displayed as `chat_message`

### Key Constraints

- **`_sendLock` (SemaphoreSlim)**: Only one LLM request at a time. Both `SendMessage` and `CyclePreset` check `CurrentCount == 0` and block.
- **Provider disposal on preset switch**: `ProcessCmdKey` and `CyclePreset` unsubscribe from the old provider's `OnStatusChange` and `Dispose()` it before creating the new one.
- **`_isDisposed` flag**: All providers check `ThrowIfDisposed()` inside async streams before making HTTP calls, preventing use-after-dispose.
- **HTML served from temp file**: `WriteHtmlToTempFile()` extracts `ui/index.html` from the embedded resource stream and writes it to `%TEMP%\YAOLlm\index.html`, then navigates via `file://` URI. This is required for CDN resources (Tailwind, Alpine, highlight.js) to load correctly.
- **WebView2 user data folder**: Set to `%TEMP%\YAOLlm\WebView2` to avoid profile lock conflicts.

## Providers

All providers implement `IAsyncEnumerable<string> StreamAsync(...)` — streaming is mandatory.

| Provider | Base Class | API Style | Web Search |
|---|---|---|---|
| `GeminiProvider` | `BaseLLMProvider` | Gemini SSE (`/v1beta/models/{m}:streamGenerateContent?alt=sse`) | Built-in grounding (no tool needed) |
| `OpenRouterProvider` | `OpenAIStyleProvider` | OpenAI-compatible SSE | Yes (`web_search` tool) |
| `OllamaProvider` | `BaseLLMProvider` | Ollama JSON streaming (`/api/chat`) | No (`SupportsWebSearch = false`) |
| `OpenAICompatibleProvider` | `OpenAIStyleProvider` | OpenAI-compatible SSE (`/v1/chat/completions`) | Yes |
| `DeepSeekProvider` | `OpenAICompatibleProvider` | DeepSeek SSE (`/v1/chat/completions`) | Yes |

### `OpenAIStyleProvider` — shared tool call handling

Providers that extend `OpenAIStyleProvider` inherit `TryParseStreamChunk`, `ToolCallBuilder`, `StreamingState`, `BuildCompletedToolCalls`, and `FormatToolDefinitions`. The subclass only needs to implement `ExecuteStreamAsync`, which receives the serialized request body and streams `IAsyncEnumerable<string>` text chunks. Tool call handling (buffering deltas, detecting `finish_reason=tool_calls`, re-issuing with tool result) is implemented in `OpenRouterProvider` and `OpenAICompatibleProvider`.

`OllamaProvider` extends `BaseLLMProvider` directly and uses a different JSON-per-line format. It does **not** support web search.

`GeminiProvider` uses its own SSE event format (`data: {...}`) and tool call re-streaming pattern via `StreamWithToolResultAsync`.

### Role mapping

- `ChatRole.Model` → `"assistant"` (OpenAI) / `"model"` (Gemini API)
- `ChatRole.System` → `"user"` in Gemini (Gemini doesn't support system messages directly)

## Configuration

Config file: `~/.yaollm.conf` (parsed via `dotenv.net`)

```
GEMINI_API_KEY=...
OPENROUTER_API_KEY=...
DEEPSEEK_API_KEY=...
OLLAMA_BASE_URL=http://localhost:11434
OPENAI_COMPATIBLE_BASE_URL=http://localhost:11434
TAVILY_API_KEY=...

PRESET_1=gemini:gemini-2.0-flash:My Gemini
PRESET_2=openrouter:openrouter/...:OpenRouter
ACTIVE_PRESET=1
```

Preset format: `provider:model[:display_name]`. Provider names are case-insensitive.

## UI Bridge Protocol

All messages are JSON. C# → JS via `CoreWebView2.PostWebMessageAsJson`. JS → C# via `window.chrome.webview.postMessage`.

### C# → JS messages

| type | fields | description |
|---|---|---|
| `chat_message` | `role`, `html` | Complete message (user/model/system/error) |
| `chat_stream` | `html` | Streaming chunk |
| `status` | `status` | Idle/Sending/Receiving/Searching |
| `provider` | `name` | Active provider display name |
| `history` | `chars` | Conversation char count |
| `warning` | `message` | System-styled warning message |
| `reset` | — | Clear all messages |
| `focus_input` | — | Focus textarea |
| `no_provider` | — | Disable chat UI |

### JS → C# messages

`send`, `capture_send`, `load_image`, `proceed`, `clear`, `hide`, `exit`, `cycle_provider`

## Code Conventions

- **Namespace**: Root `YAOLlm`; providers in `YAOLlm.Providers`.
- **Nullable**: Enabled project-wide (`<Nullable>enable</Nullable>`).
- **Async streams**: `IAsyncEnumerable<T>` with `[EnumeratorCancellation]` for cancellable `StreamAsync` implementations.
- **Logging**: `Logger.Log(string)` — suppresses consecutive duplicate messages (used heavily in streaming). Logs to both console and `log/yaollm_{date}.log` in the app directory.
- **JSON**: Uses `System.Text.Json` with `JsonNamingPolicy.CamelCase` + `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` throughout.
- **Markdown rendering**: `Markdig` with `UseAdvancedExtensions()` — used server-side in C# (`Markdig.Markdown.ToHtml`) before sending HTML to JS.
- **Image handling**: `BaseLLMProvider.DetectImageMimeType` reads magic bytes. Supported: PNG, JPEG, GIF, WebP.
- **`_console` messages**: JS console.log/warn/error are forwarded to C# logger but filtered out in `WebViewBridge` (they're not dispatched to any handler).

## Gotchas

- **No `.editorconfig` or style checker** — code style is inconsistent (braces on same/new line, field underscore prefixes vary).
- **`GeminiProvider` maps system role to `"user"`** — Gemini API has no system role, so the system prompt is injected as a user message. This means it appears as a user message in the API history.
- **Ollama does not support web search tools** — `SupportsWebSearch = false`. If a provider that normally supports tools has `SupportsWebSearch = false`, web search tool definitions are omitted from the request.
- **Provider creation throws at runtime** — if the required API key env var is missing, `CreateProvider()` throws `InvalidOperationException`/`NotSupportedException`, not at startup. The app starts but `HasProvider` will be false until a valid preset is configured.
- **Tab key cycles providers** — `ProcessCmdKey` intercepts `Tab` when the overlay is visible to call `CyclePreset()`. This also happens while a request is in flight (blocked by `_sendLock`).
- **Streaming throttle**: `streamThrottleMs = 50` — updates sent to JS at most every 50ms during streaming.
- **History trim**: `ConversationManager` caps at 32 entries (system + 31 conversation turns). Trimming removes oldest user/model pairs, keeping the system message.
- **`DotEnv.Read` with `probeLevelsToSearch: 0`** in `PresetManager.LoadConfig()` — only reads the exact config path, does not probe parent directories.
