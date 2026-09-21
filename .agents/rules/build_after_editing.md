# Rule: Always Build After Editing

Always execute the corresponding build command(s) immediately after editing source code in the project to verify compilation, type safety, and bundling:

1. **Rust / Tauri Backends**:
   - Run `cargo build` (or `cargo check`) in the relevant `src-tauri` directory (e.g. `d:\Projects\FMSuperScout-1.5.0\FMSuperScout-1.5.0\src-tauri` or `D:\Projects\fm26rte\apps\fm26-player-editor\src-tauri`).
2. **Frontend Applications**:
   - Run `npm run build` in the relevant frontend directory (e.g. `D:\Projects\fm26rte\apps\fm26-player-editor`) to ensure TypeScript type checking and asset bundling succeed without errors.
3. **Verification Integrity**:
   - Always wait for build completion and resolve any compile/type errors before reporting back to the user.
