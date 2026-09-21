import re

with open('src/updater.rs', 'r') as f:
    content = f.read()

# Remove the duplicated UPD_STATE definitions and replace with a clean one
content = re.sub(r'lazy_static! \{.*?\}\n', '', content, flags=re.DOTALL)

clean_header = """use serde::{Deserialize, Serialize};
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
"""
content = clean_header + "\n" + content[content.find("pub fn status() -> UpdateStatus {"):]

with open('src/updater.rs', 'w') as f:
    f.write(content)
