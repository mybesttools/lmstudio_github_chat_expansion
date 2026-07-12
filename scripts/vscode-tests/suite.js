const assert = require('assert');
const path = require('path');
const vscode = require('vscode');

const packageJson = require(path.resolve(__dirname, '..', '..', 'package.json'));
const EXTENSION_ID = `${packageJson.publisher}.${packageJson.name}`;
const EXPECTED_COMMANDS = [
  'lmstudio-copilot-expansion.refreshModels',
  'lmstudio-copilot-expansion.reviewSuggestedModelConfig',
  'lmstudio-copilot-expansion.checkConnection',
];

function authHeaders(apiKey) {
  return apiKey && apiKey.trim().length > 0
    ? { Authorization: `Bearer ${apiKey}` }
    : {};
}

function getModelsArray(body) {
  if (Array.isArray(body?.models)) {
    return body.models;
  }
  if (Array.isArray(body?.data)) {
    return body.data;
  }
  if (Array.isArray(body)) {
    return body;
  }
  return [];
}

function isLoaded(model) {
  if (typeof model?.loaded === 'boolean') {
    return model.loaded;
  }
  return Array.isArray(model?.loaded_instances) && model.loaded_instances.length > 0;
}

async function fetchModels(serverUrl, apiKey) {
  const response = await fetch(`${serverUrl}/api/v1/models`, {
    method: 'GET',
    headers: authHeaders(apiKey),
    signal: AbortSignal.timeout(5000),
  });

  assert(response.ok, `Expected LM Studio /api/v1/models to succeed, got ${response.status} ${response.statusText}`);
  const body = await response.json();
  const models = getModelsArray(body);
  assert(models.length > 0, 'Expected LM Studio /api/v1/models to return at least one model');
  return models;
}

async function assertModelCanBeLoaded(serverUrl, apiKey) {
  const beforeModels = await fetchModels(serverUrl, apiKey);
  const unloadedModels = beforeModels
    .filter((model) => model?.type !== 'embedding' && !isLoaded(model))
    .sort((left, right) => {
      const leftSize = typeof left?.size_bytes === 'number' ? left.size_bytes : Number.MAX_SAFE_INTEGER;
      const rightSize = typeof right?.size_bytes === 'number' ? right.size_bytes : Number.MAX_SAFE_INTEGER;
      return leftSize - rightSize;
    });

  if (unloadedModels.length === 0) {
    const loadedModel = beforeModels.find((model) => model?.type !== 'embedding' && isLoaded(model));
    assert(loadedModel, 'Expected at least one non-embedding model to be exposed by LM Studio');
    console.log(`Live model-load assertion skipped: all exposed chat models are already loaded. Checked ${beforeModels.length} models.`);
    return;
  }

  const failures = [];

  for (const unloadedModel of unloadedModels) {
    const modelId = unloadedModel.key ?? unloadedModel.id;
    const loadResponse = await fetch(`${serverUrl}/api/v1/models/load`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...authHeaders(apiKey),
      },
      body: JSON.stringify({ model: modelId }),
      signal: AbortSignal.timeout(10 * 60 * 1000),
    });

    if (!loadResponse.ok) {
      const errorText = await loadResponse.text();
      failures.push(`${modelId}: ${loadResponse.status} ${loadResponse.statusText}${errorText ? ` ${errorText}` : ''}`);
      continue;
    }

    const loadBody = await loadResponse.json();
    assert.strictEqual(loadBody.status, 'loaded', `Expected LM Studio load response status to be loaded for ${modelId}`);

    await vscode.commands.executeCommand('lmstudio-copilot-expansion.refreshModels');

    const afterModels = await fetchModels(serverUrl, apiKey);
    const nowLoaded = afterModels.find((model) => (model.key ?? model.id) === modelId);
    assert(nowLoaded, `Expected model to remain present after load: ${modelId}`);
    assert(
      isLoaded(nowLoaded),
      `Expected model to be reported as loaded after API load: ${modelId}`
    );

    console.log(`Live model-load assertion passed using model: ${modelId}`);
    return;
  }

  assert.fail(
    `Expected at least one unloaded chat model to be loadable via the LM Studio API, but all candidates failed. ${failures.join(' | ')}`
  );
}

async function assertCommandsRegistered() {
  const commands = await vscode.commands.getCommands(true);
  for (const commandId of EXPECTED_COMMANDS) {
    assert(commands.includes(commandId), `Expected command to be registered: ${commandId}`);
  }
}

async function assertLiveConnection() {
  const config = vscode.workspace.getConfiguration('lmstudio-copilot-expansion');
  const serverUrl = config.get('serverUrl', 'http://localhost:1234');
  const apiKey = config.get('apiKey', '');

  assert(apiKey && apiKey.trim().length > 0, 'Live smoke test requires lmstudio-copilot-expansion.apiKey in the active VS Code profile');

  await fetchModels(serverUrl, apiKey);

  await vscode.commands.executeCommand('lmstudio-copilot-expansion.checkConnection');
  await vscode.commands.executeCommand('lmstudio-copilot-expansion.refreshModels');
  await assertModelCanBeLoaded(serverUrl, apiKey);
}

async function run() {
  const extension = vscode.extensions.getExtension(EXTENSION_ID);
  assert(extension, `Extension not found: ${EXTENSION_ID}`);

  await extension.activate();
  assert(extension.isActive, 'Extension should be active after activation');

  await assertCommandsRegistered();

  if (process.env.LMSTUDIO_LIVE_SMOKE === '1') {
    await assertLiveConnection();
    console.log('Live VS Code smoke test passed.');
    return;
  }

  console.log('VS Code integration test passed.');
}

module.exports = { run };