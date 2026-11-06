// The launcher checks repairs with the same key as the app's updater: read it from tauri.conf.json.
fn main() {
    let conf = "../src-tauri/tauri.conf.json";
    println!("cargo:rerun-if-changed={conf}");
    let text = std::fs::read_to_string(conf).expect("terminal/src-tauri/tauri.conf.json");
    let json: serde_json::Value = serde_json::from_str(&text).expect("tauri.conf.json is JSON");
    let key = json["plugins"]["updater"]["pubkey"].as_str().expect("plugins.updater.pubkey in tauri.conf.json");
    println!("cargo:rustc-env=TAUSI_UPDATER_PUBKEY={key}");
}
