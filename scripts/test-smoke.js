const assert = require('assert');
const fs = require('fs');
const path = require('path');

const repoRoot = path.resolve(__dirname, '..');
const packageJsonPath = path.join(repoRoot, 'package.json');
const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
const distMainPath = path.join(repoRoot, packageJson.main.replace(/^\.\//, ''));

const expectedCommandIds = [
  'lmstudio-copilot-expansion.refreshModels',
  'lmstudio-copilot-expansion.reviewSuggestedModelConfig',
  'lmstudio-copilot-expansion.checkConnection',
];

const expectedToolIds = [
  'mbt_lmstudio_run_in_terminal',
  'mbt_lmstudio_read_file',
  'mbt_lmstudio_write_file',
  'mbt_lmstudio_list_directory',
  'mbt_lmstudio_search_files',
  'mbt_lmstudio_get_codebase_index',
];

function readContributionNames(sectionName) {
  const section = packageJson.contributes?.[sectionName];
  assert(Array.isArray(section), `package.json contributes.${sectionName} must be an array`);
  return section.map((entry) => entry.command ?? entry.name);
}

function assertSameMembers(actual, expected, label) {
  assert.deepStrictEqual(
    [...actual].sort(),
    [...expected].sort(),
    `${label} do not match the expected extension surface`
  );
}

assert.strictEqual(packageJson.main, './dist/extension.js', 'package.json main should target dist/extension.js');
assert(fs.existsSync(distMainPath), `Compiled extension entrypoint is missing: ${distMainPath}`);

const backendDllPath = path.join(repoRoot, 'dist', 'backend', 'LmStudioBackend.dll');
assert(fs.existsSync(backendDllPath), `Compiled backend is missing: ${backendDllPath} (run 'npm run build:backend')`);

const bundledSource = fs.readFileSync(distMainPath, 'utf8');
const providerContrib = packageJson.contributes?.languageModelChatProviders;
assert(Array.isArray(providerContrib) && providerContrib.length > 0, 'No language model chat providers contributed');
assert(
  providerContrib.some((entry) => entry.vendor === 'lmstudio-mbt'),
  'package.json does not contribute the expected lmstudio-mbt chat provider vendor'
);

assert(
  Array.isArray(packageJson.activationEvents) &&
    packageJson.activationEvents.includes('onStartupFinished'),
  'Activation event onStartupFinished is missing from package.json'
);

assertSameMembers(
  readContributionNames('commands'),
  expectedCommandIds,
  'Contributed commands'
);

assertSameMembers(
  readContributionNames('languageModelTools'),
  expectedToolIds,
  'Contributed language model tools'
);

for (const toolId of expectedToolIds) {
  assert(
    bundledSource.includes(toolId),
    `Compiled bundle does not contain expected tool id: ${toolId}`
  );
}

console.log('Smoke test passed: build artifact and extension contributions look consistent.');