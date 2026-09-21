use std::path::PathBuf;
use std::fs::File;
use std::io::{BufReader, Read};
use std::collections::HashMap;
use serde_json::Value;

fn main() {
    let p = PathBuf::from(std::env::var("LOCALAPPDATA").unwrap()).join("FMSuperScout").join("dump.json");
    
    let file = File::open(&p).unwrap();
    let mut reader = BufReader::with_capacity(1024 * 1024, file);
    
    let mut buf = String::with_capacity(256 * 1024);
    let mut stage = 0;
    let mut scanned = 0;
    let mut obj_start: isize = -1;
    let mut in_str = false;
    let mut esc = false;
    let mut depth = 0;
    
    let mut by_club: HashMap<String, i32> = HashMap::new();
    let mut chunk = [0u8; 64 * 1024];
    
    while stage != 2 {
        let n = reader.read(&mut chunk).unwrap();
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
                    // Extract club name
                    let obj_text = &buf[obj_start as usize..i + 1];
                    // Very simple parsing for testing
                    if obj_text.contains("\"club\":\"") {
                        *by_club.entry("found".to_string()).or_insert(0) += 1;
                    }
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
    
    println!("Found {} clubs", by_club.get("found").unwrap_or(&0));
}
