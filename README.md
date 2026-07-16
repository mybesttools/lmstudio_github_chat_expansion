# LM Studio Copilot Expansion by MyBestTools

Run local LM Studio models inside VS Code Copilot Chat with streaming responses and tool calling.

This extension uses the LM Studio HTTP API only. Whether your LM Studio server runs on the same machine or on a remote host, the connection flow is the same: point the extension at the server URL and use the models exposed by that API.

Install LM Studio Copilot Expansion by MyBestTools from the VS Code Marketplace when this edition is published.

## What it does

- Adds LM Studio models to the Copilot Chat model picker
- Streams responses directly into VS Code chat
- Uses the same API-based connection flow for both local and remote LM Studio servers

## Requirements

- VS Code 1.104.0 or later
- [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed and on `PATH` — the extension's chat/tool logic runs in a bundled `dotnet`-hosted backend process
- LM Studio server accessible at the configured URL (default: `http://localhost:1234`)
- At least one chat model already exposed by that LM Studio server

## Quick Start

1. Install this extension.
2. Start LM Studio and make sure its API server is reachable.
3. Load the model you want to use in LM Studio so it appears in the API model list.
4. Open Copilot Chat.
5. Pick an LM Studio model.
6. Send a prompt.

The extension does not start LM Studio, stop LM Studio, or load models on your behalf. It only talks to the configured API endpoint.

## Connection Modes

Local and remote servers are handled the same way.

Examples:

- Local LM Studio server: `http://localhost:1234`
- Remote LM Studio server: `http://your-server:1234`
- SSH tunnel to a remote server: `http://localhost:1234`

As long as the API endpoint responds and exposes a model list, the extension treats all of these the same. If you tunnel a remote LM Studio over SSH, its URL on your local machine will be `http://localhost:1234`, and the extension talks to it exactly the same as a real local server — no extra configuration needed.

### Manual provider JSON fallback

If the VS Code GUI for adding a custom chat model is bugged, you can fall back to a manual provider definition. The `customendpoint` provider config is a top-level **array** of provider objects — paste an entry like this:

```json
[
  {
    "name": "LM Studio",
    "vendor": "customendpoint",
    "apiKey": "lm-studio",
    "apiType": "chat-completions",
    "models": [
      {
        "id": "qwen/qwen3.6-27b",
        "name": "qwen/qwen3.6-27b",
        "url": "http://YOUR_TAILSCALE_IP:1234/v1/chat/completions",
        "toolCalling": true,
        "vision": false,
        "maxInputTokens": 80000,
        "maxOutputTokens": 16000,
        "streaming": true
      }
    ]
  }
]
```

Replace the model `id`, display `name`, and `url` with the values exposed by your LM Studio server. Set `toolCalling` and `vision` to match the capabilities of the model you are exposing.

## Important Settings

Most users can leave the defaults alone. These are the settings that matter most:

- `lmstudio-copilot-expansion.serverUrl`: LM Studio server URL (default: `http://localhost:1234`)
- `lmstudio-copilot-expansion.apiKey`: API key used for LM Studio server authentication when required
- `lmstudio-copilot-expansion.autoRefreshModels`: Refresh available models from the LM Studio API on startup
- `lmstudio-copilot-expansion.enableToolCalling`: Enable tool calling for supported models
- `lmstudio-copilot-expansion.maxTools`: Limit the number of tools exposed per request
- `lmstudio-copilot-expansion.logLevel`: Controls output logging verbosity. Default is `verbose`; set to `info`, `warning`, `error`, or `none`.
- `lmstudio-copilot-expansion.homeSubnets`: CIDR ranges (e.g. `192.168.1.0/24`) that count as your "home" network. When set, the extension checks this machine's current IP addresses against these ranges on startup and skips activation entirely if none match, so it doesn't try to reach your LM Studio server when you're away from its network. Ignored when `serverUrl` points at `localhost`/`127.0.0.1`/`::1`, since a loopback server is reachable regardless of network. Leave empty (default) to always activate.

## Commands

- `LM Studio: Refresh Available Models`
- `LM Studio: Review Suggested Model Config`
- `LM Studio: Check Server Connection`

## Usage

Select an LM Studio model in Copilot Chat and start chatting. The extension only uses models currently exposed by the configured LM Studio API server.

## Optional Features

- Tool calling for supported models exposed by the LM Studio API

### LM Studio Auto

Pick "LM Studio Auto" in the model picker instead of a specific model, and the extension routes each new conversation to whichever backing model fits it best — a quick model for short edits, a coding-tuned model for general work, a large-context model for planning/synthesis, a vision model when images are attached, and so on. It asks a currently loaded model to classify the request (falling back to a cheap heuristic if none is loaded or classification fails), then reuses that same pick for the rest of the conversation rather than re-classifying on every turn, since switching LM Studio models mid-chat means an unload/reload that can take real time. It shows which model it routed to as a short note at the start of the first response. It draws from `taskTypeModels` when configured for a profile, otherwise falls back to the best-scoring available model for that profile. Disable with `lmstudio-copilot-expansion.enableAutoModel`.

The active model is also made aware it can hand off the rest of the conversation itself, via a `mbt_lmstudio_switch_model` tool — only offered while "LM Studio Auto" is selected, never when you've pinned a specific model directly. If the model decides mid-conversation that a different profile fits better (e.g. it needs a bigger context window, or the remaining work is a much lighter follow-up), it can call the tool and the next response comes from the newly picked model.

### Image Generation

Image generation is not built into this extension — configure it server-side as an MCP server instead, so it's available to any MCP-compatible client, not just this extension. See [Local MCPs with LM Studio](https://www.mybesttools.com/posts/lm-studio-local-mcps) for setup instructions.

## Troubleshooting

### Models not appearing

- Make sure LM Studio is running
- Make sure the target model is loaded and visible through the LM Studio API server
- Check `lmstudio-copilot-expansion.serverUrl`
- Run `LM Studio: Check Server Connection`

### Slow responses

- LM Studio performance depends on your hardware and the model size
- Consider using a smaller/faster model
- Increase the timeout in settings if needed

## Development

The extension itself (chat provider/tool registration, VS Code UI) is TypeScript in `src/`. All LM Studio API, tool-calling, and model-scoring logic lives in the C# backend under `backend/`, which the extension spawns as a child process and talks to over stdio (see `backend/Program.cs` and `src/backend-process.ts` for the protocol). Building the extension always rebuilds and republishes that backend alongside the TypeScript bundle.

```bash
# Install dependencies
npm install

# Run the repo validations
npm test
npm run lint
npm run test:integration
npm run test:live

# Compile and watch (TypeScript only; re-run build:backend after C# changes)
npm run watch

# Build just the backend
npm run build:backend

# Package the extension (TypeScript + backend)
npm run package

# Build a VSIX for local install / Marketplace submission
npm run package:vsix
```

`npm run test:integration` launches the extension in an Extension Development Host and verifies activation plus command registration.

`npm run test:live` does the same, but seeds a temporary VS Code profile from your current settings so it can exercise a real LM Studio connection with the configured `lmstudio-copilot-expansion.apiKey`.

## License

GPL-3.0
