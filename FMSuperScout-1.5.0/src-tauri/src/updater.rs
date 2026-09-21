use serde::{Deserialize, Serialize};
use std::fs::File;
use std::io::Write;
use std::process::Command;
use std::sync::Mutex;
use lazy_static::lazy_static;

lazy_static! {
    static ref UPD_STATE: Mutex<UpdateStatus> = Mutex::new(UpdateStatus {
        phase: "idle".to_string(),
        pct: None,
        error: None,
    });
}

#[derive(Serialize, Deserialize, Clone)]
pub struct UpdateStatus {
    phase: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pct: Option<u8>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

pub fn status() -> UpdateStatus {
    UPD_STATE.lock().unwrap().clone()
}

pub fn update_install() -> Result<(), String> {
    return Err("Updater disabled in dev mode to prevent overwriting src-tauri!".to_string());
}

pub fn install() -> Result<bool, String> {
    let mut state = UPD_STATE.lock().unwrap();
    if state.phase == "idle" || state.phase == "error" {
        state.phase = "downloading".to_string();
        state.pct = Some(0);
        state.error = None;
        
        std::thread::spawn(|| {
            if let Err(e) = run_update() {
                let mut state = UPD_STATE.lock().unwrap();
                state.phase = "error".to_string();
                state.error = Some(e);
            }
        });
    }
    Ok(true)
}

fn run_update() -> Result<(), String> {
    let client = reqwest::blocking::Client::builder()
        .user_agent("FMSuperScout-updater")
        .build()
        .map_err(|e| e.to_string())?;
        
    let rel_resp = client.get("https://api.github.com/repos/mavarobli/FMSuperScout/releases/latest")
        .send().map_err(|e| e.to_string())?;
        
    let rel: serde_json::Value = rel_resp.json().map_err(|e| e.to_string())?;
    
    let assets = rel.get("assets").and_then(|a| a.as_array()).ok_or("No assets found")?;
    
    let mut exe_url = None;
    let mut exe_size = 0;
    let mut sha_url = None;
    
    for asset in assets {
        if let Some(name) = asset.get("name").and_then(|n| n.as_str()) {
            if name == "FMSuperScout-Setup.exe" {
                exe_url = asset.get("browser_download_url").and_then(|u| u.as_str()).map(|s| s.to_string());
                exe_size = asset.get("size").and_then(|s| s.as_u64()).unwrap_or(0);
            } else if name == "FMSuperScout-Setup.exe.sha256" {
                sha_url = asset.get("browser_download_url").and_then(|u| u.as_str()).map(|s| s.to_string());
            }
        }
    }
    
    let exe_url = exe_url.ok_or("Exe asset not found")?;
    let sha_url = sha_url.ok_or("SHA256 asset not found")?;
    
    let dir = crate::api::data_dir().join("update");
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    
    let exe_path = dir.join("FMSuperScout-Setup.exe");
    
    // Download exe
    let mut resp = client.get(&exe_url).send().map_err(|e| e.to_string())?;
    let mut file = File::create(&exe_path).map_err(|e| e.to_string())?;
    
    use sha2::{Sha256, Digest};
    let mut hasher = Sha256::new();
    
    let mut downloaded = 0;
    let mut chunk = [0; 64 * 1024];
    loop {
        use std::io::Read;
        let n = resp.read(&mut chunk).map_err(|e| e.to_string())?;
        if n == 0 { break; }
        
        file.write_all(&chunk[..n]).map_err(|e| e.to_string())?;
        hasher.update(&chunk[..n]);
        downloaded += n as u64;
        
        if exe_size > 0 {
            let pct = (downloaded * 100 / exe_size) as u8;
            let mut state = UPD_STATE.lock().unwrap();
            state.pct = Some(std::cmp::min(99, pct));
        }
    }
    
    {
        let mut state = UPD_STATE.lock().unwrap();
        state.phase = "verifying".to_string();
    }
    
    let expected_sha = client.get(&sha_url).send().map_err(|e| e.to_string())?.text().map_err(|e| e.to_string())?;
    let expected_sha = expected_sha.trim().split_whitespace().next().unwrap_or("").to_lowercase();
    
    let actual_sha = format!("{:x}", hasher.finalize());
    
    if expected_sha != actual_sha {
        let _ = std::fs::remove_file(&exe_path);
        return Err("SHA-256 comes out different, download deleted".to_string());
    }
    
    {
        let mut state = UPD_STATE.lock().unwrap();
        state.phase = "launching".to_string();
    }
    
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        Command::new(&exe_path)
            .creation_flags(0x08000000) // CREATE_NO_WINDOW
            .spawn()
            .map_err(|e| format!("Failed to spawn installer: {}", e))?;
    }
    #[cfg(not(target_os = "windows"))]
    {
        Command::new(&exe_path)
            .spawn()
            .map_err(|e| format!("Failed to spawn installer: {}", e))?;
    }
    
    std::thread::sleep(std::time::Duration::from_millis(1500));
    std::process::exit(0);
}
