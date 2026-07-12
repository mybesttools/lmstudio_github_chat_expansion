const fs = require('fs');
const os = require('os');
const path = require('path');
const { runTests } = require('@vscode/test-electron');

function resolveCodeUserSettingsPath() {
  if (process.env.VSCODE_USER_SETTINGS_PATH) {
    return process.env.VSCODE_USER_SETTINGS_PATH;
  }

  if (process.platform === 'darwin') {
    return path.join(os.homedir(), 'Library', 'Application Support', 'Code', 'User', 'settings.json');
  }

  if (process.platform === 'win32') {
    return path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'Code', 'User', 'settings.json');
  }

  return path.join(os.homedir(), '.config', 'Code', 'User', 'settings.json');
}

function createUserDataDir(seedFromCurrentProfile) {
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'lmstudio-vscode-test-'));

  if (seedFromCurrentProfile) {
    const settingsPath = resolveCodeUserSettingsPath();
    if (fs.existsSync(settingsPath)) {
      const targetDir = path.join(userDataDir, 'User');
      fs.mkdirSync(targetDir, { recursive: true });
      fs.copyFileSync(settingsPath, path.join(targetDir, 'settings.json'));
    }
  }

  return userDataDir;
}

async function main() {
  const extensionDevelopmentPath = path.resolve(__dirname, '..');
  const extensionTestsPath = path.resolve(__dirname, 'vscode-tests', 'suite.js');
  const userDataDir = createUserDataDir(process.env.LMSTUDIO_USE_CURRENT_VSCODE_PROFILE === '1');
  const launchArgs = [
    extensionDevelopmentPath,
    '--user-data-dir',
    userDataDir,
    '--disable-workspace-trust',
    '--skip-welcome',
    '--skip-release-notes',
  ];

  try {
    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath,
      launchArgs,
    });
  } catch (error) {
    console.error('VS Code integration tests failed.');
    console.error(error);
    process.exit(1);
  }
}

main();