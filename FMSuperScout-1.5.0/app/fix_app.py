import re

with open('app.js', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. deltas (no since)
content = re.sub(
    r"fetch\(`/api/history/deltas\?manager=\$\{mgr\}`\)",
    r"window.__TAURI__.core.invoke('get_history_deltas', { manager: mgr })",
    content
)

# 2. deltas (with since)
content = re.sub(
    r"fetch\(`/api/history/deltas\?manager=\$\{mgr\}&since=\$\{ref\}`\)",
    r"window.__TAURI__.core.invoke('get_history_deltas', { manager: mgr, since: ref })",
    content
)

# 3. get_status
content = re.sub(
    r"\(await fetch\('/api/status'\)\)\.json\(\)",
    r"(await window.__TAURI__.core.invoke('get_status'))",
    content
)

# 4. merge_history
content = re.sub(
    r"fetch\('/api/history', \{[\s\n]*method:\s*'POST',[\s\n]*headers:\s*\{[\s\n]*'Content-Type':\s*'application/json'[\s\n]*\},[\s\n]*body:\s*JSON\.stringify\(players\)[\s\n]*\}\)",
    r"window.__TAURI__.core.invoke('merge_history', { players })",
    content
)

# 5. check_setup
content = re.sub(
    r"fetch\('/api/setup'\)\.then\(r => r\.json\(\)\)",
    r"window.__TAURI__.core.invoke('check_setup')",
    content
)

# 6. history series
content = re.sub(
    r"fetch\(`/api/history/series\?uid=\$\{p\.id\}&manager=\$\{encodeURIComponent\(state\.meta\.manager \|\| 'default'\)\}`\)",
    r"window.__TAURI__.core.invoke('get_history_series', { uid: parseInt(p.id), manager: state.meta.manager || 'default' })",
    content
)

# 7. version-check
content = re.sub(
    r"await fetch\('/api/version-check'\)",
    r"await window.__TAURI__.core.invoke('version_check')",
    content
)

content = re.sub(
    r"if \(r\.ok\) tag = \(await r\.json\(\)\)\.tag;",
    r"tag = r.tag;",
    content
)

# 8. update-install
content = re.sub(
    r"fetch\('/api/update-install', \{ method: 'POST' \}\)",
    r"window.__TAURI__.core.invoke('update_install')",
    content
)

# 9. update-status
content = re.sub(
    r"\(await fetch\('/api/update-status'\)\)\.json\(\)",
    r"(await window.__TAURI__.core.invoke('update_status'))",
    content
)

# 10. diagnostics
content = re.sub(
    r"await fetch\('/api/diagnostics'\)",
    r"await window.__TAURI__.core.invoke('get_diagnostics')",
    content
)

content = re.sub(
    r"if \(!r\.ok\) throw new Error\('Diag failed'\);\n\s*const text = await r\.text\(\);",
    r"const text = r;",
    content
)

# 11. fmstatus
content = re.sub(
    r"\(await fetch\('/api/fmstatus'\)\)\.json\(\)",
    r"(await window.__TAURI__.core.invoke('check_fmstatus'))",
    content
)

# 12. refresh
content = re.sub(
    r"fetch\('/api/refresh', \{ method: 'POST' \}\)",
    r"window.__TAURI__.core.invoke('trigger_refresh')",
    content
)

# 13. heartbeat
content = re.sub(
    r"fetch\('/api/heartbeat', \{ method: 'POST' \}\)\.catch\(\(\) => \{\}\)",
    r"Promise.resolve()",
    content
)

with open('app.js', 'w', encoding='utf-8') as f:
    f.write(content)

print("Modified app.js successfully.")
