// Every agent's config writer, in a throwaway home: the entry lands where that agent reads it, in its shape, next to
// what was there, and comes out again on remove. Claude Code goes through its own CLI and is not exercised here.
// Run: node --test npm/test/clients.test.js
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');

const home = fs.mkdtempSync(path.join(os.tmpdir(), 'clocktail-genie-'));
const appdata = path.join(home, 'AppData', 'Roaming');
const local = path.join(home, 'AppData', 'Local');
Object.assign(process.env, { USERPROFILE: home, HOME: home, APPDATA: appdata, LOCALAPPDATA: local });
for (const k of ['XDG_CONFIG_HOME', 'OPENCODE_CONFIG', 'OPENCODE_CONFIG_DIR', 'CODEX_HOME', 'GEMINI_CLI_HOME', 'COPILOT_HOME',
  'QWEN_HOME', 'KIMI_CODE_HOME', 'GROK_HOME', 'PI_CODING_AGENT_DIR', 'PI_CONFIG_DIR', 'QODER_CONFIG_DIR', 'CLAUDE_CONFIG_DIR'])
  delete process.env[k];

const put = (file, text) => { fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text); };
const h = (...p) => path.join(home, ...p);

// Each agent's config as a person might already have it, with something of theirs to keep.
put(h('.cursor', 'mcp.json'), JSON.stringify({ mcpServers: { theirs: { command: 'x' } } }));
put(h('.config', 'opencode', 'opencode.json'), JSON.stringify({ model: 'm', mcp: { servers: { theirs: { type: 'local', command: ['x'] } } } }));
put(path.join(appdata, 'Code', 'User', 'mcp.json'), JSON.stringify({ servers: { theirs: { type: 'stdio', command: 'x' } } }));
put(h('.codex', 'config.toml'), 'model = "o"\n\n[mcp_servers.theirs]\ncommand = "x"\n\n[mcp_servers.clocktail]\ncommand = "old"\n\n[mcp_servers.clocktail.env]\nA = "1"\n');
put(h('.zcode', 'cli', 'config.json'), JSON.stringify({ theme: 'dark' }));
put(h('.copilot', 'mcp-config.json'), '');
for (const d of [h('.codeium', 'windsurf'), h('.gemini', 'config'), h('.commandcode'), h('.qwen'), h('.kimi-code'), path.join(appdata, 'Trae', 'User'),
  h('.workbuddy'), h('.grok'), h('.omp', 'agent'), h('.pi', 'agent'), h('.qoder'), h('.factory'), path.join(appdata, 'devin'), path.join(appdata, 'Claude')])
  fs.mkdirSync(d, { recursive: true });

const { CLIENTS, SERVER, sameFile, setupApprovalCommand } = require('../bin/clocktail.js');
const byId = id => CLIENTS.find(c => c.id === id);
const read = f => JSON.parse(fs.readFileSync(f, 'utf8'));

test('every file-based agent is found, takes the entry and gives it back', () => {
  for (const c of CLIENTS.filter(c => c.id !== 'claude-code')) {
    assert.ok(c.present(), `${c.label} not detected`);
    c.add();
    assert.ok(c.connected(), `${c.label} not connected after add`);
    assert.ok(c.remove(), `${c.label} remove found nothing`);
    assert.ok(!c.connected(), `${c.label} still connected after remove`);
    c.add();   // leave it in for the shape checks below
  }
});

test('the entry is in each dialect, next to what was there', () => {
  assert.deepStrictEqual(read(byId('cursor').file).mcpServers, { theirs: { command: 'x' }, clocktail: { command: SERVER, args: [] } });
  const oc = read(byId('opencode').file);
  assert.strictEqual(oc.model, 'm');
  assert.deepStrictEqual(oc.mcp.servers.clocktail, { type: 'local', command: [SERVER] });   // V2 file: V2 entry
  assert.deepStrictEqual(read(byId('vscode').file).servers.clocktail, { type: 'stdio', command: SERVER, args: [] });
  assert.deepStrictEqual(read(byId('copilot').file).mcpServers.clocktail, { type: 'stdio', command: SERVER, args: [], tools: ['*'] });
  assert.deepStrictEqual(read(byId('commandcode').file).mcpServers.clocktail, { transport: 'stdio', enabled: true, command: SERVER, args: [] });
  const z = read(byId('zcode').file);
  assert.strictEqual(z.theme, 'dark');
  assert.deepStrictEqual(z.mcp.servers.clocktail, { command: SERVER, args: [] });
  const toml = fs.readFileSync(byId('codex').file, 'utf8');
  assert.match(toml, /\[mcp_servers\.theirs\]\ncommand = "x"/);
  assert.ok(!toml.includes('"old"'), 'the old command is replaced');
  assert.ok(toml.includes(`[mcp_servers.clocktail]\ncommand = '${SERVER}'\nargs = []`));
  assert.ok(!toml.includes('[mcp_servers.clocktail.env]'), 'removing the entry removed its env too');
  assert.ok(fs.existsSync(byId('cursor').file + '.clocktail.bak'), 'a backup is kept');
});

test('an update keeps the env the person gave the entry (the CLOCKTAIL_MCP_* caps)', () => {
  const f = byId('cursor').file;
  const d = read(f);
  d.mcpServers.clocktail = { command: 'old.exe', args: [], env: { CLOCKTAIL_MCP_MIN_MV: '800' } };
  fs.writeFileSync(f, JSON.stringify(d));
  byId('cursor').add();
  assert.deepStrictEqual(read(f).mcpServers.clocktail, { command: SERVER, args: [], env: { CLOCKTAIL_MCP_MIN_MV: '800' } });
  const t = byId('codex').file;
  fs.writeFileSync(t, 'model = "o"\n\n[mcp_servers.clocktail]\ncommand = "old"\n\n[mcp_servers.clocktail.env]\nA = "1"\n\n[other]\nx = 1\n');
  byId('codex').add();
  byId('codex').add();   // twice: still one env table, still the person's
  const toml = fs.readFileSync(t, 'utf8');
  assert.strictEqual(toml.split('[mcp_servers.clocktail.env]').length, 2);
  assert.match(toml, /A = "1"/);
});

test('a V1 OpenCode file gets a V1 entry', () => {
  const f = byId('opencode').file;
  fs.writeFileSync(f, JSON.stringify({ mcp: { rea: { type: 'local', command: ['x'], enabled: true } } }));
  byId('opencode').add();
  assert.deepStrictEqual(read(f).mcp.clocktail, { type: 'local', command: [SERVER], enabled: true });
});

test('a file with comments is left alone', () => {
  const f = byId('cursor').file;
  fs.writeFileSync(f, '{ // mine\n "mcpServers": {} }');
  assert.throws(() => byId('cursor').add(), /not plain JSON/);
  assert.strictEqual(fs.readFileSync(f, 'utf8'), '{ // mine\n "mcpServers": {} }');
});

// Setup leaves a file that is already installed alone: an agent running it would make the copy fail (2026-10-20, a
// second agent could not be connected while Claude Code had Clocktail open).
test('an installed file with the same bytes counts as the same; a changed or missing one does not', () => {
  const a = h('same-a.exe'), b = h('same-b.exe'), c = h('same-c.exe');
  put(a, 'MZ clocktail 0.1.2');
  put(b, 'MZ clocktail 0.1.2');
  put(c, 'MZ clocktail 0.1.3');
  assert.strictEqual(sameFile(a, b), true);
  assert.strictEqual(sameFile(a, c), false, 'same size, other bytes');
  assert.strictEqual(sameFile(a, h('missing.exe')), false);
});

test('a setup plan approves the same cached installer, with PowerShell quoting and the selected options', () => {
  const node = String.raw`C:\Program Files\nodejs\node.exe`;
  const script = String.raw`C:\Users\Pat O'Neil\npm-cache\_npx\123\node_modules\clocktail\bin\clocktail.js`;
  const approval = setupApprovalCommand('codex,vscode', ['--no-service', '--full', '--allow-reads'], node, script);
  assert.strictEqual(approval, String.raw`& 'C:\Program Files\nodejs\node.exe' 'C:\Users\Pat O''Neil\npm-cache\_npx\123\node_modules\clocktail\bin\clocktail.js' setup --yes --client 'codex,vscode' --no-service --full --allow-reads`);
});

test.after(() => fs.rmSync(home, { recursive: true, force: true }));

test('a running server is named by the agent that started it', () => {
  const { agentName, agentList } = require('../bin/clocktail.js');
  assert.equal(agentName('claude.exe', String.raw`C:\Users\x\.local\bin\claude.exe`), 'Claude Code');
  assert.equal(agentName('claude.exe', String.raw`C:\Users\x\AppData\Local\AnthropicClaude\app-1.2.3\claude.exe`), 'Claude Desktop');
  assert.equal(agentName('OpenCode.exe', ''), 'OpenCode');
  assert.equal(agentName('mystery.exe', ''), 'mystery.exe');
  assert.equal(agentList([{ agent: 'OpenCode' }, { agent: 'Claude Code' }, { agent: 'OpenCode' }]), 'OpenCode and Claude Code');
  assert.equal(agentList([]), '');
});

test('a relay counts once: the worker under it is not an agent', () => {
  const { serversByAgent } = require('../bin/clocktail.js');
  const dir = String.raw`c:\users\x\appdata\local\programs\clocktail`;
  const exe = String.raw`C:\Users\x\AppData\Local\Programs\Clocktail\clocktail-mcp.exe`;
  const rows = [
    ['100', exe, 'claude.exe', String.raw`C:\Users\x\.local\bin\claude.exe`],
    ['101', exe, 'clocktail-mcp.exe', exe],
    ['200', exe, 'OpenCode.exe', ''],
    ['201', exe, 'clocktail-mcp.exe', exe],
    ['300', String.raw`D:\elsewhere\clocktail-mcp.exe`, 'code.exe', ''],
  ];
  assert.deepStrictEqual(serversByAgent(rows, dir), [{ pid: 100, agent: 'Claude Code' }, { pid: 200, agent: 'OpenCode' }]);
});

// --lite (server 0.4.0): every dialect starts the server with it, and an update without the flag keeps the choice.
test('--lite reaches the server in every dialect, and an update without it keeps it', () => {
  const json = { cursor: { command: SERVER, args: ['--lite'] }, vscode: { type: 'stdio', command: SERVER, args: ['--lite'] },
    copilot: { type: 'stdio', command: SERVER, args: ['--lite'], tools: ['*'] },
    commandcode: { transport: 'stdio', enabled: true, command: SERVER, args: ['--lite'] } };
  for (const [id, want] of Object.entries(json)) {
    const c = byId(id), key = id === 'vscode' ? 'servers' : 'mcpServers';
    fs.writeFileSync(c.file, JSON.stringify({ [key]: { theirs: { command: 'x' } } }));
    c.add({ mode: '--lite' });
    assert.deepStrictEqual(read(c.file)[key], { theirs: { command: 'x' }, clocktail: want }, id);
    c.add();
    assert.deepStrictEqual(read(c.file)[key].clocktail, want, `${id}: an update keeps --lite`);
  }
  // Set by hand next to the person's env: both survive.
  const f = byId('cursor').file;
  fs.writeFileSync(f, JSON.stringify({ mcpServers: { clocktail: { command: 'old.exe', args: ['--lite'], env: { A: '1' } } } }));
  byId('cursor').add();
  assert.deepStrictEqual(read(f).mcpServers.clocktail, { command: SERVER, args: ['--lite'], env: { A: '1' } });

  const oc = byId('opencode');
  fs.writeFileSync(oc.file, JSON.stringify({ mcp: { servers: { theirs: { type: 'local', command: ['x'] } } } }));
  oc.add({ mode: '--lite' });
  oc.add();
  assert.deepStrictEqual(read(oc.file).mcp.servers.clocktail, { type: 'local', command: [SERVER, '--lite'] });   // V2
  fs.writeFileSync(oc.file, JSON.stringify({ mcp: { rea: { type: 'local', command: ['x'], enabled: true } } }));
  oc.add({ mode: '--lite' });
  oc.add();
  assert.deepStrictEqual(read(oc.file).mcp.clocktail, { type: 'local', command: [SERVER, '--lite'], enabled: true });   // V1

  const t = byId('codex').file;
  fs.writeFileSync(t, 'model = "o"\n');
  byId('codex').add({ mode: '--lite' });
  byId('codex').add();
  assert.ok(fs.readFileSync(t, 'utf8').includes(`[mcp_servers.clocktail]\ncommand = '${SERVER}'\nargs = ['--lite']\n`));
  fs.writeFileSync(t, '[mcp_servers.clocktail]\ncommand = "old"\nargs = ["--lite"]\n\n[mcp_servers.clocktail.env]\nA = "1"\n');
  byId('codex').add();
  const toml = fs.readFileSync(t, 'utf8');
  assert.ok(toml.includes("args = ['--lite']"), 'a hand-written --lite is kept');
  assert.match(toml, /\[mcp_servers\.clocktail\.env\]\nA = "1"/);
  assert.strictEqual(toml.split('--lite').length, 2, 'once');
});

test('setup can select full mode and preserves explicit full settings on update', () => {
  const cursor = byId('cursor');
  const env = { CLOCKTAIL_TOOLS: 'full', A: '1' };
  put(cursor.file, JSON.stringify({ mcpServers: { clocktail: { command: 'old.exe', env } } }));
  cursor.add();
  assert.deepStrictEqual(read(cursor.file).mcpServers.clocktail, { command: SERVER, args: [], env });
  cursor.add({ mode: '--full' });
  cursor.add();
  assert.deepStrictEqual(read(cursor.file).mcpServers.clocktail, { command: SERVER, args: ['--full'], env });
  cursor.add({ mode: '--lite' });
  assert.deepStrictEqual(read(cursor.file).mcpServers.clocktail, { command: SERVER, args: ['--lite'], env });

  const oc = byId('opencode');
  put(oc.file, JSON.stringify({ mcp: { servers: { clocktail: { type: 'local', command: ['old.exe', '--full'], environment: env } } } }));
  oc.add();
  assert.deepStrictEqual(read(oc.file).mcp.servers.clocktail, { type: 'local', command: [SERVER, '--full'], environment: env });

  const codex = byId('codex');
  put(codex.file, `[mcp_servers.clocktail]
command = "old"
args = [
  "--full",
]
[mcp_servers.clocktail.env]
CLOCKTAIL_TOOLS = "lite"
`);
  codex.add();
  const toml = fs.readFileSync(codex.file, 'utf8');
  assert.match(toml, /args = \['--full'\]/);
  assert.match(toml, /CLOCKTAIL_TOOLS = "lite"/);
});

test('Claude Code uses the default and retains an explicit mode on update', () => {
  const claude = byId('claude-code');
  const cp = require('node:child_process');
  const original = cp.spawnSync;
  const calls = [];
  let current = '';
  cp.spawnSync = command => {
    calls.push(command);
    if (command === 'where claude') return { status: 0, stdout: 'claude' };
    if (command === 'claude mcp get clocktail') return { status: current ? 0 : 1, stdout: current };
    return { status: 0 };
  };
  try {
    claude.add();
    assert.match(calls.at(-1), /claude mcp add clocktail -s user -- .*clocktail-mcp\.exe"?$/);
    current = `Command: ${SERVER}\nArgs: --full\nEnvironment:\n  A=1\n`;
    calls.length = 0;
    claude.add();
    assert.equal(calls.length, 2, 'a matching entry stays in place');
    claude.add({ mode: '--lite' });
    assert.match(calls.at(-1), /clocktail-mcp\.exe"? --lite$/);
    current = 'Command: old.exe\nArgs: \nEnvironment:\n  CLOCKTAIL_TOOLS=FULL\n';
    claude.add();
    assert.match(calls.at(-1), /clocktail-mcp\.exe"? --full$/);
  } finally {
    cp.spawnSync = original;
  }
});

// --allow-reads: Claude Code stops asking before each read and still asks before every write.
const READS = ['card_report', 'gpu_status', 'card_settings', 'limiter', 'curve_around', 'data_points', 'read_log', 'manual',
  'list_presets', 'preview_daily', 'diagnostic_report', 'lab_status', 'live_read'].map(t => `mcp__clocktail__${t}`);

test('--allow-reads adds the 13 reads once each, next to what was there; uninstall takes back only those', () => {
  const { allowReads, disallowReads } = require('../bin/clocktail.js');
  const f = h('.claude', 'settings.json');
  assert.strictEqual(disallowReads(), false, 'no file: nothing to take back');
  put(f, JSON.stringify({ model: 'opus', permissions: { allow: ['Bash(git status)', READS[0]], deny: ['Read(.env)'] } }));
  assert.strictEqual(allowReads(), true);
  assert.strictEqual(allowReads(), false, 'twice: nothing more to add');
  let d = read(f);
  assert.strictEqual(d.model, 'opus');
  assert.deepStrictEqual(d.permissions, { allow: ['Bash(git status)', ...READS], deny: ['Read(.env)'] });
  assert.ok(!d.permissions.allow.some(r => r.includes('*')), 'never a wildcard: it would let the writes through');
  assert.ok(fs.existsSync(f + '.clocktail.bak'), 'backed up first');

  d.permissions.allow.push('mcp__clocktail__stock');   // the person's own rule for a write stays theirs
  fs.writeFileSync(f, JSON.stringify(d));
  assert.strictEqual(disallowReads(), true);
  assert.deepStrictEqual(read(f), { model: 'opus', permissions: { allow: ['Bash(git status)', 'mcp__clocktail__stock'], deny: ['Read(.env)'] } });
  assert.strictEqual(disallowReads(), false);

  fs.writeFileSync(f, '{ // mine\n}');
  assert.throws(() => allowReads(), /not plain JSON/);
  assert.strictEqual(fs.readFileSync(f, 'utf8'), '{ // mine\n}');
});

test('--allow-reads follows CLAUDE_CONFIG_DIR and creates the file when there is none', () => {
  const { allowReads, disallowReads } = require('../bin/clocktail.js');
  process.env.CLAUDE_CONFIG_DIR = h('claude-elsewhere');
  try {
    allowReads();
    const f = h('claude-elsewhere', 'settings.json');
    assert.deepStrictEqual(read(f), { permissions: { allow: READS } });
    disallowReads();
    assert.deepStrictEqual(read(f), { permissions: { allow: [] } });
  } finally { delete process.env.CLAUDE_CONFIG_DIR; }
});
