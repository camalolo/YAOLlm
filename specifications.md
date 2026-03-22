# YAOLlm Project Specifications

## Overview
This project ("YAOLlm") is a Windows Forms application that acts as a UI overlay integrated with a language model (LLM) service for processing natural language queries and image inputs. The application runs in the system tray and uses global hotkeys to toggle visibility. It supports sending text input to an LLM, processing images (by capturing screen or sending image data), and integrating with external web APIs.

## Entry Point
The `Program.cs` file serves as the entry point. It initializes the Logger, StatusManager, and PresetManager, creates the main overlay form (`MainForm`), and runs the application via a custom TrayApplicationContext to ensure the form remains hidden by default.

## Components

### Logger
**File:** Logger.cs  
**Functionality:**  
- Creates log files inside the "log" directory.  
- Appends timestamped log entries.  
- Catches logging errors and writes error messages to the console.

### MainForm (UI Overlay)
**File:** MainForm.cs  
**Functionality:**  
- Implements a borderless Windows Form overlay that stays hidden by default.  
- Sets up UI elements including:
  - A chat box for displaying conversation messages.
  - An input field for user queries.
  - Control buttons such as "send", "capture_send", "proceed", "search_online", "playing", and "clear".  
- Configures a system tray icon with a context menu and double-click functionality.  
- Registers a global hotkey (Ctrl+F12) to toggle the overlay's visibility.  
- Supports capturing the screen and sending images to the LLM.  
- Uses asynchronous tasks to manage LLM processing and update conversation history.

### PresetManager (Provider Configuration)
**File:** PresetManager.cs  
**Functionality:**  
- Manages multiple LLM provider presets (Gemini, OpenRouter, Ollama, OpenAI-compatible).  
- Loads/saves configuration from `.gemini.conf` in the user's home directory.  
- Creates appropriate provider instances based on preset configuration.  
- Handles preset switching at runtime via hotkeys.

### ILLMProvider Interface
**File:** ILLMProvider.cs  
**Functionality:**  
- Defines the common interface for all LLM providers.  
- Providers include: GeminiProvider, OpenRouterProvider, OllamaProvider, OpenAICompatibleProvider.  
- Each provider implements its own API communication and response parsing.

### Tools and Prompts
**File:** ToolDefinitions.cs  
**Functionality:**  
- Defines available tool functions for memory searches and online queries.  
- Provides initial prompt text and templates for processing LLM responses.  

### Other Components
Additional files (`TavilySearchService.cs`, `StatusManager.cs`, `ProviderLogger.cs`) support:
- Web search integration via Tavily API.
- Status tracking and UI updates throughout the application.

## Application Workflow

1. **Startup:**  
   - Initializes Logger, StatusManager, and loads config via PresetManager.  
   - Creates the MainForm and the system tray icon.  
   - Registers the global hotkey (Ctrl+F12) to toggle overlay visibility.

2. **User Interaction:**  
   - Users input text queries or invoke screen capture to process images.  
   - MainForm sends user inputs to the current provider for processing.

3. **LLM Processing:**  
   - LLM requests are handled asynchronously through provider-specific API calls.  
   - Responses are displayed in the chat box while conversation history is maintained.

4. **Logging and Status Updates:**  
   - All key operations and errors are logged using the Logger.  
   - The UI is updated with status messages (e.g., sending data, idle).

## Summary
The YAOLlm project combines a rich Windows Forms UI overlay with multiple LLM backends to enable interactive text and image input processing. It integrates comprehensive logging, web search, and tool-based functionalities to provide a seamless user experience.
