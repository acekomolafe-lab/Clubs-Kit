# Agent Guidelines for FMSuperScout

## Mandatory Rules

### Always Build After Editing
Whenever source code is modified in the repository or related subprojects, always execute the corresponding build command immediately to verify that code compiles cleanly and tests/types pass:

- **FMSuperScout Rust Backend (`FMSuperScout-1.5.0/src-tauri`)**:
  - Run `cargo build`
- **FM26 RTE Player Editor Backend (`D:\Projects\fm26rte\apps\fm26-player-editor\src-tauri`)**:
  - Run `cargo build`
- **FM26 RTE Player Editor Frontend (`D:\Projects\fm26rte\apps\fm26-player-editor`)**:
  - Run `npm run build`
- **Node Servers & Scripts**:
  - Run syntax/execution tests against live data
