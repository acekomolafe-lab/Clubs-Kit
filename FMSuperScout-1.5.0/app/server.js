// FMSuperScout — lokale server (geen dependencies nodig)
// Serveert de web-app en de meest recente data-dump van de BepInEx-plugin.
'use strict';

const http = require('http');
const https = require('https');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const os = require('os');
const { spawn, execFile } = require('child_process');

const PORT = Number(process.env.PORT) || 8765;
const APP_DIR = __dirname;
// De plugin schrijft dumps hierheen. Zelfde bron als de plugin (%LOCALAPPDATA%): bij een
// verplaatst/redirected profiel wijkt homedir+AppData af en keken app en plugin naar
// verschillende mappen ("F9 werkt, app blijft leeg").
const DATA_DIR = path.join(
  process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'FMSuperScout');
const DEFAULT_SPONSOR_CONFIG_PATH = 'D:\\KitbasherLegacy v0.8\\Sponsors\\sponsorConfig.json';
const DEFAULT_TEMPLATE_CONFIG_PATH = 'D:\\KitbasherLegacy v0.8\\Templates\\KitTemplates\\templateConfig.json';

// App-modus (standalone venster): server sluit zichzelf af zodra het venster dicht is.
// Het sluiten van het venster wordt betrouwbaar gemeld via /api/bye (pagehide-beacon).
// De heartbeat is alleen een vangnet als dat beacon niet aankwam. De drempel staat ruim
// (90s): browsers knijpen timers in een achtergrondvenster af tot ~1×/min, en een te
// strakke drempel liet de server dan tijdens het spelen sneuvelen → "Nieuwe data" gaf
// daarna een fout omdat de server weg was. Elke API-aanvraag telt ook als teken van leven.
const APP_MODE = process.env.FMSS_APP === '1';
let lastBeat = Date.now();
let pendingExit = null;
if (APP_MODE) {
  setInterval(() => {
    if (Date.now() - lastBeat > 90000) { console.log('Venster gesloten (geen heartbeat), server stopt.'); process.exit(0); }
  }, 5000);
}

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
};

// ---------- zelf-update ----------
// POST /api/update-install downloadt de nieuwste Setup.exe uit onze GitHub-release,
// verifieert de SHA-256 tegen het .sha256-asset uit dezelfde release en start dan de
// installer. Daarna stopt de server zichzelf, zodat de installer node.exe en de
// app-bestanden kan vervangen. GET /api/update-status geeft de voortgang aan de pagina.
let updState = { phase: 'idle' };

function httpsGet(url, redirects = 5) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, { headers: { 'User-Agent': 'FMSuperScout-updater' }, timeout: 30000 }, res => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirects > 0) {
        res.resume();
        resolve(httpsGet(res.headers.location, redirects - 1));
      } else if (res.statusCode !== 200) {
        res.resume(); reject(new Error('HTTP ' + res.statusCode));
      } else resolve(res);
    }).on('error', reject);
    // Zonder time-out blijft de update-pill eeuwig op "downloaden" hangen bij een
    // stille verbinding; nu valt hij netjes terug op de releasepagina-link.
    req.on('timeout', () => req.destroy(new Error('netwerk-time-out')));
  });
}
async function readAll(res) { const chunks = []; for await (const c of res) chunks.push(c); return Buffer.concat(chunks); }

async function runUpdate() {
  updState = { phase: 'downloading', pct: 0 };
  const rel = JSON.parse((await readAll(await httpsGet('https://api.github.com/repos/mavarobli/FMSuperScout/releases/latest'))).toString('utf8'));
  const exeAsset = (rel.assets || []).find(a => a.name === 'FMSuperScout-Setup.exe');
  const shaAsset = (rel.assets || []).find(a => a.name === 'FMSuperScout-Setup.exe.sha256');
  // Zonder hash-asset niet installeren: de hash-check is de enige integriteitscontrole.
  if (!exeAsset || !shaAsset) throw new Error('Release-assets onvolledig (exe of .sha256 ontbreekt)');

  const dir = path.join(DATA_DIR, 'update');
  fs.mkdirSync(dir, { recursive: true });
  const exePath = path.join(dir, 'FMSuperScout-Setup.exe');
  const res = await httpsGet(exeAsset.browser_download_url);
  const total = Number(res.headers['content-length']) || exeAsset.size || 0;
  const hash = crypto.createHash('sha256');
  let got = 0;
  const out = fs.createWriteStream(exePath);
  await new Promise((resolve, reject) => {
    res.on('data', c => {
      got += c.length; hash.update(c);
      if (total) updState = { phase: 'downloading', pct: Math.min(99, Math.round(100 * got / total)) };
    });
    res.pipe(out);
    out.on('finish', resolve);
    res.on('error', reject); out.on('error', reject);
  });

  updState = { phase: 'verifying' };
  const expect = (await readAll(await httpsGet(shaAsset.browser_download_url))).toString('utf8').trim().split(/\s+/)[0].toLowerCase();
  if (hash.digest('hex') !== expect) {
    try { fs.unlinkSync(exePath); } catch { }
    throw new Error('SHA-256 komt niet overeen, download verwijderd');
  }

  updState = { phase: 'launching' };
  // 'spawn'-event afwachten: pas als Windows het proces echt gestart heeft mag de
  // server zichzelf afsluiten. Een spawn-fout (SmartScreen-block, ontbrekend bestand)
  // wordt zo een nette foutstatus in plaats van een app die zomaar verdwijnt.
  await new Promise((resolve, reject) => {
    const child = spawn(exePath, [], { detached: true, stdio: 'ignore' });
    child.once('spawn', () => { child.unref(); resolve(); });
    child.once('error', reject);
  });
  // Korte adempauze zodat de pagina de eindstatus nog kan ophalen, dan vrij baan
  // voor de installer (die node.exe moet kunnen vervangen).
  setTimeout(() => { console.log('Installer gestart, server stopt voor de update.'); process.exit(0); }, 1500);
}

// ---------- ontwikkel-historie (trends) ----------
// De app stuurt na elke geladen dump een compacte momentopname (uid → [ca, pa, waarde]).
// Opslag is delta-only: het eerste punt per speler is de basislijn; daarna komt er alleen
// een punt bij als CA of PA veranderd is, of de waarde meer dan 2,5% verschoven is (FM
// herberekent waardes voortdurend een beetje — dat geruis slaan we bewust niet op).
// Een oudere in-game datum dan de laatste (save teruggeladen) gooit de "toekomst" weg.
// Bestand per manager (carrière) in DATA_DIR\history; datums yyyy-mm-dd sorteren als tekst.
const HIST_DIR = path.join(DATA_DIR, 'history');
const HIST_MAX_DATES = 120;   // ~2+ seizoenen wekelijks dumpen; daarboven oudste samenvouwen
let histCache = null, histCacheFile = null;

function histSlug(manager) {
  const s = String(manager || 'default').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return (s || 'default').slice(0, 60);
}
function histFile(manager) { return path.join(HIST_DIR, histSlug(manager) + '.json'); }
function loadHist(manager) {
  const file = histFile(manager);
  if (histCacheFile === file && histCache) return histCache;
  let h = { manager: String(manager || 'default'), dates: [], players: {} };
  try { h = JSON.parse(fs.readFileSync(file, 'utf8')); } catch { /* nog geen historie */ }
  histCache = h; histCacheFile = file;
  return h;
}
// Laatst bekende [ca,pa,waarde] van een speler, in datumvolgorde.
function histLastPoint(entries, dates) {
  for (let i = dates.length - 1; i >= 0; i--) { const e = entries[dates[i]]; if (e) return e; }
  return null;
}
function mergeSnapshot(manager, gameDate, players) {
  const h = loadHist(manager);

  // Rewind of her-dump op dezelfde datum: alle punten vanaf die datum vervallen.
  if (h.dates.length && h.dates[h.dates.length - 1] >= gameDate) {
    const drop = new Set(h.dates.filter(d => d >= gameDate));
    h.dates = h.dates.filter(d => d < gameDate);
    for (const uid of Object.keys(h.players)) {
      const e = h.players[uid];
      for (const d of drop) delete e[d];
      if (!Object.keys(e).length) delete h.players[uid];
    }
  }

  let added = 0;
  for (const uid in players) {
    const [ca, pa, val] = players[uid];
    const entries = h.players[uid];
    const prev = entries ? histLastPoint(entries, h.dates) : null;
    const valJump = prev ? Math.abs((val ?? 0) - (prev[2] ?? 0)) > Math.max(10000, (prev[2] ?? 0) * 0.025) : true;
    if (prev && prev[0] === ca && prev[1] === pa && !valJump) continue;
    (h.players[uid] || (h.players[uid] = {}))[gameDate] = [ca, pa, val];
    added++;
  }
  h.dates.push(gameDate);

  // Snoeien: oudste datums samenvouwen tot een nieuwe basislijn zodat het bestand
  // begrensd blijft. Per speler blijft zijn laatst bekende stand vóór de zaaglijn
  // bewaard als basispunt, dus geen reeks raakt zijn startwaarde kwijt.
  if (h.dates.length > HIST_MAX_DATES) {
    const cut = h.dates.length - HIST_MAX_DATES;
    const dropped = h.dates.slice(0, cut), newBase = h.dates[cut];
    for (const uid of Object.keys(h.players)) {
      const e = h.players[uid];
      let base = null;
      for (const d of dropped) if (e[d]) { base = e[d]; delete e[d]; }
      if (base && !e[newBase]) e[newBase] = base;
      if (!Object.keys(e).length) delete h.players[uid];
    }
    h.dates = h.dates.slice(cut);
  }

  // Atomair wegschrijven: eerst .tmp, dan hernoemen — een crash halverwege laat de
  // bestaande historie intact in plaats van een half JSON-bestand achter.
  fs.mkdirSync(HIST_DIR, { recursive: true });
  const file = histFile(manager);
  fs.writeFileSync(file + '.tmp', JSON.stringify(h));
  fs.renameSync(file + '.tmp', file);
  histCache = h; histCacheFile = file;
  return { ok: true, dates: h.dates.length, added };
}

// Request-body inlezen (JSON), zonder restrictieve limiet (tot 1GB)
function readBody(req, limit = 1024 * 1024 * 1024) {
  return new Promise((resolve, reject) => {
    const chunks = []; let size = 0;
    req.on('data', c => { size += c.length; chunks.push(c); });
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

function latestDump() {
  try {
    const files = fs.readdirSync(DATA_DIR)
      .filter(f => f.startsWith('dump') && f.endsWith('.json'))
      .map(f => {
        const full = path.join(DATA_DIR, f);
        const st = fs.statSync(full);
        return { full, mtime: st.mtimeMs, size: st.size };
      })
      .sort((a, b) => b.mtime - a.mtime);
    return files[0] || null;
  } catch {
    return null;
  }
}

// Extraheert club/div/clubRep per speler zonder de hele dump (honderden MB's) in het
// geheugen te JSON.parse-n. We streamen het bestand chunk-voor-chunk, zoeken de
// "players":[ array en tokenizen alleen de top-level objecten daarin (met correcte
// string/escape-tracking) om alleen "club", "div" en "clubRep" eruit te lezen.
function extractClubs(fullPath) {
  return new Promise((resolve, reject) => {
    const byClub = new Map();
    const rs = fs.createReadStream(fullPath, { encoding: 'utf8', highWaterMark: 1 << 20 });
    let buf = '';
    let stage = 'seek-array'; // seek-array -> in-array -> done
    let depth = 0;
    let objStart = -1;   // index in buf waar het huidige object begint (-1 = geen open object)
    let scanned = 0;      // hoever we al gescand hebben in buf (nooit terug omlaag, behalve na trim)
    let inStr = false, esc = false;

    const FIELD_RE = /"club"\s*:\s*"((?:[^"\\]|\\.)*)"|"div"\s*:\s*"((?:[^"\\]|\\.)*)"|"clubRep"\s*:\s*(-?\d+)|"nat"\s*:\s*\["((?:[^"\\]|\\.)*)"|"teamType"\s*:\s*(\d+)/g;
    function unesc(s) { try { return JSON.parse('"' + s + '"'); } catch { return s; } }
    function handleObject(objText) {
      let club = null, div = '', rep = 0, nat = '', tt = -1;
      FIELD_RE.lastIndex = 0;
      let m;
      while ((m = FIELD_RE.exec(objText))) {
        if (m[1] !== undefined) club = unesc(m[1]);
        else if (m[2] !== undefined) div = unesc(m[2]);
        else if (m[3] !== undefined) rep = parseInt(m[3], 10) || 0;
        else if (m[4] !== undefined) nat = unesc(m[4]);
        else if (m[5] !== undefined) tt = parseInt(m[5], 10);
      }
      if (!club) return;
      let rec = byClub.get(club);
      if (!rec) { rec = { club, division: '', country: nat || '', rep: 0, players: 0, _nats: {}, _senior: false }; byClub.set(club, rec); }
      rec.players++;
      if (tt === 0 && div) {
        rec.division = div;
        rec._senior = true;
      } else if (!rec._senior && div && !rec.division) {
        rec.division = div;
      }
      if (nat) rec._nats[nat] = (rec._nats[nat] || 0) + 1;
      if (rep > rec.rep) rec.rep = rep;
    }

    rs.on('data', chunk => {
      if (stage === 'done') return;
      buf += chunk;
      if (stage === 'seek-array') {
        const idx = buf.indexOf('"players":[');
        if (idx === -1) {
          if (buf.length > 32) buf = buf.slice(buf.length - 32);
          return;
        }
        buf = buf.slice(idx + '"players":['.length);
        stage = 'in-array';
        scanned = 0;
      }
      if (stage !== 'in-array') return;

      let i = scanned;
      const n = buf.length;
      while (i < n) {
        const c = buf[i];
        if (objStart === -1) {
          if (c === ']') { stage = 'done'; rs.destroy(); i = n; break; }
          if (c === '{') { objStart = i; depth = 1; }
          i++;
          continue;
        }
        if (inStr) {
          if (esc) esc = false;
          else if (c === '\\') esc = true;
          else if (c === '"') inStr = false;
          i++;
          continue;
        }
        if (c === '"') { inStr = true; i++; continue; }
        if (c === '{') depth++;
        else if (c === '}') {
          depth--;
          if (depth === 0) {
            handleObject(buf.slice(objStart, i + 1));
            objStart = -1;
          }
        }
        i++;
      }
      scanned = i;
      if (stage === 'in-array') {
        const keepFrom = objStart === -1 ? scanned : objStart;
        if (keepFrom > 0) {
          buf = buf.slice(keepFrom);
          if (objStart !== -1) objStart -= keepFrom;
          scanned -= keepFrom;
        }
      }
    });
    const finish = () => {
      const colorsMap = new Map();
      const normMap = new Map();
      const norm = s => String(s || '').toLowerCase().replace(/\b(f\.?c\.?|c\.?f\.?|s\.?c\.?|a\.?f\.?c\.?)\b/gi, '').replace(/[^a-z0-9]/g, '');
      try {
        const clubsFile = path.join(DATA_DIR, 'clubs.json');
        const parsed = JSON.parse(fs.readFileSync(clubsFile, 'utf8'));
        const list = Array.isArray(parsed) ? parsed : (Array.isArray(parsed?.clubs) ? parsed.clubs : []);
        for (const item of list) {
          if (item && item.name) {
            colorsMap.set(item.name, item);
            const n = norm(item.name);
            if (n && !normMap.has(n)) normMap.set(n, item);
          }
        }
        if (!Array.isArray(parsed) && !Array.isArray(parsed?.clubs) && typeof parsed === 'object') {
          for (const [k, v] of Object.entries(parsed)) {
            if (v && typeof v === 'object') {
              colorsMap.set(k, v);
              const n = norm(k);
              if (n && !normMap.has(n)) normMap.set(n, v);
            }
          }
        }
      } catch (e) {}
      const arr = [...byClub.values()];
      for (const c of arr) {
        if (!c.country && c._nats) {
          const topNat = Object.entries(c._nats).sort((a,b) => b[1] - a[1])[0]?.[0];
          if (topNat) c.country = topNat;
        }
        delete c._nats;
        let badgeCache = {};
        try {
          const bcFile = path.join(DATA_DIR, 'badge_cache.json');
          if (fs.existsSync(bcFile)) badgeCache = JSON.parse(fs.readFileSync(bcFile, 'utf8'));
        } catch(e) {}
        const rc = colorsMap.get(c.club) || normMap.get(norm(c.club));
        if (rc) {
          if (rc.id) c.clubId = rc.id;
          if (rc.bgColor || rc.background) c.bgColor = rc.bgColor || rc.background;
          if (rc.fgColor || rc.foreground) c.fgColor = rc.fgColor || rc.foreground;
          if (rc.k1OutColor || rc.outlineColor || rc.outline) c.outlineColor = rc.k1OutColor || rc.outlineColor || rc.outline;
          if (rc.k1BgColor) c.k1BgColor = rc.k1BgColor;
          if (rc.k1FgColor) c.k1FgColor = rc.k1FgColor;
          if (rc.k1OutColor) c.k1OutColor = rc.k1OutColor;
          if (rc.k1Style) c.k1Style = rc.k1Style;
          if (rc.k2BgColor) c.k2BgColor = rc.k2BgColor;
          if (rc.k2FgColor) c.k2FgColor = rc.k2FgColor;
          if (rc.k2OutColor) c.k2OutColor = rc.k2OutColor;
          if (rc.k2Style) c.k2Style = rc.k2Style;
          if (rc.k3BgColor) c.k3BgColor = rc.k3BgColor;
          if (rc.k3FgColor) c.k3FgColor = rc.k3FgColor;
          if (rc.k3OutColor) c.k3OutColor = rc.k3OutColor;
          if (rc.k3Style) c.k3Style = rc.k3Style;
          if (rc.country || rc.nation) c.country = rc.country || rc.nation;
          if (rc.division && !c.division) c.division = rc.division;
          if (rc.badgePath) c.badgePath = rc.badgePath;
          if (rc.shortName) c.shortName = rc.shortName;
          if (rc.initials) c.initials = rc.initials;
        }
        if (!c.badgePath) {
          const id = String(c.clubId || '');
          if (id && badgeCache[id]) {
            c.badgePath = badgeCache[id];
          } else {
            const clean = (c.club || '').replace(/[\/\\:\*\?"<>\|]/g, '').trim().replace(/\s+/g, '_');
            c.badgePath = `C:\\Users\\aceik\\AppData\\Roaming\\GeneratedKits\\Badges\\${clean} Logo.png`;
          }
        }
      }
      return arr;
    };
    rs.on('close', () => resolve(finish()));
    rs.on('end', () => resolve(finish()));
    rs.on('error', reject);
  });
}

// Installatiestaat van de mod-laag.
let setupCache = null;
function checkSetup(cb) {
  if (setupCache && Date.now() - setupCache.at < 5000) return cb(setupCache.val);
  const done = gameDir => {
    const at = p => { try { return fs.readdirSync(p).length; } catch { return -1; } };
    const val = gameDir
      ? {
        known: true, gameDir,
        pluginInstalled: fs.existsSync(path.join(gameDir, 'BepInEx', 'plugins', 'FMSuperScout.dll')),
        bepinex: fs.existsSync(path.join(gameDir, 'BepInEx')),
        interopReady: at(path.join(gameDir, 'BepInEx', 'interop')) > 0,
      }
      : { known: false };
    setupCache = { at: Date.now(), val };
    cb(val);
  };
  execFile('reg', ['query', 'HKLM\\Software\\FMSuperScout', '/v', 'GamePath'],
    { windowsHide: true }, (err, out) => {
      const m = !err && /GamePath\s+REG_SZ\s+(.+)/i.exec(out || '');
      done(m ? m[1].trim() : null);
    });
}

const server = http.createServer(async (req, res) => {
  const host = String(req.headers.host || '').toLowerCase();
  if (!/^(localhost|127\.0\.0\.1)(:\d+)?$/.test(host)) {
    res.writeHead(403); res.end('Forbidden');
    return;
  }
  const origin = String(req.headers.origin || '').toLowerCase();
  if (origin && !/^https?:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(origin)) {
    res.writeHead(403); res.end('Forbidden');
    return;
  }
  const url = new URL(req.url, `http://localhost:${PORT}`);
  if (url.pathname !== '/api/bye') lastBeat = Date.now();

  if (url.pathname === '/api/status') {
    const dump = latestDump();
    let plugin = null;
    try {
      const sf = path.join(DATA_DIR, 'status.json');
      if (fs.existsSync(sf)) {
        plugin = JSON.parse(fs.readFileSync(sf, 'utf8'));
        plugin.mtime = fs.statSync(sf).mtimeMs;
      }
    } catch { /* geen status */ }
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({
      dataDir: DATA_DIR,
      hasDump: !!dump,
      dumpFile: dump ? path.basename(dump.full) : null,
      dumpTime: dump ? dump.mtime : null,
      dumpSize: dump ? dump.size : null,
      appMode: APP_MODE,
      plugin,
    }));
    return;
  }

  if (url.pathname === '/api/scan-config') {
    const cf = path.join(DATA_DIR, 'scan-config.json');
    if (req.method === 'POST') {
      let body = '';
      req.on('data', c => { body += c; if (body.length > 1024) req.destroy(); });
      req.on('end', () => {
        try {
          const inp = JSON.parse(body || '{}');
          const db = ['men', 'women', 'both'].includes(inp.db) ? inp.db : 'men';
          fs.writeFileSync(cf, JSON.stringify({ db }));
          res.writeHead(200, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ db }));
        } catch { res.writeHead(400); res.end(); }
      });
      return;
    }
    let db = 'men';
    try {
      const raw = fs.readFileSync(cf, 'utf8');
      const m = raw.match(/"db":"(men|women|both)"/);
      db = m ? m[1] : (raw.includes('"includeWomen":true') ? 'both' : 'men');
    } catch { }
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ db }));
    return;
  }

  if (url.pathname === '/api/sponsor-groupings') {
    const configPath = url.searchParams.get('path') || DEFAULT_SPONSOR_CONFIG_PATH;
    if (req.method === 'POST') {
      let body = '';
      req.on('data', chunk => { body += chunk; if (body.length > 1024 * 1024) req.destroy(); });
      req.on('end', () => {
        try {
          const input = JSON.parse(body || '{}');
          const targetPath = input.path || configPath;
          const names = Array.isArray(input.names) ? input.names : [];
          const assignments = input.assignments && typeof input.assignments === 'object' && !Array.isArray(input.assignments)
            ? input.assignments
            : null;
          const config = JSON.parse(fs.readFileSync(targetPath, 'utf8'));
          if (!Array.isArray(config.SponsorGroupings)) throw new Error('SponsorGroupings array is missing');
          const existing = new Set(config.SponsorGroupings
            .filter(group => group && typeof group.Name === 'string')
            .map(group => group.Name.trim().toLowerCase()));
          const existingIds = new Set(config.SponsorGroupings
            .filter(group => group && group.Id != null)
            .map(group => String(group.Id).trim().toLowerCase()));
          let added = 0;
          const namesToSave = assignments
            ? names.concat(Object.values(assignments))
            : names;
          for (const rawName of namesToSave) {
            const name = String(rawName || '').trim();
            const key = name.toLowerCase();
            if (!name || key === 'all' || key === 'none' || existing.has(key) || existingIds.has(key)) continue;
            config.SponsorGroupings.push({
              Id: `fmss-${Date.now()}-${config.SponsorGroupings.length}`,
              Name: name,
              Subsponsors: [],
              CompoundName: `${name} (0 items)`
            });
            existing.add(key);
            existingIds.add(String(config.SponsorGroupings[config.SponsorGroupings.length - 1].Id).toLowerCase());
            added++;
          }
          let assignmentsChanged = false;
          if (assignments) {
            const savedAssignments = config.TeamAssignments && typeof config.TeamAssignments === 'object' && !Array.isArray(config.TeamAssignments)
              ? { ...config.TeamAssignments }
              : {};
            const byId = new Map(config.SponsorGroupings
              .filter(group => group && group.Id != null)
              .map(group => [String(group.Id).trim().toLowerCase(), String(group.Id).trim()]));
            const byName = new Map(config.SponsorGroupings
              .filter(group => group && typeof group.Name === 'string')
              .map(group => [group.Name.trim().toLowerCase(), String(group.Id || '').trim()]));

            for (const [rawTeamId, rawGrouping] of Object.entries(assignments)) {
              const teamId = String(rawTeamId || '').trim();
              const grouping = String(rawGrouping || '').trim();
              if (!teamId) continue;

              if (!grouping || grouping.toLowerCase() === 'all') {
                if (Object.prototype.hasOwnProperty.call(savedAssignments, teamId)) {
                  delete savedAssignments[teamId];
                  assignmentsChanged = true;
                }
                continue;
              }

              const groupingId = byId.get(grouping.toLowerCase()) || byName.get(grouping.toLowerCase());
              if (!groupingId) throw new Error(`Sponsor grouping "${grouping}" was not found`);
              if (savedAssignments[teamId] !== groupingId) {
                savedAssignments[teamId] = groupingId;
                assignmentsChanged = true;
              }
            }

            if (assignmentsChanged || !config.TeamAssignments) {
              config.TeamAssignments = savedAssignments;
              assignmentsChanged = true;
            }
          }
          if (added || assignmentsChanged) fs.writeFileSync(targetPath, JSON.stringify(config, null, 2) + '\n');
          res.writeHead(200, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ added, assignments: config.TeamAssignments || {} }));
        } catch (e) {
          res.writeHead(500, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ error: `Failed to write sponsor config ${targetPath || configPath}: ${String(e.message || e)}` }));
        }
      });
      return;
    }
    try {
      const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
      if (!Array.isArray(config.SponsorGroupings)) {
        throw new Error('SponsorGroupings array is missing');
      }
      const groupings = config.SponsorGroupings
        .filter(group => group && typeof group.Name === 'string' && group.Name.trim())
        .map(group => ({ id: String(group.Id || ''), name: group.Name }));
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify(groupings));
    } catch (e) {
      res.writeHead(500, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: `Failed to read sponsor config ${configPath}: ${String(e.message || e)}` }));
    }
    return;
  }

  if (url.pathname === '/api/template-groupings') {
    const configPath = url.searchParams.get('path') || DEFAULT_TEMPLATE_CONFIG_PATH;
    if (req.method === 'POST') {
      let body = '';
      req.on('data', chunk => { body += chunk; });
      req.on('end', () => {
        let targetPath = configPath;
        try {
          const parsed = JSON.parse(body || '{}');
          if (parsed.path && typeof parsed.path === 'string' && parsed.path.trim()) {
            targetPath = parsed.path.trim();
          }
          if (!fs.existsSync(targetPath)) {
            res.writeHead(404, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ error: `Template config not found at: ${targetPath}` }));
            return;
          }
          const raw = fs.readFileSync(targetPath, 'utf8');
          const config = JSON.parse(raw);
          if (!Array.isArray(config.TemplateGroupings)) {
            config.TemplateGroupings = [];
          }
          const existing = new Set(
            config.TemplateGroupings
              .filter(g => g && (g.Name || g.Id))
              .map(g => String(g.Name || g.Id || '').trim().toLowerCase())
          );
          let added = 0;
          const incomingBrands = Array.isArray(parsed.brands) ? parsed.brands : [];
          for (const brand of incomingBrands) {
            const trimmed = String(brand || '').trim();
            if (!trimmed || trimmed.toLowerCase() === 'all' || trimmed.toLowerCase() === 'none' || existing.has(trimmed.toLowerCase())) {
              continue;
            }
            config.TemplateGroupings.push({
              Id: trimmed,
              Name: trimmed,
              Brands: [trimmed]
            });
            existing.add(trimmed.toLowerCase());
            added += 1;
          }
          if (added > 0) {
            fs.writeFileSync(targetPath, JSON.stringify(config, null, 2) + '\n');
          }



          res.writeHead(200, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ added }));
        } catch (e) {
          res.writeHead(500, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ error: `Failed to write template config ${targetPath || configPath}: ${String(e.message || e)}` }));
        }
      });
      return;
    }
    try {
      if (!fs.existsSync(configPath)) {
        res.writeHead(404, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: `Template config not found at: ${configPath}` }));
        return;
      }
      const raw = fs.readFileSync(configPath, 'utf8');
      const parsed = JSON.parse(raw);
      const groupings = (parsed.TemplateGroupings || []).map(group => ({
        id: String(group.Id || group.Name || ''),
        name: group.Name || '',
        brands: Array.isArray(group.Brands) ? group.Brands : []
      }));
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify(groupings));
    } catch (e) {
      res.writeHead(500, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: `Failed to read template config ${configPath}: ${String(e.message || e)}` }));
    }
    return;
  }

  if (url.pathname === '/api/sponsor-assignments') {
    const configPath = url.searchParams.get('path') || DEFAULT_SPONSOR_CONFIG_PATH;
    try {
      const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
      const assignments = config.TeamAssignments && typeof config.TeamAssignments === 'object' && !Array.isArray(config.TeamAssignments)
        ? config.TeamAssignments
        : {};
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify(assignments));
    } catch (e) {
      res.writeHead(500, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: `Failed to read sponsor assignments ${configPath}: ${String(e.message || e)}` }));
    }
    return;
  }

  if (url.pathname === '/api/version-check') {
    (async () => {
      try {
        const buf = await readAll(await httpsGet('https://github.com/mavarobli/FMSuperScout/releases/latest/download/version.json'));
        const tag = String(JSON.parse(buf.toString('utf8')).tag || '');
        if (!/^v\d/.test(tag)) throw new Error('geen tag in version.json');
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ tag }));
      } catch { res.writeHead(404); res.end(); }
    })();
    return;
  }

  if (url.pathname === '/api/diagnostics') {
    try {
      const txt = fs.readFileSync(path.join(DATA_DIR, 'diagnostics.txt'), 'utf8');
      res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end(txt);
    } catch { res.writeHead(404); res.end(); }
    return;
  }

  if (url.pathname === '/api/heartbeat') {
    lastBeat = Date.now();
    if (pendingExit) { clearTimeout(pendingExit); pendingExit = null; }
    res.writeHead(204); res.end();
    return;
  }

  if (url.pathname === '/api/bye' && req.method === 'POST') {
    res.writeHead(204); res.end();
    if (APP_MODE && !pendingExit) {
      pendingExit = setTimeout(() => {
        if (Date.now() - lastBeat > 4500) process.exit(0);
        else pendingExit = null;
      }, 3000);
    }
    return;
  }

  if (url.pathname === '/api/fmstatus') {
    execFile('tasklist', ['/FI', 'IMAGENAME eq fm.exe', '/NH'], { windowsHide: true }, (err, out) => {
      const running = !err && /fm\.exe/i.test(out || '');
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ running }));
    });
    return;
  }

  if (url.pathname === '/api/setup') {
    checkSetup(s => {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(s));
    });
    return;
  }

  if (url.pathname === '/api/refresh' && req.method === 'POST') {
    try {
      fs.mkdirSync(DATA_DIR, { recursive: true });
      fs.writeFileSync(path.join(DATA_DIR, 'request.flag'), String(Date.now()));
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ok: true }));
    } catch (e) {
      res.writeHead(500, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ok: false, error: String(e) }));
    }
    return;
  }

  if (url.pathname === '/api/history' && req.method === 'POST') {
    readBody(req).then(buf => {
      const { manager, gameDate, players } = JSON.parse(buf.toString('utf8'));
      if (!gameDate || !players) throw new Error('gameDate/players ontbreekt');
      const r = mergeSnapshot(manager, String(gameDate), players);
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(r));
    }).catch(e => {
      res.writeHead(400, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ok: false, error: String(e.message || e) }));
    });
    return;
  }

  if (url.pathname === '/api/history/deltas') {
    const h = loadHist(url.searchParams.get('manager'));
    const since = String(url.searchParams.get('since') || '');
    const idx = new Map(h.dates.map((d, i) => [d, i]));
    const p = {};
    if (!since) {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ dates: h.dates, ref: null, p }));
      return;
    }
    for (const uid in h.players) {
      const e = h.players[uid];
      const keys = Object.keys(e).sort();
      if (!keys.length) continue;
      let ref = null;
      for (const k of keys) { if (k > since) break; ref = e[k]; }
      p[uid] = ref ? [ref[0], ref[1], idx.get(keys[0]) ?? 0] : [null, null, idx.get(keys[0]) ?? 0];
    }
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ dates: h.dates, ref: since, p }));
    return;
  }

  if (url.pathname === '/api/history/series') {
    const h = loadHist(url.searchParams.get('manager'));
    const uid = String(url.searchParams.get('uid') || '');
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ dates: h.dates, entries: h.players[uid] || {} }));
    return;
  }

  if (url.pathname === '/api/update-install' && req.method === 'POST') {
    if (updState.phase === 'idle' || updState.phase === 'error')
      runUpdate().catch(e => { updState = { phase: 'error', error: String(e.message || e) }; });
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: true }));
    return;
  }

  if (url.pathname === '/api/update-status') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(updState));
    return;
  }

  if (url.pathname === '/api/clubs') {
    try {
      const clubsFile = path.join(DATA_DIR, 'clubs.json');
      if (fs.existsSync(clubsFile)) {
        const raw = fs.readFileSync(clubsFile, 'utf8');
        const parsed = JSON.parse(raw);
        const list = Array.isArray(parsed) ? parsed : (Array.isArray(parsed?.clubs) ? parsed.clubs : []);
        const hasDivisions = list.some(c => c.division && c.division.trim() !== '');
        if (hasDivisions) {
          let badgeCache = {};
          try {
            const bcFile = path.join(DATA_DIR, 'badge_cache.json');
            if (fs.existsSync(bcFile)) badgeCache = JSON.parse(fs.readFileSync(bcFile, 'utf8'));
          } catch(e) {}
          const formatted = list.map(rc => {
            const id = String(rc.id || rc.clubId || '');
            let bp = rc.badgePath || '';
            if (!bp) {
              if (id && badgeCache[id]) bp = badgeCache[id];
              else {
                const clean = (rc.name || rc.club || '').replace(/[\/\\:\*\?"<>\|]/g, '').trim().replace(/\s+/g, '_');
                bp = `C:\\Users\\aceik\\AppData\\Roaming\\GeneratedKits\\Badges\\${clean} Logo.png`;
              }
            }
            return {
              club: rc.name || rc.club || '',
              clubId: id,
              division: rc.division || '',
              country: rc.country || rc.nation || '',
              bgColor: rc.k1BgColor || rc.bgColor || rc.background || '',
              fgColor: rc.k1FgColor || rc.fgColor || rc.foreground || '',
              outlineColor: rc.k1OutColor || rc.outlineColor || rc.outline || '',
              k1BgColor: rc.k1BgColor || rc.bgColor || '',
              k1FgColor: rc.k1FgColor || rc.fgColor || '',
              k1OutColor: rc.k1OutColor || rc.outlineColor || '',
              k1Style: rc.k1Style || 0,
              k2BgColor: rc.k2BgColor || '',
              k2FgColor: rc.k2FgColor || '',
              k2OutColor: rc.k2OutColor || '',
              k2Style: rc.k2Style || 0,
              k3BgColor: rc.k3BgColor || '',
              k3FgColor: rc.k3FgColor || '',
              k3OutColor: rc.k3OutColor || '',
              k3Style: rc.k3Style || 0,
              badgePath: bp,
              shortName: rc.shortName || '',
              initials: rc.initials || '',
            };
          }).filter(c => c.club);
          res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
          res.end(JSON.stringify({ clubs: formatted }));
          return;
        }
      }
      const dump = latestDump();
      if (!dump) {
        res.writeHead(404, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'No clubs database found yet. Press F9 in Football Manager to dump first.' }));
        return;
      }
      const clubs = await extractClubs(dump.full);
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ clubs }));
    } catch (e) {
      res.writeHead(500, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: String((e && e.message) || e) }));
    }
    return;
  }

  if (url.pathname === '/api/dump') {
    const dump = latestDump();
    if (!dump) {
      res.writeHead(404, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: 'No dump found yet. Press the dump hotkey (F9) in FM26.' }));
      return;
    }
    res.writeHead(200, {
      'Content-Type': 'application/json',
      'Content-Length': dump.size,
      'Cache-Control': 'no-store',
    });
    const rs = fs.createReadStream(dump.full);
    rs.on('error', () => res.destroy());
    rs.pipe(res);
    return;
  }

  // Statische bestanden
  let file = url.pathname === '/' ? '/index.html' : url.pathname;
  file = path.normalize(file).replace(/^([.][.][\\/])+/, '');
  const full = path.join(APP_DIR, file);
  if (!full.startsWith(APP_DIR) || !fs.existsSync(full) || !fs.statSync(full).isFile()) {
    res.writeHead(404); res.end('Not found');
    return;
  }
  res.writeHead(200, { 'Content-Type': MIME[path.extname(full)] || 'application/octet-stream' });
  const sf = fs.createReadStream(full);
  sf.on('error', () => res.destroy());
  sf.pipe(res);
});

process.on('uncaughtException', e => console.error('Onverwachte fout (server draait door):', e));

const URL_LOCAL = `http://localhost:${PORT}`;

function openApp() {
  const edge = [
    path.join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    path.join(process.env['ProgramFiles'] || 'C:\\Program Files', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  ].find(p => { try { return fs.existsSync(p); } catch { return false; } });
  try {
    if (edge) {
      const profile = path.join(DATA_DIR, 'window');
      spawn(edge, [`--app=${URL_LOCAL}`, `--user-data-dir=${profile}`, '--window-size=1440,900', '--no-first-run'],
        { detached: true, stdio: 'ignore' }).unref();
    } else {
      spawn('cmd', ['/c', 'start', '""', URL_LOCAL], { detached: true, stdio: 'ignore' }).unref();
    }
  } catch { }
}

server.on('error', err => {
  if (err.code === 'EADDRINUSE') {
    if (APP_MODE) openApp();
    else console.error(`Poort ${PORT} is al in gebruik; open ${URL_LOCAL}`);
    process.exit(0);
  }
  throw err;
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`FMSuperScout draait op ${URL_LOCAL}`);
  console.log(`Data-map: ${DATA_DIR}`);
  if (APP_MODE) openApp();
});
