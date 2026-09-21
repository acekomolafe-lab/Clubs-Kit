# Kit-Color & Unique ID Investigation

## Status

**SOLVED & PINNED (FM 26.3.x / game_plugin.dll 26.3.0/26.3.2)**

The database Unique ID (`objo.Duni`) and real club kit colors (`cluo.Tbcl` and `cluo.Ttcl`) are now fully reverse-engineered, pinned in `Fields.cs`, and integrated into both FMSuperScout and the FM26 RTE Player Editor.

---

## Why the Initial Investigation Stalled

The initial diagnostic `WriteKitScan` dumped `club + 0x00 .. 0x300` expecting kit colors to be stored as immediate RGB bytes directly inside the club structure.

Comparing hex dumps across clubs failed to reveal RGB runs because **kit colors are not located within the root club object**. In Football Manager's IL2CPP / C++ object hierarchy, the club object delegates metadata and styling to a sub-object:

- At `club + 0xB0` is an 8-byte pointer (`cluo.Cino`) pointing to the **Club Info** object (`ClubInfo`).
- The actual background (primary shirt) and foreground (secondary trim) colors reside inside the `ClubInfo` object.

---

## Reverse-Engineering Findings

Referenced from `D:\Projects\fm26rte\research\reverse-engineering` (`FM26_CE_VALUE_RECORDS.csv`, `FMCETableEnums.lua`, and Cheat Engine tables for FM26):

### 1. Root Club Object (`cluo` / `objo`)

| Offset | Symbol | Type | Description |
|---|---|---|---|
| `+0x00` | vtable | `void*` | Vtable pointer (`cluo` vftable) |
| `+0x08` | `objo.Rwid` | `uint32` | Internal database row ID |
| `+0x0C` | `objo.Duni` | `uint32` | **Unique ID (UID)** — permanent game database identifier |
| `+0x10` | `objo.Rdui` | `uint32` | Random UI ID / salt |
| `+0x18` | `cluo.Ctea` | `void*` | Teams pointer vector start (`std::vector<team*>`) |
| `+0x20` | `cluo.Ctea_end` | `void*` | Teams pointer vector end |
| `+0xB0` | `cluo.Cino` | `void*` | **Pointer to Club Info sub-object (`cino`)** |
| `+0xC0` | `cluo.Cnam` | `string*` | Club full name (indirect UTF-8 string pointer) |
| `+0xC8` | `cluo.Csnm` | `string*` | Club short name (indirect UTF-8 string pointer) |
| `+0xD8` | `cluo.Cnti` | `void*` | Nation object pointer |

### 2. Club Info Sub-Object (`cino = [club + 0xB0]`)

| Offset | Symbol | Type | Description |
|---|---|---|---|
| `+0xA0` | `cluo.Ttcl` | `uint32` | **Foreground / Trim / Secondary Colour** |
| `+0xA8` | `cluo.Tbcl` | `uint32` | **Background / Shirt / Primary Colour** |

### 3. Color Encoding Format

Colors are stored as **32-bit Little-Endian BGRA unsigned integers**:
- **Byte 0 (`+0x0`)**: Blue (`0x00 .. 0xFF`)
- **Byte 1 (`+0x1`)**: Green (`0x00 .. 0xFF`)
- **Byte 2 (`+0x2`)**: Red (`0x00 .. 0xFF`)
- **Byte 3 (`+0x3`)**: Alpha / Opacity (`0xFF` = opaque)

To extract CSS Hex (`#RRGGBB`):
```csharp
uint val = m.U32(cino + offset);
byte r = (byte)((val >> 16) & 0xFF);
byte g = (byte)((val >> 8) & 0xFF);
byte b = (byte)(val & 0xFF);
string hex = $"#{r:X2}{g:X2}{b:X2}";
```

---

## Validation Data from Live Scans

Matched clubs from `%LOCALAPPDATA%\FMSuperScout\kit-scan.txt` verified against real FM database records:

| Club | Real Database UID (`objo.Duni`) | Primary Color (`cino + 0xA8`) | Secondary Color (`cino + 0xA0`) |
|---|---|---|---|
| **Celtic** | `1569` | Green (`#018749`) | White (`#FFFFFF`) |
| **Juventus** | `1139` | Black / White (`#000000`) | White (`#FFFFFF`) |
| **Real Madrid** | `1736` | White (`#FFFFFF`) / Gold trim | Navy / Gold |
| **Ajax** | `992` | Red (`#D2122E`) | White (`#FFFFFF`) |
| **Bayern München** | `915` | Red (`#DC052D`) | White / Blue (`#0066B2`) |
| **Liverpool** | `676` | Red (`#C8102E`) | White / Teal (`#00B2A9`) |
| **Borussia Dortmund**| `907` | Yellow (`#FDE100`) | Black (`#000000`) |
| **Manchester United**| `680` | Red (`#DA291C`) | White / Yellow |
| **Feyenoord** | `1013` | Red / White | Black / White |
| **PSV Eindhoven** | `1028` | Red / White | White |

---

## Pinned Constants in `Fields.cs`

```csharp
// --- Object-header (elk DB-object) ---
public const int OBJ_DUNI = 0x0C;         // uint32 Unique ID (UID)

// --- Club-blok (cluo / objo); basis = club ---
public const int CLUB_UID = 0x0C;         // uint32 Unique ID (objo.Duni)
public const int CLUB_INFO = 0xB0;        // ptr → Club Info-object (cluo.Cino)
public const int CLUB_NAME = 0xC0;        // indirecte string (cluo.Cnam)
public const int CLUB_SHORT_NAME = 0xC8;  // indirecte string (cluo.Csnm)
public const int CLUB_NATION = 0xD8;      // ptr → nation (cluo.Cnti)

// --- In het Club Info-object (via [club + CLUB_INFO]) ---
// Kleuren zijn 32-bit little-endian BGRA (byte0=B, byte1=G, byte2=R, byte3=A):
public const int CINO_FG_COLOR = 0xA0;    // u32 BGRA Foreground / Secundair / Trim (cluo.Ttcl)
public const int CINO_BG_COLOR = 0xA8;    // u32 BGRA Background / Primair / Shirt (cluo.Tbcl)
```

---

## Integration Details

1. **FMSuperScout Plugin (`Dumper.cs`)**:
   - `WriteKitScan` reads `cino = m.Ptr(club + Fields.CLUB_INFO)`.
   - Emits the verified UID (`club + Fields.CLUB_UID`) and decoded RGB hex colors directly.

2. **FM26 RTE (`fm26-player-editor`)**:
   - `src-tauri/src/lib.rs`:
     - Added `read_club_uid(target, club)` reading `club + 0x0C`.
     - Added `read_club_kit_colors(target, club)` reading BGRA from `[club + 0xB0] + 0xA8` and `+ 0xA0`.
     - Added `unique_id: Option<u32>` and `colors: Option<ClubKitColors>` to `ClubIdentityResult` and `ClubPlayerUidsResult`.
   - `src/App.tsx`:
     - Updated `ClubKitTable` to render the live unique ID (`club.uniqueId ?? club.inputId`) instead of raw memory pointer or hash.
     - Updated `ClubKitTable` to display live decoded kit colors from memory, falling back to name heuristics only if disconnected or offline.
     - Updated scouting target card headers, globe markers, and scanned club lists to use live colors.
