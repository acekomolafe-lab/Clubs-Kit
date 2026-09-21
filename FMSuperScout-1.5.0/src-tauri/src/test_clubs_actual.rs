use app::api;
use app::clubs;
use std::path::PathBuf;

fn main() {
    let p = PathBuf::from(std::env::var("LOCALAPPDATA").unwrap()).join("FMSuperScout").join("dump.json");
    let rt = tokio::runtime::Runtime::new().unwrap();
    rt.block_on(async {
        match clubs::get_database_clubs().await {
            Ok(v) => {
                if let Some(c) = v.get("clubs").and_then(|a| a.as_array()) {
                    println!("Success: extracted {} clubs.", c.len());
                } else {
                    println!("Success, but 'clubs' key is not an array. Value: {:?}", v);
                }
            }
            Err(e) => {
                println!("Error: {}", e);
            }
        }
    });
}
