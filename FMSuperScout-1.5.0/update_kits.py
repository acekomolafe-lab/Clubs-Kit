import os

app_dir = r"d:\Projects\FMSuperScout-1.5.0\FMSuperScout-1.5.0\app"

# 1. Update clubs.js
clubs_js_path = os.path.join(app_dir, "clubs.js")
with open(clubs_js_path, "r", encoding="utf-8") as f:
    js_content = f.read()

# KIT_SLOTS
js_content = js_content.replace(
"""const KIT_SLOTS = [
  { key: 'background', label: 'Background', def: DEFAULT_BACKGROUND },
  { key: 'foreground', label: 'Foreground', def: DEFAULT_FOREGROUND },
  { key: 'outline', label: 'Outline', def: DEFAULT_OUTLINE },
];""",
"""const KIT_SLOTS = [
  { key: 'homeBg', label: 'Home Bg', def: DEFAULT_BACKGROUND },
  { key: 'homeFg', label: 'Home Fg', def: DEFAULT_FOREGROUND },
  { key: 'homeOut', label: 'Home Out', def: DEFAULT_OUTLINE },
  { key: 'awayBg', label: 'Away Bg', def: DEFAULT_BACKGROUND },
  { key: 'awayFg', label: 'Away Fg', def: DEFAULT_FOREGROUND },
  { key: 'awayOut', label: 'Away Out', def: DEFAULT_OUTLINE },
  { key: 'thirdBg', label: 'Third Bg', def: DEFAULT_BACKGROUND },
  { key: 'thirdFg', label: 'Third Fg', def: DEFAULT_FOREGROUND },
  { key: 'thirdOut', label: 'Third Out', def: DEFAULT_OUTLINE },
];""")

# Legacy loadKitColors
js_content = js_content.replace(
"""  // Preserve colors saved by the previous home/away/third naming scheme.
  for (const rec of Object.values(kitColors)) {
    if (!rec || typeof rec !== 'object') continue;
    if (!rec.background && rec.home) rec.background = rec.home;
    if (!rec.foreground && rec.away) rec.foreground = rec.away;
    if (!rec.outline && rec.third) rec.outline = rec.third;
  }
  // Migrate old single-color storage (one kit per club) into the background slot.
  try {
    const legacy = JSON.parse(localStorage.getItem(KIT_KEY_LEGACY) || '{}');
    for (const [club, hex] of Object.entries(legacy)) {
      if (!kitColors[club]) kitColors[club] = { background: hex };
    }
  } catch { /* no legacy data */ }""",
"""  // Preserve colors saved by the previous home/away/third naming scheme.
  for (const rec of Object.values(kitColors)) {
    if (!rec || typeof rec !== 'object') continue;
    if (!rec.homeBg && rec.home) rec.homeBg = rec.home;
    if (!rec.awayBg && rec.away) rec.awayBg = rec.away;
    if (!rec.thirdBg && rec.third) rec.thirdBg = rec.third;
  }
  // Migrate old single-color storage (one kit per club) into the background slot.
  try {
    const legacy = JSON.parse(localStorage.getItem(KIT_KEY_LEGACY) || '{}');
    for (const [club, hex] of Object.entries(legacy)) {
      if (!kitColors[club]) kitColors[club] = { homeBg: hex };
    }
  } catch { /* no legacy data */ }""")

# loadClubs
js_content = js_content.replace(
"""  clubs = (data.clubs || []).map(c => {
    if (c.bgColor || c.fgColor || c.outlineColor) {
      if (!kitColors[c.club]) kitColors[c.club] = {};
      if (c.bgColor && !kitColors[c.club].background) kitColors[c.club].background = c.bgColor;
      if (c.fgColor && !kitColors[c.club].foreground) kitColors[c.club].foreground = c.fgColor;
      if (c.outlineColor && !kitColors[c.club].outline) kitColors[c.club].outline = c.outlineColor;
    }
    return { ...c, clubId: c.clubId || clubIdOf(c.club) };
  });""",
"""  clubs = (data.clubs || []).map(c => {
    if (c.homeBg || c.homeFg || c.homeOut || c.awayBg || c.awayFg || c.awayOut || c.thirdBg || c.thirdFg || c.thirdOut) {
      if (!kitColors[c.club]) kitColors[c.club] = {};
      if (c.homeBg && !kitColors[c.club].homeBg) kitColors[c.club].homeBg = c.homeBg;
      if (c.homeFg && !kitColors[c.club].homeFg) kitColors[c.club].homeFg = c.homeFg;
      if (c.homeOut && !kitColors[c.club].homeOut) kitColors[c.club].homeOut = c.homeOut;
      
      if (c.awayBg && !kitColors[c.club].awayBg) kitColors[c.club].awayBg = c.awayBg;
      if (c.awayFg && !kitColors[c.club].awayFg) kitColors[c.club].awayFg = c.awayFg;
      if (c.awayOut && !kitColors[c.club].awayOut) kitColors[c.club].awayOut = c.awayOut;
      
      if (c.thirdBg && !kitColors[c.club].thirdBg) kitColors[c.club].thirdBg = c.thirdBg;
      if (c.thirdFg && !kitColors[c.club].thirdFg) kitColors[c.club].thirdFg = c.thirdFg;
      if (c.thirdOut && !kitColors[c.club].thirdOut) kitColors[c.club].thirdOut = c.thirdOut;
    }
    return { ...c, clubId: c.clubId || clubIdOf(c.club) };
  });""")

# render tbody
js_content = js_content.replace(
"""      ${kitCellHtml(c.club, 'background')}
      ${kitCellHtml(c.club, 'foreground')}
      ${kitCellHtml(c.club, 'outline')}""",
"""      ${kitCellHtml(c.club, 'homeBg')}
      ${kitCellHtml(c.club, 'homeFg')}
      ${kitCellHtml(c.club, 'homeOut')}
      ${kitCellHtml(c.club, 'awayBg')}
      ${kitCellHtml(c.club, 'awayFg')}
      ${kitCellHtml(c.club, 'awayOut')}
      ${kitCellHtml(c.club, 'thirdBg')}
      ${kitCellHtml(c.club, 'thirdFg')}
      ${kitCellHtml(c.club, 'thirdOut')}""")

# exportCsv
js_content = js_content.replace(
"""  const rows = [['ClubID', 'Club', 'Division', 'Reputation', 'Players', 'Background', 'Foreground', 'Outline']];
  for (const c of list) {
    rows.push([
      c.clubId, c.club, c.division || '', c.rep || 0, c.players,
      kitColorOf(c.club, 'background'), kitColorOf(c.club, 'foreground'), kitColorOf(c.club, 'outline'),
    ]);
  }""",
"""  const rows = [['ClubID', 'Club', 'Division', 'Reputation', 'Players', 'Home Bg', 'Home Fg', 'Home Out', 'Away Bg', 'Away Fg', 'Away Out', 'Third Bg', 'Third Fg', 'Third Out']];
  for (const c of list) {
    rows.push([
      c.clubId, c.club, c.division || '', c.rep || 0, c.players,
      kitColorOf(c.club, 'homeBg'), kitColorOf(c.club, 'homeFg'), kitColorOf(c.club, 'homeOut'),
      kitColorOf(c.club, 'awayBg'), kitColorOf(c.club, 'awayFg'), kitColorOf(c.club, 'awayOut'),
      kitColorOf(c.club, 'thirdBg'), kitColorOf(c.club, 'thirdFg'), kitColorOf(c.club, 'thirdOut')
    ]);
  }""")

with open(clubs_js_path, "w", encoding="utf-8") as f:
    f.write(js_content)


# 2. Update clubs.html
clubs_html_path = os.path.join(app_dir, "clubs.html")
with open(clubs_html_path, "r", encoding="utf-8") as f:
    html_content = f.read()

html_content = html_content.replace(
"""        <th>Background</th>
        <th>Foreground</th>
        <th>Outline</th>""",
"""        <th>Home Bg</th>
        <th>Home Fg</th>
        <th>Home Out</th>
        <th>Away Bg</th>
        <th>Away Fg</th>
        <th>Away Out</th>
        <th>Third Bg</th>
        <th>Third Fg</th>
        <th>Third Out</th>""")

with open(clubs_html_path, "w", encoding="utf-8") as f:
    f.write(html_content)

print("Updated clubs.js and clubs.html successfully!")
