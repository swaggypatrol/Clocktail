#!/usr/bin/env node
// The setup genie. `npx -y clocktail setup` copies the server and the service into the user's programs folder,
// connects the agents the person picks, and installs the service (one UAC prompt). Agents then run the native
// server directly: Node is needed for setup only, never at run time.
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');
const readline = require('readline');

const VERSION = require('../package.json').version;
const HOME = os.homedir();
const APPDATA = process.env.APPDATA || path.join(HOME, 'AppData', 'Roaming');
const LOCAL = process.env.LOCALAPPDATA || path.join(HOME, 'AppData', 'Local');
const INSTALL_DIR = path.join(LOCAL, 'Programs', 'Clocktail');
const STATE_DIR = path.join(LOCAL, 'Clocktail');
const DIST = path.join(__dirname, '..', 'dist');
// PresentMon (Intel, MIT) is the service's frame-rate capture; its licence files go with it.
// clocktail-lab.exe goes beside the server: lab_test and tune_memory start it from there.
const FILES = ['clocktail-mcp.exe', 'clocktail-lab.exe', 'Clocktail.Broker.exe', 'PresentMon.exe', 'PresentMon-LICENSE.txt', 'PresentMon-THIRD_PARTY.txt'];
const SERVER = path.join(INSTALL_DIR, 'clocktail-mcp.exe');
const BROKER = path.join(INSTALL_DIR, 'Clocktail.Broker.exe');
const NAME = 'clocktail';

// ---- the agents it knows how to connect ------------------------------------------------------------------------
// Where each agent keeps its MCP servers, which environment variables move it, and the entry shape it expects are
// ported from REA's setup (https://github.com/morluto/rea, MIT License, Copyright (c) 2026 morluto); see
// THIRD_PARTY_NOTICES.md. Windows paths only: Clocktail runs on Windows.

const ENV = process.env;
const XDG = ENV.XDG_CONFIG_HOME || path.join(HOME, '.config');
const inHome = (...p) => path.join(HOME, ...p);

/** The stdio entry each config dialect expects. */
function entry(dialect) {
  switch (dialect) {
    case 'opencode': return { type: 'local', command: [SERVER], enabled: true };
    case 'opencode_v2': return { type: 'local', command: [SERVER] };   // V2 connects a server unless it says disabled
    case 'stdio': return { type: 'stdio', command: SERVER, args: [] };
    case 'copilot': return { type: 'stdio', command: SERVER, args: [], tools: ['*'] };
    case 'commandcode': return { transport: 'stdio', enabled: true, command: SERVER, args: [] };
    default: return { command: SERVER, args: [] };
  }
}

const dig = (o, keys) => keys.reduce((t, k) => (t && typeof t === 'object' ? t[k] : undefined), o);
function digCreate(o, keys) {
  let t = o;
  for (const k of keys) { if (!t[k] || typeof t[k] !== 'object') t[k] = {}; t = t[k]; }
  return t;
}

/**
 * A client whose config is a JSON file with a table of servers. `locate(doc)` says where the table is and which
 * dialect its entries use (OpenCode's depends on the file); by default `mcpServers` with plain command/args.
 */
function jsonClient(id, label, file, o = {}) {
  const locate = o.locate || (() => ({ keys: [o.key || 'mcpServers'], dialect: o.dialect }));
  return {
    id, label, file, note: o.note,
    present: () => fs.existsSync(o.marker || path.dirname(file)),
    connected() { const d = readJson(file); return !!(d && (dig(d, locate(d).keys) || {})[NAME]); },
    add() {
      const d = readJson(file, true) || {};
      const { keys, dialect } = locate(d);
      const table = digCreate(d, keys);
      table[NAME] = { ...entry(dialect), ...keptEnv(table[NAME]) };
      writeJson(file, d);
    },
    remove() {
      const d = readJson(file);
      const t = d && dig(d, locate(d).keys);
      if (!t || !t[NAME]) return false;
      delete t[NAME];
      writeJson(file, d);
      return true;
    },
  };
}

/** A client whose config is TOML with `[mcp_servers.<name>]` tables (Codex, Grok Build). */
function tomlClient(id, label, file) {
  const header = `[mcp_servers.${NAME}]`;
  return {
    id, label, file,
    present: () => fs.existsSync(path.dirname(file)),
    connected: () => readText(file).includes(header),
    add() {
      const text = readText(file);
      const kept = withoutTomlTable(text, `mcp_servers.${NAME}`).trimEnd();
      // The person's own variables (the CLOCKTAIL_MCP_* caps) survive an update: an env line, or the env subtable.
      const envLine = tomlTableLines(text, `mcp_servers.${NAME}`).slice(1).filter(l => /^\s*env\s*=/.test(l));
      const envTable = tomlTableLines(text, `mcp_servers.${NAME}.env`);
      fs.mkdirSync(path.dirname(file), { recursive: true });
      backup(file);
      fs.writeFileSync(file, `${kept}${kept ? '\n\n' : ''}${header}\ncommand = '${SERVER}'\nargs = []\n`
        + envLine.map(l => l.trim() + '\n').join('')
        + (envTable.length ? `\n${envTable.join('\n').trimEnd()}\n` : ''));
    },
    remove() {
      const text = readText(file);
      if (!text.includes(header)) return false;
      backup(file);
      fs.writeFileSync(file, withoutTomlTable(text, `mcp_servers.${NAME}`).trimEnd() + '\n');
      return true;
    },
  };
}

/**
 * Claude Desktop from the Microsoft Store (MSIX) reads a package-private copy of its config once it has saved one;
 * until then it reads the usual %APPDATA% file. A running Claude Desktop rewrites the file from memory, so it must be
 * quit first.
 */
function claudeDesktop() {
  const appDataDir = path.join(APPDATA, 'Claude');
  const appDataFile = path.join(appDataDir, 'claude_desktop_config.json');
  const packages = path.join(LOCAL, 'Packages');
  let family;
  try { family = fs.readdirSync(packages).find(n => /^(?:AnthropicPBC\.)?Claude_[0-9a-hjkmnp-tv-z]{13}$/i.test(n)); } catch { /* none */ }
  let file = appDataFile, marker = appDataDir;
  if (family) {
    marker = path.join(packages, family);
    const privateFile = path.join(marker, 'LocalCache', 'Roaming', 'Claude', 'claude_desktop_config.json');
    file = fs.existsSync(privateFile) || !fs.existsSync(appDataFile) ? privateFile : appDataFile;
  }
  return jsonClient('claude-desktop', 'Claude Desktop', file, { marker, note: 'quit Claude Desktop fully first: a running one rewrites this file' });
}

/** OpenCode: OPENCODE_CONFIG_DIR, else OPENCODE_CONFIG, else the XDG folder; .jsonc wins over .json when both exist. */
function openCode() {
  const dir = ENV.OPENCODE_CONFIG_DIR || path.join(XDG, 'opencode');
  const candidates = ENV.OPENCODE_CONFIG && !ENV.OPENCODE_CONFIG_DIR
    ? [ENV.OPENCODE_CONFIG]
    : [path.join(dir, 'opencode.json'), path.join(dir, 'opencode.jsonc')];
  const file = [...candidates].reverse().find(f => fs.existsSync(f)) || candidates[0];
  // A V2 file keeps servers under mcp.servers; a V1 file (or a V1 server that happens to be named "servers") under mcp.
  const locate = d => {
    const s = d.mcp && d.mcp.servers;
    return s && typeof s === 'object' && typeof s.type !== 'string'
      ? { keys: ['mcp', 'servers'], dialect: 'opencode_v2' }
      : { keys: ['mcp'], dialect: 'opencode' };
  };
  return jsonClient('opencode', 'OpenCode', file, { marker: dir, locate });
}

const CLIENTS = [
  {
    id: 'claude-code', label: 'Claude Code', file: 'claude mcp (user scope)',
    present: () => !!which('claude') || fs.existsSync(ENV.CLAUDE_CONFIG_DIR || inHome('.claude')),
    connected: () => run('claude', ['mcp', 'get', NAME], { quiet: true }).status === 0,
    add() {
      if (!which('claude')) throw new Error(`the claude command is not on PATH; run: claude mcp add ${NAME} -s user -- "${SERVER}"`);
      // Already pointing at the server: left as it is, with any -e variables the person gave it (an update replaces the
      // exe in place, so the entry needs no change).
      const now = run('claude', ['mcp', 'get', NAME], { quiet: true });
      if (now.status === 0 && (now.stdout || '').toLowerCase().includes(SERVER.toLowerCase())) return;
      run('claude', ['mcp', 'remove', NAME, '-s', 'user'], { quiet: true });
      const r = run('claude', ['mcp', 'add', NAME, '-s', 'user', '--', SERVER], { quiet: true });
      if (r.status !== 0) throw new Error(`claude mcp add failed: ${(r.stderr || r.stdout || '').trim()}`);
    },
    remove: () => !!which('claude') && run('claude', ['mcp', 'remove', NAME, '-s', 'user'], { quiet: true }).status === 0,
  },
  claudeDesktop(),
  tomlClient('codex', 'Codex', path.join(ENV.CODEX_HOME || inHome('.codex'), 'config.toml')),
  jsonClient('cursor', 'Cursor', inHome('.cursor', 'mcp.json')),
  jsonClient('gemini', 'Gemini CLI', path.join(ENV.GEMINI_CLI_HOME || HOME, '.gemini', 'settings.json')),
  jsonClient('windsurf', 'Windsurf', inHome('.codeium', 'windsurf', 'mcp_config.json')),
  jsonClient('devin', 'Devin', path.join(APPDATA, 'devin', 'mcp_config.json')),
  openCode(),
  jsonClient('antigravity', 'Antigravity', inHome('.gemini', 'config', 'mcp_config.json')),
  jsonClient('copilot', 'GitHub Copilot CLI', path.join(ENV.COPILOT_HOME || inHome('.copilot'), 'mcp-config.json'), { dialect: 'copilot' }),
  jsonClient('commandcode', 'Command Code', inHome('.commandcode', 'mcp.json'), { dialect: 'commandcode' }),
  jsonClient('qwen', 'Qwen Code', path.join(ENV.QWEN_HOME || inHome('.qwen'), 'settings.json')),
  jsonClient('kimi', 'Kimi Code', path.join(ENV.KIMI_CODE_HOME || inHome('.kimi-code'), 'mcp.json')),
  jsonClient('vscode', 'VS Code', path.join(APPDATA, 'Code', 'User', 'mcp.json'), { key: 'servers', dialect: 'stdio' }),
  jsonClient('trae', 'Trae', path.join(APPDATA, 'Trae', 'User', 'mcp.json')),
  jsonClient('workbuddy', 'WorkBuddy', inHome('.workbuddy', 'mcp.json')),
  tomlClient('grok', 'Grok Build', path.join(ENV.GROK_HOME || inHome('.grok'), 'config.toml')),
  jsonClient('omp', 'OMP', path.join(ENV.PI_CODING_AGENT_DIR || inHome(ENV.PI_CONFIG_DIR || '.omp', 'agent'), 'mcp.json'), { dialect: 'stdio' }),
  jsonClient('pi', 'Pi', path.join(ENV.PI_CODING_AGENT_DIR || inHome('.pi', 'agent'), 'mcp.json'), { dialect: 'stdio' }),
  jsonClient('qoder', 'Qoder', path.join(ENV.QODER_CONFIG_DIR || inHome('.qoder'), 'settings.json')),
  jsonClient('droid', 'Factory Droid', inHome('.factory', 'mcp.json')),
  jsonClient('zcode', 'ZCode', inHome('.zcode', 'cli', 'config.json'), { locate: () => ({ keys: ['mcp', 'servers'] }) }),
];

// ---- commands ----------------------------------------------------------------------------------------------------

async function setup(opts) {
  say(`\nClocktail ${VERSION} setup\n`);
  if (process.platform !== 'win32') return fail('Clocktail runs on Windows only. Your card has our sympathies.');
  checkGpu();

  // 0. What it is about to do, and the person's yes. Run by an agent (no terminal) or with --plan, it stops after the
  // plan: the agent shows it to the person and runs the approval command it ends with.
  const chosen = await pickClients(opts);
  say('\n  Setup will:');
  say(`    - copy the Clocktail server and service to ${INSTALL_DIR}`);
  for (const c of chosen) {
    say(`    - add Clocktail to ${c.label}  (${c.file}${c.id === 'claude-code' ? '' : ', backed up first'})`);
    if (c.note) say(`      ${c.note}`);
  }
  if (!opts.noService) say('    - install the Clocktail service (Windows asks for admin rights once)');
  const card = cardState();
  say(`    - leave your card as it is: ${card.now}`);
  const open = runningServers();
  if (open.length) say(`\n  Clocktail is open in ${agentList(open)}. Nothing needs closing: they keep the version they started with until you restart them.`);
  if (opts.plan || (!opts.yes && !process.stdin.isTTY)) {
    const found = CLIENTS.filter(c => safe(() => c.present()));
    const ids = chosen.length ? chosen.map(c => c.id).join(',') : 'none';
    say(`
  Nothing has been changed. To approve this plan, run:
    npx -y clocktail@latest setup --yes --client ${ids}${opts.noService ? ' --no-service' : ''}

  Agents found: ${found.map(c => `${c.id} (${c.label})`).join(', ') || 'none'}.
  To connect only some of them, list their ids after --client.
`);
    return;
  }
  if (!opts.yes) {
    const ok = (await ask('\n  Proceed? [Y/n] ')).trim().toLowerCase();
    if (ok !== '' && ok !== 'y' && ok !== 'yes') return fail('Nothing was changed.');
  }
  say('');

  // 1. The files. One already there with the same bytes stays. One an agent is running cannot be overwritten, but
  // Windows lets it be renamed: it moves aside (the agent runs on from it), the new one takes its place, and the old
  // one is deleted once nothing runs it (the next setup or uninstall).
  fs.mkdirSync(INSTALL_DIR, { recursive: true });
  removeMovedAside();
  let copied = 0, movedAside = false;
  for (const f of FILES) {
    const src = path.join(DIST, f), dst = path.join(INSTALL_DIR, f);
    if (!fs.existsSync(src)) return fail(`this package is missing ${f}; it was built wrong. Nothing was changed.`);
    if (sameFile(src, dst)) continue;
    try { fs.copyFileSync(src, dst); }
    catch (e) {
      if (e.code !== 'EBUSY' && e.code !== 'EPERM') throw e;
      try { fs.renameSync(dst, `${dst}.old-${Date.now()}`); fs.copyFileSync(src, dst); movedAside = true; }
      catch (e2) { return fail(`${f} is in use by ${agentList(runningServers()) || 'another program'} and could not be moved aside (${e2.code}). Close those agents and run setup again.`); }
    }
    copied++;
  }
  say(copied > 0 ? `  installed to ${INSTALL_DIR}` : `  already installed in ${INSTALL_DIR} (same version)`);

  // 2. The agents.
  for (const c of chosen) {
    try { c.add(); say(`  connected ${c.label}  (${c.file})`); }
    catch (e) { say(`  could not connect ${c.label}: ${e.message}`); }
  }
  if (chosen.length === 0) say(`  no agent connected. Point any MCP client at:\n    ${SERVER}`);

  // 3. The service.
  if (opts.noService) say('  service not installed (--no-service). Your agent can install it later with install_service.');
  else {
    say('\n  Installing the service. Windows will ask for admin rights once: the service is the only part that writes to the card.\n');
    const r = run(BROKER, ['install']);
    if (r.status !== 0) say('  The service did not install. Your agent can try again later with install_service.');
  }

  await verify();
  const stale = movedAside ? runningServers() : [];
  say(`
Done. ${card.done}
${stale.length ? `\n${agentList(stale)} still run${stale.length === 1 ? 's' : ''} the old version: restart ${stale.length === 1 ? 'it' : 'them'} to load ${VERSION} (in Claude Code, /mcp and reconnecting clocktail also does it).\n` : ''}
Restart your agent, then tell it something like:
  "How's my card doing?"
  "Let's tune my GPU. Start from the founders preset."   (RTX 5090 only)
  "Let's tune my GPU from stock."

Uninstall any time:  npx -y clocktail uninstall
`);
}

async function uninstall(opts) {
  say('\nClocktail uninstall\n');
  let atStock = !cardState().profile;
  if (fs.existsSync(BROKER)) {
    say('  putting the card back to stock');
    atStock = run(BROKER, ['stock'], { quiet: true }).status === 0;
    if (!atStock) say('  the service did not answer, so the card could not be put back to stock now: it returns to stock at the next restart');
    say('  removing the service (Windows will ask for admin rights once)');
    run(BROKER, ['uninstall']);
  }
  for (const c of CLIENTS) {
    try { if (c.present() && c.remove()) say(`  disconnected ${c.label}`); }
    catch (e) { say(`  could not disconnect ${c.label}: ${e.message}`); }
  }
  removeMovedAside();
  try { fs.rmSync(INSTALL_DIR, { recursive: true, force: true }); say(`  removed ${INSTALL_DIR}`); }
  catch (e) { say(`  could not remove ${INSTALL_DIR} (${e.code}): it is open in ${agentList(runningServers()) || 'another program'}; close it and run uninstall again`); }
  if (opts.purge) {
    fs.rmSync(STATE_DIR, { recursive: true, force: true });
    say(`  removed ${STATE_DIR} (profiles and tuning logs)`);
  } else if (fs.existsSync(STATE_DIR)) {
    say(`  kept your profiles and tuning logs in ${STATE_DIR} (--purge removes them)`);
  }
  say(atStock ? '\nUninstalled. The card is at stock. It never knew.\n' : '\nUninstalled. The card returns to stock at the next restart.\n');
}

/** Every part checked for real: the files, the service answering with a GPU, the server starting and listing its
 * tools, and the agents that point at it. Exit code 1 when anything fails. */
async function verify() {
  say(`\nClocktail ${VERSION} verify\n`);
  let all = true;
  const check = (name, ok, detail) => { all = all && ok; say(`  ${ok ? 'ok  ' : 'FAIL'}  ${name}: ${detail}`); };
  check('files', fs.existsSync(SERVER) && fs.existsSync(BROKER), fs.existsSync(SERVER) ? INSTALL_DIR : 'not installed: run setup');
  // A service just (re)started takes a few seconds to open its pipe: ask again for up to 15 s before calling it down.
  let s = { status: 1, stdout: '' }, gpus = [];
  for (const until = Date.now() + 15000; fs.existsSync(BROKER);) {
    s = run(BROKER, ['status'], { quiet: true });
    try { gpus = JSON.parse(s.stdout).ping.gpus || []; } catch { gpus = []; }
    if ((s.status === 0 && gpus.length > 0) || Date.now() > until) break;
    await new Promise(r => setTimeout(r, 1000));
  }
  check('service', s.status === 0 && gpus.length > 0, s.status === 0
    ? `answering, ${gpus.length} GPU${gpus.length === 1 ? '' : 's'}`
    : 'not answering: run setup again, or ask your agent to call install_service');
  const tools = fs.existsSync(SERVER) ? await probeServer() : null;
  check('MCP server', !!tools, tools ? `starts and lists ${tools.length} tools` : 'did not start');
  const connected = CLIENTS.filter(c => safe(() => c.present()) && safe(() => c.connected()));
  check('agents', connected.length > 0, connected.map(c => c.label).join(', ') || `none connected; point your MCP client at ${SERVER}`);
  say(all ? '\n  All good. An agent that was already running loads Clocktail after a restart.\n' : '');
  if (!all) process.exitCode = 1;
}

/** Start the installed server, ask it for its tools over stdio as an agent would, and stop it. */
function probeServer() {
  return new Promise(resolve => {
    let p;
    try { p = cp.spawn(SERVER, [], { stdio: ['pipe', 'pipe', 'ignore'], windowsHide: true }); } catch { return resolve(null); }
    let buf = '', done = false;
    const finish = v => { if (done) return; done = true; clearTimeout(timer); try { p.kill(); } catch { /* gone */ } resolve(v); };
    const timer = setTimeout(() => finish(null), 20000);
    const send = m => p.stdin.write(JSON.stringify({ jsonrpc: '2.0', ...m }) + '\n');
    p.on('error', () => finish(null));
    p.on('exit', () => finish(null));
    p.stdout.on('data', d => {
      buf += d;
      let i;
      while ((i = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, i); buf = buf.slice(i + 1);
        let m; try { m = JSON.parse(line); } catch { continue; }
        if (m.id === 1) { send({ method: 'notifications/initialized' }); send({ id: 2, method: 'tools/list', params: {} }); }
        if (m.id === 2) finish(m.result && m.result.tools ? m.result.tools.map(t => t.name) : null);
      }
    });
    send({ id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'clocktail-verify', version: VERSION } } });
  });
}

function status() {
  say(`\nClocktail ${VERSION}`);
  say(`  files:   ${fs.existsSync(SERVER) ? INSTALL_DIR : 'not installed'}`);
  const s = fs.existsSync(BROKER) ? run(BROKER, ['status'], { quiet: true }) : null;
  say(`  service: ${s && s.status === 0 ? 'running' : 'not running'}`);
  for (const c of CLIENTS) if (c.present()) say(`  ${c.label}: ${safe(() => c.connected()) ? 'connected' : 'not connected'}`);
  say('');
}

function help() {
  say(`
Clocktail ${VERSION}: agent-assisted overclocking for NVIDIA RTX 50, served over MCP.

  npx -y clocktail setup        install, connect your agents, install the service
      --client a,b              connect these without asking (${CLIENTS.map(c => c.id).join(', ')}, or none)
      --no-service              skip the service (your agent can install it later)
      --yes                     do not ask before making the changes
      --plan                    show what setup would change and change nothing (what an agent shows you first)
  npx -y clocktail verify       check every part for real: files, service, server, agents
  npx -y clocktail status       what is installed and connected
  npx -y clocktail uninstall    card back to stock, service and files removed
      --purge                   also delete profiles and tuning logs
`);
}

// ---- choosing agents ----------------------------------------------------------------------------------------------

async function pickClients(opts) {
  if (opts.client) {
    if (opts.client === 'none') return [];
    return opts.client.split(',').map(id => CLIENTS.find(c => c.id === id.trim()) || fail(`unknown client '${id}'`));
  }
  const found = CLIENTS.filter(c => safe(() => c.present()));
  if (found.length === 0) {
    say('  No agent found on this computer (looked for: ' + CLIENTS.map(c => c.label).join(', ') + ').');
    return [];
  }
  if (!process.stdin.isTTY) return found;
  say('  Agents found on this computer:');
  found.forEach((c, i) => say(`    ${i + 1}. ${c.label}`));
  const answer = (await ask('  Connect which? [Enter = all, or numbers like 1,3, or "none"] ')).trim().toLowerCase();
  if (answer === '') return found;
  if (answer === 'none') return [];
  return answer.split(/[\s,]+/).map(n => found[Number(n) - 1]).filter(Boolean);
}

function ask(q) {
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  return new Promise(res => rl.question(q, a => { rl.close(); res(a); }));
}

// ---- small helpers ------------------------------------------------------------------------------------------------

function checkGpu() {
  const smi = run('nvidia-smi', ['--query-gpu=name', '--format=csv,noheader'], { quiet: true });
  const names = smi.status === 0 ? smi.stdout.trim().split(/\r?\n/).filter(Boolean) : [];
  if (names.length === 0) say('  Note: no NVIDIA GPU answered nvidia-smi. Clocktail needs an RTX 50 card and its driver.');
  else if (!names.some(n => /RTX\s*50\d0/i.test(n))) say(`  Note: found ${names.join(', ')}. Clocktail is built for RTX 50 cards and will refuse writes it cannot verify.`);
  else say(`  GPU: ${names.join(', ')}`);
}

function readText(file) { try { return fs.readFileSync(file, 'utf8'); } catch { return ''; } }

/** The JSON in `file`, or null when there is none. A file that is not plain JSON (comments) is never overwritten. */
function readJson(file, forWrite) {
  const text = readText(file).replace(/^﻿/, '');
  if (text.trim() === '') return null;
  try { return JSON.parse(text); }
  catch {
    if (forWrite) throw new Error(`${file} is not plain JSON (comments?), so it was left alone. Add this server by hand: ${SERVER}`);
    return null;
  }
}

function writeJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  backup(file);
  fs.writeFileSync(file, JSON.stringify(value, null, 2) + '\n');
}

/** What the card runs, from the daily profiles the service keeps (setup itself never writes to the card). */
function cardState() {
  let best = null;
  for (const d of safe(() => fs.readdirSync(STATE_DIR)) || []) {
    const p = safe(() => JSON.parse(fs.readFileSync(path.join(STATE_DIR, d, 'profile.json'), 'utf8')));
    if (p && Array.isArray(p.Writes) && p.Writes.length && (!best || String(p.KeptAt) > String(best.KeptAt))) best = p;
  }
  const kept = best && typeof best.KeptAt === 'string' ? ` (kept ${best.KeptAt.slice(0, 10)})` : '';
  if (best && !best.Suspended) return { profile: best, now: `on your daily profile${kept}`, done: `Your card keeps its daily profile${kept}: setup never touches the card.` };
  if (best) return { profile: best, now: `at stock (your daily profile${kept} is suspended)`, done: `Your card stays at stock; your daily profile${kept} is kept, and load_daily puts it back.` };
  return { profile: null, now: 'at stock', done: 'Your card is still at stock: nothing has been overclocked yet, which is the safest overclock.' };
}

/** The installed server's running copies, each with the program that started it (the agent). */
function runningServers() {
  const ps = "Get-CimInstance Win32_Process -Filter \"Name='clocktail-mcp.exe'\" | ForEach-Object { $p = Get-CimInstance Win32_Process -Filter \"ProcessId=$($_.ParentProcessId)\"; '{0}|{1}|{2}|{3}' -f $_.ProcessId, $_.ExecutablePath, $p.Name, $p.ExecutablePath }";
  const r = run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', ps], { quiet: true });
  if (r.status !== 0) return [];
  const dir = INSTALL_DIR.toLowerCase();
  return serversByAgent(r.stdout.split(/\r?\n/).filter(Boolean).map(l => l.trim().split('|')), dir);
}

/** Rows of pid|exe|parent name|parent path, as the query above prints them: the installed servers an agent started. Since
 * 0.2.0 each agent's server is a relay with a worker under it; the worker (its parent is the server itself) is not an agent. */
function serversByAgent(rows, dir) {
  return rows.filter(([, exe, parent]) => (exe || '').toLowerCase().startsWith(dir) && String(parent || '').toLowerCase() !== 'clocktail-mcp.exe')
    .map(([pid, , parent, parentPath]) => ({ pid: Number(pid), agent: agentName(parent, parentPath) }));
}

const AGENT_NAMES = {
  'opencode.exe': 'OpenCode', 'code.exe': 'VS Code', 'cursor.exe': 'Cursor', 'codex.exe': 'Codex', 'windsurf.exe': 'Windsurf',
  'kiro.exe': 'Kiro', 'zed.exe': 'Zed', 'goose.exe': 'Goose', 'node.exe': 'a Node.js agent (Gemini CLI, Copilot CLI or similar)',
};
/** The agent's name from the program that started a server. */
function agentName(exe, exePath) {
  const e = String(exe || '').toLowerCase();
  if (e === 'claude.exe') return /AnthropicClaude|\\Claude\\app-/i.test(exePath || '') ? 'Claude Desktop' : 'Claude Code';
  return AGENT_NAMES[e] || exe || 'an unknown program';
}
function agentList(servers) {
  const names = [...new Set(servers.map(s => s.agent))];
  return names.length <= 1 ? names.join('') : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
}

/** Old copies an earlier setup moved aside, deleted once no agent runs them. */
function removeMovedAside() {
  for (const f of safe(() => fs.readdirSync(INSTALL_DIR)) || [])
    if (/\.old-\d+$/.test(f)) safe(() => fs.unlinkSync(path.join(INSTALL_DIR, f)));
}

function backup(file) { if (fs.existsSync(file)) fs.copyFileSync(file, file + '.clocktail.bak'); }

/** True when `dst` exists with the same bytes as `src`. Reading is fine while an agent runs the file; writing is not. */
function sameFile(src, dst) {
  try {
    if (!fs.existsSync(dst) || fs.statSync(src).size !== fs.statSync(dst).size) return false;
    const hash = p => require('crypto').createHash('sha256').update(fs.readFileSync(p)).digest('hex');
    return hash(src) === hash(dst);
  } catch { return false; }
}

/** The TOML text without the table `[name]` and its keys (up to the next table header). */
/** The lines of exactly one TOML table, its header first; none when it is not there. */
function tomlTableLines(text, name) {
  const out = [];
  let inside = false;
  for (const line of text.split(/\r?\n/)) {
    const header = line.match(/^\s*\[([^\]]+)\]\s*$/);
    if (header) inside = header[1].trim() === name;
    if (inside) out.push(line);
  }
  return out;
}

/** What of an existing entry the person set themselves and an update keeps: its environment ("environment" in
 * OpenCode), where the CLOCKTAIL_MCP_* caps live. */
function keptEnv(old) {
  if (!old || typeof old !== 'object') return {};
  const keep = {};
  for (const k of ['env', 'environment']) if (old[k] && typeof old[k] === 'object') keep[k] = old[k];
  return keep;
}

function withoutTomlTable(text, name) {
  const out = [];
  let skipping = false;
  for (const line of text.split(/\r?\n/)) {
    const header = line.match(/^\s*\[([^\]]+)\]\s*$/);
    if (header) { const h = header[1].trim(); skipping = h === name || h.startsWith(name + '.'); }
    if (!skipping) out.push(line);
  }
  return out.join('\n');
}

function which(cmd) {
  const r = run('where', [cmd], { quiet: true });
  return r.status === 0 ? r.stdout.split(/\r?\n/)[0].trim() : null;
}

function run(cmd, args, { quiet } = {}) {
  // .cmd shims (claude, npm) need a shell on Windows; quote every argument for it.
  const shell = !/\.exe$/i.test(cmd);
  const q = a => (/[\s"&|<>^]/.test(a) ? `"${a.replace(/"/g, '\\"')}"` : a);
  const o = { encoding: 'utf8', stdio: quiet ? 'pipe' : 'inherit', windowsHide: true };
  const r = shell ? cp.spawnSync([cmd, ...args].map(q).join(' '), { ...o, shell: true }) : cp.spawnSync(cmd, args, o);
  return { status: r.error ? -1 : r.status, stdout: r.stdout || '', stderr: r.stderr || (r.error ? r.error.message : '') };
}

function safe(f) { try { return f(); } catch { return false; } }
function say(s) { process.stdout.write(s + '\n'); }
function fail(msg) { say(`\n  ${msg}\n`); process.exit(1); }

// ---- entry --------------------------------------------------------------------------------------------------------

// Loaded by the tests as a module: hand them the agents and run nothing.
if (require.main !== module) { module.exports = { CLIENTS, SERVER, withoutTomlTable, sameFile, agentName, agentList, serversByAgent }; return; }

const argv = process.argv.slice(2);
const opts = {
  client: (() => { const i = argv.indexOf('--client'); return i >= 0 ? argv[i + 1] : null; })(),
  noService: argv.includes('--no-service'),
  purge: argv.includes('--purge'),
  yes: argv.includes('--yes') || argv.includes('-y'),
  plan: argv.includes('--plan'),
};
const cmd = argv.find(a => !a.startsWith('-') && a !== opts.client) || 'help';
Promise.resolve().then(() => ({ setup, uninstall, status, verify, help }[cmd] || help)(opts)).catch(e => fail(e.message));
