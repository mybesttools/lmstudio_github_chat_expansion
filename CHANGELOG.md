# Changelog

## [1.3.0] - 2026-07-16

### Added

New `homeSubnets` setting: a list of CIDR ranges (e.g. `192.168.1.0/24`) that count as your "home" network. When set, the extension checks this machine's current IP addresses against these ranges on startup and skips activation entirely — no backend process, no provider registration — if none match, so it doesn't try to reach your LM Studio server when you're away from its network (e.g. at work or on public Wi-Fi). The check is skipped whenever `serverUrl` points at `localhost`/`127.0.0.1`/`::1`, since a loopback server is reachable regardless of network. Leave `homeSubnets` empty (the default) to always activate.

## [1.2.4] - 2026-07-12

### Fixed

Removed references to the Marketplace PAT leak from the changelog.

## [1.2.3] - 2026-07-12

### Fixed

.vscodeignore did not exclude the local publish.sh helper script, so vsce was bundling it straight into the packaged .vsix. publish.sh is now excluded from the package.

## [1.2.2] - 2026-07-12

### Fixed

The manual provider JSON fallback example in the README was missing the top-level array wrapper the customendpoint provider actually requires — pasted as-is it would fail to load. The License section said MIT while LICENSE/package.json say GPL-3.0; the switch-model tool was referenced by its display name instead of its registered tool name (mbt_lmstudio_switch_model); and the Commands list was missing LM Studio: Review Suggested Model Config.

## [1.2.1] - 2026-07-12

### Fixed

The README described a `treatAsRemote` setting that skips "local CLI operations" (auto-start, `lms ls` model discovery, `lms server stop`) for SSH-tunneled remote servers. No such setting, CLI invocation, or auto-start/stop behavior exists in the extension — it only ever talks to the configured HTTP API, as the same document states elsewhere. The fabricated section has been removed.

## [1.2.0] - 2026-07-12

### Added

The active model can now hand off the rest of a conversation itself: a new `mbt_lmstudio_switch_model` tool lets it request a different task profile mid-conversation (e.g. it realizes it needs a bigger context window, or the remaining work is much lighter than expected). The tool is only ever offered when "LM Studio Auto" is selected — a directly-pinned model selection stays pinned, so answers never silently come from elsewhere while the picker shows something else. The switch takes effect starting with the model's next response and, like the initial Auto classification, sticks for the rest of the conversation.

## [1.1.0] - 2026-07-12

### Added

New "LM Studio Auto" entry in the model picker (`lmstudio-copilot-expansion.enableAutoModel`, default on). When selected, the extension classifies the first request of a conversation into one of the existing task profiles (quick/coding/planning/toolUse/vision/review) — asking a currently loaded model to pick, with a cheap heuristic fallback — and routes to the best-fitting backing model (preferring a `taskTypeModels` override when configured for that profile). The pick sticks for the rest of the conversation instead of re-classifying every turn, since switching LM Studio models mid-chat can require a real unload/reload. The response opens with a short note naming which model it routed to.

## [1.0.2] - 2026-07-12

### Added

Configured task-profile models (`taskTypeModels` in settings) are now verified against the connected LM Studio server on every startup, not just once. If a configured model id no longer resolves to any model on the server (deleted, renamed, or the server points somewhere else now), the extension surfaces a "Save suggested config" prompt with replacement picks — and, unlike the existing one-time heuristic suggestion, this check re-prompts on every startup until the broken entry is fixed.

## [1.0.1] - 2026-07-12

### Fixed

Tool-call stream chunks sent from the C# backend to the TypeScript extension host used a flat wire shape (`{ type: "toolCall", id, name, arguments }`) that didn't match what the extension host expected (`{ toolCall: { id, type, function: { name, arguments } } }`). As a result, tool calls were parsed correctly by the backend but silently dropped before reaching VS Code's chat UI — text-only responses worked, but any response containing a tool call (e.g. `create_file`) produced no visible output. The backend now emits the shape the extension host already expected.

## [1.0.0] - 2026-07-11

### Initial Release

All LM Studio HTTP/SSE handling, task-profile scoring and advisories, tool budgeting, and the terminal/file/codebase-index tool implementations were built into a new C# (.NET 8) backend process, spawned and driven by the TypeScript extension over a stdio JSON protocol. The TypeScript side is a thin shell responsible only for the VS Code-specific contribution points (chat provider/tool registration, settings, commands, output channel) that must run in the Node.js extension host. Running the extension now requires the .NET 8 runtime to be installed.
