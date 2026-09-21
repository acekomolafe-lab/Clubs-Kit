use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs::File;
use std::io::{BufReader, Read};
use std::path::PathBuf;

#[derive(Serialize, Deserialize, Clone)]
pub struct ClubRec {
    club: String,
    division: String,
    country: String,
    rep: i32,
    players: i32,
    #[serde(default, skip_serializing)]
    senior: bool,
}

fn load_kitbasher_cache() -> HashMap<String, (String, String, String)> {
    let mut map = HashMap::new();
    let cache_path = PathBuf::from(r"C:\Program Files\Kitbasher\cache.json");
    if cache_path.exists() {
        if let Ok(data) = std::fs::read_to_string(&cache_path) {
            if let Ok(items) = serde_json::from_str::<Vec<serde_json::Value>>(&data) {
                for item in items {
                    let sn = item.get("ShortName").and_then(|v| v.as_str()).unwrap_or("").to_string();
                    let init = item.get("Initials").and_then(|v| v.as_str()).unwrap_or("").to_string();
                    let bp = item.get("Kits").and_then(|k| k.as_array())
                        .and_then(|arr| arr.get(0))
                        .and_then(|k0| k0.get("BadgePath"))
                        .and_then(|b| b.as_str()).unwrap_or("").to_string();

                    if let Some(tid) = item.get("TeamId").and_then(|v| v.as_str()) {
                        if !tid.is_empty() {
                            map.insert(tid.to_string(), (sn.clone(), init.clone(), bp.clone()));
                        }
                    }
                    if let Some(name) = item.get("Name").and_then(|v| v.as_str()) {
                        let clean = name.to_lowercase().trim().to_string();
                        if !clean.is_empty() {
                            map.insert(clean, (sn, init, bp));
                        }
                    }
                }
            }
        }
    }
    map
}

fn load_badge_cache() -> HashMap<String, String> {
    let data_dir = crate::api::data_dir();
    let cache_file = data_dir.join("badge_cache.json");
    if cache_file.exists() {
        if let Ok(content) = std::fs::read_to_string(&cache_file) {
            if let Ok(map) = serde_json::from_str::<HashMap<String, String>>(&content) {
                if !map.is_empty() {
                    return map;
                }
            }
        }
    }
    let map = scan_graphics_badges();
    if !map.is_empty() {
        let _ = std::fs::write(&cache_file, serde_json::to_string(&map).unwrap_or_default());
    }
    map
}

fn scan_graphics_badges() -> HashMap<String, String> {
    let mut badge_map: HashMap<String, (String, u8, bool)> = HashMap::new(); // clubId -> (path, priority, is_icon)

    let candidate_dirs = [
        (PathBuf::from(r"D:\Sports Interactive\Football Manager 26\graphics\LOGOS\Custom Logos\Custom Logo"), 1),
        (PathBuf::from(r"D:\Sports Interactive\Football Manager 26\graphics\LOGOS\Custom Logos\SORTITOUSTSI\sortitoutsi Metallic Logos\logos\clubs\normal"), 2),
        (PathBuf::from(r"D:\Sports Interactive\Football Manager 26\graphics\LOGOS"), 3),
        (PathBuf::from(r"D:\Sports Interactive\Football Manager 26\graphics"), 4),
        (PathBuf::from(r"D:\Sports Interactive\Football Manager 2024\graphics"), 5),
    ];

    for (dir, priority) in candidate_dirs {
        if dir.exists() {
            scan_dir_badges(&dir, priority, &mut badge_map);
        }
    }

    badge_map.into_iter().map(|(k, (p, _, _))| (k, p)).collect()
}

fn scan_dir_badges(dir: &std::path::Path, priority: u8, badge_map: &mut HashMap<String, (String, u8, bool)>) {
    let entries = match std::fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => return,
    };

    let mut subdirs = Vec::new();
    let mut files_in_dir = std::collections::HashSet::new();
    let mut has_config = false;

    for entry in entries.flatten() {
        if let Ok(ft) = entry.file_type() {
            if ft.is_dir() {
                subdirs.push(entry.path());
            } else if ft.is_file() {
                if let Some(name) = entry.file_name().to_str() {
                    files_in_dir.insert(name.to_string());
                    if name.eq_ignore_ascii_case("config.xml") {
                        has_config = true;
                    }
                }
            }
        }
    }

    if has_config {
        let config_path = dir.join("config.xml");
        if let Ok(content) = std::fs::read_to_string(&config_path) {
            parse_config_records(&content, dir, &files_in_dir, priority, badge_map);
        }
    }

    for sub in subdirs {
        scan_dir_badges(&sub, priority, badge_map);
    }
}

fn parse_config_records(
    content: &str,
    dir: &std::path::Path,
    files_in_dir: &std::collections::HashSet<String>,
    priority: u8,
    badge_map: &mut HashMap<String, (String, u8, bool)>,
) {
    let mut pos = 0;
    while let Some(record_start) = content[pos..].find("<record") {
        let actual_start = pos + record_start;
        let record_end = match content[actual_start..].find('>') {
            Some(idx) => actual_start + idx + 1,
            None => break,
        };
        let record = &content[actual_start..record_end];
        pos = record_end;

        let from_str = if let Some(idx) = record.find("from=\"") {
            let start = idx + 6;
            if let Some(end) = record[start..].find('"') {
                &record[start..start + end]
            } else {
                continue;
            }
        } else {
            continue;
        };

        let to_str = if let Some(idx) = record.find("to=\"") {
            let start = idx + 4;
            if let Some(end) = record[start..].find('"') {
                &record[start..start + end]
            } else {
                continue;
            }
        } else {
            continue;
        };

        if to_str.contains("/background") {
            continue;
        }

        let prefix = "graphics/pictures/club/";
        let club_part = if let Some(idx) = to_str.find(prefix) {
            &to_str[idx + prefix.len()..]
        } else {
            continue;
        };

        let slash_idx = match club_part.find('/') {
            Some(i) => i,
            None => continue,
        };
        let club_id = &club_part[..slash_idx];
        if club_id.is_empty() || !club_id.chars().all(|c| c.is_ascii_digit()) {
            continue;
        }

        let rest = &club_part[slash_idx + 1..];
        let is_icon = rest.starts_with("icon");
        let is_valid_kind = rest.starts_with("logo")
            || rest.starts_with("huge")
            || rest.starts_with("normal")
            || is_icon;
        if !is_valid_kind {
            continue;
        }

        let extensions = [".png", ".jpg", ".jpeg", ".svg", ""];
        let mut matched_path = None;

        for ext in extensions {
            let filename = format!("{}{}", from_str, ext);
            if files_in_dir.contains(&filename) {
                matched_path = Some(dir.join(&filename));
                break;
            }
            for f in files_in_dir {
                if f.eq_ignore_ascii_case(&filename) {
                    matched_path = Some(dir.join(f));
                    break;
                }
            }
            if matched_path.is_some() {
                break;
            }
        }

        if let Some(full_path) = matched_path {
            let path_str = full_path.to_string_lossy().to_string();
            match badge_map.get_mut(club_id) {
                Some(existing) => {
                    if priority < existing.1 {
                        *existing = (path_str, priority, is_icon);
                    } else if priority == existing.1 && existing.2 && !is_icon {
                        *existing = (path_str, priority, is_icon);
                    }
                }
                None => {
                    badge_map.insert(club_id.to_string(), (path_str, priority, is_icon));
                }
            }
        }
    }
}

pub async fn get_database_clubs() -> Result<serde_json::Value, String> {
    let data_dir = crate::api::data_dir();
    let clubs_file = data_dir.join("clubs.json");
    if clubs_file.exists() {
        if let Ok(content) = std::fs::read_to_string(&clubs_file) {
            if let Ok(json) = serde_json::from_str::<serde_json::Value>(&content) {
                let clubs_arr = json.get("clubs").and_then(|c| c.as_array())
                    .or_else(|| json.as_array());
                if let Some(real_clubs) = clubs_arr {
                    let has_divisions = real_clubs.iter().any(|rc| {
                        rc.get("division").and_then(|d| d.as_str()).map(|s| !s.is_empty()).unwrap_or(false)
                    });
                    if has_divisions {
                        let kb_cache = load_kitbasher_cache();
                        let badge_cache = load_badge_cache();
                        let mut list = Vec::with_capacity(real_clubs.len());
                        for rc in real_clubs {
                            let name = rc.get("name").or_else(|| rc.get("club")).and_then(|n| n.as_str()).unwrap_or("");
                            if name.is_empty() { continue; }
                            let id = rc.get("id").or_else(|| rc.get("clubId")).cloned().unwrap_or(serde_json::Value::Null);
                            let division = rc.get("division").and_then(|d| d.as_str()).unwrap_or("");
                            let country = rc.get("country").or_else(|| rc.get("nation")).and_then(|c| c.as_str()).unwrap_or("");
                            let bg = rc.get("bgColor").or_else(|| rc.get("background")).and_then(|b| b.as_str()).unwrap_or("");
                            let fg = rc.get("fgColor").or_else(|| rc.get("foreground")).and_then(|f| f.as_str()).unwrap_or("");
                            let out = rc.get("k1OutColor").or_else(|| rc.get("outlineColor")).or_else(|| rc.get("outline")).and_then(|o| o.as_str()).unwrap_or("");
                            let k1_bg = rc.get("k1BgColor").and_then(|x| x.as_str()).unwrap_or("");
                            let k1_fg = rc.get("k1FgColor").and_then(|x| x.as_str()).unwrap_or("");
                            let k1_out = rc.get("k1OutColor").or_else(|| rc.get("outlineColor")).and_then(|x| x.as_str()).unwrap_or("");
                            let k1_style = rc.get("k1Style").and_then(|x| x.as_i64()).unwrap_or(0);

                            let k2_bg = rc.get("k2BgColor").and_then(|x| x.as_str()).unwrap_or("");
                            let k2_fg = rc.get("k2FgColor").and_then(|x| x.as_str()).unwrap_or("");
                            let k2_out = rc.get("k2OutColor").and_then(|x| x.as_str()).unwrap_or("");
                            let k2_style = rc.get("k2Style").and_then(|x| x.as_i64()).unwrap_or(0);

                            let k3_bg = rc.get("k3BgColor").and_then(|x| x.as_str()).unwrap_or("");
                            let k3_fg = rc.get("k3FgColor").and_then(|x| x.as_str()).unwrap_or("");
                            let k3_out = rc.get("k3OutColor").and_then(|x| x.as_str()).unwrap_or("");
                            let k3_style = rc.get("k3Style").and_then(|x| x.as_i64()).unwrap_or(0);

                            let id_key = id.as_i64().map(|v| v.to_string())
                                .or_else(|| id.as_u64().map(|v| v.to_string()))
                                .or_else(|| id.as_str().map(|s| s.to_string()));

                            let (short_name, initials, kb_bp) = id_key
                                .as_ref()
                                .and_then(|k| kb_cache.get(k))
                                .or_else(|| kb_cache.get(&name.to_lowercase().trim().to_string()))
                                .cloned()
                                .unwrap_or_default();

                            let resolved_badge_path = rc.get("badgePath")
                                .and_then(|b| b.as_str())
                                .filter(|s| !s.is_empty())
                                .map(|s| s.to_string())
                                .or_else(|| {
                                    id_key.as_ref().and_then(|k| badge_cache.get(k)).cloned()
                                })
                                .or_else(|| {
                                    if !kb_bp.is_empty() { Some(kb_bp) } else { None }
                                })
                                .unwrap_or_else(|| {
                                    let clean = name.replace(|c: char| "/\\:*?\"<>|".contains(c), "").trim().replace(' ', "_");
                                    format!(r"C:\Users\aceik\AppData\Roaming\GeneratedKits\Badges\{} Logo.png", clean)
                                });

                            list.push(serde_json::json!({
                                "club": name,
                                "clubId": id,
                                "division": division,
                                "country": country,
                                "bgColor": bg,
                                "fgColor": fg,
                                "outlineColor": out,
                                "k1BgColor": k1_bg,
                                "k1FgColor": k1_fg,
                                "k1OutColor": k1_out,
                                "k1Style": k1_style,
                                "k2BgColor": k2_bg,
                                "k2FgColor": k2_fg,
                                "k2OutColor": k2_out,
                                "k2Style": k2_style,
                                "k3BgColor": k3_bg,
                                "k3FgColor": k3_fg,
                                "k3OutColor": k3_out,
                                "k3Style": k3_style,
                                "shortName": short_name,
                                "initials": initials,
                                "badgePath": resolved_badge_path,
                            }));
                        }
                        return Ok(serde_json::json!({ "clubs": list }));
                    }
                }
            }
        }
    }

    // Fallback if clubs.json is not present: extract from dump
    let dump = crate::api::get_latest_dump();
    if let Some(d) = dump {
        return extract_clubs(d.0).await;
    }
    Err("No clubs database found. Press F9 in Football Manager to dump the database first.".to_string())
}

pub async fn extract_clubs(dump_path: PathBuf) -> Result<serde_json::Value, String> {
    let file = File::open(&dump_path).map_err(|e| e.to_string())?;
    let mut reader = BufReader::with_capacity(1024 * 1024, file);
    
    let mut by_club: HashMap<String, ClubRec> = HashMap::new();
    
    let mut buf = String::with_capacity(2 * 1024 * 1024);
    let mut stage = 0; // 0: seek-array, 1: in-array, 2: done
    
    let mut obj_start = -1isize;
    let mut scanned = 0;
    let mut in_str = false;
    let mut esc = false;
    let mut depth = 0;

    let mut chunk = [0u8; 64 * 1024];
    
    while stage != 2 {
        let n = reader.read(&mut chunk).map_err(|e| e.to_string())?;
        if n == 0 { break; }
        
        let chunk_str = String::from_utf8_lossy(&chunk[..n]);
        buf.push_str(&chunk_str);
        
        if stage == 0 {
            if let Some(idx) = buf.find("\"players\":[") {
                buf = buf[idx + "\"players\":[".len()..].to_string();
                stage = 1;
                scanned = 0;
            } else {
                if buf.len() > 32 {
                    let len = buf.len();
                    buf = buf[len - 32..].to_string();
                }
                continue;
            }
        }
        
        if stage != 1 { continue; }
        
        let mut i = scanned;
        let bytes = buf.as_bytes();
        
        while i < bytes.len() {
            let c = bytes[i];
            
            if obj_start == -1 {
                if c == b']' {
                    stage = 2;
                    break;
                }
                if c == b'{' {
                    obj_start = i as isize;
                    depth = 1;
                }
                i += 1;
                continue;
            }
            
            if in_str {
                if esc {
                    esc = false;
                } else if c == b'\\' {
                    esc = true;
                } else if c == b'"' {
                    in_str = false;
                }
                i += 1;
                continue;
            }
            
            if c == b'"' {
                in_str = true;
                i += 1;
                continue;
            }
            
            if c == b'{' {
                depth += 1;
            } else if c == b'}' {
                depth -= 1;
                if depth == 0 {
                    let obj_text = &buf[obj_start as usize..i + 1];
                    handle_object(obj_text, &mut by_club);
                    obj_start = -1;
                }
            }
            i += 1;
        }
        
        scanned = if stage == 2 { bytes.len() } else { i };
        
        if stage == 1 {
            let keep_from = if obj_start == -1 { scanned } else { obj_start as usize };
            if keep_from > 0 {
                buf = buf[keep_from..].to_string();
                if obj_start != -1 {
                    obj_start -= keep_from as isize;
                }
                scanned -= keep_from;
            }
        }
    }
    
    let mut clubs: Vec<serde_json::Value> = by_club.values().map(|c| serde_json::to_value(c).unwrap()).collect();
    
    // Read clubs.json if exists
    let clubs_file = crate::api::data_dir().join("clubs.json");
    if let Ok(content) = std::fs::read_to_string(&clubs_file) {
        if let Ok(json) = serde_json::from_str::<serde_json::Value>(&content) {
            if let Some(real_clubs) = json.get("clubs").and_then(|c| c.as_array()) {
                let mut map = HashMap::new();
                let mut norm_map = HashMap::new();
                for rc in real_clubs {
                    if let Some(name) = rc.get("name").and_then(|n| n.as_str()) {
                        map.insert(name.to_string(), rc.clone());
                        let norm = normalize_club_name(name);
                        if !norm.is_empty() {
                            norm_map.entry(norm).or_insert_with(|| rc.clone());
                        }
                    }
                }
                for c in &mut clubs {
                    if let Some(name) = c.get("club").and_then(|n| n.as_str()) {
                        let matched = map.get(name).or_else(|| {
                            let n = normalize_club_name(name);
                            norm_map.get(&n)
                        });
                        if let Some(rc) = matched {
                            if let Some(obj) = c.as_object_mut() {
                                if let Some(id) = rc.get("id") { obj.insert("clubId".to_string(), id.clone()); }
                                if let Some(bg) = rc.get("bgColor").or_else(|| rc.get("background")) { obj.insert("bgColor".to_string(), bg.clone()); }
                                if let Some(fg) = rc.get("fgColor").or_else(|| rc.get("foreground")) { obj.insert("fgColor".to_string(), fg.clone()); }
                                if let Some(out) = rc.get("k1OutColor").or_else(|| rc.get("outlineColor")).or_else(|| rc.get("outline")) { obj.insert("outlineColor".to_string(), out.clone()); }
                                if let Some(x) = rc.get("k1BgColor") { obj.insert("k1BgColor".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k1FgColor") { obj.insert("k1FgColor".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k1OutColor") { obj.insert("k1OutColor".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k1Style") { obj.insert("k1Style".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k2BgColor") { obj.insert("k2BgColor".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k2FgColor") { obj.insert("k2FgColor".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k2OutColor") { obj.insert("k2OutColor".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k2Style") { obj.insert("k2Style".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k3BgColor") { obj.insert("k3BgColor".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k3FgColor") { obj.insert("k3FgColor".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k3OutColor") { obj.insert("k3OutColor".to_string(), x.clone()); }
                                if let Some(x) = rc.get("k3Style") { obj.insert("k3Style".to_string(), x.clone()); }
                                if let Some(country) = rc.get("country").or_else(|| rc.get("nation")).and_then(|c| c.as_str()) {
                                    if !country.is_empty() {
                                        obj.insert("country".to_string(), serde_json::Value::String(country.to_string()));
                                    }
                                }
                                if let Some(div_val) = rc.get("division").and_then(|d| d.as_str()) {
                                    if !div_val.is_empty() {
                                        obj.insert("division".to_string(), serde_json::Value::String(div_val.to_string()));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    let badge_cache = load_badge_cache();
    for c in &mut clubs {
        if let Some(obj) = c.as_object_mut() {
            let name = obj.get("club").and_then(|n| n.as_str()).unwrap_or("");
            let id_key = obj.get("clubId")
                .and_then(|id| id.as_i64().map(|v| v.to_string())
                    .or_else(|| id.as_u64().map(|v| v.to_string()))
                    .or_else(|| id.as_str().map(|s| s.to_string())));
            let bp = id_key.as_ref().and_then(|k| badge_cache.get(k)).cloned().unwrap_or_else(|| {
                let clean = name.replace(|c: char| "/\\:*?\"<>|".contains(c), "").trim().replace(' ', "_");
                format!(r"C:\Users\aceik\AppData\Roaming\GeneratedKits\Badges\{} Logo.png", clean)
            });
            obj.insert("badgePath".to_string(), serde_json::Value::String(bp));
        }
    }
    
    Ok(serde_json::json!({ "clubs": clubs }))
}

fn handle_object(obj_text: &str, by_club: &mut HashMap<String, ClubRec>) {
    let mut club = None;
    let mut div = String::new();
    let mut nat = String::new();
    let mut rep = 0;
    
    // Quick and dirty manual regex-like parsing for performance
    if let Some(idx) = obj_text.find("\"club\":\"") {
        let start = idx + 8;
        if let Some(end) = obj_text[start..].find('"') {
            club = Some(unescape(&obj_text[start..start+end]));
        }
    }
    
    if let Some(idx) = obj_text.find("\"div\":\"") {
        let start = idx + 7;
        if let Some(end) = obj_text[start..].find('"') {
            div = unescape(&obj_text[start..start+end]);
        }
    }

    if let Some(idx) = obj_text.find("\"nat\":[\"") {
        let start = idx + 8;
        if let Some(end) = obj_text[start..].find('"') {
            nat = unescape(&obj_text[start..start+end]);
        }
    }
    
    if let Some(idx) = obj_text.find("\"clubRep\":") {
        let start = idx + 10;
        let mut end = start;
        while end < obj_text.len() {
            let c = obj_text.as_bytes()[end];
            if c.is_ascii_digit() || c == b'-' {
                end += 1;
            } else {
                break;
            }
        }
        if let Ok(r) = obj_text[start..end].parse::<i32>() {
            rep = r;
        }
    }
    
    let mut team_type = -1;
    if let Some(idx) = obj_text.find("\"teamType\":") {
        let start = idx + 11;
        let mut end = start;
        while end < obj_text.len() {
            let c = obj_text.as_bytes()[end];
            if c.is_ascii_digit() {
                end += 1;
            } else {
                break;
            }
        }
        if let Ok(tt) = obj_text[start..end].parse::<i32>() {
            team_type = tt;
        }
    }
    
    if let Some(c_name) = club {
        if c_name.is_empty() { return; }
        
        let rec = by_club.entry(c_name.clone()).or_insert(ClubRec {
            club: c_name,
            division: String::new(),
            country: String::new(),
            rep: 0,
            players: 0,
            senior: false,
        });
        
        rec.players += 1;
        if team_type == 0 && !div.is_empty() {
            rec.division = div;
            rec.senior = true;
        } else if !rec.senior && rec.division.is_empty() && !div.is_empty() {
            rec.division = div;
        }
        if rec.country.is_empty() && !nat.is_empty() {
            rec.country = nat;
        }
        if rep > rec.rep {
            rec.rep = rep;
        }
    }
}

fn unescape(s: &str) -> String {
    s.replace("\\\"", "\"").replace("\\\\", "\\")
}

fn normalize_club_name(s: &str) -> String {
    let lower = s.to_lowercase();
    let mut clean = String::with_capacity(lower.len());
    for part in lower.split_whitespace() {
        let p = part.trim_matches(|c: char| !c.is_alphanumeric());
        if p == "fc" || p == "cf" || p == "sc" || p == "afc" {
            continue;
        }
        for ch in part.chars() {
            if ch.is_alphanumeric() {
                clean.push(ch);
            }
        }
    }
    clean
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_database_clubs_load() {
        let res = tauri::async_runtime::block_on(get_database_clubs());
        assert!(res.is_ok(), "Failed to load database clubs: {:?}", res);
        let v = res.unwrap();
        let list = v.get("clubs").and_then(|c| c.as_array()).expect("Expected 'clubs' array");
        assert!(list.len() > 1000, "Expected at least 1,000 clubs in database, found: {}", list.len());
        let arsenal = list.iter().find(|c| c.get("club").and_then(|n| n.as_str()) == Some("Arsenal"));
        assert!(arsenal.is_some(), "Expected Arsenal in clubs list");
        let badge_path = arsenal.unwrap().get("badgePath").and_then(|b| b.as_str()).unwrap_or("");
        assert!(!badge_path.is_empty(), "Expected Arsenal to have a badgePath");
        assert!(badge_path.ends_with(".png") || badge_path.ends_with(".svg"), "Expected image file extension: {}", badge_path);
        println!("Loaded {} clubs! Arsenal badgePath: {}", list.len(), badge_path);
    }
}



