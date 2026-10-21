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

const { CLIENTS, SERVER, sameFile } = require('../bin/clocktail.js');
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
