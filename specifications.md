# Gemini Project Specifications

## Overview
This project ("Gemini") is a Windows Forms application that acts as a UI overlay integrated with a language model (LLM) service for processing natural language queries and image inputs. The application runs in the system tray and uses global hotkeys to toggle visibility. It supports sending text input to an LLM, processing images (by capturing screen or sending image data), and integrating with external web APIs.

## Entry Point
The `Program.cs` file serves as the entry point. It initializes the Logger, StatusManager, and GeminiClient, creates the main overlay form (`MainForm`), and runs the application via a custom TrayApplicationContext to ensure the form remains hidden by default.

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

### GeminiClient (Communication with LLM)
**File:** GeminiClient.cs  
**Functionality:**  
- Loads environment variables from a `.gemini` file in the user's home directory to retrieve API keys (GEMINI_API_KEY, GOOGLE_SEARCH_API_KEY, GOOGLE_SEARCH_ENGINE_ID).  
- Maintains conversation history with an initial prompt.  
- Interfaces with an external LLM API (Google's generative language API) to process text and image inputs.  
- Uses callbacks to update the UI (chat updates, history counter, and status).  
- Provides methods to clear conversation history and update the LLM model.

### Tools and Prompts
**File:** ToolsAndPrompts.cs  
**Functionality:**  
- Defines available tool functions for memory searches and online queries.  
- Provides initial prompt text and templates for processing LLM responses.  
- Specifies tools such as:
  - `search_memory_summaries`
  - `search_memory_content`
  - `search_google`

### Other Components
Additional files (such as `Search.cs`, `Embeddings.cs`, `MemoryManager.cs`, `SQLiteMemoryStore.cs`, and `StatusManager.cs`) likely support:
- Memory management to maintain context and conversation history.
- Local memory storage and search functionalities.
- Status tracking and UI updates throughout the application.

## Application Workflow

1. **Startup:**  
   - Initializes Logger, StatusManager, and loads API keys via GeminiClient.  
   - Creates the MainForm and the system tray icon.  
   - Registers the global hotkey (Ctrl+F12) to toggle overlay visibility.

2. **User Interaction:**  
   - Users input text queries or invoke screen capture to process images.  
   - MainForm sends user inputs to GeminiClient for processing.

3. **LLM Processing:**  
   - LLM requests are handled asynchronously through external API calls.  
   - Responses are displayed in the chat box while conversation history is maintained.

4. **Logging and Status Updates:**  
   - All key operations and errors are logged using the Logger.  
   - The UI is updated with status messages (e.g., sending data, idle).

## Summary
The Gemini project combines a rich Windows Forms UI overlay with an LLM backend to enable interactive text and image input processing. It integrates comprehensive logging, memory management, and tool-based search functionalities to provide a seamless user experience.