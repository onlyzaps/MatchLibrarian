using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MatchLibrarian;

public class MatchLibrarian : BasePlugin
{
    public override string ModuleName => "MatchLibrarian";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "VinSix";

    private MatchData _currentMatch = new();

    private readonly Dictionary<ulong, PlayerRuntimeStats> _liveStats = new();
    private readonly HttpListener _httpListener = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private string _basePath = "";
    private bool _isServerRunning = false;
    private const int ApiPort = 5000;

    public override void Load(bool hotReload)
    {
        _basePath = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "configs", "plugins", "MatchLibrarian", "matches");
        Directory.CreateDirectory(_basePath);

        SetupNewMatch();
        StartHttpServer();

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
    }

    public override void Unload(bool hotReload)
    {
        _isServerRunning = false;
        try { _httpListener.Stop(); _httpListener.Close(); } catch { }
    }

    // Logic

    private void SetupNewMatch()
    {
        _liveStats.Clear();
        _currentMatch = new MatchData
        {
            MatchID = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss"), // Changed to UtcNow
            MapName = Server.MapName,
            StartTime = DateTime.UtcNow, // Changed to UtcNow
            MatchComplete = false
        };
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart e, GameEventInfo i)
    {
        _currentMatch.LastUpdated = DateTime.UtcNow; // Changed to UtcNow
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath e, GameEventInfo i)
    {
        var victim = e.Userid;
        var attacker = e.Attacker;
        var assister = e.Assister;

        if (victim == null) return HookResult.Continue;

        // Update Live Stats
        var vStats = GetLiveStats(victim);
        vStats.Deaths++;

        // Generate Death Entry
        _currentMatch.KillFeed.Add(CreateKillFeedEntry(
            _currentMatch.TotalRounds + 1,
            "Death",
            victim,
            attacker,
            e.Weapon,
            e.DmgHealth,
            e.Headshot
        ));

        if (attacker != null && attacker.IsValid && attacker != victim)
        {
            var aStats = GetLiveStats(attacker);
            aStats.Kills++;
            if (e.Headshot) aStats.Headshots++;

            // Generate Kill Entry
            _currentMatch.KillFeed.Add(CreateKillFeedEntry(
                _currentMatch.TotalRounds + 1,
                "Kill",
                attacker,
                victim,
                e.Weapon,
                e.DmgHealth,
                e.Headshot
            ));
        }

        if (assister != null && assister.IsValid)
        {
            var asStats = GetLiveStats(assister);
            asStats.Assists++;
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted e, GameEventInfo i)
    {
        if (e.Userid == null) return HookResult.Continue;
        _currentMatch.EventFeed.Add(new EventFeedItem
        {
            Round = _currentMatch.TotalRounds + 1,
            PlayerName = e.Userid.PlayerName,
            PlayerSteamID = e.Userid.SteamID,
            Event = "Bomb Planted",
            Timestamp = DateTime.UtcNow.ToString("HH:mm:ss") // Changed to UtcNow
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombDefused(EventBombDefused e, GameEventInfo i)
    {
        if (e.Userid == null) return HookResult.Continue;
        _currentMatch.EventFeed.Add(new EventFeedItem
        {
            Round = _currentMatch.TotalRounds + 1,
            PlayerName = e.Userid.PlayerName,
            PlayerSteamID = e.Userid.SteamID,
            Event = "Bomb Defused",
            Timestamp = DateTime.UtcNow.ToString("HH:mm:ss") // Changed to UtcNow
        });
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd e, GameEventInfo i)
    {
        _currentMatch.TotalRounds++;
        _currentMatch.LastUpdated = DateTime.UtcNow; // Changed to UtcNow

        int currentCT = _currentMatch.CTScoreHistory.LastOrDefault();
        int currentT = _currentMatch.TScoreHistory.LastOrDefault();

        // Comparison Logic: Cast Enum to int to match the event Winner type
        if (e.Winner == (int)CsTeam.CounterTerrorist)
        {
            currentCT++;
            _currentMatch.CTWins = currentCT;
        }
        else if (e.Winner == (int)CsTeam.Terrorist)
        {
            currentT++;
            _currentMatch.TWins = currentT;
        }

        _currentMatch.CTScoreHistory.Add(currentCT);
        _currentMatch.TScoreHistory.Add(currentT);

        // Snapshot Logic
        var connectedPlayers = Utilities.GetPlayers();

        foreach (var p in connectedPlayers)
        {
            if (!p.IsValid) continue;
            var stats = GetLiveStats(p);

            stats.IsAlive = p.PawnIsAlive;
            stats.Team = (int)p.TeamNum;
            stats.CurrentInventory = GetPlayerInventoryString(p);
            stats.Score = p.Score;
            stats.MVPs = p.MVPs;
        }

        foreach (var kvp in _liveStats)
        {
            ulong steamId = kvp.Key;
            PlayerRuntimeStats runtime = kvp.Value;

            var exportPlayer = _currentMatch.Players.FirstOrDefault(x => x.SteamID == steamId);
            if (exportPlayer == null)
            {
                exportPlayer = new PlayerObject
                {
                    SteamID = steamId,
                    Name = runtime.Name,
                    IsBot = runtime.IsBot
                };

                int roundsMissed = _currentMatch.TotalRounds - 1;
                for (int k = 0; k < roundsMissed; k++)
                {
                    exportPlayer.Team.Add(0);
                    exportPlayer.Kills.Add(0);
                    exportPlayer.Deaths.Add(0);
                    exportPlayer.Assists.Add(0);
                    exportPlayer.Score.Add(0);
                    exportPlayer.Alive.Add(false);
                    exportPlayer.Inventory.Add("");
                    exportPlayer.ZeusKills.Add(0);
                    exportPlayer.MVPs.Add(0);
                }

                _currentMatch.Players.Add(exportPlayer);
            }

            exportPlayer.Team.Add(runtime.Team);
            exportPlayer.Kills.Add(runtime.Kills);
            exportPlayer.Deaths.Add(runtime.Deaths);
            exportPlayer.Assists.Add(runtime.Assists);
            exportPlayer.ZeusKills.Add(runtime.ZeusKills);
            exportPlayer.MVPs.Add(runtime.MVPs);
            exportPlayer.Score.Add(runtime.Score);
            exportPlayer.Alive.Add(runtime.IsAlive);
            exportPlayer.Inventory.Add(runtime.CurrentInventory);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnMatchEnd(EventCsWinPanelMatch e, GameEventInfo i)
    {
        _currentMatch.MatchComplete = true;
        SaveMatch();
        return HookResult.Continue;
    }

    // Helper functions

    private void SaveMatch()
    {
        try
        {
            var n = _currentMatch.StartTime; // This is now UTC
            var d = Path.Combine(_basePath, n.Year.ToString(), n.Month.ToString("00"), n.Day.ToString("00"));
            Directory.CreateDirectory(d);
            string json = JsonSerializer.Serialize(_currentMatch, JsonOptions);
            File.WriteAllText(Path.Combine(d, $"{_currentMatch.MatchID}.json"), json);
        }
        catch (Exception ex) { Console.WriteLine($"[MatchLibrarian] Save Fail: {ex.Message}"); }
    }

    private PlayerRuntimeStats GetLiveStats(CCSPlayerController p)
    {
        if (!_liveStats.TryGetValue(p.SteamID, out var stats))
        {
            stats = new PlayerRuntimeStats
            {
                Name = p.PlayerName,
                IsBot = p.IsBot,
                Team = (int)p.TeamNum
            };
            _liveStats[p.SteamID] = stats;
        }
        stats.Name = p.PlayerName;
        return stats;
    }

    private static KillFeedItem CreateKillFeedEntry(int round, string type, CCSPlayerController mainPlayer, CCSPlayerController? opponent, string weapon, int dmg, bool hs)
    {
        // Cast TeamNum (byte) to CsTeam for comparison
        string teamStr = (CsTeam)mainPlayer.TeamNum == CsTeam.Terrorist ? "T" : "CT";

        return new KillFeedItem
        {
            Round = round,
            Type = type,
            PlayerTeam = teamStr,
            PlayerName = mainPlayer.PlayerName,
            PlayerSteamID = mainPlayer.SteamID,
            OpponentName = opponent != null && opponent.IsValid ? opponent.PlayerName : "World/Self",
            OpponentSteamID = opponent != null && opponent.IsValid ? opponent.SteamID : 0,
            Weapon = weapon,
            Damage = dmg,
            IsHeadshot = hs,
            Timestamp = DateTime.UtcNow.ToString("HH:mm:ss") // Changed to UtcNow
        };
    }

    private static string GetPlayerInventoryString(CCSPlayerController p)
    {
        if (p.PlayerPawn == null || !p.PlayerPawn.IsValid || p.PlayerPawn.Value == null) return "";
        var weaponServices = p.PlayerPawn.Value.WeaponServices;
        if (weaponServices == null || weaponServices.MyWeapons == null) return "";

        List<string> weapons = [];
        foreach (var weaponHandle in weaponServices.MyWeapons)
        {
            if (weaponHandle != null && weaponHandle.IsValid && weaponHandle.Value != null)
            {
                string name = weaponHandle.Value.DesignerName.Replace("weapon_", "");
                if (!string.IsNullOrEmpty(name)) weapons.Add(name);
            }
        }
        return string.Join(", ", weapons);
    }

    // HTTP server
    private void StartHttpServer()
    {
        Task.Run(() =>
        {
            try
            {
                _httpListener.Prefixes.Add($"http://*:{ApiPort}/");
                _httpListener.Start();
                _isServerRunning = true;
                while (_isServerRunning) { try { ProcessRequest(_httpListener.GetContext()); } catch { break; } }
            }
            catch { }
        });
    }

    private void ProcessRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // CORS & Headers
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Content-Type", "application/json");

        string path = request.Url?.AbsolutePath.ToLower() ?? "";
        string jsonResponse = "[]";
        int statusCode = 200;

        try
        {
            // ---------------------------------------------------------
            // ENDPOINT: /api/dates
            // Scans matches/YYYY/MM/DD and returns ["2025/12/15", "2025/12/16"]
            // ---------------------------------------------------------
            if (path == "/api/dates")
            {
                var dates = new List<string>();

                if (Directory.Exists(_basePath))
                {
                    // Scan Years
                    foreach (var yearPath in Directory.GetDirectories(_basePath))
                    {
                        var year = Path.GetFileName(yearPath);

                        // Scan Months
                        foreach (var monthPath in Directory.GetDirectories(yearPath))
                        {
                            var month = Path.GetFileName(monthPath);

                            // Scan Days
                            foreach (var dayPath in Directory.GetDirectories(monthPath))
                            {
                                var day = Path.GetFileName(dayPath);
                                // Force forward slashes for Web/PHP compatibility
                                dates.Add($"{year}/{month}/{day}");
                            }
                        }
                    }
                }
                jsonResponse = JsonSerializer.Serialize(dates);
            }
            // ---------------------------------------------------------
            // ENDPOINT: /api/matches?date=2025/12/15
            // Returns ["match1.json", "match2.json"] from that specific folder
            // ---------------------------------------------------------
            else if (path == "/api/matches")
            {
                var dateParam = request.QueryString["date"]; // e.g., "2025/12/15"
                var fileList = new List<string>();

                if (!string.IsNullOrEmpty(dateParam))
                {
                    // Securely combine base path with the requested date folder
                    // We shouldn't allow ".." to escape the directory
                    if (!dateParam.Contains(".."))
                    {
                        var targetPath = Path.Combine(_basePath, dateParam);

                        if (Directory.Exists(targetPath))
                        {
                            foreach (var filePath in Directory.GetFiles(targetPath, "*.json"))
                            {
                                fileList.Add(Path.GetFileName(filePath));
                            }
                        }
                    }
                }
                jsonResponse = JsonSerializer.Serialize(fileList);
            }
            // ---------------------------------------------------------
            // ENDPOINT: /api/data?file=2025/12/15/match_xyz.json
            // Returns the raw JSON content of the specific file
            // ---------------------------------------------------------
            else if (path == "/api/data")
            {
                var fileParam = request.QueryString["file"]; // e.g., "2025/12/15/match.json"

                if (!string.IsNullOrEmpty(fileParam) && !fileParam.Contains(".."))
                {
                    var fullPath = Path.Combine(_basePath, fileParam);

                    if (File.Exists(fullPath))
                    {
                        jsonResponse = File.ReadAllText(fullPath);
                    }
                    else
                    {
                        statusCode = 404;
                        jsonResponse = JsonSerializer.Serialize(new { error = "File not found" });
                    }
                }
                else
                {
                    statusCode = 400;
                    jsonResponse = JsonSerializer.Serialize(new { error = "Invalid file path" });
                }
            }
            else
            {
                statusCode = 404; // Endpoint not found
            }
        }
        catch (Exception ex)
        {
            statusCode = 500;
            jsonResponse = JsonSerializer.Serialize(new { error = ex.Message });
            Console.WriteLine($"[MatchLibrarian] API Error: {ex.Message}");
        }

        // Write Response
        byte[] buffer = Encoding.UTF8.GetBytes(jsonResponse);
        response.StatusCode = statusCode;
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }
}

// DATA CLASSES

public class PlayerRuntimeStats
{
    public string Name { get; set; } = "";
    public bool IsBot { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int ZeusKills { get; set; }
    public int MVPs { get; set; }
    public int Score { get; set; }
    public int Headshots { get; set; }
    public bool IsAlive { get; set; }
    public string CurrentInventory { get; set; } = "";
    public int Team { get; set; }
}

public class MatchData
{
    public string MatchID { get; set; } = "";
    public string MapName { get; set; } = "";
    public string WorkshopID { get; set; } = "0";
    public string CollectionID { get; set; } = "0";
    public DateTime StartTime { get; set; }
    public DateTime LastUpdated { get; set; }
    public bool MatchComplete { get; set; }
    public int CTWins { get; set; }
    public int TWins { get; set; }
    public int TotalRounds { get; set; }
    public List<int> CTScoreHistory { get; set; } = [];
    public List<int> TScoreHistory { get; set; } = [];
    public List<PlayerObject> Players { get; set; } = [];
    public List<KillFeedItem> KillFeed { get; set; } = [];
    public List<EventFeedItem> EventFeed { get; set; } = [];
    public List<object> ChatFeed { get; set; } = [];
}

public class PlayerObject
{
    public ulong SteamID { get; set; }
    public string Name { get; set; } = "";
    public bool IsBot { get; set; }

    public List<int> Team { get; set; } = [];
    public List<int> Kills { get; set; } = [];
    public List<int> Deaths { get; set; } = [];
    public List<int> Assists { get; set; } = [];
    public List<int> ZeusKills { get; set; } = [];
    public List<int> MVPs { get; set; } = [];
    public List<int> Score { get; set; } = [];
    public List<bool> Alive { get; set; } = [];
    public List<string> Inventory { get; set; } = [];
}

public class KillFeedItem
{
    public int Round { get; set; }
    public string Type { get; set; } = "";
    public string PlayerTeam { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public ulong PlayerSteamID { get; set; }
    public string OpponentName { get; set; } = "";
    public ulong OpponentSteamID { get; set; }
    public string Weapon { get; set; } = "";
    public int Damage { get; set; }
    public bool IsHeadshot { get; set; }
    public string Timestamp { get; set; } = "";
}

public class EventFeedItem
{
    public int Round { get; set; }
    public string PlayerName { get; set; } = "";
    public ulong PlayerSteamID { get; set; }
    public string Event { get; set; } = "";
    public string Timestamp { get; set; } = "";
}