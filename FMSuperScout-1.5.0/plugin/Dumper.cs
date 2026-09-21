using System.Diagnostics;

namespace FMSuperScout;

internal static class Dumper
{
    private static readonly string OutDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FMSuperScout");



    // Diagnose-data (histogram van alle voorkomende class-offsets, gezet tijdens de scan).
    internal static Dictionary<int, long> AllOffHist = [];
    internal static long VtGp;
    internal static int LinkedViaSquad;
    internal static int ClubCount;
    internal static string MyClub;       // club van de human-manager
    internal static string ManagerName;  // naam van de human-manager
    internal static int MyClubRep;       // reputatie van jouw club (~0..10000)
    internal static int GameYear;        // afgeleid huidig seizoensjaar
    internal static DateTime? GameDate;  // exacte in-game datum uit geheugen (null = niet gevonden)
    internal static string GameVersion;  // versie van game_plugin.dll (bv. "26.3.2.0")
    internal static bool VersionOk = true; // major.minor == gepinde offsets-versie
    // Team-object van de human-manager, waaruit de in-game datum wordt gelezen (team-schema).
    internal static ulong DiagMyTeam;
    // Datum-stemmen van alle teams ([team+0xA0]+0x94), als kruischeck op de team-schema-lezing.
    internal static Dictionary<uint, int> DateVotes = [];
    // Fase-timing (ms per stap) — om te zien waar de scan-tijd heen gaat.
    internal static List<string> PhaseLog = [];
    // Speler/staf-ontdubbeling: ruwe staftelling en hoeveel daarvan óók als speler voorkwam.
    internal static int DiagStaffRaw;
    internal static int DiagStaffAlsoPlayer;
    // Spelers die in de selectie van meer dan één club staan (= huurrelatie, zie PickSquad).
    internal static HashSet<uint> MultiClub = [];
    internal static List<string> MultiClubSample = [];

    private const int ChunkSize = 32 * 1024 * 1024; // 32 MB leesblokken

    // Herbruikbare leesbuffers over scans heen (max 8 workers × 32 MB). Per scan vers
    // alloceren liet de LOH groeien — zie de toelichting bij MemScan._gpPool.
    private static readonly byte[][] BufPool = new byte[8][];

    // Leesbron van de laatste scan ("live" / "snapshot…") — gaat mee in diagnostics.
    internal static string ScanMode = "live";

    // Thread-lokale verzameling voor de parallelle scan: elke worker vult zijn eigen
    // buffer + collecties zonder locks; aan het eind mergen we ze samen.
    private sealed class ScanLocal
    {
        public readonly Dictionary<uint, Person> Players = [];
        public readonly Dictionary<uint, Person> Staff = [];
        public readonly Dictionary<int, int> OffsetHist = [];
        public readonly Dictionary<int, long> AllOffHist = [];
        public readonly List<(ulong person, string name, string club, int rep)> Managers = [];
        public readonly Dictionary<ulong, uint> PersonToUid = [];
        public readonly List<ulong> ClubObjs = [];
        public long Candidates, VtGp, Women;
    }

    // Statusbestand dat de web-app pollt (betrouwbare F9-feedback, ook zonder console).
    // progress (0..1) is échte scanvoortgang: gescande bytes / totaal; de app toont er
    // een voortgangsbalk mee. Invariant-cultuur: JSON eist een punt als decimaalteken.
    private static void WriteStatus(string state, int players, int staff, string error = null, double progress = -1)
    {
        try
        {
            Directory.CreateDirectory(OutDir);
            string errField = error == null ? "" : $",\"error\":\"{JsonEscape(error)}\"";
            string progField = progress < 0 ? "" :
                ",\"progress\":" + System.Math.Clamp(progress, 0, 1).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            // tmp + move: WriteAllText trunceert ter plekke, waardoor de app een half
            // statusbestand kon lezen (één gemiste poll-tick). Move op zelfde volume is atomair.
            string sf = Path.Combine(OutDir, "status.json");
            File.WriteAllText(sf + ".tmp",
                $"{{\"state\":\"{state}\",\"players\":{players},\"staff\":{staff},\"at\":\"{DateTime.Now:s}\"{progField}{errField}}}");
            File.Move(sf + ".tmp", sf, true);
        }
        catch { }
    }

    // Foutstatus wegschrijven zodat de web-app niet eeuwig op "scanning" blijft hangen.
    public static void WriteError(string message) => WriteStatus("error", 0, 0, message);

    // Database-keuze: 0 = mannen, 1 = vrouwen, 2 = beide (Instellingen → scan-config.json).
    private static int ScanDb;

    // De lopende scan; TryStartDump geeft via ReleaseScan() de geheugen-momentopname vrij
    // zodra de dump klaar of gesneuveld is. Eén tegelijk, bewaakt door _dumpBusy.
    private static MemScan CurrentScan;
    internal static void ReleaseScan()
    {
        var m = CurrentScan; CurrentScan = null;
        m?.ReleaseSnapshot();
    }

    // Ook overige control-chars (< 0x20) neutraliseren: een exceptionmelding met zo'n teken
    // maakte anders ongeldige status.json op precies het moment dat er een fout te tonen was.
    private static string JsonEscape(string s)
    {
        s = (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        return new string([.. s.Select(c => c < ' ' ? ' ' : c)]);
    }

    // 0xFFFFFFFF is FM's "niet ingesteld"-sentinel → onbekend (-1). Anders de waarde.
    private static long Money(uint v) => v == 0xFFFFFFFF ? -1 : v;
    // Marktwaarde-veld (pl+0x234): naast 0xFFFFFFFF gebruikt FM 300.000.000 als "niet vastgelegd".
    private static long MoneyVal(uint v) => (v == 0xFFFFFFFF || v == 300_000_000u) ? -1 : v;
    // JSON: negatief (onbekend) → null, anders getal.
    private static void Money(JsonWriter j, string key, long v) { if (v < 0) j.Null4(key); else j.Prop(key, v); }

    private enum DumpResult { Done, RetryWithSnapshot }

    // Scanstrategie: eerst live (snel, geen extra geheugendruk — zoals t/m 1.4.0). Alleen
    // als de live scan >10% van de regio's niet kon lezen volgt automatisch één herkansing
    // met een bevroren geheugen-momentopname (VA-kloon). Zo betaalt niemand de snapshot-
    // kosten (commit-geheugen, tragere game tijdens de scan) tenzij het aantoonbaar nodig is.
    public static void DumpAll()
    {
        if (DumpCore(useSnapshot: false) == DumpResult.RetryWithSnapshot)
        {
            ReleaseScan();
            Plugin.Log.LogInfo("Herkansing: scan opnieuw vanuit een bevroren geheugen-momentopname (VA-kloon)…");
            DumpCore(useSnapshot: true);
        }
    }

    private static DumpResult DumpCore(bool useSnapshot)
    {
        var sw = Stopwatch.StartNew();
        Plugin.Log.LogInfo("FMSuperScout: geheugen scannen…");
        Directory.CreateDirectory(OutDir);
        // Database-keuze (Instellingen in de app → scan-config.json): mannen, vrouwen of
        // beide. Per scan gelezen, dus een omgezette keuze geldt vanaf de eerstvolgende
        // F9. Standaard mannen, zodat bestaande saves compact blijven.
        try
        {
            string cfg = File.ReadAllText(Path.Combine(OutDir, "scan-config.json"));
            ScanDb = cfg.Contains("\"db\":\"women\"") ? 1
                   : cfg.Contains("\"db\":\"men\"") ? 0 : 2;
        }
        catch { ScanDb = 2; }
        WriteStatus("scanning", 0, 0);
        // Alle dump-specifieke statische staat resetten: mislukt de detectie in déze save,
        // dan mogen badge, Mijn club-filter en historie niet stilletjes op de vorige
        // carrière blijven draaien.
        GameDate = null;   // niet de datum van een vorige dump/save laten doorwerken in AgeFrom
        MyClub = null; ManagerName = null; MyClubRep = 0;
        GameYear = 0; ClubCount = 0; LinkedViaSquad = 0; VtGp = 0;
        DiagMyTeam = 0; DateVotes = []; AllOffHist = [];
        DiagStaffRaw = 0; DiagStaffAlsoPlayer = 0;
        MultiClub = []; MultiClubSample = [];
        // Fase-timing: waar gaat de tijd heen? Elke Phase() logt de duur sinds de vorige.
        PhaseLog = [];
        long tPrev = 0;
        void Phase(string name) { long now = sw.ElapsedMilliseconds; PhaseLog.Add($"{name}: {now - tPrev} ms"); tPrev = now; }

        // Geheugenstatus vóór de scan: stuurt de adaptieve keuzes hieronder en maakt
        // veldrapporten ("faalt willekeurig") herleidbaar tot wel/geen geheugendruk.
        var (availPhys, availCommit, memLoad) = MemScan.MemoryStatus();
        const ulong MB = 1024 * 1024;
        Plugin.Log.LogInfo($"Geheugen: {availPhys / MB:N0} MB fysiek vrij · {availCommit / MB:N0} MB commit vrij · belasting {memLoad}%");

        var mem = new MemScan(useSnapshot);
        // Bijgehouden zodat TryStartDump's finally de VA-kloon op élk pad vrijgeeft
        // (ook bij een exception mid-scan) — zolang de kloon leeft, kost elke door FM
        // gewijzigde pagina een copy-on-write-kopie.
        CurrentScan = mem;
        if (useSnapshot)
        {
            PhaseLog.Add($"snapshot maken: {mem.SnapshotMs} ms");
            Plugin.Log.LogInfo(mem.Snapshotted
                ? $"Scan leest uit een bevroren geheugen-momentopname (VA-kloon, opname {mem.SnapshotMs} ms)."
                : $"Snapshot niet beschikbaar ({mem.SnapshotError ?? "onbekend"}) — herkansing leest opnieuw live geheugen.");
        }
        ScanMode = !useSnapshot ? "live"
            : mem.Snapshotted ? "snapshot (VA-kloon), herkansing"
            : $"live, herkansing (snapshot mislukt: {mem.SnapshotError ?? "onbekend"})";
        if (mem.ImageNote != null) Plugin.Log.LogWarning(mem.ImageNote);
        Phase("MemScan-ctor (image-reads)");
        if (mem.GaBase == 0)
        {
            Plugin.Log.LogError("GameAssembly.dll niet gevonden — kan niet dumpen.");
            WriteError("GameAssembly.dll niet gevonden. Is FM26 goed geladen?");
            return DumpResult.Done;
        }
        Plugin.Log.LogInfo($"Scanregio's: {mem.ScanRegions.Count}, GameAssembly {mem.GaBase:X}-{mem.GaEnd:X}, " +
                           $"game_plugin {mem.GpBase:X}-{mem.GpEnd:X}");
        DetectGameVersion(mem);
        if (mem.GpBase == 0)
        {
            // game_plugin.dll (de native database) laadt pas ná de BepInEx-chainloader, tijdens
            // het opstarten van FM. Een dump in dat venster is gegarandeerd leeg — vroeger
            // schreef die een lege dump.json over de goede heen (issue #7). Nu: afbreken,
            // bestaande dump met rust laten.
            Plugin.Log.LogError("game_plugin.dll niet geladen — dump afgebroken, bestaande dump.json blijft staan.");
            WriteError("FM26 is still starting up (game database not loaded yet). Load your save, then try again.");
            return DumpResult.Done;
        }

        var players = new Dictionary<uint, Person>();
        var staff = new Dictionary<uint, Person>();
        var offsetHist = new Dictionary<int, int>();     // matches (speler/staf)
        var allOffHist = new Dictionary<int, long>();     // ALLE class-offsets (diagnose)
        var managers = new List<(ulong person, string name, string club, int rep)>(); // human-managers
        var personToUid = new Dictionary<ulong, uint>(1 << 17); // person/objectstart-adres → uid
        var clubObjs = new List<ulong>();                        // gedetecteerde club-objecten
        long candidates = 0, vtGp = 0, women = 0;

        // Voortgang + parallellisatie. De geheugenregio's zijn onafhankelijk, dus verdeel ze
        // over de cores (ReadProcessMemory is thread-safe en de IL2CPP-GC verplaatst niets).
        // Elke thread werkt in eigen buffer + eigen verzamelingen; aan het eind mergen we onder
        // een lock. De hoofdscan is veruit de langste fase → die krijgt 0..0.85 van de balk.
        ulong totalBytes = 0;
        foreach (var (start, size) in mem.ScanRegions) totalBytes += size;
        long doneBytes = 0, lastProgMs = 0, unreadBytes = 0;
        ulong modLo = mem.ModLo, modHi = mem.ModHi;   // snelle inline-afwijzing in de hotloop
        object progLock = new();
        var regions = mem.ScanRegions.Where(r => r.size >= 0x40).ToList();
        // Cap op 8 workers: elke worker draagt een 32MB-leesbuffer, dus cores-1 werd op
        // 16/32-core-machines 480MB-1GB extra RAM bovenop FM zelf — precies de machines
        // waar mega-saves toch al tegen OOM aanhikken. Boven ~8 workers is ReadProcessMemory
        // bovendien de flessenhals, niet de CPU.
        int maxDop = System.Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        // Weinig fysiek geheugen vrij? Minder workers: minder buffers en minder paging-druk
        // tegelijk. Trager, maar het faalt niet — belangrijker op een doorsnee-pc.
        if (availPhys != 0 && availPhys < 2048 * MB && maxDop > 2)
        {
            maxDop = 2;
            Plugin.Log.LogInfo($"Weinig geheugen vrij ({availPhys / MB:N0} MB) — scan beperkt tot 2 workers.");
        }

        // N workers, elk een round-robin-deel van de regio's (regio's variëren sterk in
        // grootte → interleaven balanceert de last). Task.Run i.p.v. Parallel.ForEach:
        // de Il2Cpp-referenties bevatten een uitgeklede NullableAttribute waardoor de
        // compiler struikelt over Parallel's geannoteerde delegate; een parameterloze
        // Task-lambda omzeilt dat volledig.
        var locals = new ScanLocal[maxDop];
        var tasks = new System.Threading.Tasks.Task[maxDop];
        // Vangnet-tellers: één regio met onverwachte inhoud (zoals de corrupte meta-pointer
        // van issue #16) mag nooit meer de complete dump laten sneuvelen. Zo'n regio telt
        // voortaan als onleesbaar en de scan gaat door; na afloop loggen we wat er miste.
        long regionErrors = 0;
        string firstRegionError = null;
        for (int t = 0; t < maxDop; t++)
        {
            int worker = t;
            var L = locals[worker] = new ScanLocal();
            tasks[worker] = System.Threading.Tasks.Task.Run(() =>
            {
                var buf = BufPool[worker] ??= new byte[ChunkSize];
                for (int ri = worker; ri < regions.Count; ri += maxDop)
                {
                    var (start, size) = regions[ri];
                    ulong scanned = 0;
                    try
                    {
                    while (scanned < size)
                    {
                        int want = (int)System.Math.Min((ulong)ChunkSize, size - scanned);
                        ulong chunkBase = start + scanned;
                        if (!mem.ReadBlock(chunkBase, buf, want))
                        {
                            // Eén onleesbare pagina mag geen 32 MB data kosten: probeer een
                            // kleiner venster (1 MB). Lukt ook dat niet, tel het als onleesbaar —
                            // die teller bepaalt straks of dit resultaat een bestaande dump mag
                            // vervangen (een scan tijdens save-unload leest half geheugen en
                            // leverde anders stilletjes een uitgedunde dump op).
                            int fine = (int)System.Math.Min((ulong)(1 << 20), size - scanned);
                            if (want <= fine || !mem.ReadBlock(chunkBase, buf, fine))
                            {
                                scanned += (ulong)fine;
                                Interlocked.Add(ref doneBytes, fine);
                                Interlocked.Add(ref unreadBytes, fine);
                                continue;
                            }
                            want = fine;
                        }
                        for (int i = 0; i + 0x10 <= want; i += 8)
                        {
                            ulong vt = BitConverter.ToUInt64(buf, i);
                            if (vt < modLo || vt >= modHi) continue;      // snelle afwijzing
                            bool inGp = mem.InGp(vt);
                            if (!inGp && !mem.InGa(vt)) continue;         // alleen DB/managed-vtables
                            L.Candidates++;
                            if (inGp) L.VtGp++;

                            ulong p = chunkBase + (ulong)i;
                            int off = mem.DynamicOffsetFromVtable(vt);    // gecachte image → geen syscall
                            if (off == 0) continue;
                            uint uid = BitConverter.ToUInt32(buf, i + Fields.OBJ_DUNI);   // uit de buffer
                            if (uid == 0 || uid == 0xFFFFFFFF) continue;
                            if (off is > 0 and < 0x2000)
                                L.AllOffHist[off] = L.AllOffHist.GetValueOrDefault(off) + 1;

                            bool isPlayer = off == Fields.PLAYER_OFFSET || off == Fields.PLAYER_STAFF_OFFSET;
                            bool isStaff = off == Fields.STAFF_OFFSET || off == Fields.HUMAN_MANAGER_OFFSET;
                            if (!isPlayer && !isStaff)
                            {
                                ulong tb = mem.Ptr(p + 0x18), te = mem.Ptr(p + 0x20);
                                if (tb != 0 && te > tb && (te - tb) % 8 == 0 && (te - tb) / 8 is >= 1 and <= 64
                                    && ClubNameOf(mem, p) != null)
                                    L.ClubObjs.Add(p);
                                continue;
                            }

                            ulong basePtr = p - (ulong)off;
                            if (isPlayer)
                            {
                                ushort ca = mem.U16(basePtr + Fields.PLAO_CA);
                                ushort pa = mem.U16(basePtr + Fields.PLAO_PA);
                                if (ca > 200 || pa > 200) continue;
                                // Databasekeuze: sla het niet-gekozen geslacht over
                                // (person+0x19 bit 0x10 = vrouw). ScanDb 2 = beide → niets overslaan.
                                bool female = (mem.U8(p + (ulong)Fields.PERO_GENDER) & Fields.GENDER_FEMALE_BIT) != 0;
                                if (female ? ScanDb == 0 : ScanDb == 1) { L.Women++; continue; }
                                L.OffsetHist[off] = L.OffsetHist.GetValueOrDefault(off) + 1;
                                if (!L.Players.ContainsKey(uid))
                                {
                                    L.Players[uid] = ReadPlayer(mem, p, basePtr, uid, ca, pa);
                                    L.PersonToUid[p] = uid;
                                    L.PersonToUid[basePtr] = uid;
                                }
                            }
                            else
                            {
                                ushort ca = mem.U16(basePtr + Fields.NPLO_CA);
                                ushort pa = mem.U16(basePtr + Fields.NPLO_PA);
                                if (ca > 200 || pa > 200) continue;
                                L.OffsetHist[off] = L.OffsetHist.GetValueOrDefault(off) + 1;
                                if (!L.Staff.ContainsKey(uid))
                                {
                                    var st = ReadStaff(mem, p, basePtr, uid, ca, pa);
                                    L.Staff[uid] = st;
                                    if (off == Fields.HUMAN_MANAGER_OFFSET)
                                    {
                                        var (mc, mrep, _) = ResolveClub(mem, p);
                                        L.Managers.Add((p, st.Name, mc ?? st.Club, mrep));
                                    }
                                }
                            }
                        }
                        // 16 bytes overlappen met het volgende venster: een object dat precies op
                        // de chunkgrens begint viel anders in géén van beide vensters (de lus eist
                        // i+0x10 <= want). Dubbel gezien = onschuldig, de uid-checks ontdubbelen.
                        int step = (ulong)want < size - scanned ? want - 16 : want;
                        scanned += (ulong)step;
                        long done = Interlocked.Add(ref doneBytes, step);
                        long now = sw.ElapsedMilliseconds;
                        if (now - Interlocked.Read(ref lastProgMs) >= 500 && totalBytes > 0 && Monitor.TryEnter(progLock))
                        {
                            try { lastProgMs = now; WriteStatus("scanning", 0, 0, null, 0.85 * done / totalBytes); }
                            finally { Monitor.Exit(progLock); }
                        }
                    }
                    }
                    catch (System.Exception ex)
                    {
                        long rest = (long)(size - scanned);
                        Interlocked.Add(ref doneBytes, rest);
                        Interlocked.Add(ref unreadBytes, rest);
                        Interlocked.Increment(ref regionErrors);
                        Interlocked.CompareExchange(ref firstRegionError, ex.Message, null);
                    }
                }
            });
        }
        System.Threading.Tasks.Task.WaitAll(tasks);
        if (regionErrors > 0)
            Plugin.Log.LogWarning($"{regionErrors} scanregio('s) overgeslagen na interne fout (eerste: {firstRegionError}) — rest van de scan is doorgegaan.");

        // Merge alle worker-resultaten in de gedeelde verzamelingen (single-threaded, geen lock nodig).
        foreach (var L in locals)
        {
            foreach (var kv in L.Players) players.TryAdd(kv.Key, kv.Value);
            foreach (var kv in L.Staff) staff.TryAdd(kv.Key, kv.Value);
            foreach (var kv in L.PersonToUid) personToUid[kv.Key] = kv.Value;
            foreach (var kv in L.OffsetHist) offsetHist[kv.Key] = offsetHist.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (var kv in L.AllOffHist) allOffHist[kv.Key] = allOffHist.GetValueOrDefault(kv.Key) + kv.Value;
            clubObjs.AddRange(L.ClubObjs);
            managers.AddRange(L.Managers);
            candidates += L.Candidates; vtGp += L.VtGp; women += L.Women;
        }
        Phase("hoofdscan (parallel, geheugen doorlopen)");
        // Onleesbaar-fractie altijd meten en loggen — de belangrijkste indicator bij
        // veldrapporten, óók als de scan verder gewoon slaagt.
        double unreadFrac = totalBytes > 0 ? (double)Interlocked.Read(ref unreadBytes) / totalBytes : 0;
        if (unreadFrac > 0)
            Plugin.Log.LogInfo($"Onleesbaar tijdens scan: {unreadFrac:P1} van {totalBytes / MB:N0} MB.");
        // Live poging miste te veel? Direct herkansen met een bevroren momentopname, vóórdat
        // we tijd steken in koppeling en wegschrijven. De snapshot leest ook pagina's die FM
        // ondertussen vrijgeeft/herschrijft, dus dit vangt scans tijdens simulatie/laden af.
        if (!useSnapshot && unreadFrac > 0.10)
        {
            Plugin.Log.LogWarning($"{(int)System.Math.Round(unreadFrac * 100)}% van de scanregio's was live onleesbaar — herkansing met snapshot volgt.");
            return DumpResult.RetryWithSnapshot;
        }
        Plugin.Log.LogInfo($"vtables in game_plugin: {vtGp:N0} van {candidates:N0} kandidaten · database: " +
            (ScanDb == 2 ? "beide (niets overgeslagen)" : ScanDb == 1 ? $"vrouwen ({women:N0} mannen overgeslagen)" : $"mannen ({women:N0} vrouwen overgeslagen)"));
        Dumper.AllOffHist = allOffHist;
        Dumper.VtGp = vtGp;
        WriteStatus("scanning", players.Count, staff.Count, null, 0.87);

        // ---- Squad-gebaseerde clubkoppeling (authoritatief) ----
        // Loop clubs → teams → spelerslijst. Elke speler krijgt de club van zijn selectie.
        // Ook: welke club heeft de human-manager als teammanager → jouw club.
        var mgrAddrs = new HashSet<ulong>(managers.Select(x => x.person));
        var squadClub = new Dictionary<uint, (string club, int tt, int rep, string div)>();
        MultiClub = [];
        MultiClubSample = [];
        DiagMyTeam = 0;
        foreach (ulong club in clubObjs)
        {
            string cname = ClubNameOf(mem, club);
            if (cname == null) continue;
            ulong tb = mem.Ptr(club + 0x18), te = mem.Ptr(club + 0x20);
            if (tb == 0 || te <= tb) continue;
            ulong teamCount = (te - tb) / 8;
            if (teamCount > 512) continue;
            for (ulong ti = 0; ti < teamCount; ti++)
            {
                ulong team = mem.Ptr(tb + ti * 8);
                if (team == 0) continue;
                int tt = mem.U8(team + 0x28);            // teamtype (0 = eerste elftal)
                int trep = mem.U16(team + 0xA8);
                if (trep is < 0 or > 12000) trep = 0;
                // manager van dit team → is het jouw human-manager?
                ulong mgr = mem.Ptr(team + 0x80);
                if (mgr != 0 && mgrAddrs.Contains(mgr) && (MyClub == null || tt == 0))
                { MyClub = cname; MyClubRep = trep; DiagMyTeam = team; }
                // spelerslijst
                ulong pb = mem.Ptr(team + 0x38), pe = mem.Ptr(team + 0x40);
                if (pb == 0 || pe <= pb) continue;
                ulong pcount = (pe - pb) / 8;
                if (pcount > 1000) continue;
                for (ulong pi = 0; pi < pcount; pi++)
                {
                    ulong pp = mem.Ptr(pb + pi * 8);
                    if (pp == 0 || !personToUid.TryGetValue(pp, out uint puid)) continue;
                    PickSquad(squadClub, players, puid, cname, tt, trep, CompNameOf(mem, team));
                }
            }
        }
        Phase("squad-walk 1 (gedetecteerde clubs)");
        // ---- Squad-walk v2 (15-07): clubs uit de bewezen contract-keten, en lijst-entries
        // opgelost door te PROBEN tegen de bekende person-adressen (de entries bleken geen
        // person-pointers; naamresolutie gaf rommel, maar een pointerveld erin wijst wél
        // naar de speler). Levert per speler het échte team (tt: 0=1e, 3=reserves, 11=U18)
        // en de team-divisie (jeugdspelers → jeugdcompetitie). En passant stemmen alle
        // teams over de in-game datum via [team+0xA0]+0x94 (droeg exact "vandaag", 15-07).
        var clubAddrs = new HashSet<ulong>();
        foreach (var p in players.Values)
        {
            if (p.PersonAddr == 0) continue;
            ulong pcon = mem.Ptr(p.PersonAddr + (ulong)Fields.PERO_FULL_CONTRACT);
            if (pcon == 0) continue;
            ulong pteam = mem.Ptr(pcon + 0x10);
            if (pteam == 0) continue;
            ulong pclub = mem.Ptr(pteam + 0x30);
            if (pclub != 0) clubAddrs.Add(pclub);
        }
        foreach (var s in staff.Values)
        {
            if (s.PersonAddr == 0) continue;
            ulong scon = mem.Ptr(s.PersonAddr + (ulong)Fields.PERO_FULL_CONTRACT);
            if (scon == 0) continue;
            ulong steam = mem.Ptr(scon + 0x10);
            if (steam == 0) continue;
            ulong sclub = mem.Ptr(steam + 0x30);
            if (sclub != 0) clubAddrs.Add(sclub);
        }
        foreach (ulong c in clubObjs)
        {
            if (c != 0) clubAddrs.Add(c);
        }
        DateVotes = [];
        foreach (ulong club in clubAddrs)
        {
            string cname = ClubNameOf(mem, club);
            if (cname == null) continue;
            ulong tb2 = mem.Ptr(club + 0x18), te2 = mem.Ptr(club + 0x20);
            if (tb2 == 0 || te2 <= tb2 || (te2 - tb2) % 8 != 0) continue;
            long tcnt2 = (long)((te2 - tb2) / 8);
            if (tcnt2 > 512) continue;
            for (long ti = 0; ti < tcnt2; ti++)
            {
                ulong team = mem.Ptr(tb2 + (ulong)ti * 8);
                if (team == 0) continue;
                int tt = mem.U8(team + 0x28);
                int trep = mem.U16(team + 0xA8);
                if (trep is < 0 or > 12000) trep = 0;
                // Datum-stem: [team+0xA0]+0x94 = eerstvolgende wedstrijd van dit team. Normaliseer
                // op de gedecodeerde datum (het rauwe veld kan vlagbits in 9-15 dragen; op de rauwe
                // waarde stemmen versnippert dezelfde dag → v0.1.11-bug: alles weggefilterd).
                ulong dobj = mem.Ptr(team + (ulong)Fields.TEAM_SCHEDULE);
                if (dobj != 0)
                {
                    var (dy, ddoy) = DecodeFmDate(mem.U32(dobj + (ulong)Fields.SCHED_NEXT_MATCH));
                    if (dy is >= 2020 and <= 2060)
                    {
                        uint norm = ((uint)dy << 16) | (uint)ddoy;
                        DateVotes[norm] = DateVotes.GetValueOrDefault(norm) + 1;
                    }
                }
                string tdiv = CompNameOf(mem, team);
                ulong pb2 = mem.Ptr(team + 0x38), pe2 = mem.Ptr(team + 0x40);
                if (pb2 == 0 || pe2 <= pb2 || (pe2 - pb2) % 8 != 0) continue;
                long pcnt2 = (long)((pe2 - pb2) / 8);
                if (pcnt2 > 1000) continue;
                for (long pi = 0; pi < pcnt2; pi++)
                {
                    ulong pp = mem.Ptr(pb2 + (ulong)pi * 8);
                    if (pp == 0) continue;
                    int hitOff = -1;
                    if (personToUid.TryGetValue(pp, out uint puid)) hitOff = -2;   // entry ís de speler
                    else
                        for (int off = 0x00; off <= 0x80; off += 8)
                        {
                            ulong q = mem.Ptr(pp + (ulong)off);
                            if (q != 0 && personToUid.TryGetValue(q, out puid)) { hitOff = off; break; }
                        }
                    if (hitOff == -1) continue;
                    PickSquad(squadClub, players, puid, cname, tt, trep, tdiv);
                }
            }
        }
        Phase("squad-walk 2 (contract-keten-clubs)");
        Plugin.Log.LogInfo($"Squad-walk v2: {squadClub.Count} spelers gekoppeld over {clubAddrs.Count} keten-clubs.");

        // Koppel clubs aan spelers (squad wint; anders blijft contract-keten-fallback staan).
        foreach (var p in players.Values)
            if (squadClub.TryGetValue(p.Uid, out var sc))
            { p.Club = sc.club; if (sc.rep > 0) p.ClubRep = sc.rep; if (sc.div != null) p.Div = sc.div; p.TeamType = sc.tt; }

        // Verzamel alle vtables van bewezen clubs om ook de ~13.000 clubs zonder actieve spelers/contracten
        // in de database te vinden (totaal 30.489 clubs in FM-database).
        var clubVtables = new HashSet<ulong>();
        foreach (var c in clubAddrs)
        {
            ulong vt = mem.Ptr(c);
            if (vt != 0 && mem.InGp(vt))
                clubVtables.Add(vt);
        }

        if (clubVtables.Count > 0)
        {
            Phase("alle database-clubs scannen");
            var ctasks = new System.Threading.Tasks.Task[maxDop];
            var clocals = new List<ulong>[maxDop];
            for (int t = 0; t < maxDop; t++)
            {
                int worker = t;
                clocals[worker] = [];
                ctasks[worker] = System.Threading.Tasks.Task.Run(() =>
                {
                    var buf = BufPool[worker] ??= new byte[ChunkSize];
                    for (int ri = worker; ri < regions.Count; ri += maxDop)
                    {
                        var (start, size) = regions[ri];
                        ulong scanned = 0;
                        while (scanned < size)
                        {
                            int want = (int)System.Math.Min((ulong)ChunkSize, size - scanned);
                            ulong chunkBase = start + scanned;
                            if (!mem.ReadBlock(chunkBase, buf, want))
                            {
                                scanned += (ulong)want;
                                continue;
                            }
                            for (int i = 0; i + 0x10 <= want; i += 8)
                            {
                                ulong vt = BitConverter.ToUInt64(buf, i);
                                if (clubVtables.Contains(vt))
                                {
                                    uint uid = BitConverter.ToUInt32(buf, i + Fields.CLUB_UID);
                                    if (uid > 0 && uid < 0x7FFFFFFF)
                                    {
                                        ulong p = chunkBase + (ulong)i;
                                        clocals[worker].Add(p);
                                    }
                                }
                            }
                            scanned += (ulong)want;
                        }
                    }
                });
            }
            System.Threading.Tasks.Task.WaitAll(ctasks);
            int addedClubs = 0;
            for (int t = 0; t < maxDop; t++)
            {
                foreach (var p in clocals[t])
                {
                    if (clubAddrs.Add(p))
                        addedClubs++;
                }
            }
            Plugin.Log.LogInfo($"Alle database-clubs gescand: {clubAddrs.Count} clubs in totaal ({addedClubs} extra gevonden zonder actieve contracten).");
        }

        Dumper.LinkedViaSquad = squadClub.Count;
        Dumper.ClubCount = clubAddrs.Count;

        // Mijn team via de manager-keten (person→contract→team) — hieruit leest FindGameDate
        // de in-game datum (team-schema). Robuuster dan de squad-walk-match hierboven.
        foreach (var (person, name, club, rep) in managers)
        {
            ulong mcon = mem.Ptr(person + (ulong)Fields.PERO_FULL_CONTRACT);
            if (mcon == 0) continue;
            ulong mteam = mem.Ptr(mcon + 0x10);
            if (mteam == 0) continue;
            DiagMyTeam = mteam;
            break;
        }

        // Human-manager fallback als geen team-match gevonden is.
        var me = managers.FirstOrDefault(x => !string.IsNullOrEmpty(x.club));
        if (me.person == 0 && managers.Count > 0) me = managers[0];
        ManagerName = me.name;
        if (MyClub == null) { MyClub = me.club; MyClubRep = me.rep; }
        Plugin.Log.LogInfo($"Manager: {ManagerName ?? "?"} · club: {MyClub ?? "?"} (rep {MyClubRep}) · " +
                           $"{clubAddrs.Count} clubs, {squadClub.Count} spelers via selectie gekoppeld");

        // Huidig seizoensjaar afleiden uit de data: de jeugdinstroom genereert elk jaar
        // een groot cohort ~16-jarigen. Het hoogste geboortejaar met een fors cohort +16
        // ≈ het huidige in-game jaar. Robuust en patch-bestendig (geen offsets nodig).
        var byHist = new Dictionary<int, int>();
        foreach (var pl in players.Values)
            if (pl.BirthYear is >= 1990 and <= 2100)
                byHist[pl.BirthYear] = byHist.GetValueOrDefault(pl.BirthYear) + 1;
        int youngestCohort = byHist.Where(kv => kv.Value >= 30).Select(kv => kv.Key).DefaultIfEmpty(0).Max();
        GameYear = youngestCohort > 0 ? youngestCohort + 16 : DateTime.Now.Year;
        Plugin.Log.LogInfo($"Afgeleid seizoensjaar: {GameYear} (jongste cohort {youngestCohort})");

        // ---- Exacte in-game datum (best effort) ----
        // Zoek in de statics/code van game_plugin naar u32's die exact een FM-datum coderen
        // rond het afgeleide seizoensjaar. De echte "vandaag" staat daar doorgaans in meerdere
        // globals tegelijk; een toevallige constante vrijwel nooit. Daarom eisen we ≥2 hits op
        // exact dezelfde waarde en geven we voorrang aan het cohort-jaar. Vinden we niets
        // betrouwbaars, dan blijft de oude fallback (systeemmaand/-dag) gewoon staan.
        FindGameDate(mem, players.Values, staff.Values);
        Phase("koppeling + datum + seizoensjaar");

        // ---- Speler/staf-ontdubbeling ----
        // Elke Person draagt náást speler-data ook een non-player/coaching-facet (class-offset
        // 0x100). Daardoor werd vrijwel elke speler óók via die facet opgepikt en als "staf"
        // dubbel geteld: totaal ≈ 2× de database. Echte staf (coaches, scouts, fysio's) heeft
        // geen speler-facet en blijft dus staan. We meten eerst de overlap (diagnose) en
        // verwijderen dan alle uids uit de staflijst die al een speler zijn. Speler-coaches
        // (offset 0x380) tellen al als speler, dus die verdwijnen niet uit beeld.
        DiagStaffRaw = staff.Count;
        var staffAlsoPlayer = staff.Keys.Where(uid => players.ContainsKey(uid)).ToList();
        DiagStaffAlsoPlayer = staffAlsoPlayer.Count;
        foreach (var uid in staffAlsoPlayer) staff.Remove(uid);
        Plugin.Log.LogInfo($"Speler/staf-ontdubbeling: staf ruw {DiagStaffRaw}, ook speler {DiagStaffAlsoPlayer}, netto staf {staff.Count}.");

        Plugin.Log.LogInfo($"Gevonden: {players.Count} spelers, {staff.Count} staf " +
                           $"({candidates:N0} kandidaten, {sw.ElapsedMilliseconds} ms). JSON schrijven…");

        // Vangnet: een geladen save heeft altijd spelers. 0 spelers én 0 staf betekent dat de
        // scan zelf faalde (save niet geladen, of offsets verschoven na een FM-patch). Zo'n
        // lege dump mag nooit een bestaande, goede dump.json overschrijven. Diag wél schrijven:
        // die is juist nu nodig voor een probleemrapport.
        if (players.Count == 0 && staff.Count == 0)
        {
            WriteDiag(mem, players, staff, offsetHist, candidates, sw.ElapsedMilliseconds);
            Plugin.Log.LogError("0 spelers en 0 staf gevonden — dump niet weggeschreven, bestaande dump.json blijft staan.");
            WriteError("The scan found no players. Is your save fully loaded? If it is, FMSuperScout may need an update for this FM version.");
            return DumpResult.Done;
        }
        // Tweede vangnet: was ook ná de herkansing een flink deel onleesbaar, dan is dit
        // resultaat vrijwel zeker uitgedund en mag het een bestaande, goede dump niet
        // vervangen. Zonder bestaande dump: wél schrijven (iets > niets), met logregel.
        // De melding benoemt geheugendruk expliciet: dit pad wordt in de praktijk bereikt
        // op machines waar Windows krap zit (snapshot faalt daar ook, bv. Win32 1450).
        if (unreadFrac > 0.10)
        {
            int pct = (int)System.Math.Round(unreadFrac * 100);
            Plugin.Log.LogWarning($"{pct}% van de scanregio's was onleesbaar ({players.Count} spelers gevonden).");
            if (File.Exists(Path.Combine(OutDir, "dump.json")))
            {
                WriteDiag(mem, players, staff, offsetHist, candidates, sw.ElapsedMilliseconds);
                Plugin.Log.LogError("Dump niet weggeschreven — bestaande dump.json blijft staan. Probeer opnieuw met volledig geladen save.");
                WriteError($"The scan could not read {pct}% of FM's memory, even after a retry with a frozen snapshot. " +
                           "Windows is probably low on memory: close other apps, make sure the page file is not capped, or reboot. " +
                           "Your existing data was kept.");
                return DumpResult.Done;
            }
        }
        WriteStatus("scanning", players.Count, staff.Count, null, 0.90);

        WriteJson(players.Values, staff.Values, mem, clubAddrs);
        Phase("JSON schrijven");
        WriteDiag(mem, players, staff, offsetHist, candidates, sw.ElapsedMilliseconds);
        // clubAddrs (contract-keten-clubs, squad-walk 2) is de authoritatieve, volledige
        // clublijst — dezelfde bron als de "club"-waarde in elke speler in dump.json.
        // clubObjs is slechts een kleine heuristische subset (hier: 56) en miste daardoor
        // vrijwel alle referentieclubs, ook al staan die overduidelijk in de dump.
        WriteKitScan(mem, clubAddrs);
        WriteClubs(mem, clubAddrs);

        Plugin.Log.LogInfo($"Klaar in {sw.ElapsedMilliseconds} ms. Bestand in {OutDir}. " +
                           "Open de FMSuperScout web-app en klik Verversen.");
        WriteStatus("done", players.Count, staff.Count);   // web-app-banner leest dit
        return DumpResult.Done;
    }

    // ---------- speler ----------
    private static Person ReadPlayer(MemScan m, ulong person, ulong pl, uint uid, ushort ca, ushort pa)
    {
        var e = new Person
        {
            Uid = uid,
            Ca = ca,
            Pa = pa,
            IsPlayer = true,
            Name = ReadName(m, person)
        };
        (e.BirthYear, e.BirthDoy) = DecodeFmDate(m.U32(person + Fields.PERO_DOB));
        e.Age = AgeFrom(e.BirthYear, e.BirthDoy);
        e.Nat = ReadNation(m, person);
        e.NatId = ReadNationId(m, person);
        e.Height = m.U16(pl + Fields.PLAO_HEIGHT);
        if (e.Height is < 140 or > 220) e.Height = 0;

        // attributen (÷5)
        foreach (var (key, off) in Fields.PlayerAttrs)
            e.Attrs[key] = Attr(m, pl + (ulong)Fields.PLAO_ATTRS + (ulong)off);
        foreach (var (key, off) in Fields.PlayerHiddenAttrs)
            e.Attrs[key] = Attr(m, pl + (ulong)Fields.PLAO_ATTRS + (ulong)off);

        int lf = Attr(m, pl + (ulong)Fields.PLAO_ATTRS + Fields.FOOT_LEFT);
        int rf = Attr(m, pl + (ulong)Fields.PLAO_ATTRS + Fields.FOOT_RIGHT);
        e.Foot = (rf >= 14 && lf >= 14) ? "Beide" : (rf >= lf ? "Rechts" : "Links");

        // posities
        var pos = new List<(string k, int v)>();
        foreach (var (key, off) in Fields.Positions)
        {
            int v = m.U8(pl + (ulong)Fields.PLAO_POSITIONS + (ulong)off);
            if (v >= 1) pos.Add((key, v));
        }
        int top = pos.Count > 0 ? pos.Max(x => x.v) : 0;
        e.PosArr = [.. pos.Where(x => x.v >= System.Math.Max(15, top - 2)).OrderByDescending(x => x.v).Select(x => x.k)];
        if (e.PosArr.Count == 0 && pos.Count > 0)
            e.PosArr = [.. pos.OrderByDescending(x => x.v).Take(1).Select(x => x.k)];

        // Marktwaarde: 0x234 is FM's echte transferwaarde (geverifieerd via offset-discovery
        // tegen in-game bedragen); 0x238 is de vraagprijs (meestal niet ingesteld).
        e.Value = MoneyVal(m.U32(pl + Fields.PLAO_GUIDE_VALUE));   // 0x234
        e.GuideValue = Money(m.U32(pl + Fields.PLAO_TRANSFER_VALUE)); // 0x238 (vraagprijs)
        ulong con = m.Ptr(person + Fields.PERO_FULL_CONTRACT);
        if (con != 0)
        {
            e.Wage = Money(m.U32(con + Fields.CON_WEEKLY_WAGE));
            e.Expires = FmDateIso(m.U32(con + Fields.CON_EXPIRY));
            byte flags = m.U8(con + Fields.CON_STATUS_FLAGS);
            e.Listed = (flags & (1 << 0)) != 0 || (flags & (1 << 3)) != 0; // Listed / by request
            // Bit 1 = huurlijst-kandidaat (naast 0=Listed, 3=by request, 4=NFS, 5=Release).
            // Nog niet in-game geverifieerd; diagnostics.txt toont een bit-histogram en een
            // sample zodat de pin met één echte dump te bevestigen is.
            e.LoanListed = (flags & (1 << 1)) != 0;
            e.StatusFlags = flags;
            e.NotForSale = (flags & (1 << 4)) != 0;
            e.SetForRelease = (flags & (1 << 5)) != 0;
        }
        e.CurRep = m.U16(pl + Fields.PLAO_CUR_REP);
        e.WorldRep = m.U16(pl + Fields.PLAO_WORLD_REP);
        e.Ambition = ClampAttr(m.U8(person + Fields.PERO_AMBITION));
        e.Loyalty = ClampAttr(m.U8(person + Fields.PERO_LOYALTY));
        e.Professionalism = ClampAttr(m.U8(person + Fields.PERO_PROFESSIONALISM));
        e.Adaptability = ClampAttr(m.U8(person + Fields.PERO_ADAPTABILITY));
        e.Pressure = ClampAttr(m.U8(person + Fields.PERO_PRESSURE));
        e.Sportsmanship = ClampAttr(m.U8(person + Fields.PERO_SPORTSMANSHIP));
        e.Temperament = ClampAttr(m.U8(person + Fields.PERO_TEMPERAMENT));
        e.Controversy = ClampAttr(m.U8(person + Fields.PERO_CONTROVERSY));
        e.PersonAddr = person;
        e.PlAddr = pl;
        e.Gender = (m.U8(person + (ulong)Fields.PERO_GENDER) & Fields.GENDER_FEMALE_BIT) != 0 ? 1 : 0;
        var (cname, crep, cdiv) = ResolveClub(m, person);
        e.Club = cname; e.ClubRep = crep; e.Div = cdiv;
        // Moederclub = club uit het volledige contract (person+0xA8→team→club). Voor verhuurde
        // spelers wijkt dit af van de squad-club (waar ze nú spelen); daarmee detecteert de app
        // huur: eigenaar==mijn club & speelt elders = verhuurd; speelt bij mij & eigenaar elders
        // = gehuurd. squad-walk overschrijft e.Club straks met de huidige club.
        e.OwnerClub = cname;
        return e;
    }

    // ---------- staf ----------
    private static Person ReadStaff(MemScan m, ulong person, ulong st, uint uid, ushort ca, ushort pa)
    {
        var e = new Person
        {
            Uid = uid,
            Ca = ca,
            Pa = pa,
            IsPlayer = false,
            Name = ReadName(m, person)
        };
        (e.BirthYear, e.BirthDoy) = DecodeFmDate(m.U32(person + Fields.PERO_DOB));
        e.Age = AgeFrom(e.BirthYear, e.BirthDoy);
        e.Nat = ReadNation(m, person);
        e.NatId = ReadNationId(m, person);

        foreach (var (key, off) in Fields.StaffAttrs)
            e.StaffAttrs[key] = Attr(m, st + (ulong)Fields.NPLO_ATTRS + (ulong)off);

        ulong con = m.Ptr(person + Fields.PERO_FULL_CONTRACT);
        if (con != 0)
        {
            e.Wage = Money(m.U32(con + Fields.CON_WEEKLY_WAGE));
            e.Expires = FmDateIso(m.U32(con + Fields.CON_EXPIRY));
            e.JobId = m.U8(con + 0x26);          // pero.Pcjo — functie-byte; app vertaalt per taal
            e.Job = JobName(e.JobId);
        }
        if (string.IsNullOrEmpty(e.Job)) e.Job = "Staflid";
        e.Gender = (m.U8(person + (ulong)Fields.PERO_GENDER) & Fields.GENDER_FEMALE_BIT) != 0 ? 1 : 0;
        var (sclub, _, sdiv) = ResolveClub(m, person);
        e.Club = sclub; e.Div = sdiv;
        return e;
    }

    private static int Attr(MemScan m, ulong addr)
    {
        int raw = m.U8(addr);
        int v = (int)System.Math.Floor(raw / 5.0 + 0.5);
        return System.Math.Clamp(v, 0, 20);
    }

    // Persoonlijkheid is als rauwe byte 1..20 opgeslagen (geen ×5).
    private static int ClampAttr(int raw) => raw is >= 1 and <= 20 ? raw : 0;

    private static string ReadName(MemScan m, ulong person)
    {
        string common = m.NestedString(person + Fields.PERO_COMMON_NAME);
        if (!string.IsNullOrEmpty(common)) return common;
        string first = m.NestedString(person + Fields.PERO_FIRST_NAME);
        string second = m.NestedString(person + Fields.PERO_SECOND_NAME);
        string[] parts = [first, second];
        string name = string.Join(" ", parts.Where(s => !string.IsNullOrEmpty(s)));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static List<string> ReadNation(MemScan m, ulong person)
    {
        ulong nat = m.Ptr(person + Fields.PERO_NATION);
        if (nat == 0) return [];
        string s = m.IndirectString(nat + Fields.NATION_SHORT_NAME)
                 ?? m.IndirectString(nat + Fields.NATION_NAME);
        return s == null ? [] : [s];
    }

    // Taalonafhankelijk land-ID: de DB-UID in de objectheader van het nation-object
    // (zelfde OBJ_DUNI-plek als bij persons). Groundwork voor issue #15 — landnamen
    // komen in de gametaal binnen; met dit ID kan de app straks zelf vertalen en de
    // EU-lijst taalvrij maken. De app negeert het veld voorlopig.
    private static uint ReadNationId(MemScan m, ulong person)
    {
        ulong nat = m.Ptr(person + Fields.PERO_NATION);
        if (nat == 0) return 0;
        uint id = m.U32(nat + (ulong)Fields.OBJ_DUNI);
        return id == 0xFFFFFFFF ? 0 : id;
    }

    // Huidige club via de brondata-keten (authoritatief, uit gedecompileerde CE-tabel):
    //   contract = [person + 0xA8]   (pero.Pflc, volledig contract)
    //   team     = [contract + 0x10] (pero.Pcti)
    //   club     = [team + 0x30]     (teao.Tclu)
    //   naam     = indirecte string op club + 0xC0 (cluo.Cnam) / +0xC8 (Csnm)
    private const int CLUB_NAME = 0xC0;
    private const int CLUB_SHORT_NAME = 0xC8;

    // Fallback-club via contract-keten: contract(0xA8)→team(0x10)→club(0x30). Voor spelers
    // buiten geladen competities (die niet in een selectie-object staan). De squad-walk
    // overschrijft dit met de authoritatieve club. Ook gebruikt voor staf/manager.
    // Welke selectie-hit wint voor deze speler?
    //
    // Binnen dezelfde club: het hoogste elftal (laagste teamtype), zoals altijd.
    // Bij twee verschillende clubs staat de speler op huurbasis ergens anders. De moederclub
    // kennen we al uit de contract-keten (OwnerClub), dus de ándere club is waar hij speelt.
    // Dat is bewust géén teamtype-vergelijking meer: de oude regel "laagste teamtype wint"
    // koos bij een verhuurde eerste-elftalspeler willekeurig de moederclub, waarna club en
    // moederclub gelijk werden en de huur onzichtbaar was (26-07: 167 huren op 51.753 spelers,
    // allemaal teamtype 0 — jeugd- en reserveverhuur werd structureel gemist).
    private static void PickSquad(Dictionary<uint, (string club, int tt, int rep, string div)> squad,
                                  Dictionary<uint, Person> players,
                                  uint uid, string cname, int tt, int trep, string div)
    {
        if (!squad.TryGetValue(uid, out var cur)) { squad[uid] = (cname, tt, trep, div); return; }

        bool take;
        if (cur.club == cname)
        {
            take = tt < cur.tt;
        }
        else
        {
            string parent = players.TryGetValue(uid, out var pe) ? pe.OwnerClub : null;
            bool curIsParent = parent != null && cur.club == parent;
            bool newIsParent = parent != null && cname == parent;
            if (curIsParent == newIsParent) take = tt < cur.tt;   // geen van beide (of allebei) de moederclub
            else take = curIsParent;                              // de niet-moederclub is waar hij speelt

            if (MultiClub.Add(uid) && MultiClubSample.Count < 25)
            {
                string name = pe?.Name ?? "?";
                string won = take ? cname : cur.club, lost = take ? cur.club : cname;
                MultiClubSample.Add($"{name,-24} speelt: {won}  ·  ook in selectie: {lost}  ·  moederclub: {parent ?? "-"}");
            }
        }
        if (take) squad[uid] = (cname, tt, trep, div);
    }

    private static (string name, int rep, string div) ResolveClub(MemScan m, ulong person)
    {
        ulong con = m.Ptr(person + (ulong)Fields.PERO_FULL_CONTRACT);
        if (con == 0) return (null, 0, null);
        ulong team = m.Ptr(con + 0x10);
        if (team == 0) return (null, 0, null);
        int rep = m.U16(team + 0xA8);
        if (rep is < 0 or > 12000) rep = 0;
        ulong club = m.Ptr(team + 0x30);
        return (club == 0 ? null : ClubNameOf(m, club), rep, CompNameOf(m, team));
    }

    // Competitienaam van een team: [team+0x50/0x60] → VOLLEDIGE naam op comp+0x40, anders
    // de korte op comp+0x48. Volgorde bewust: de korte naam mist bij niet-gelicentieerde
    // competities de landkwalificatie (heel Spanje werd "Eerste Divisie"); de volledige
    // naam heeft die wel ("Oostenrijkse Eredivisie" — geverifieerd 15-07 via de comp-kaart).
    private static readonly int[] TeamCompOffs = [Fields.TEAM_COMP, Fields.TEAM_COMP_ALT];
    private static string CompNameOf(MemScan m, ulong team)
    {
        foreach (int toff in TeamCompOffs)
        {
            ulong comp = m.Ptr(team + (ulong)toff);
            if (comp == 0) continue;
            string s = m.IndirectString(comp + Fields.COMP_NAME);
            if (!PlausibleClub(s)) s = m.IndirectString(comp + Fields.COMP_SHORT_NAME);
            if (PlausibleClub(s)) return s;
        }
        return null;
    }

    private static string ClubNameOf(MemScan m, ulong club)
    {
        string name = m.IndirectString(club + CLUB_NAME) ?? m.IndirectString(club + CLUB_SHORT_NAME);
        return PlausibleClub(name) ? name : null;
    }

    // Alleen echte namen: overwegend Latijnse letters, geen Cyrillische/rare bytes.
    // Accepteert het hele Latijnse Unicode-blok (t/m Latin Extended-A/B + Additional),
    // anders vallen bv. Poolse (Lech Poznań, ł/ń/ś) en Turkse (ğ/ş/ı) clubnamen weg
    // en toont de app "onbekende club" terwijl de reputatie wél gelezen is.
    private static bool PlausibleClub(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length is < 2 or > 48) return false;
        int latin = 0, weird = 0;
        foreach (char c in s)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
            else if (c > 0x7F && !(char.IsLetter(c) && (c <= 0x24F || (c >= 0x1E00 && c <= 0x1EFF)))) weird++;
        }
        return latin >= 2 && weird == 0;
    }

    // Echte functie uit personJobTypes-enum (byte op contract+0x26).
    private static readonly Dictionary<int, string> Jobs = new()
    {
        [1] = "Speler", [2] = "Coach", [3] = "Speler/Coach", [4] = "Voorzitter",
        [6] = "Directeur", [8] = "Algemeen directeur", [10] = "Technisch directeur",
        [12] = "Fysiotherapeut", [14] = "Scout", [16] = "Manager", [17] = "Speler/Manager",
        [20] = "Assistent-manager", [21] = "Speler/Assistent-manager", [22] = "Media-analist",
        [24] = "Algemeen manager", [26] = "Fitnesscoach", [27] = "Speler/Fitnesscoach",
        [34] = "Keeperstrainer", [35] = "Speler/Keeperstrainer", [36] = "Hoofd data-analyse",
        [38] = "Clubarts", [40] = "Hoofd sportwetenschap", [42] = "Data-analist",
        [44] = "Hoofdscout", [45] = "Speler/Hoofdscout", [46] = "Arts", [48] = "Sportwetenschapper",
        [49] = "Speler/Jeugdtrainer", [50] = "Hoofd fysiotherapie", [52] = "U19-manager",
        [54] = "Trainer eerste elftal", [64] = "Hoofd jeugdopleiding", [65] = "Speler/Hoofd jeugd",
        [66] = "Eigenaar", [70] = "President", [86] = "Loanmanager", [88] = "Technisch directeur",
        [144] = "Interim-manager",
    };
    private static string JobName(int v) => Jobs.TryGetValue(v, out var s) ? s : null;

    // FM-datum: u32, jaar = raw>>16, dag-van-jaar = raw & 0x1ff
    private static (int year, int doy) DecodeFmDate(uint raw)
    {
        int year = (int)(raw >> 16);
        int doy = (int)(raw & 0x1ff);
        if (year is < 1900 or > 2100 || doy is < 1 or > 366) return (0, 0);
        return (year, doy);
    }

    private static string FmDateIso(uint raw)
    {
        var (year, doy) = DecodeFmDate(raw);
        if (year < 2000) return null; // contractdatums zijn 2025+; <2000 = sentinel/geen contract
        if (doy > (DateTime.IsLeapYear(year) ? 366 : 365)) return null; // 366 in niet-schrikkeljaar rolde door naar 1 jan van het jaar erna
        try { return new DateTime(year, 1, 1).AddDays(doy - 1).ToString("yyyy-MM-dd"); }
        catch { return null; }
    }

    private static int AgeFrom(int year, int doy)
    {
        if (year == 0) return 0;
        // Eerste schatting met de systeemdatum; wordt na FindGameDate herberekend met de
        // echte in-game datum als die gevonden is.
        return AgeAt(year, doy, GameDate ?? DateTime.Now);
    }

    private static int AgeAt(int year, int doy, DateTime now)
    {
        int age = now.Year - year - (doy <= now.DayOfYear ? 0 : 1);
        return age is >= 0 and <= 80 ? age : 0;
    }

    // Versie van game_plugin.dll (de module waarop alle offsets zijn gepind). Wijkt de
    // major.minor af van de gepinde versie, dan meldt de web-app "data mogelijk onbetrouwbaar".
    private static void DetectGameVersion(MemScan mem)
    {
        GameVersion = null; VersionOk = true;
        try
        {
            if (string.IsNullOrEmpty(mem.GpPath)) return;
            var fvi = FileVersionInfo.GetVersionInfo(mem.GpPath);
            GameVersion = fvi.FileVersion;
            if (string.IsNullOrEmpty(GameVersion)) return;   // geen versie-info: geen oordeel
            VersionOk = fvi.FileMajorPart == Fields.SUPPORTED_MAJOR && fvi.FileMinorPart == Fields.SUPPORTED_MINOR;
            Plugin.Log.LogInfo($"game_plugin.dll versie {GameVersion} (offsets gepind op {Fields.SUPPORTED_VERSION}.x → {(VersionOk ? "ok" : "AFWIJKEND")})");
        }
        catch (Exception e) { Plugin.Log.LogWarning("Versiedetectie mislukt: " + e.Message); }
    }

    // In-game datum: gelezen van het schema-object van MIJN team ([team+0xA0]+0x94, of +0x18) =
    // de eerstvolgende wedstrijddatum. Op wedstrijddagen is dat exact "vandaag"; tussen duels
    // (winter-/zomerstop) loopt het tot ~2 weken achter. BEKENDE BEPERKING (15-07): de échte
    // wereldklok wordt niet als leesbaar FM-datum-u32 op team-/schema-/competitie-/club-objecten
    // opgeslagen (discovery over 9.800 teams gaf nergens een gedeelde "vandaag"); hij leeft
    // vermoedelijk als C#-DateTime in GameAssembly of op een globaal wereld-object. Bewust niet
    // verder achterna gejaagd — de impact is cosmetisch (leeftijd verandert alleen op verjaardag).
    // Rechtstreeks gelezen (team via manager-keten); teamstemmen (DateVotes) als kruischeck.
    // Lukt het niet, dan blijft de afgeleide datum (seizoensjaar + systeemmaand/-dag) staan.
    private static void FindGameDate(MemScan mem, IEnumerable<Person> players, IEnumerable<Person> staff)
    {
        GameDate = null;
        try
        {
            uint pin = 0;
            if (DiagMyTeam != 0)
            {
                ulong sch = mem.Ptr(DiagMyTeam + (ulong)Fields.TEAM_SCHEDULE);
                if (sch != 0)
                    foreach (int so in new[] { Fields.SCHED_NEXT_MATCH, Fields.SCHED_NEXT_MATCH_ALT })
                    {
                        var (y, d) = DecodeFmDate(mem.U32(sch + (ulong)so));
                        if (y >= GameYear - 1 && y <= GameYear + 1) { pin = ((uint)y << 16) | (uint)d; break; }
                    }
            }
            if (pin != 0)
            {
                var (year, doy) = DecodeFmDate(pin);
                GameDate = new DateTime(year, 1, 1).AddDays(doy - 1);
                GameYear = year;
                foreach (var p in players.Concat(staff))
                    if (p.BirthYear > 0) p.Age = AgeAt(p.BirthYear, p.BirthDoy, GameDate.Value);
                Plugin.Log.LogInfo($"In-game datum via team-schema: {GameDate.Value:yyyy-MM-dd} (kruischeck {DateVotes.GetValueOrDefault(pin)} teamstemmen)");
            }
            else Plugin.Log.LogInfo("In-game datum: team-schema niet leesbaar — bron blijft 'derived'.");
        }
        catch (Exception e) { Plugin.Log.LogWarning("Datum-bepaling mislukt: " + e.Message); }
    }

    // ---------- 3D & 2D kit extraction ----------
    private sealed class KitColorInfo
    {
        public string Bg;
        public string Fg;
        public string OutColor;
        public int Style;
    }

    private sealed class ClubKitsInfo
    {
        public KitColorInfo Home;
        public KitColorInfo Away;
        public KitColorInfo Third;
    }

    private static ClubKitsInfo ExtractClubKits(MemScan m, ulong cino, uint clubId = 0)
    {
        if (cino == 0) return null;
        var res = new ClubKitsInfo();

        static string ToHex(uint c)
        {
            if (c == 0) return null;
            byte r = (byte)((c >> 16) & 0xFF);
            byte g = (byte)((c >> 8) & 0xFF);
            byte b = (byte)(c & 0xFF);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        ulong ptr78 = m.Ptr(cino + 0x78);
        ulong ptr80 = m.Ptr(cino + 0x80);
        if (ptr78 != 0 && ptr80 >= ptr78)
        {
            int count = (int)((ptr80 - ptr78) / 8);
            if (count > 64) count = 64; // Sanity limit

            // Debug dump for Arsenal and East London United
            if (clubId == 602 || clubId == 2000778304)
            {
                string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\FMSuperScout\debug_cino_dump2.txt";
                File.AppendAllText(path, $"\n\n=== Club {clubId} (Cino: {cino:X}) ===\n");
                byte[] raw = new byte[0x200];
                m.ReadBlock(cino, raw, 0x200);
                for(int i=0; i<0x200; i++) File.AppendAllText(path, $"{raw[i]:X2} ");
                
                File.AppendAllText(path, $"\nKITS (ptr78={ptr78:X}, ptr80={ptr80:X}, count={count}):\n");
                for (int k = 0; k < count; k++)
                {
                    ulong kitPtr = m.Ptr(ptr78 + (ulong)(k * 8));
                    if (kitPtr == 0) continue;
                    byte[] kbuf = new byte[40];
                    if (m.ReadBlock(kitPtr, kbuf, 40))
                    {
                        File.AppendAllText(path, $"[{k:D2}] ptr={kitPtr:X}: ");
                        for(int j=0; j<40; j++) File.AppendAllText(path, $"{kbuf[j]:X2} ");
                        File.AppendAllText(path, "\n");
                    }
                }
            }
            
            var rawKits = new List<(string fg, string bg, string outColor, int style, byte b34, byte b35, byte b37)>();
            byte[] kbuf_ = new byte[40];
            for (int k = 0; k < count; k++)
            {
                ulong kitPtr = m.Ptr(ptr78 + (ulong)(k * 8));
                if (kitPtr == 0) continue;
                if (m.ReadBlock(kitPtr, kbuf_, 40))
                {
                    bool hasSentinel = true;
                    for (int s = 0x18; s < 0x20; s++)
                    {
                        if (kbuf_[s] != 0xFF) { hasSentinel = false; break; }
                    }
                    if (!hasSentinel) continue;

                    uint u00 = BitConverter.ToUInt32(kbuf_, 0x00);
                    uint u04 = BitConverter.ToUInt32(kbuf_, 0x04);
                    uint u08 = BitConverter.ToUInt32(kbuf_, 0x08);
                    uint u0C = BitConverter.ToUInt32(kbuf_, 0x0C);
                    uint u10 = BitConverter.ToUInt32(kbuf_, 0x10);
                    int style = BitConverter.ToInt32(kbuf_, 0x14);
                    byte b34 = kbuf_[0x22];
                    byte b35 = kbuf_[0x23];
                    byte b37 = kbuf_[0x25];

                    // Filter out goalkeepers (b37 == 0) and non-shirt pieces ((b35 & 0x0F) != 1)
                    if (b37 == 0 || (b35 & 0x0F) != 1) continue;

                    string bg = ToHex(u04);
                    string fg = ToHex(u00) ?? bg;
                    string outC = ToHex(u08) ?? fg ?? bg;

                    if (bg != null || fg != null)
                    {
                        rawKits.Add((fg, bg, outC, style, b34, b35, b37));
                    }
                }
            }

            if (rawKits.Count > 0)
            {
                var (hFg, hBg, hOut, hStyle, _, _, _) = rawKits.FirstOrDefault(k => k.b34 == 0);
                if (hBg != null) res.Home = new KitColorInfo { Bg = hBg, Fg = hFg ?? hBg, OutColor = hOut ?? hFg ?? hBg, Style = hStyle };

                var (aFg, aBg, aOut, aStyle, _, _, _) = rawKits.FirstOrDefault(k => k.b34 == 1);
                if (aBg != null) res.Away = new KitColorInfo { Bg = aBg, Fg = aFg ?? aBg, OutColor = aOut ?? aFg ?? aBg, Style = aStyle };

                var (tFg, tBg, tOut, tStyle, _, _, _) = rawKits.FirstOrDefault(k => k.b34 == 2);
                if (tBg != null) res.Third = new KitColorInfo { Bg = tBg, Fg = tFg ?? tBg, OutColor = tOut ?? tFg ?? tBg, Style = tStyle };
            }
        }

        // Only use 2D Cino colors as fallback if the 3D home kit is missing
        uint k1Bg = m.U32(cino + (ulong)Fields.CINO_BG_COLOR);
        uint k1Fg = m.U32(cino + (ulong)Fields.CINO_FG_COLOR);
        if ((k1Bg != 0 || k1Fg != 0) && res.Home == null)
        {
            string bg = ToHex(k1Bg);
            string fg = ToHex(k1Fg) ?? bg;
            res.Home = new KitColorInfo { Bg = bg, Fg = fg, OutColor = fg, Style = 0 };
        }

        return res;
    }

    // ---------- output ----------
    private static void WriteJson(IEnumerable<Person> players, IEnumerable<Person> staff, MemScan mem = null, IEnumerable<ulong> clubs = null)
    {
        // Atomair: eerst naar dump.json.tmp schrijven en pas na een geslaagde Close
        // over dump.json heen schuiven. De web-app kan dump.json op elk moment lezen;
        // zonder dit kon een half geschreven bestand stilletjes als halve spelerslijst
        // geladen worden (of de lezing botsen met het schrijven).
        string path = Path.Combine(OutDir, "dump.json");
        string tmp = path + ".tmp";
        using var j = new JsonWriter(tmp);   // 'using': ook bij een schrijffout gaat de tmp-handle dicht
        j.BeginObj();
        j.Key("meta"); j.BeginObj();
        j.Prop("generated", DateTime.Now.ToString("s"));
        int gy = GameYear > 0 ? GameYear : DateTime.Now.Year;
        if (GameDate is DateTime gd)
        {
            // Exacte in-game datum uit het geheugen gevonden.
            j.Prop("gameDate", gd.ToString("yyyy-MM-dd"));
            j.Prop("gameDateSource", "memory");
        }
        else
        {
            // Fallback: afgeleid seizoensjaar met de systeemmaand/-dag (jaar is het betrouwbare deel).
            j.Prop("gameDate", $"{gy:D4}-{DateTime.Now:MM-dd}");
            j.Prop("gameDateSource", "derived");
        }
        j.Prop("gameYear", gy);
        j.Prop("pluginVersion", Plugin.Version);
        j.Prop("gameVersion", GameVersion);
        j.Prop("supportedVersion", Fields.SUPPORTED_VERSION);
        j.Prop("versionOk", VersionOk);
        j.Prop("manager", ManagerName);
        j.Prop("myClub", MyClub);
        j.Prop("myClubRep", MyClubRep);
        j.Prop("currency", "GBP");
        j.Prop("source", "FMSuperScout plugin v" + Plugin.Version);
        j.EndObj();

        // Voortgang 0.90→1.0 tijdens het wegschrijven (laatste ~10% van de doorlooptijd).
        int total = 0, written = 0;
        if (players is ICollection<Person> pc) total += pc.Count;
        if (staff is ICollection<Person> sc) total += sc.Count;

        int pcnt = players is ICollection<Person> pc2 ? pc2.Count : 0;
        int scnt = staff is ICollection<Person> sc2 ? sc2.Count : 0;
        j.Key("players"); j.BeginArr();
        foreach (var p in players) { WritePerson(j, p, true); WriteJsonProgress(++written, total, pcnt, scnt); }
        j.EndArr();

        j.Key("staff"); j.BeginArr();
        foreach (var p in staff) { WritePerson(j, p, false); WriteJsonProgress(++written, total, pcnt, scnt); }
        j.EndArr();

        if (mem != null && clubs != null)
        {
            j.Key("clubs"); j.BeginArr();
            var seenClubs = new HashSet<uint>();
            foreach (ulong club in clubs)
            {
                string cname = ClubNameOf(mem, club);
                if (cname == null) continue;
                uint uid = mem.U32(club + (ulong)Fields.CLUB_UID);
                if (uid == 0 || uid == 0xFFFFFFFF || !seenClubs.Add(uid)) continue;

                ulong nat = mem.Ptr(club + (ulong)Fields.CLUB_NATION);
                string country = nat != 0 ? (mem.IndirectString(nat + Fields.NATION_NAME) ?? mem.IndirectString(nat + Fields.NATION_SHORT_NAME)) : null;

                string div = null;
                ulong tb = mem.Ptr(club + 0x18), te = mem.Ptr(club + 0x20);
                if (tb != 0 && te > tb && (te - tb) % 8 == 0)
                {
                    long tcnt = (long)((te - tb) / 8);
                    if (tcnt <= 64)
                    {
                        ulong firstTeam = 0;
                        ulong fallbackTeam = 0;
                        for (long ti = 0; ti < tcnt; ti++)
                        {
                            ulong t = mem.Ptr(tb + (ulong)ti * 8);
                            if (t == 0) continue;
                            if (fallbackTeam == 0) fallbackTeam = t;
                            int tt = mem.U8(t + 0x28);
                            if (tt == 0) { firstTeam = t; break; }
                        }
                        ulong teamToUse = firstTeam != 0 ? firstTeam : fallbackTeam;
                        if (teamToUse != 0) div = CompNameOf(mem, teamToUse);
                    }
                }

                ulong cino = mem.Ptr(club + (ulong)Fields.CLUB_INFO);
                if (cname == "Arsenal" || cname == "Aston Villa" || cname == "Blackburn Rovers" || cname == "Brentford" || cname == "Brighton & Hove Albion") {
                    byte[] debugMem = new byte[0x500];
                    if (mem.ReadBlock(cino, debugMem, 0x500)) {
                        System.IO.File.AppendAllText(Path.Combine(OutDir, "debug_cino_dump.txt"), $"\n=== {cname} (Cino: {cino:X}) ===\n" + BitConverter.ToString(debugMem).Replace("-", " "));
                    }
                }
                var kits = ExtractClubKits(mem, cino, uid);

                j.BeginObj();
                j.Prop("id", uid);
                j.Prop("name", cname);
                if (country != null) j.Prop("country", country);
                if (div != null) j.Prop("division", div);
                if (kits?.Home?.Bg != null) { j.Prop("bgColor", kits.Home.Bg); j.Prop("k1BgColor", kits.Home.Bg); }
                if (kits?.Home?.Fg != null) { j.Prop("fgColor", kits.Home.Fg); j.Prop("k1FgColor", kits.Home.Fg); }
                if (kits?.Home?.OutColor != null) { j.Prop("outlineColor", kits.Home.OutColor); j.Prop("k1OutColor", kits.Home.OutColor); }
                if (kits?.Home != null && kits.Home.Style > 0) j.Prop("k1Style", kits.Home.Style);

                if (kits?.Away?.Bg != null) j.Prop("k2BgColor", kits.Away.Bg);
                if (kits?.Away?.Fg != null) j.Prop("k2FgColor", kits.Away.Fg);
                if (kits?.Away?.OutColor != null) j.Prop("k2OutColor", kits.Away.OutColor);
                if (kits?.Away != null && kits.Away.Style > 0) j.Prop("k2Style", kits.Away.Style);

                if (kits?.Third?.Bg != null) j.Prop("k3BgColor", kits.Third.Bg);
                if (kits?.Third?.Fg != null) j.Prop("k3FgColor", kits.Third.Fg);
                if (kits?.Third?.OutColor != null) j.Prop("k3OutColor", kits.Third.OutColor);
                if (kits?.Third != null && kits.Third.Style > 0) j.Prop("k3Style", kits.Third.Style);
                j.EndObj();

            }
            j.EndArr();
        }

        j.EndObj();
        j.Close();
        File.Move(tmp, path, true);
    }

    // Met de echte aantallen: de eerdere 0,0 liet de tellers in status.json terugflappen
    // naar nul tijdens de schrijffase.
    private static void WriteJsonProgress(int written, int total, int players, int staff)
    {
        if (total > 0 && written % 8192 == 0)
            WriteStatus("scanning", players, staff, null, 0.90 + 0.10 * written / total);
    }

    private static void WritePerson(JsonWriter j, Person p, bool isPlayer)
    {
        j.BeginObj();
        j.Prop("id", p.Uid);
        j.Prop("name", p.Name ?? "?");
        j.Prop("age", p.Age);
        if (p.BirthYear > 0) { j.Prop("dob", $"{p.BirthYear:D4}"); j.Prop("birthYear", p.BirthYear); j.Prop("birthDoy", p.BirthDoy); }
        j.Key("nat"); j.BeginArr(); foreach (var n in p.Nat) j.Val(n); j.EndArr();
        if (p.NatId != 0) j.Prop("natId", p.NatId);
        j.Prop("club", p.Club);
        // Moederclub alleen emitten als die afwijkt van de huidige club (= huurrelatie); scheelt ruis.
        if (isPlayer && p.OwnerClub != null && p.OwnerClub != p.Club) j.Prop("ownerClub", p.OwnerClub);
        j.Prop("div", p.Div);
        // Gender alleen emitten als vrouw (bij "vrouwenvoetbal meenemen"); mannen blijven veld-loos.
        if (p.Gender == 1) j.Prop("gender", 1);
        j.Prop("ca", p.Ca);
        j.Prop("pa", p.Pa);
        Money(j, "wage", p.Wage);
        j.Prop("expires", p.Expires);
        if (isPlayer)
        {
            j.Prop("pos", string.Join(", ", p.PosArr));
            j.Key("posArr"); j.BeginArr(); foreach (var x in p.PosArr) j.Val(x); j.EndArr();
            if (p.TeamType >= 0) j.Prop("teamType", p.TeamType);   // 0=1e, ~3=reserves, ≥10=jeugd
            j.Prop("foot", p.Foot);
            if (p.Height > 0) j.Prop("height", p.Height);
            Money(j, "value", p.Value);
            // Vraagprijs = waardeveld. Een los opgeslagen "echte vraagprijs" bestaat niet:
            // FM berekent de geëiste som per onderhandeling (koper-afhankelijk). De enige
            // gematerialiseerde vraagprijs (club zet expliciet een prijs bij Listed) landt
            // exact in dit waardeveld (ijking 14-07, 4/4 ±1%). Rest: app-model + clausules.
            Money(j, "askingPrice", p.Value);
            j.Null4("wageDemand");
            j.Prop("listed", p.Listed);
            j.Prop("loanListed", p.LoanListed);
            j.Prop("notForSale", p.NotForSale);
            j.Prop("setForRelease", p.SetForRelease);
            j.Prop("clubRep", p.ClubRep);
            j.Prop("worldRep", p.WorldRep);
            j.Prop("ambition", p.Ambition);
            j.Prop("loyalty", p.Loyalty);
            j.Prop("professionalism", p.Professionalism);
            j.Prop("adaptability", p.Adaptability);
            j.Prop("pressure", p.Pressure);
            j.Prop("sportsmanship", p.Sportsmanship);
            j.Prop("temperament", p.Temperament);
            j.Prop("controversy", p.Controversy);
            j.Key("attrs"); j.BeginObj();
            foreach (var kv in p.Attrs) { j.Key(kv.Key); j.Val((long)kv.Value); }
            j.EndObj();
        }
        else
        {
            j.Prop("job", p.Job);
            if (p.JobId > 0) j.Prop("jobId", p.JobId);   // taalonafhankelijk; app vertaalt
            j.Key("staffAttrs"); j.BeginObj();
            foreach (var kv in p.StaffAttrs) { j.Key(kv.Key); j.Val((long)kv.Value); }
            j.EndObj();
        }
        j.EndObj();
    }

    // === Kit-color investigation (issue: export clubs with kit color to CSV) ===
    // No offset for kit colors is pinned yet (Fields.cs has none). This is a gated,
    // opt-in diagnostic: when DATA_DIR/kit-scan.json contains {"enabled":true}, hex-dump
    // a window of bytes around each club object that matches one of the reference clubs
    // below. Reference clubs are chosen because their real-world primary kit colors are
    // unambiguous and well known, so a human (or a follow-up pinning session) can diff
    // the hex dumps across clubs and look for a 3/4-byte run that lines up with each
    // club's real shirt color at a consistent offset. Keeps the dump small: only a
    // handful of clubs are captured, not all ~5000+, and it never runs unless enabled.
    private static readonly (string match, string primaryHex, string secondaryHex)[] KitReferenceClubs =
    [
        ("Ajax", "#D2122E", "#FFFFFF"),            // rood/wit
        ("Feyenoord", "#C1272D", "#FFFFFF"),        // rood/wit
        ("PSV", "#ED1C24", "#FFFFFF"),              // rood/wit
        ("FC Barcelona", "#A50044", "#004D98"),     // garnet/blue
        ("Real Madrid", "#FEBE10", "#FFFFFF"),      // wit met goud-accent
        ("Manchester United", "#DA291C", "#FFE500"),// rood
        ("Liverpool", "#C8102E", "#00B2A9"),        // rood
        ("Juventus", "#000000", "#FFFFFF"),         // zwart/wit
        ("Bayern", "#DC052D", "#0066B2"),           // rood/blauw
        ("Borussia Dortmund", "#FDE100", "#000000"),// geel/zwart
        ("Celtic", "#018749", "#FFFFFF"),           // groen/wit
        ("Rangers", "#1B458F", "#FFFFFF"),          // blauw/wit
        ("London", "#112233", "#445566"),           // RB London
    ];

    private static bool KitScanEnabled()
    {
        try
        {
            string cf = Path.Combine(OutDir, "kit-scan.json");
            if (!File.Exists(cf)) return false;
            string raw = File.ReadAllText(cf);
            return raw.Contains("\"enabled\":true") || raw.Contains("\"enabled\": true");
        }
        catch { return false; }
    }

    // Window size around the club-object base (club+0xC0/+0xC8 hold the name, see
    // ClubNameOf) that we hex-dump. Generous but bounded: covers everything from the
    // object header through the team-list pointers and well past the name fields,
    // in case kit colors live either just before or just after the name block.
    private const int KitScanWindow = 0x300;

    private static void WriteKitScan(MemScan m, IEnumerable<ulong> clubObjs)
    {
        if (!KitScanEnabled()) return;
        try
        {
            string path = Path.Combine(OutDir, "kit-scan.txt");
            using var w = new StreamWriter(path, false);
            w.WriteLine($"FMSuperScout kit-color investigation dump — {DateTime.Now}");
            w.WriteLine("Resolved via reverse-engineering: club colors are located in the Club Info sub-object (cluo.Cino).");
            w.WriteLine($"  club + 0x{Fields.CLUB_UID:X2} = Unique ID (objo.Duni)");
            w.WriteLine($"  club + 0x{Fields.CLUB_INFO:X2} = Pointer to Club Info (cluo.Cino)");
            w.WriteLine($"  [club + 0x{Fields.CLUB_INFO:X2}] + 0x{Fields.CINO_BG_COLOR:X2} = Background / Primary shirt color (cluo.Tbcl, 32-bit BGRA)");
            w.WriteLine($"  [club + 0x{Fields.CLUB_INFO:X2}] + 0x{Fields.CINO_FG_COLOR:X2} = Foreground / Secondary trim color (cluo.Ttcl, 32-bit BGRA)");
            w.WriteLine();
            w.WriteLine("Reference colors (approximate, primary shirt / secondary trim):");
            foreach (var (match, primaryHex, secondaryHex) in KitReferenceClubs) w.WriteLine($"  {match,-20} {primaryHex} / {secondaryHex}");
            w.WriteLine();

            var buf = new byte[KitScanWindow];
            int dumped = 0;
            foreach (ulong club in clubObjs)
            {
                string cname = ClubNameOf(m, club);
                if (cname == null) continue;
                var (match, primaryHex, secondaryHex) = Array.Find(KitReferenceClubs,
                    r => cname.Contains(r.match, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;

                uint uid = m.U32(club + (ulong)Fields.CLUB_UID);
                ulong cino = m.Ptr(club + (ulong)Fields.CLUB_INFO);

                string colorReport = "No Club Info ptr";
                if (cino != 0)
                {
                    uint bg = m.U32(cino + (ulong)Fields.CINO_BG_COLOR);
                    uint fg = m.U32(cino + (ulong)Fields.CINO_FG_COLOR);
                    byte bgR = (byte)((bg >> 16) & 0xFF), bgG = (byte)((bg >> 8) & 0xFF), bgB = (byte)(bg & 0xFF);
                    byte fgR = (byte)((fg >> 16) & 0xFF), fgG = (byte)((fg >> 8) & 0xFF), fgB = (byte)(fg & 0xFF);
                    colorReport = $"Primary: #{bgR:X2}{bgG:X2}{bgB:X2}, Secondary: #{fgR:X2}{fgG:X2}{fgB:X2} (Cino: 0x{cino:X})";
                }

                w.WriteLine($"=== {cname} (ref: {match}, UID: {uid}) ===");
                w.WriteLine($"  Detected Kit Colors: {colorReport}");
                w.WriteLine($"  Expected Kit Colors: {primaryHex} / {secondaryHex}");

                if (cino != 0 && !m.ReadBlock(cino, buf, KitScanWindow))
                {
                    w.WriteLine($"  [cino memory read failed]");
                }
                else if (cino != 0)
                {
                    static string FormatAscii(byte[] b, int offset, int length)
                    {
                        char[] chars = new char[length];
                        for (int k = 0; k < length; k++)
                        {
                            byte val = b[offset + k];
                            chars[k] = val >= 32 && val <= 126 ? (char)val : '.';
                        }
                        return new string(chars);
                    }

                    w.WriteLine("  Cino Memory Dump:");
                    for (int i = 0; i < KitScanWindow; i += 16)
                    {
                        var hex = BitConverter.ToString(buf, i, 16).Replace("-", " ");
                        var ascii = FormatAscii(buf, i, 16);
                        w.WriteLine($"  +0x{i:03X}: {hex}  {ascii}");
                    }
                    
                    // Dump memory at Cino + 0x78
                    ulong ptr78 = m.Ptr(cino + 0x78);
                    if (ptr78 != 0)
                    {
                        w.WriteLine($"  Pointer at Cino+0x78: 0x{ptr78:X}");
                        if (m.ReadBlock(ptr78, buf, 256))
                        {
                            w.WriteLine("  Memory at Cino+0x78:");
                            for (int i = 0; i < 256; i += 16)
                            {
                                var hex = BitConverter.ToString(buf, i, 16).Replace("-", " ");
                                var ascii = FormatAscii(buf, i, 16);
                                w.WriteLine($"  +0x{i:03X}: {hex}  {ascii}");
                            }
                            
                            // Iterate through all 3D kit pointers at ptr78
                            w.WriteLine("  All 3D Kit Entries at Cino+0x78:");
                            for (int k = 0; k < 32; k++)
                            {
                                ulong kitPtr = m.Ptr(ptr78 + (ulong)(k * 8));
                                if (kitPtr == 0) break;
                                byte[] kbuf = new byte[40];
                                if (m.ReadBlock(kitPtr, kbuf, 40))
                                {
                                    string HexC(int off) {
                                        uint c = BitConverter.ToUInt32(kbuf, off);
                                        return c == 0 ? "none   " : $"#{(c >> 16) & 0xFF:X2}{(c >> 8) & 0xFF:X2}{c & 0xFF:X2}";
                                    }
                                    int style = BitConverter.ToInt32(kbuf, 0x14);
                                    byte b34 = kbuf[0x22], b35 = kbuf[0x23], b37 = kbuf[0x25];
                                    string tailHex = BitConverter.ToString(kbuf, 0x18, 16).Replace("-", " ");
                                    w.WriteLine($"    [{k,2}] (0x{kitPtr:X}) shirt={HexC(0x04)} shorts={HexC(0x00)} socks={HexC(0x08)} num={HexC(0x0C)} trim={HexC(0x10)} style={style,-4} (b34={b34}, b35={b35}, b37={b37}) tail=[{tailHex}]");
                                }
                            }
                        }
                        else
                        {
                            w.WriteLine($"  [Failed to read memory at Cino+0x78 (0x{ptr78:X})]");
                        }
                    }
                }
                w.WriteLine($"  Club Object Address: 0x{club:X}");
                var clubBuf = new byte[1024];
                if (m.ReadBlock(club, clubBuf, 1024))
                {
                    w.WriteLine("  Club Memory Dump (1024 bytes):");
                    for (int off = 0; off < 1024; off += 16)
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append($"  +0x{off:X3}: ");
                        for (int i = 0; i < 16 && off + i < 1024; i++) sb.Append($"{clubBuf[off + i]:X2} ");
                        sb.Append(' ');
                        for (int i = 0; i < 16 && off + i < 1024; i++)
                        {
                            byte b = clubBuf[off + i];
                            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                        }
                        w.WriteLine(sb.ToString());
                    }
                }
                w.WriteLine();
                dumped++;
            }
            w.WriteLine($"Clubs matched against reference list: {dumped} / {KitReferenceClubs.Length}");
            if (dumped == 0)
                w.WriteLine("No reference clubs found in this save — pick a save/league that includes some of the clubs above.");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"kit-scan mislukt: {ex.Message}"); }
    }

    private static void WriteClubs(MemScan m, IEnumerable<ulong> clubObjs)
    {
        try
        {
            string path = Path.Combine(OutDir, "clubs.json");
            string tmp = path + ".tmp";
            using (var j = new JsonWriter(tmp))
            {
                j.BeginObj();
                j.Key("clubs"); j.BeginArr();
                var seen = new HashSet<uint>();
                foreach (ulong club in clubObjs)
                {
                    string cname = ClubNameOf(m, club);
                    if (cname == null) continue;
                    uint uid = m.U32(club + (ulong)Fields.CLUB_UID);
                    if (uid == 0 || uid == 0xFFFFFFFF || !seen.Add(uid)) continue;
                    
                    ulong nat = m.Ptr(club + (ulong)Fields.CLUB_NATION);
                    string country = nat != 0 ? (m.IndirectString(nat + Fields.NATION_NAME) ?? m.IndirectString(nat + Fields.NATION_SHORT_NAME)) : null;

                    string div = null;
                    ulong tb = m.Ptr(club + 0x18), te = m.Ptr(club + 0x20);
                    if (tb != 0 && te > tb && (te - tb) % 8 == 0)
                    {
                        long tcnt = (long)((te - tb) / 8);
                        if (tcnt <= 64)
                        {
                            ulong firstTeam = 0;
                            ulong fallbackTeam = 0;
                            for (long ti = 0; ti < tcnt; ti++)
                            {
                                ulong t = m.Ptr(tb + (ulong)ti * 8);
                                if (t == 0) continue;
                                if (fallbackTeam == 0) fallbackTeam = t;
                                int tt = m.U8(t + 0x28);
                                if (tt == 0) // First Team (senior)
                                {
                                    firstTeam = t;
                                    break;
                                }
                            }
                            ulong teamToUse = firstTeam != 0 ? firstTeam : fallbackTeam;
                            if (teamToUse != 0) div = CompNameOf(m, teamToUse);
                        }
                    }

                    ulong cino = m.Ptr(club + (ulong)Fields.CLUB_INFO);
                    if (cname == "Arsenal" || cname == "Aston Villa" || cname == "Blackburn Rovers" || cname == "Brentford" || cname == "Brighton & Hove Albion") {
                        byte[] debugMem = new byte[0x500];
                        if (m.ReadBlock(cino, debugMem, 0x500)) {
                            System.IO.File.AppendAllText(Path.Combine(OutDir, "debug_cino_dump.txt"), $"\n=== {cname} (Cino: {cino:X}) ===\n" + BitConverter.ToString(debugMem).Replace("-", " "));
                        }
                    }
                    var kits = ExtractClubKits(m, cino, uid);

                    j.BeginObj();
                    j.Prop("id", uid);
                    j.Prop("name", cname);
                    if (country != null) j.Prop("country", country);
                    if (div != null) j.Prop("division", div);
                    if (kits?.Home?.Bg != null) { j.Prop("bgColor", kits.Home.Bg); j.Prop("k1BgColor", kits.Home.Bg); }
                    if (kits?.Home?.Fg != null) { j.Prop("fgColor", kits.Home.Fg); j.Prop("k1FgColor", kits.Home.Fg); }
                    if (kits?.Home?.OutColor != null) { j.Prop("outlineColor", kits.Home.OutColor); j.Prop("k1OutColor", kits.Home.OutColor); }
                    if (kits?.Home != null && kits.Home.Style > 0) j.Prop("k1Style", kits.Home.Style);

                    if (kits?.Away?.Bg != null) j.Prop("k2BgColor", kits.Away.Bg);
                    if (kits?.Away?.Fg != null) j.Prop("k2FgColor", kits.Away.Fg);
                    if (kits?.Away?.OutColor != null) j.Prop("k2OutColor", kits.Away.OutColor);
                    if (kits?.Away != null && kits.Away.Style > 0) j.Prop("k2Style", kits.Away.Style);

                    if (kits?.Third?.Bg != null) j.Prop("k3BgColor", kits.Third.Bg);
                    if (kits?.Third?.Fg != null) j.Prop("k3FgColor", kits.Third.Fg);
                    if (kits?.Third?.OutColor != null) j.Prop("k3OutColor", kits.Third.OutColor);
                    if (kits?.Third != null && kits.Third.Style > 0) j.Prop("k3Style", kits.Third.Style);
                    j.EndObj();
                }
                j.EndArr();
                j.EndObj();
                j.Close();
            }
            File.Move(tmp, path, true);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"WriteClubs mislukt: {ex.Message}"); }
    }

    private static void WriteDiag(MemScan m, Dictionary<uint, Person> players, Dictionary<uint, Person> staff,

        Dictionary<int, int> hist, long candidates, long ms)
    {
        try
        {
            string path = Path.Combine(OutDir, "diagnostics.txt");
            using var w = new StreamWriter(path, false);
            w.WriteLine($"FMSuperScout diagnostics — {DateTime.Now}");
            var (dAvailPhys, dAvailCommit, dLoad) = MemScan.MemoryStatus();
            w.WriteLine($"Scanregio's: {m.ScanRegions.Count}  ·  leesbron: {ScanMode}");
            w.WriteLine($"Geheugen (na scan): {dAvailPhys / (1024 * 1024):N0} MB fysiek vrij · {dAvailCommit / (1024 * 1024):N0} MB commit vrij · belasting {dLoad}%");
            if (m.ImageNote != null) w.WriteLine($"Let op: {m.ImageNote}");
            w.WriteLine($"GameAssembly.dll: {m.GaBase:X}-{m.GaEnd:X}");
            w.WriteLine($"game_plugin.dll:  {m.GpBase:X}-{m.GpEnd:X}");
            w.WriteLine($"Kandidaten: {candidates:N0}  (vtable in game_plugin: {VtGp:N0})");
            w.WriteLine($"Spelers: {players.Count}  Staf: {staff.Count}  Tijd: {ms} ms");
            w.WriteLine($"Staf ruw: {DiagStaffRaw}  ·  ook speler (verwijderd als dubbel): {DiagStaffAlsoPlayer}  ·  netto staf: {staff.Count}");
            w.WriteLine("Fasen: " + string.Join(" · ", PhaseLog));
            w.WriteLine();
            // Repin-hints bij een afwijkende gameversie: welke pinnen staan er, waar zitten
            // de pieken nu — samen met docs/repin-guide.md is dat het halve herstelwerk.
            if (!VersionOk)
            {
                w.WriteLine("=== REPIN-HINTS (gameversie wijkt af van gepinde " + Fields.SUPPORTED_VERSION + ".x) ===");
                w.WriteLine($"Gepind: speler=0x{Fields.PLAYER_OFFSET:X} speler+staf=0x{Fields.PLAYER_STAFF_OFFSET:X} " +
                            $"staf=0x{Fields.STAFF_OFFSET:X} manager=0x{Fields.HUMAN_MANAGER_OFFSET:X}");
                w.WriteLine("Kandidaten nu (grootste class-pieken hieronder). Vuistregels: elke class toont");
                w.WriteLine("als twee pieken 0x28 uit elkaar (neem de laagste van het paar); de staf-piek is");
                w.WriteLine("groter dan de spelerpiek; manager is een mini-piek (~2). Volledige werkwijze:");
                w.WriteLine("docs/repin-guide.md in de repo. Na het pinnen: SUPPORTED_* in Fields.cs bijwerken.");
                w.WriteLine();
            }
            // Health-check: de grote pieken horen speler=0x288 en staf=0x100 te zijn. Wijkt dit
            // af na een FM-patch, dan zijn de class-offsets verschoven en moeten ze opnieuw gepind.
            w.WriteLine("=== Class-offsets (meta+4) met plausibele UID, top 15 ===");
            foreach (var kv in AllOffHist.OrderByDescending(x => x.Value).Take(15))
                w.WriteLine($"  0x{kv.Key:X} ({kv.Key,5}) : {kv.Value:N0}");
            w.WriteLine();
            w.WriteLine("Matches per offset (speler/staf-filter geslaagd):");
            foreach (var kv in hist.OrderByDescending(x => x.Value))
                w.WriteLine($"  0x{kv.Key:X} ({kv.Key}) : {kv.Value}");
            w.WriteLine();
            w.WriteLine($"Mijn club: {ManagerName} · {MyClub} · reputatie={MyClubRep}");
            w.WriteLine($"Clubs gedetecteerd: {ClubCount} · spelers via selectie gekoppeld: {LinkedViaSquad}");
            // NB: de "memory"-datum komt uit het team-wedstrijdschema (eerstvolgende speeldag),
            // dus hij kan enkele dagen vóórlopen op de echte in-game kalenderdag.
            w.WriteLine($"In-game datum: {(GameDate is DateTime g2 ? g2.ToString("yyyy-MM-dd") + " (team-schema, ≈ speeldag)" : "derived")} · game-versie: {GameVersion ?? "?"}");
            w.WriteLine();

            w.WriteLine("Sample spelers (eerste 12):");
            foreach (var p in players.Values.Take(12))
                w.WriteLine($"  {p.Name} lft={p.Age} CA={p.Ca} PA={p.Pa} pos={string.Join("/", p.PosArr)} club={p.Club} div={p.Div} val={p.Value} exp={p.Expires}");
            w.WriteLine();
            w.WriteLine("Sample staf (eerste 8):");
            foreach (var p in staff.Values.Take(8))
                w.WriteLine($"  {p.Name} lft={p.Age} CA={p.Ca} PA={p.Pa} rol={p.Job} club={p.Club}");
            w.WriteLine();

            // Contract-statusflags: bit-histogram + huurlijst-sample, om de loan-listed-pin
            // (bit 1) te verifiëren tegen wat FM zelf toont bij deze spelers.
            w.WriteLine("=== Contract-statusflags (bit-histogram over spelers) ===");
            var bitHist = new int[8];
            foreach (var p in players.Values)
                for (int b = 0; b < 8; b++)
                    if ((p.StatusFlags & (1 << b)) != 0) bitHist[b]++;
            w.WriteLine("  bit0=Listed bit1=LoanListed? bit3=ByRequest bit4=NotForSale bit5=Release");
            for (int b = 0; b < 8; b++)
                if (bitHist[b] > 0) w.WriteLine($"  bit{b}: {bitHist[b]:N0}");
            w.WriteLine("Sample te huur (bit 1, eerste 8) — check deze in FM (Transferstatus: te huur?):");
            foreach (var p in players.Values.Where(x => x.LoanListed).Take(8))
                w.WriteLine($"  {p.Name} lft={p.Age} club={p.Club}");
            w.WriteLine();

            // Huur-overzicht: moederclub (volledig contract) ≠ huidige squad-club.
            var loans = players.Values.Where(x => x.OwnerClub != null && x.OwnerClub != x.Club).ToList();
            w.WriteLine($"=== Huur-overzicht: {loans.Count:N0} huurrelaties (moederclub ≠ huidige club) ===");
            w.WriteLine($"  spelers in meer dan één clubselectie: {MultiClub.Count:N0}");
            var ttHist = loans.GroupBy(x => x.TeamType).OrderBy(g => g.Key);
            w.WriteLine("  per teamtype: " + string.Join(" · ", ttHist.Select(g => $"tt{g.Key}={g.Count():N0}")));
            if (MyClub != null)
            {
                int inMine = loans.Count(x => x.Club == MyClub), outMine = loans.Count(x => x.OwnerClub == MyClub);
                w.WriteLine($"  bij mijn club: {inMine} gehuurd · {outMine} verhuurd");
            }
            w.WriteLine("  Eerste 40:");
            foreach (var p in loans.Take(40))
                w.WriteLine($"    {p.Name,-24} speelt: {p.Club ?? "-"}  ·  moederclub: {p.OwnerClub}");
            w.WriteLine();
            // Dubbele selectie-hits: hier koos PickSquad de niet-moederclub. Valse positieven
            // zouden hier zichtbaar worden (bv. een B-elftal dat als aparte club telt).
            w.WriteLine("=== Speler in twee clubselecties (steekproef, check in FM) ===");
            foreach (var s in MultiClubSample) w.WriteLine("  " + s);
        }
        catch (Exception e) { Plugin.Log.LogWarning("Diag schrijven mislukt: " + e.Message); }
    }
}

internal sealed class Person
{
    public uint Uid;
    public string Name;
    public int Age;
    public int BirthYear;
    public int BirthDoy;
    public List<string> Nat = [];
    public uint NatId;          // DB-UID van het (eerste) land — taalonafhankelijk (issue #15)
    public int JobId;           // functie-byte uit het contract — taalonafhankelijk
    public string Club;
    public string OwnerClub;    // moederclub (volledig contract); ≠ Club bij huur
    public string Div;
    public int Gender;          // 0 = man, 1 = vrouw
    public int TeamType = -1;   // 0 = 1e elftal, ~3 = reserves, ≥10 = jeugd; -1 = onbekend
    public bool IsPlayer;
    public ushort Ca;
    public ushort Pa;
    public int Height;
    public string Foot;
    public List<string> PosArr = [];
    public long Value;
    public long GuideValue;
    public long Wage;
    public string Expires;
    public bool Listed;
    public bool LoanListed;
    public byte StatusFlags;
    public bool NotForSale;
    public bool SetForRelease;
    public int CurRep;
    public int WorldRep;
    public int ClubRep;
    public int Ambition;
    public int Loyalty;
    public int Professionalism;
    public int Adaptability;
    public int Pressure;
    public int Sportsmanship;
    public int Temperament;
    public int Controversy;
    public ulong PersonAddr;
    public ulong PlAddr;      // player-data object (basePtr), voor de waarde-offset-diagnose
    public string Job;
    public Dictionary<string, int> Attrs = [];
    public Dictionary<string, int> StaffAttrs = [];
}
