use serde::{Deserialize, Serialize};
use std::fs;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU16, Ordering};
use std::thread;
use tauri::command;
use zip::write::FileOptions;
use zip::{CompressionMethod, ZipWriter};

use crate::{clubs, history, updater};

static STREAM_PORT: AtomicU16 = AtomicU16::new(0);

fn get_data_dir() -> PathBuf {
    if let Ok(local_app_data) = std::env::var("LOCALAPPDATA") {
        PathBuf::from(local_app_data).join("FMSuperScout")
    } else {
        dirs::data_local_dir().unwrap_or_else(|| PathBuf::from(".")).join("FMSuperScout")
    }
}

pub fn log_to_file(msg: &str) {
    let dir = get_data_dir();
    let _ = fs::create_dir_all(&dir);
    let log_path = dir.join("app_debug.log");
    if let Ok(mut f) = fs::OpenOptions::new().create(true).append(true).open(log_path) {
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis();
        let _ = writeln!(f, "[{}] {}", now, msg);
    }
}

#[command]
pub fn log_debug(msg: String) {
    log_to_file(&msg);
}

pub fn start_stream_server() {
    thread::spawn(|| {
        let listener = match TcpListener::bind("127.0.0.1:0") {
            Ok(l) => l,
            Err(e) => {
                log_to_file(&format!("Failed to bind stream server: {}", e));
                return;
            }
        };
        if let Ok(addr) = listener.local_addr() {
            let port = addr.port();
            STREAM_PORT.store(port, Ordering::SeqCst);
            log_to_file(&format!("Local stream server started on http://127.0.0.1:{}", port));
        }
        for stream in listener.incoming() {
            if let Ok(mut stream) = stream {
                thread::spawn(move || {
                    handle_stream_client(&mut stream);
                });
            }
        }
    });
}

fn handle_stream_client(stream: &mut TcpStream) {
    let mut reader = BufReader::new(match stream.try_clone() {
        Ok(s) => s,
        Err(_) => return,
    });
    let mut req_line = String::new();
    if reader.read_line(&mut req_line).is_err() || req_line.is_empty() {
        return;
    }
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line).is_err() || line == "\r\n" || line == "\n" || line.is_empty() {
            break;
        }
    }

    let parts: Vec<&str> = req_line.split_whitespace().collect();
    if parts.len() < 2 {
        return;
    }
    let method = parts[0];
    let path = parts[1];

    if method == "OPTIONS" {
        let resp = "HTTP/1.1 204 No Content\r\n\
            Access-Control-Allow-Origin: *\r\n\
            Access-Control-Allow-Methods: GET, OPTIONS\r\n\
            Access-Control-Allow-Headers: *\r\n\
            Connection: close\r\n\r\n";
        let _ = stream.write_all(resp.as_bytes());
        return;
    }

    if path.starts_with("/api/dump") {
        log_to_file("Stream server: incoming GET /api/dump");
        if let Some((dump_path, _, size)) = get_latest_dump() {
            log_to_file(&format!("Stream server: streaming dump {:?} ({} bytes)", dump_path, size));
            if let Ok(mut file) = fs::File::open(&dump_path) {
                let header = format!(
                    "HTTP/1.1 200 OK\r\n\
                    Content-Type: application/json; charset=utf-8\r\n\
                    Content-Length: {}\r\n\
                    Access-Control-Allow-Origin: *\r\n\
                    Access-Control-Expose-Headers: Content-Length\r\n\
                    Cache-Control: no-store\r\n\
                    Connection: close\r\n\r\n",
                    size
                );
                if stream.write_all(header.as_bytes()).is_ok() {
                    let mut buf = [0u8; 65536];
                    let mut total_sent: u64 = 0;
                    loop {
                        match file.read(&mut buf) {
                            Ok(0) => break,
                            Ok(n) => {
                                if stream.write_all(&buf[..n]).is_err() {
                                    log_to_file(&format!("Stream server: client aborted after {} bytes", total_sent));
                                    return;
                                }
                                total_sent += n as u64;
                            }
                            Err(e) => {
                                log_to_file(&format!("Stream server: file read error after {} bytes: {}", total_sent, e));
                                break;
                            }
                        }
                    }
                    let _ = stream.flush();
                    log_to_file(&format!("Stream server: successfully sent {} bytes", total_sent));
                    return;
                }
            } else {
                log_to_file("Stream server: failed to open dump file");
            }
        } else {
            log_to_file("Stream server: no dump found");
        }
        let not_found = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        let _ = stream.write_all(not_found.as_bytes());
    } else {
        let not_found = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        let _ = stream.write_all(not_found.as_bytes());
    }
}

pub(crate) fn get_latest_dump() -> Option<(PathBuf, u64, u64)> {
    let dir = get_data_dir();
    let entries = fs::read_dir(dir).ok()?;
    
    let mut latest: Option<(PathBuf, u64, u64)> = None;
    
    for entry in entries.flatten() {
        let path = entry.path();
        if let Some(name) = path.file_name().and_then(|n| n.to_str()) {
            if name.starts_with("dump") && name.ends_with(".json") {
                if let Ok(meta) = entry.metadata() {
                    let mtime = meta.modified().unwrap_or(std::time::SystemTime::UNIX_EPOCH)
                        .duration_since(std::time::UNIX_EPOCH).unwrap_or_default().as_millis() as u64;
                    let size = meta.len();
                    
                    if let Some((_, latest_mtime, _)) = latest {
                        if mtime > latest_mtime {
                            latest = Some((path, mtime, size));
                        }
                    } else {
                        latest = Some((path, mtime, size));
                    }
                }
            }
        }
    }
    
    latest
}

#[derive(Serialize)]
pub struct StatusResponse {
    #[serde(rename = "dataDir")]
    data_dir: String,
    #[serde(rename = "hasDump")]
    has_dump: bool,
    #[serde(rename = "dumpFile")]
    dump_file: Option<String>,
    #[serde(rename = "dumpTime")]
    dump_time: Option<u64>,
    #[serde(rename = "dumpSize")]
    dump_size: Option<u64>,
    #[serde(rename = "dumpUrl")]
    dump_url: Option<String>,
    #[serde(rename = "appMode")]
    app_mode: bool,
    plugin: Option<serde_json::Value>,
}

#[command]
pub fn get_status() -> Result<StatusResponse, String> {
    let data_dir = get_data_dir();
    let dump = get_latest_dump();
    
    let mut plugin = None;
    let sf = data_dir.join("status.json");
    if let Ok(content) = fs::read_to_string(&sf) {
        if let Ok(mut json) = serde_json::from_str::<serde_json::Value>(&content) {
            if let Ok(meta) = fs::metadata(&sf) {
                let mtime = meta.modified().unwrap_or(std::time::SystemTime::UNIX_EPOCH)
                    .duration_since(std::time::UNIX_EPOCH).unwrap_or_default().as_millis() as u64;
                if let Some(obj) = json.as_object_mut() {
                    obj.insert("mtime".to_string(), serde_json::Value::Number(mtime.into()));
                }
            }
            plugin = Some(json);
        }
    }
    
    let port = STREAM_PORT.load(Ordering::SeqCst);
    let dump_url = if port > 0 && dump.is_some() {
        Some(format!("http://127.0.0.1:{}/api/dump", port))
    } else {
        None
    };

    Ok(StatusResponse {
        data_dir: data_dir.to_string_lossy().into_owned(),
        has_dump: dump.is_some(),
        dump_file: dump.as_ref().map(|(p, _, _)| p.to_string_lossy().into_owned()),
        dump_time: dump.as_ref().map(|(_, m, _)| *m),
        dump_size: dump.as_ref().map(|(_, _, s)| *s),
        dump_url,
        app_mode: true, // Always true in Tauri
        plugin,
    })
}

#[derive(Serialize, Deserialize)]
pub struct ScanConfig {
    db: String,
}

#[command]
pub fn get_scan_config() -> Result<ScanConfig, String> {
    let cf = get_data_dir().join("scan-config.json");
    let mut db = "both".to_string();
    if let Ok(raw) = fs::read_to_string(cf) {
        if raw.contains("\"db\":\"women\"") {
            db = "women".to_string();
        } else if raw.contains("\"db\":\"men\"") {
            db = "men".to_string();
        } else if raw.contains("\"db\":\"both\"") || raw.contains("\"includeWomen\":true") {
            db = "both".to_string();
        }
    }
    Ok(ScanConfig { db })
}

#[command]
pub fn set_scan_config(db: String) -> Result<ScanConfig, String> {
    let cf = get_data_dir().join("scan-config.json");
    let db_val = if ["men", "women", "both"].contains(&db.as_str()) { db } else { "both".to_string() };
    let json = serde_json::json!({ "db": db_val });
    let _ = fs::create_dir_all(get_data_dir());
    let _ = fs::write(cf, json.to_string());
    Ok(ScanConfig { db: db_val })
}

#[command]
pub fn get_diagnostics() -> Result<String, String> {
    let txt = fs::read_to_string(get_data_dir().join("diagnostics.txt"))
        .map_err(|_| "Not found".to_string())?;
    Ok(txt)
}

#[derive(Serialize)]
pub struct FmStatus {
    running: bool,
}

#[command]
pub fn check_fmstatus() -> Result<FmStatus, String> {
    use sysinfo::System;
    let mut sys = System::new_all();
    sys.refresh_processes(sysinfo::ProcessesToUpdate::All, true);
    let running = sys.processes().values().any(|p| p.name().to_string_lossy().to_lowercase().contains("fm.exe"));
    Ok(FmStatus { running })
}

#[derive(Serialize)]
pub struct SetupStatus {
    known: bool,
    #[serde(rename = "gameDir")]
    game_dir: Option<String>,
    #[serde(rename = "pluginInstalled")]
    plugin_installed: Option<bool>,
    bepinex: Option<bool>,
    #[serde(rename = "interopReady")]
    interop_ready: Option<bool>,
}

#[command]
pub fn check_setup() -> Result<SetupStatus, String> {
    use winreg::enums::*;
    use winreg::RegKey;
    let hkcu = RegKey::predef(HKEY_LOCAL_MACHINE);
    let game_dir = if let Ok(key) = hkcu.open_subkey("SOFTWARE\\FMSuperScout") {
        key.get_value::<String, _>("GamePath").ok()
    } else {
        None
    };
    
    if let Some(dir) = game_dir {
        let path = PathBuf::from(&dir);
        let plugin = path.join("BepInEx").join("plugins").join("FMSuperScout.dll").exists();
        let bepinex = path.join("BepInEx").exists();
        let interop_ready = path.join("BepInEx").join("interop").read_dir().map(|i| i.count() > 0).unwrap_or(false);
        Ok(SetupStatus {
            known: true,
            game_dir: Some(dir),
            plugin_installed: Some(plugin),
            bepinex: Some(bepinex),
            interop_ready: Some(interop_ready),
        })
    } else {
        Ok(SetupStatus {
            known: false,
            game_dir: None,
            plugin_installed: None,
            bepinex: None,
            interop_ready: None,
        })
    }
}

#[command]
pub fn trigger_refresh() -> Result<bool, String> {
    let dir = get_data_dir();
    fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis().to_string();
    fs::write(dir.join("request.flag"), now).map_err(|e| e.to_string())?;
    Ok(true)
}

#[derive(Serialize)]
pub struct VersionResponse {
    tag: String,
}

#[command]
pub fn version_check() -> Result<VersionResponse, String> {
    let resp = reqwest::blocking::get("https://github.com/mavarobli/FMSuperScout/releases/latest/download/version.json")
        .map_err(|e| e.to_string())?;
    let json: serde_json::Value = resp.json().map_err(|e| e.to_string())?;
    let tag = json.get("tag").and_then(|v| v.as_str()).unwrap_or("").to_string();
    if tag.starts_with("v") {
        Ok(VersionResponse { tag })
    } else {
        Err("Invalid tag".to_string())
    }
}

// Forward to other modules
#[command]
pub fn update_install() -> Result<bool, String> {
    updater::install()
}

#[command]
pub fn update_status() -> Result<updater::UpdateStatus, String> {
    Ok(updater::status())
}

#[command]
pub fn get_history_deltas(manager: Option<String>, since: Option<String>) -> Result<serde_json::Value, String> {
    history::get_deltas(manager.unwrap_or_else(|| "default".to_string()), since)
}

#[command]
pub fn get_history_series(manager: Option<String>, uid: String) -> Result<serde_json::Value, String> {
    history::get_series(manager.unwrap_or_else(|| "default".to_string()), uid)
}

#[command]
pub fn merge_history(manager: String, game_date: String, players: std::collections::HashMap<String, (u16, u16, Option<i64>)>) -> Result<serde_json::Value, String> {
    history::merge(manager, game_date, players)
}

#[command]
pub async fn get_clubs() -> Result<serde_json::Value, String> {
    clubs::get_database_clubs().await
}

const DEFAULT_SPONSOR_CONFIG_PATH: &str = r"D:\KitbasherLegacy v0.8\Sponsors\sponsorConfig.json";

#[derive(Deserialize)]
struct SponsorConfigFile {
    #[serde(rename = "SponsorGroupings")]
    sponsor_groupings: Vec<SponsorGroupingFile>,
}

#[derive(Deserialize)]
struct SponsorGroupingFile {
    #[serde(rename = "Id")]
    id: String,
    #[serde(rename = "Name")]
    name: String,
}

#[derive(Serialize)]
pub struct SponsorGroupingInfo {
    pub id: String,
    pub name: String,
}

#[command]
pub fn get_sponsor_groupings(config_path: Option<String>) -> Result<Vec<SponsorGroupingInfo>, String> {
    let path = config_path
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| DEFAULT_SPONSOR_CONFIG_PATH.to_string());
    let content = fs::read_to_string(&path)
        .map_err(|e| format!("Failed to read sponsor config {}: {}", path, e))?;
    let config: SponsorConfigFile = serde_json::from_str(&content)
        .map_err(|e| format!("Failed to parse sponsor config {}: {}", path, e))?;

    Ok(config
        .sponsor_groupings
        .into_iter()
        .filter(|group| !group.name.trim().is_empty())
        .map(|group| SponsorGroupingInfo {
            id: group.id,
            name: group.name,
        })
        .collect())
}

const DEFAULT_TEMPLATE_CONFIG_PATH: &str = r"D:\KitbasherLegacy v0.8\Templates\KitTemplates\templateConfig.json";

#[derive(Deserialize)]
struct TemplateConfigFile {
    #[serde(rename = "TemplateGroupings")]
    template_groupings: Option<Vec<TemplateGroupingFile>>,
}

#[derive(Deserialize)]
struct TemplateGroupingFile {
    #[serde(rename = "Name")]
    name: Option<String>,
    #[serde(rename = "Id")]
    id: Option<String>,
    #[serde(rename = "Brands")]
    brands: Option<Vec<String>>,
}

#[derive(Serialize)]
pub struct TemplateGroupingInfo {
    pub id: String,
    pub name: String,
    pub brands: Vec<String>,
}

#[derive(Serialize)]
pub struct SaveTemplateGroupingsResult {
    pub added: usize,
}

#[command]
pub fn get_template_groupings(config_path: Option<String>) -> Result<Vec<TemplateGroupingInfo>, String> {
    let path = config_path
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| DEFAULT_TEMPLATE_CONFIG_PATH.to_string());
    let content = fs::read_to_string(&path)
        .map_err(|e| format!("Failed to read template config {}: {}", path, e))?;
    let config: TemplateConfigFile = serde_json::from_str(&content)
        .map_err(|e| format!("Failed to parse template config {}: {}", path, e))?;

    Ok(config
        .template_groupings
        .unwrap_or_default()
        .into_iter()
        .map(|group| TemplateGroupingInfo {
            id: group.id.unwrap_or_else(|| group.name.clone().unwrap_or_default()),
            name: group.name.unwrap_or_default(),
            brands: group.brands.unwrap_or_default(),
        })
        .collect())
}

#[command]
pub fn save_template_groupings(
    config_path: Option<String>,
    brands: Vec<String>,
) -> Result<SaveTemplateGroupingsResult, String> {
    let path = config_path
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| DEFAULT_TEMPLATE_CONFIG_PATH.to_string());
    let content = fs::read_to_string(&path)
        .map_err(|e| format!("Failed to read template config {}: {}", path, e))?;
    let mut config: serde_json::Value = serde_json::from_str(&content)
        .map_err(|e| format!("Failed to parse template config {}: {}", path, e))?;
    let groupings = config
        .get_mut("TemplateGroupings")
        .and_then(serde_json::Value::as_array_mut)
        .ok_or_else(|| format!("TemplateGroupings array is missing in {}", path))?;

    let existing: std::collections::HashSet<String> = groupings
        .iter()
        .filter_map(|group| {
            group.get("Name").and_then(serde_json::Value::as_str)
                .or_else(|| group.get("Id").and_then(serde_json::Value::as_str))
        })
        .map(|s| s.trim().to_lowercase())
        .collect();

    let mut added = 0;
    for brand in &brands {
        let trimmed = brand.trim();
        if trimmed.is_empty()
            || trimmed.eq_ignore_ascii_case("ALL")
            || trimmed.eq_ignore_ascii_case("None")
            || existing.contains(&trimmed.to_lowercase())
        {
            continue;
        }
        groupings.push(serde_json::json!({
            "Id": trimmed,
            "Name": trimmed,
            "Brands": vec![trimmed]
        }));
        added += 1;
    }

    if added > 0 {
        let updated = serde_json::to_string_pretty(&config)
            .map_err(|e| format!("Failed to serialize template config {}: {}", path, e))?;
        fs::write(&path, updated)
            .map_err(|e| format!("Failed to write template config {}: {}", path, e))?;
    }

    Ok(SaveTemplateGroupingsResult { added })
}

#[derive(Serialize)]
pub struct SaveSponsorGroupingsResult {
    pub added: usize,
}

#[command]
pub fn save_sponsor_groupings(config_path: Option<String>, names: Vec<String>) -> Result<SaveSponsorGroupingsResult, String> {
    let path = config_path
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| DEFAULT_SPONSOR_CONFIG_PATH.to_string());
    let content = fs::read_to_string(&path)
        .map_err(|e| format!("Failed to read sponsor config {}: {}", path, e))?;
    let mut config: serde_json::Value = serde_json::from_str(&content)
        .map_err(|e| format!("Failed to parse sponsor config {}: {}", path, e))?;
    let groupings = config
        .get_mut("SponsorGroupings")
        .and_then(serde_json::Value::as_array_mut)
        .ok_or_else(|| format!("SponsorGroupings array is missing in {}", path))?;
    let existing: std::collections::HashSet<String> = groupings
        .iter()
        .filter_map(|group| group.get("Name").and_then(serde_json::Value::as_str))
        .map(|name| name.trim().to_lowercase())
        .collect();
    let mut added = 0;
    for name in names {
        let trimmed = name.trim();
        if trimmed.is_empty() || trimmed.eq_ignore_ascii_case("ALL") || trimmed.eq_ignore_ascii_case("NONE") || existing.contains(&trimmed.to_lowercase()) {
            continue;
        }
        groupings.push(serde_json::json!({
            "Id": format!("fmss-{}", added + groupings.len()),
            "Name": trimmed,
            "Subsponsors": [],
            "CompoundName": format!("{} (0 items)", trimmed)
        }));
        added += 1;
    }
    if added > 0 {
        let updated = serde_json::to_string_pretty(&config)
            .map_err(|e| format!("Failed to serialize sponsor config {}: {}", path, e))?;
        fs::write(&path, updated)
            .map_err(|e| format!("Failed to write sponsor config {}: {}", path, e))?;
    }
    Ok(SaveSponsorGroupingsResult { added })
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClubBadgeQuery {
    pub id: String,
    pub name: String,
    #[serde(alias = "existing_path")]
    pub existing_path: Option<String>,
    pub paired_id: Option<String>,
}

#[derive(Serialize)]
pub struct BadgeDirectoryInfo {
    pub exists: bool,
    pub count: usize,
    pub subfolders: usize,
    pub sample: Vec<String>,
}

fn is_badge_file(name: &str) -> bool {
    let lower = name.to_lowercase();
    lower.ends_with(".png") || lower.ends_with(".svg") || lower.ends_with(".jpg") || lower.ends_with(".jpeg")
}

fn should_skip_dir(name: &str) -> bool {
    let lower = name.to_lowercase();
    lower.starts_with('.') || lower == "node_modules" || lower == "$recycle.bin" || lower == "system volume information"
}

fn badge_file_score(path: &std::path::Path) -> i32 {
    let mut score = 0;
    let s = path.to_string_lossy().to_lowercase();
    if s.contains("\\normal\\") || s.contains("/normal/") {
        score += 30;
    } else if s.contains("\\large\\") || s.contains("/large/") || s.contains("\\huge\\") || s.contains("/huge/") {
        score += 25;
    } else if s.contains("\\clubs\\") || s.contains("/clubs/") {
        score += 15;
    } else if s.contains("\\logos\\") || s.contains("/logos/") {
        score += 10;
    }

    if s.contains("\\small\\") || s.contains("/small/") || s.contains("\\icon\\") || s.contains("/icon/") || s.contains("\\mini\\") || s.contains("/mini/") {
        score -= 40;
    }

    if s.ends_with(".png") {
        score += 5;
    } else if s.ends_with(".svg") {
        score += 4;
    }

    score
}

// Valid 1x1 transparent PNG bytes (120 bytes, valid IDAT CRC for ImageMagick)
pub const TRANSPARENT_PNG: [u8; 120] = [
    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48,
    0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00,
    0x00, 0x1f, 0x15, 0xc4, 0x89, 0x00, 0x00, 0x00, 0x01, 0x73, 0x52, 0x47, 0x42, 0x00,
    0xae, 0xce, 0x1c, 0xe9, 0x00, 0x00, 0x00, 0x04, 0x67, 0x41, 0x4d, 0x41, 0x00, 0x00,
    0xb1, 0x8f, 0x0b, 0xfc, 0x61, 0x05, 0x00, 0x00, 0x00, 0x09, 0x70, 0x48, 0x59, 0x73,
    0x00, 0x00, 0x0e, 0xc3, 0x00, 0x00, 0x0e, 0xc3, 0x01, 0xc7, 0x6f, 0xa8, 0x64, 0x00,
    0x00, 0x00, 0x0d, 0x49, 0x44, 0x41, 0x54, 0x18, 0x57, 0x63, 0xf8, 0xff, 0xff, 0x3f,
    0x03, 0x00, 0x08, 0xfc, 0x02, 0xfe, 0x88, 0x5f, 0x06, 0xe0, 0x00, 0x00, 0x00, 0x00,
    0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
];

#[command]
pub fn resolve_badges(
    badge_dir: String,
    clubs: Vec<ClubBadgeQuery>,
) -> Result<std::collections::HashMap<String, String>, String> {
    let base_dir = PathBuf::from(&badge_dir);
    if !base_dir.exists() {
        let _ = fs::create_dir_all(&base_dir);
    }

    // Ensure a default fallback badge exists so Kitbasher never fails File.Exists check
    let default_badge_path = base_dir.join("default_badge.png");
    let needs_write = match fs::metadata(&default_badge_path) {
        Ok(m) => m.len() != 120,
        Err(_) => true,
    };
    if needs_write {
        let _ = fs::write(&default_badge_path, &TRANSPARENT_PNG);
    }

    // Recursively scan badge directory and all subfolders (up to 12 levels)
    let mut scanned_files: Vec<PathBuf> = Vec::new();
    let mut config_files: Vec<PathBuf> = Vec::new();
    let mut stack: Vec<(PathBuf, usize)> = vec![(base_dir.clone(), 0)];

    while let Some((current_dir, depth)) = stack.pop() {
        if depth > 12 {
            continue;
        }
        if let Ok(entries) = fs::read_dir(&current_dir) {
            for entry in entries.flatten() {
                if let Ok(ft) = entry.file_type() {
                    let fname = entry.file_name();
                    let name_str = fname.to_string_lossy();
                    if ft.is_dir() {
                        if !should_skip_dir(&name_str) {
                            stack.push((entry.path(), depth + 1));
                        }
                    } else if ft.is_file() {
                        if is_badge_file(&name_str) {
                            scanned_files.push(entry.path());
                        } else if name_str.eq_ignore_ascii_case("config.xml") {
                            config_files.push(entry.path());
                        }
                    }
                }
            }
        }
    }

    let mut config_xml_map: std::collections::HashMap<String, PathBuf> = std::collections::HashMap::new();
    for cfg in config_files {
        let parent = match cfg.parent() {
            Some(p) => p.to_path_buf(),
            None => continue,
        };
        if let Ok(content) = fs::read_to_string(&cfg) {
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
                    } else { continue; }
                } else { continue; };

                let to_str = if let Some(idx) = record.find("to=\"") {
                    let start = idx + 4;
                    if let Some(end) = record[start..].find('"') {
                        &record[start..start + end]
                    } else { continue; }
                } else { continue; };

                if to_str.contains("/background") { continue; }

                let prefix = "graphics/pictures/club/";
                let club_part = if let Some(idx) = to_str.find(prefix) {
                    &to_str[idx + prefix.len()..]
                } else { continue; };

                let slash_idx = match club_part.find('/') {
                    Some(i) => i,
                    None => continue,
                };
                let club_id = &club_part[..slash_idx];
                if club_id.is_empty() || !club_id.chars().all(|c| c.is_ascii_digit()) {
                    continue;
                }

                for ext in &[".png", ".svg", ".jpg", ".jpeg", ""] {
                    let fname = format!("{}{}", from_str, ext);
                    let candidate = parent.join(&fname);
                    if candidate.is_file() {
                        config_xml_map.entry(club_id.to_string()).or_insert(candidate);
                        break;
                    }
                }
            }
        }
    }

    let graphics_badge_cache = crate::clubs::load_badge_cache();

    let mut files_by_name: std::collections::HashMap<String, PathBuf> = std::collections::HashMap::new();
    let mut files_by_stem: std::collections::HashMap<String, PathBuf> = std::collections::HashMap::new();
    let mut files_by_id: std::collections::HashMap<String, PathBuf> = std::collections::HashMap::new();
    let mut files_normalized: Vec<(String, PathBuf, i32)> = Vec::new();
    let mut score_map: std::collections::HashMap<String, i32> = std::collections::HashMap::new();

    for path in scanned_files {
        let score = badge_file_score(&path);
        if let Some(file_name) = path.file_name().and_then(|n| n.to_str()) {
            let name_lower = file_name.to_lowercase();
            let stem_lower = path.file_stem().and_then(|s| s.to_str()).unwrap_or("").to_lowercase();
            let norm_stem: String = stem_lower.chars().filter(|c| c.is_alphanumeric()).collect();

            // 1. By exact filename
            let old_score = score_map.get(&name_lower).copied().unwrap_or(i32::MIN);
            if score > old_score {
                score_map.insert(name_lower.clone(), score);
                files_by_name.insert(name_lower.clone(), path.clone());
            }

            // 2. By stem
            let stem_key = format!("stem:{}", stem_lower);
            let old_stem_score = score_map.get(&stem_key).copied().unwrap_or(i32::MIN);
            if score > old_stem_score {
                score_map.insert(stem_key, score);
                files_by_stem.insert(stem_lower.clone(), path.clone());
            }

            // 3. By ID if stem is an ID or starts/ends with an ID
            let mut extracted_id: Option<String> = None;
            if stem_lower.chars().all(|c| c.is_ascii_digit()) && !stem_lower.is_empty() {
                extracted_id = Some(stem_lower.clone());
            } else if let Some(id_part) = stem_lower.strip_prefix("club_").or_else(|| stem_lower.strip_prefix("logo_")) {
                if id_part.chars().all(|c| c.is_ascii_digit()) && !id_part.is_empty() {
                    extracted_id = Some(id_part.to_string());
                }
            } else {
                let prefix_digits: String = stem_lower.chars().take_while(|c| c.is_ascii_digit()).collect();
                if prefix_digits.len() >= 2 {
                    let rest = &stem_lower[prefix_digits.len()..];
                    if rest.starts_with('_') || rest.starts_with(' ') || rest.starts_with('-') {
                        extracted_id = Some(prefix_digits);
                    }
                }
            }

            if let Some(id) = extracted_id {
                let id_key = format!("id:{}", id);
                let old_id_score = score_map.get(&id_key).copied().unwrap_or(i32::MIN);
                if score > old_id_score {
                    score_map.insert(id_key, score);
                    files_by_id.insert(id, path.clone());
                }
            }

            // 4. Normalized stem for club name matching
            if !norm_stem.is_empty() {
                files_normalized.push((norm_stem, path.clone(), score));
            }
        }
    }

    files_normalized.sort_by(|a, b| b.2.cmp(&a.2));

    let mut results = std::collections::HashMap::new();

    for c in clubs {
        let mut resolved_path: Option<PathBuf> = None;

        // 1. Check existing_path if given, valid, and not default fallback
        if let Some(p) = &c.existing_path {
            let p_lower = p.to_lowercase();
            let is_fallback = p_lower.ends_with("default_badge.png") || p_lower == "default_badge.png";
            if !is_fallback {
                let existing = PathBuf::from(p);
                if existing.exists() {
                    resolved_path = Some(existing);
                } else if let Some(fname) = existing.file_name().and_then(|n| n.to_str()) {
                    let name_lower = fname.to_lowercase();
                    if let Some(matched) = files_by_name.get(&name_lower) {
                        resolved_path = Some(matched.clone());
                    }
                }
            }
        }

        // 2. Check config.xml mapping & graphics badge cache for club ID and paired ID
        if resolved_path.is_none() && !c.id.is_empty() {
            let mut ids_to_check = vec![c.id.clone()];
            if let Some(ref pid) = c.paired_id {
                if !pid.is_empty() {
                    ids_to_check.push(pid.clone());
                }
            }
            if let Ok(num) = c.id.parse::<u64>() {
                if num >= 2000000000 {
                    ids_to_check.push((num - 2000000000).to_string());
                } else {
                    ids_to_check.push((num + 2000000000).to_string());
                }
            }

            for check_id in &ids_to_check {
                if let Some(mapped) = config_xml_map.get(check_id) {
                    if mapped.exists() {
                        resolved_path = Some(mapped.clone());
                        break;
                    }
                }
                if let Some(cached_path) = graphics_badge_cache.get(check_id) {
                    let pb = PathBuf::from(cached_path);
                    if pb.exists() {
                        resolved_path = Some(pb);
                        break;
                    }
                }
            }
        }

        // 3. Check by direct ID in files (e.g. 602.png, 602.svg, club_602.png in any subfolder)
        if resolved_path.is_none() && !c.id.is_empty() {
            let mut ids_to_check = vec![c.id.clone()];
            if let Some(ref pid) = c.paired_id {
                if !pid.is_empty() {
                    ids_to_check.push(pid.clone());
                }
            }
            if let Ok(num) = c.id.parse::<u64>() {
                if num >= 2000000000 {
                    ids_to_check.push((num - 2000000000).to_string());
                } else {
                    ids_to_check.push((num + 2000000000).to_string());
                }
            }
            
            for check_id in ids_to_check {
                if let Some(matched) = files_by_id.get(&check_id) {
                    resolved_path = Some(matched.clone());
                    break;
                } else if let Some(matched) = files_by_stem.get(&check_id.to_lowercase()) {
                    resolved_path = Some(matched.clone());
                    break;
                } else {
                    let mut found = false;
                    for ext in &[".png", ".svg", ".jpg", ".jpeg"] {
                        let candidate1 = format!("{}{}", check_id.to_lowercase(), ext);
                        if let Some(matched) = files_by_name.get(&candidate1) {
                            resolved_path = Some(matched.clone());
                            found = true;
                            break;
                        }
                        let candidate2 = format!("club_{}{}", check_id.to_lowercase(), ext);
                        if let Some(matched) = files_by_name.get(&candidate2) {
                            resolved_path = Some(matched.clone());
                            found = true;
                            break;
                        }
                    }
                    if found { break; }
                }
            }
        }

        // 3. Check by normalized club name
        if resolved_path.is_none() && !c.name.is_empty() {
            let norm_club: String = c.name.chars().filter(|c| c.is_alphanumeric()).collect::<String>().to_lowercase();
            if !norm_club.is_empty() {
                let norm_club_with_logo = format!("{}logo", norm_club);
                for (norm_stem, path, _score) in &files_normalized {
                    if norm_stem == &norm_club || norm_stem == &norm_club_with_logo {
                        resolved_path = Some(path.clone());
                        break;
                    }
                }
                if resolved_path.is_none() && norm_club.len() >= 4 {
                    for (norm_stem, path, _score) in &files_normalized {
                        if norm_stem.starts_with(&norm_club) || (norm_club.len() >= 6 && norm_stem.contains(&norm_club)) {
                            resolved_path = Some(path.clone());
                            break;
                        }
                    }
                }
            }
        }

        let final_path = resolved_path.unwrap_or_else(|| default_badge_path.clone());
        results.insert(c.id, final_path.to_string_lossy().to_string());
    }

    Ok(results)
}

#[command]
pub fn save_teams_csv(
    csv_content: String,
    filename: Option<String>,
    directory: Option<String>,
    full_path: Option<String>,
) -> Result<String, String> {
    log_to_file(&format!(
        "save_teams_csv called: filename={:?}, directory={:?}, full_path={:?}, content_len={}",
        filename, directory, full_path, csv_content.len()
    ));
    let mut target_file = if let Some(fp) = full_path.filter(|p| !p.trim().is_empty()) {
        PathBuf::from(fp)
    } else {
        let target_dir = directory
            .filter(|d| !d.trim().is_empty())
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(r"D:\KitbasherLegacy v0.8\TeamList"));
        let _ = fs::create_dir_all(&target_dir);
        let fname = filename
            .filter(|f| !f.trim().is_empty())
            .unwrap_or_else(|| "teams.csv".to_string());
        target_dir.join(fname)
    };

    let mut path_str = target_file.to_string_lossy().to_string();
    if path_str.to_lowercase().ends_with(".cvs") {
        path_str.truncate(path_str.len() - 4);
        path_str.push_str(".csv");
        target_file = PathBuf::from(path_str);
    } else if !path_str.to_lowercase().ends_with(".csv") {
        path_str.push_str(".csv");
        target_file = PathBuf::from(path_str);
    }

    if let Some(parent) = target_file.parent() {
        let _ = fs::create_dir_all(parent);
    }
    fs::write(&target_file, csv_content.as_bytes())
        .map_err(|e| {
            let err = format!("Failed to save CSV to {}: {}", target_file.display(), e);
            log_to_file(&err);
            err
        })?;
    log_to_file(&format!("Successfully saved teams CSV to {}", target_file.display()));
    Ok(target_file.to_string_lossy().to_string())
}

#[command]
pub fn save_kitbasher_zip(
    cache_json: String,
    archive_path: String,
    badge_paths: Vec<String>,
) -> Result<String, String> {
    if archive_path.trim().is_empty() {
        return Err("A ZIP save path is required".to_string());
    }

    let mut path_str = archive_path.trim().to_string();
    if !path_str.to_lowercase().ends_with(".zip") {
        path_str.push_str(".zip");
    }
    let target_file = PathBuf::from(path_str);

    if let Some(parent) = target_file.parent() {
        if !parent.as_os_str().is_empty() {
            fs::create_dir_all(parent).map_err(|e| {
                format!("Failed to create ZIP directory {}: {}", parent.display(), e)
            })?;
        }
    }

    let mut badge_files: std::collections::HashMap<String, (String, PathBuf, String)> =
        std::collections::HashMap::new();
    for raw_path in badge_paths {
        if raw_path.trim().is_empty() {
            continue;
        }
        let source_path = PathBuf::from(&raw_path);
        let file_name = source_path
            .file_name()
            .and_then(|name| name.to_str())
            .filter(|name| !name.is_empty())
            .unwrap_or("default_badge.png")
            .to_string();

        let is_default = file_name.eq_ignore_ascii_case("default_badge.png");
        if !source_path.is_file() {
            if is_default {
                if let Some(parent) = source_path.parent() {
                    let _ = fs::create_dir_all(parent);
                }
                let _ = fs::write(&source_path, &TRANSPARENT_PNG);
            } else {
                eprintln!("Warning: badge file does not exist: {}", source_path.display());
                continue;
            }
        }

        let name_key = file_name.to_lowercase();
        let source_key = fs::canonicalize(&source_path)
            .unwrap_or_else(|_| source_path.clone())
            .to_string_lossy()
            .to_lowercase();

        if let Some((_, existing_path, existing_key)) = badge_files.get(&name_key) {
            if existing_key != &source_key {
                return Err(format!(
                    "Badge filename collision in ZIP: {} ({}) and {}",
                    file_name,
                    existing_path.display(),
                    source_path.display()
                ));
            }
            continue;
        }

        badge_files.insert(name_key, (file_name, source_path, source_key));
    }

    let file = fs::File::create(&target_file)
        .map_err(|e| format!("Failed to create ZIP {}: {}", target_file.display(), e))?;
    let mut zip = ZipWriter::new(file);
    let options = || FileOptions::default().compression_method(CompressionMethod::Deflated);

    zip.start_file("cache.json", options())
        .map_err(|e| format!("Failed to add cache.json to ZIP: {}", e))?;
    zip.write_all(cache_json.as_bytes())
        .map_err(|e| format!("Failed to write cache.json to ZIP: {}", e))?;

    // Kitbasher's Import_Teams_List_Click specifically uses:
    // ZipArchive.GetEntry(Path.Combine("Badges", Path.GetFileName(kit.BadgePath)))
    // On Windows, Path.Combine produces a backslash: "Badges\club_xxx.png".
    // We write both the Windows backslash entry (for Kitbasher on Windows)
    // and standard forward-slash entry (for generic ZIP tools and cross-platform extractors).
    for (file_name, source_path, _) in badge_files.values() {
        let badge_bytes = if file_name.eq_ignore_ascii_case("default_badge.png") && !source_path.is_file() {
            TRANSPARENT_PNG.to_vec()
        } else {
            fs::read(source_path).unwrap_or_else(|_| TRANSPARENT_PNG.to_vec())
        };

        // 1. Windows backslash entry required by Kitbasher's Path.Combine("Badges", ...)
        let win_entry = format!(r"Badges\{}", file_name);
        zip.start_file(&win_entry, options())
            .map_err(|e| format!("Failed to add badge {} to ZIP: {}", file_name, e))?;
        zip.write_all(&badge_bytes)
            .map_err(|e| format!("Failed to write badge {} to ZIP: {}", file_name, e))?;

        // 2. Forward-slash entry for standard ZIP utilities
        let unix_entry = format!("Badges/{}", file_name);
        zip.start_file(&unix_entry, options())
            .map_err(|e| format!("Failed to add badge {} to ZIP: {}", file_name, e))?;
        zip.write_all(&badge_bytes)
            .map_err(|e| format!("Failed to write badge {} to ZIP: {}", file_name, e))?;
    }

    // Ensure default_badge.png is in the ZIP archive if cache.json mentions it and wasn't added
    if cache_json.contains("default_badge.png") && !badge_files.contains_key("default_badge.png") {
        let win_entry = r"Badges\default_badge.png";
        zip.start_file(win_entry, options())
            .map_err(|e| format!("Failed to add default_badge.png to ZIP: {}", e))?;
        zip.write_all(&TRANSPARENT_PNG)
            .map_err(|e| format!("Failed to write default_badge.png to ZIP: {}", e))?;

        let unix_entry = "Badges/default_badge.png";
        zip.start_file(unix_entry, options())
            .map_err(|e| format!("Failed to add default_badge.png to ZIP: {}", e))?;
        zip.write_all(&TRANSPARENT_PNG)
            .map_err(|e| format!("Failed to write default_badge.png to ZIP: {}", e))?;
    }

    zip.finish()
        .map_err(|e| format!("Failed to finish ZIP {}: {}", target_file.display(), e))?;
    log_to_file(&format!(
        "Successfully saved Kitbasher ZIP to {} ({} badges)",
        target_file.display(),
        badge_files.len()
    ));
    Ok(target_file.to_string_lossy().to_string())
}

#[command]
pub fn select_directory(title: Option<String>, default_path: Option<String>) -> Result<Option<String>, String> {
    let mut dialog = rfd::FileDialog::new();
    if let Some(t) = &title {
        dialog = dialog.set_title(t);
    }
    if let Some(p) = &default_path {
        if !p.is_empty() {
            let path = PathBuf::from(p);
            if path.exists() {
                dialog = dialog.set_directory(&path);
            }
        }
    }
    let res = dialog.pick_folder().map(|p| p.to_string_lossy().to_string());
    Ok(res)
}

#[command]
pub fn select_save_file(
    title: Option<String>,
    default_dir: Option<String>,
    default_filename: Option<String>,
    filter_name: Option<String>,
    filter_ext: Option<String>,
) -> Result<Option<String>, String> {
    log_to_file(&format!(
        "select_save_file called: title={:?}, default_dir={:?}, default_filename={:?}, filter_name={:?}, filter_ext={:?}",
        title, default_dir, default_filename, filter_name, filter_ext
    ));
    let mut dialog = rfd::FileDialog::new();
    if let Some(t) = &title {
        dialog = dialog.set_title(t);
    }
    if let Some(dir) = &default_dir {
        let trimmed = dir.trim();
        if !trimmed.is_empty() {
            let path = PathBuf::from(trimmed);
            if !path.exists() {
                let _ = fs::create_dir_all(&path);
            }
            if path.exists() {
                dialog = dialog.set_directory(&path);
            } else if let Some(parent) = path.parent() {
                if parent.exists() {
                    dialog = dialog.set_directory(parent);
                }
            }
        }
    }
    if let Some(fname) = &default_filename {
        let trimmed = fname.trim();
        if !trimmed.is_empty() {
            dialog = dialog.set_file_name(trimmed);
        }
    }
    if let (Some(name), Some(ext)) = (&filter_name, &filter_ext) {
        let exts = [ext.as_str()];
        dialog = dialog.add_filter(name, &exts);
    }
    let res = dialog.save_file().map(|p| {
        let mut path_str = p.to_string_lossy().to_string();
        if let Some(ext) = &filter_ext {
            let ext_lower = ext.to_lowercase();
            let target_ext = format!(".{}", ext_lower);
            if ext_lower == "csv" && path_str.to_lowercase().ends_with(".cvs") {
                path_str.truncate(path_str.len() - 4);
                path_str.push_str(".csv");
            } else if !path_str.to_lowercase().ends_with(&target_ext) {
                path_str.push_str(&target_ext);
            }
        }
        path_str
    });
    log_to_file(&format!("select_save_file returning: {:?}", res));
    Ok(res)
}

#[command]
pub fn select_open_file(
    title: Option<String>,
    default_dir: Option<String>,
    filter_name: Option<String>,
    filter_ext: Option<String>,
) -> Result<Option<String>, String> {
    log_to_file(&format!(
        "select_open_file called: title={:?}, default_dir={:?}, filter_name={:?}, filter_ext={:?}",
        title, default_dir, filter_name, filter_ext
    ));
    let mut dialog = rfd::FileDialog::new();
    if let Some(t) = &title {
        dialog = dialog.set_title(t);
    }
    if let Some(dir) = &default_dir {
        let trimmed = dir.trim();
        if !trimmed.is_empty() {
            let path = PathBuf::from(trimmed);
            if path.exists() {
                dialog = dialog.set_directory(&path);
            } else if let Some(parent) = path.parent() {
                if parent.exists() {
                    dialog = dialog.set_directory(parent);
                }
            }
        }
    }
    if let (Some(name), Some(ext)) = (&filter_name, &filter_ext) {
        let exts = [ext.as_str()];
        dialog = dialog.add_filter(name, &exts);
    }
    let res = dialog.pick_file().map(|p| p.to_string_lossy().to_string());
    log_to_file(&format!("select_open_file returning: {:?}", res));
    Ok(res)
}

#[command]
pub fn check_badge_directory(dir: String) -> Result<BadgeDirectoryInfo, String> {
    let path = PathBuf::from(&dir);
    if !path.exists() {
        return Ok(BadgeDirectoryInfo {
            exists: false,
            count: 0,
            subfolders: 0,
            sample: Vec::new(),
        });
    }
    let mut count = 0;
    let mut subfolders = 0;
    let mut sample = Vec::new();
    let mut stack: Vec<(PathBuf, usize)> = vec![(path.clone(), 0)];

    while let Some((current_dir, depth)) = stack.pop() {
        if depth > 12 {
            continue;
        }
        if let Ok(entries) = fs::read_dir(&current_dir) {
            for entry in entries.flatten() {
                if let Ok(ft) = entry.file_type() {
                    let fname = entry.file_name();
                    let name_str = fname.to_string_lossy();
                    if ft.is_dir() {
                        if !should_skip_dir(&name_str) {
                            subfolders += 1;
                            stack.push((entry.path(), depth + 1));
                        }
                    } else if ft.is_file() {
                        if is_badge_file(&name_str) {
                            count += 1;
                            if sample.len() < 5 {
                                sample.push(name_str.to_string());
                            }
                        }
                    }
                }
            }
        }
    }
    Ok(BadgeDirectoryInfo {
        exists: true,
        count,
        subfolders,
        sample,
    })
}

pub(crate) fn data_dir() -> PathBuf {
    get_data_dir()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_zip_kitbasher_entries() {
        let temp_dir = std::env::temp_dir();
        let test_badge = temp_dir.join("test_badge_fms.png");
        let _ = fs::write(&test_badge, b"dummy png content");

        let zip_path = temp_dir.join("test_kitbasher.zip");
        if zip_path.exists() {
            let _ = fs::remove_file(&zip_path);
        }

        let cache_json = r#"[{"Name":"Test Club","Kits":[{"BadgePath":"Badges\\test_badge_fms.png"}]}]"#.to_string();
        let res = save_kitbasher_zip(
            cache_json,
            zip_path.to_string_lossy().to_string(),
            vec![test_badge.to_string_lossy().to_string()],
        );
        assert!(res.is_ok(), "save_kitbasher_zip failed: {:?}", res);

        let zip_file = fs::File::open(&zip_path).unwrap();
        let mut archive = zip::ZipArchive::new(zip_file).unwrap();
        
        // Ensure both Windows backslash and Unix forward slash entries are present
        assert!(archive.by_name(r"Badges\test_badge_fms.png").is_ok(), "Backslash entry missing in ZIP");
        assert!(archive.by_name("Badges/test_badge_fms.png").is_ok(), "Forward-slash entry missing in ZIP");
        assert!(archive.by_name("cache.json").is_ok(), "cache.json missing in ZIP");

        let _ = fs::remove_file(&test_badge);
        let _ = fs::remove_file(&zip_path);
    }
}

