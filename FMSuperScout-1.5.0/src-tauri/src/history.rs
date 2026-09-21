use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap};
use std::fs;
use std::path::PathBuf;

use crate::api::data_dir;

const HIST_MAX_DATES: usize = 120;

#[derive(Serialize, Deserialize, Clone)]
pub struct History {
    manager: String,
    dates: Vec<String>,
    players: HashMap<String, BTreeMap<String, (u16, u16, Option<i64>)>>,
}

fn hist_slug(manager: &str) -> String {
    let s = manager.to_lowercase().replace(|c: char| !c.is_ascii_alphanumeric(), "-");
    let s = s.trim_matches('-');
    if s.is_empty() {
        "default".to_string()
    } else {
        s.chars().take(60).collect()
    }
}

fn hist_file(manager: &str) -> PathBuf {
    data_dir().join("history").join(format!("{}.json", hist_slug(manager)))
}

fn load_hist(manager: &str) -> History {
    let file = hist_file(manager);
    if let Ok(content) = fs::read_to_string(&file) {
        if let Ok(h) = serde_json::from_str(&content) {
            return h;
        }
    }
    History {
        manager: manager.to_string(),
        dates: Vec::new(),
        players: HashMap::new(),
    }
}

fn hist_last_point(entries: &BTreeMap<String, (u16, u16, Option<i64>)>, dates: &[String]) -> Option<(u16, u16, Option<i64>)> {
    for d in dates.iter().rev() {
        if let Some(e) = entries.get(d) {
            return Some(e.clone());
        }
    }
    None
}

pub fn merge(manager: String, game_date: String, new_players: HashMap<String, (u16, u16, Option<i64>)>) -> Result<serde_json::Value, String> {
    let mut h = load_hist(&manager);
    
    if let Some(last) = h.dates.last() {
        if last >= &game_date {
            let drop: Vec<String> = h.dates.iter().filter(|&d| d >= &game_date).cloned().collect();
            h.dates.retain(|d| d < &game_date);
            let mut empty_players = Vec::new();
            for (uid, e) in h.players.iter_mut() {
                for d in &drop {
                    e.remove(d);
                }
                if e.is_empty() {
                    empty_players.push(uid.clone());
                }
            }
            for uid in empty_players {
                h.players.remove(&uid);
            }
        }
    }
    
    let mut added = 0;
    for (uid, (ca, pa, val)) in new_players {
        let entries = h.players.entry(uid).or_insert_with(BTreeMap::new);
        let prev = hist_last_point(entries, &h.dates);
        
        let val_jump = if let Some(p) = &prev {
            let p_val = p.2.unwrap_or(0);
            let n_val = val.unwrap_or(0);
            (n_val - p_val).abs() > std::cmp::max(10000, (p_val as f64 * 0.025) as i64)
        } else {
            true
        };
        
        if let Some(p) = &prev {
            if p.0 == ca && p.1 == pa && !val_jump {
                continue;
            }
        }
        entries.insert(game_date.clone(), (ca, pa, val));
        added += 1;
    }
    h.dates.push(game_date.clone());
    
    if h.dates.len() > HIST_MAX_DATES {
        let cut = h.dates.len() - HIST_MAX_DATES;
        let dropped = h.dates[..cut].to_vec();
        let new_base = h.dates[cut].clone();
        
        let mut empty_players = Vec::new();
        for (uid, e) in h.players.iter_mut() {
            let mut base = None;
            for d in &dropped {
                if let Some(val) = e.remove(d) {
                    base = Some(val);
                }
            }
            if let Some(b) = base {
                if !e.contains_key(&new_base) {
                    e.insert(new_base.clone(), b);
                }
            }
            if e.is_empty() {
                empty_players.push(uid.clone());
            }
        }
        for uid in empty_players {
            h.players.remove(&uid);
        }
        h.dates.drain(..cut);
    }
    
    let hist_dir = data_dir().join("history");
    let _ = fs::create_dir_all(&hist_dir);
    let file = hist_file(&manager);
    let tmp = file.with_extension("json.tmp");
    fs::write(&tmp, serde_json::to_string(&h).map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
    fs::rename(&tmp, &file).map_err(|e| e.to_string())?;
    
    Ok(serde_json::json!({
        "ok": true,
        "dates": h.dates.len(),
        "added": added
    }))
}

pub fn get_deltas(manager: String, since: Option<String>) -> Result<serde_json::Value, String> {
    let h = load_hist(&manager);
    
    if since.is_none() || since.as_ref().unwrap().is_empty() {
        return Ok(serde_json::json!({
            "dates": h.dates,
            "ref": null,
            "p": {}
        }));
    }
    
    let since = since.unwrap();
    let mut idx = HashMap::new();
    for (i, d) in h.dates.iter().enumerate() {
        idx.insert(d.clone(), i);
    }
    
    let mut p = HashMap::new();
    for (uid, e) in &h.players {
        if e.is_empty() { continue; }
        
        let mut ref_val = None;
        let mut first_key = None;
        
        for (k, v) in e {
            if first_key.is_none() {
                first_key = Some(k.clone());
            }
            if k > &since {
                break;
            }
            ref_val = Some(v.clone());
        }
        
        let first_idx = first_key.and_then(|k| idx.get(&k)).copied().unwrap_or(0);
        
        if let Some((ca, pa, _)) = ref_val {
            p.insert(uid, (Some(ca), Some(pa), first_idx));
        } else {
            let none_val: Option<u16> = None;
            p.insert(uid, (none_val, none_val, first_idx));
        }
    }
    
    Ok(serde_json::json!({
        "dates": h.dates,
        "ref": since,
        "p": p
    }))
}

pub fn get_series(manager: String, uid: String) -> Result<serde_json::Value, String> {
    let h = load_hist(&manager);
    let entries = h.players.get(&uid).cloned().unwrap_or_else(BTreeMap::new);
    Ok(serde_json::json!({
        "dates": h.dates,
        "entries": entries
    }))
}
