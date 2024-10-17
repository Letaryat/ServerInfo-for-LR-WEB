using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using MySqlConnector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace ServerInfo
{

    public class ServerInfoConfig : BasePluginConfig
    {
        [JsonPropertyName("server_info")] public string server_info { get; set; } = "ip:port";
        [JsonPropertyName("password")] public string password { get; set; } = "password";
        [JsonPropertyName("url")] public string url { get; set; } = "https://website.eu";
        [JsonPropertyName("debug_mode")] public bool debug_mode { get; set; } = false;
        [JsonPropertyName("statistic_type")] public int statistic_type { get; set; } = 0;
    }

    public partial class ServerInfo : BasePlugin, IPluginConfig<ServerInfoConfig>
    {
        public override string ModuleName => "ServerInfo for LR WEB";
        public override string ModuleAuthor => "E!N // Little edit: Letaryat";
        public override string ModuleVersion => "1.6";
        public override string ModuleDescription => "Server side plugin for Module Monitoring Rich (Invite does not work)";

        public ServerInfoConfig Config { get; set; }

        private bool isDebugMode = false;

        private const int MaxPlayers = 65;
        public string Server { get; private set; } = "";
        public string Password { get; private set; } = "";
        public string Url { get; private set; } = "";
        private int statisticType = 0;

        private const long SteamId64Base = 76561197960265728;
        private const string LogPrefix = "ServerInfo |";
        private const string TeamCT = "CT";
        private const string TeamTerrorist = "TERRORIST";
        private readonly HttpClient httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        private readonly Dictionary<string, int> rankCache = new();
        public Dictionary<int, PlayerInfo> PlayerList { get; private set; } = new Dictionary<int, PlayerInfo>();

    public void OnConfigParsed(ServerInfoConfig config)
    {
        Server = config.server_info;
        Password = config.password;
        Url = config.url;
        statisticType = config.statistic_type;
        isDebugMode = config.debug_mode;
        // Once we've validated the config, we can set it to the instance
        //Config = config;
    }

        public override void Load(bool hotReload)
        {
            //LoadCfg();
            Console.WriteLine("Sprawdzanie configu: " + Server + " " + Password + " " + Url + " " + statisticType + isDebugMode);
            GetIP();
            AddServerInfoCommands();

            RegisterClientAuthListener();
        }

        private void AddServerInfoCommands()
        {
            AddCommand("css_getserverinfo", "Get server info",
                (player, info) => Task.Run(async () => await UpdatePlayerInfoAsync()));

            //AddCommand("css_reloadserverinfo", "Forced to read the cfg",
            //    (player, info) => OnConfigParsed());
        }

        private void RegisterClientAuthListener()
        {
            LogDebug("Registering client authorization listener...");
            RegisterListener<Listeners.OnClientAuthorized>((slot, steamid) =>
            {
                LogDebug($"Client authorized event triggered. Slot: {slot}, SteamID: {steamid}");
                HandleClientAuthorization(slot, steamid);
            });
            LogDebug("Client authorization listener registered successfully.");
        }

        private void HandleClientAuthorization(int slot, SteamID steamid)
        {
            LogDebug($"Handling client authorization for slot {slot}, SteamID: {steamid}");
            CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);

            if (!IsPlayerValid(player))
            {
                LogDebug($"Invalid player in slot {slot}. Authorization skipped.");
                return;
            }

            LogDebug($"Valid player found in slot {slot}. Proceeding with session initialization.");
            Task.Run(() =>
            {
                CounterStrikeSharp.API.Server.NextFrame(() => InitPlayerSession(player!, steamid));
            });
        }

        private void InitPlayerSession(CCSPlayerController player, SteamID steamid)
        {
            if (!PlayerList.TryGetValue(player.Slot, out PlayerInfo? playerInfo))
            {
                var steamId2 = player.AuthorizedSteamID != null
                    ? ConvertToSteam2((long)player.AuthorizedSteamID.SteamId64)
                    : null;

                playerInfo = new PlayerInfo
                {
                    UserId = player.UserId,
                    SteamId = player.AuthorizedSteamID?.SteamId64.ToString(),
                    SteamId2 = steamId2,
                    Name = player.PlayerName,
                    SessionStartTime = DateTime.Now,
                };

                PlayerList[player.Slot] = playerInfo;
                LogDebug($"Initialized new PlayerInfo for {player.PlayerName} with SessionStartTime: {playerInfo.SessionStartTime}");
            }
            else
            {
                UpdatePlayerStats(player, playerInfo);
                LogDebug($"Updated PlayerInfo for {player.PlayerName}");
            }
        }

        private string GetIP()
        {
            LogDebug("Fetching server IP and port...");
            try
            {
                var serverIp = GetServerIP();
                var serverPort = GetServerPort();
                string ipPort = $"{serverIp}:{serverPort}";
                LogDebug($"Server IP and port: {ipPort}");
                return ipPort;
            }
            catch (Exception ex)
            {
                LogDebug($"Error fetching server IP and port: {ex.Message}");
                return "Unknown IP:Unknown Port";
            }
        }

        private string GetServerIP()
        {
            return ConVar.Find("ip")?.StringValue ?? "Unknown IP";
        }

        private string GetServerPort()
        {
            return ConVar.Find("hostport")?.GetPrimitiveValue<int>().ToString() ?? "Unknown Port";
        }

        public async Task<int> GetRankFromDatabaseAsync(string? steamid)
        {
            LogDebug("Fetching rank from database for Steam ID: " + steamid);

            if (steamid != null && rankCache.TryGetValue(steamid, out int cachedRank))
            {
                LogDebug($"Rank for {steamid} fetched from cache: {cachedRank}");
                return cachedRank;
            }

            var dbConfig = LoadDbConfig();
            if (dbConfig == null)
            {
                LogDebug("Database configuration not found.");
                return 0;
            }

            try
            {
                int rank = await ExecuteRankQueryAsync(steamid, dbConfig);
                if (steamid != null)
                {
                    rankCache[steamid] = rank;
                }
                return rank;
            }
            catch (Exception ex)
            {
                LogDatabaseError(ex);
                return 0;
            }
        }

        private async Task<int> ExecuteRankQueryAsync(string? steamid, DbConfig dbConfig)
        {
            var connectionString = BuildConnectionString(dbConfig);
            using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();

            var query = $"SELECT rank FROM {dbConfig.Name} WHERE steam = @SteamId";
            using var command = new MySqlCommand(query, connection);
            command.Parameters.AddWithValue("@SteamId", steamid);
            var result = await command.ExecuteScalarAsync();

            if (result != null)
            {
                LogDebug("Rank fetched successfully: " + result);
                return Convert.ToInt32(result);
            }

            return 0;
        }

        private string BuildConnectionString(DbConfig dbConfig)
        {
            if (!uint.TryParse(dbConfig.DbPort, out uint port))
            {
                LogDebug($"Invalid or missing port number in configuration: {dbConfig.DbPort}. Using default port 3306.");
                port = 3306;
            }

            return new MySqlConnectionStringBuilder
            {
                Server = dbConfig.DbHost,
                UserID = dbConfig.DbUser,
                Password = dbConfig.DbPassword,
                Database = dbConfig.DbName,
                Port = port
            }.ToString();
        }

        private void LogDatabaseError(Exception ex)
        {
            Console.WriteLine("Error when connecting to the database: " + ex.Message);
            LogDebug("Database connection error: " + ex.Message);
        }

        private DbConfig? LoadDbConfig()
        {
            switch (statisticType)
            {
                case 1:
                    return LoadDbConfigForType1(GetConfigFilePathForType(1));
                case 2:
                    return LoadDbConfigForType2(GetConfigFilePathForType(2));
                case 3:
                    return LoadDbConfigForType3(GetConfigFilePathForType(3));
                default:
                    LogDebug("Unknown statistic type: " + statisticType);
                    return null;
            }
        }

        private DbConfig? LoadDbConfigForType1(string configFilePath)
        {
            if (string.IsNullOrEmpty(configFilePath))
            {
                LogDebug("Database not configured or disabled.");
                return null;
            }

            if (!File.Exists(configFilePath))
            {
                LogDebug("Dbconfig not found at: " + configFilePath);
                return null;
            }

            try
            {
                LogDebug("Loading dbconfig from: " + configFilePath);
                var configJson = File.ReadAllText(configFilePath);
                return JsonConvert.DeserializeObject<DbConfig>(configJson);
            }
            catch (Exception ex)
            {
                LogDebug($"Error loading dbconfig from {configFilePath}: {ex.Message}");
                return null;
            }
        }

        private DbConfig? LoadDbConfigForType2(string configFilePath)
        {
            if (!File.Exists(configFilePath))
            {
                LogDebug("Dbconfig for type 2 not found at: " + configFilePath);
                return null;
            }

            try
            {
                LogDebug("Loading dbconfig for type 2 from: " + configFilePath);
                var configJson = File.ReadAllText(configFilePath);
                var configObject = JsonConvert.DeserializeObject<JObject>(configJson);

                if (configObject == null)
                {
                    LogDebug("Failed to deserialize JSON for dbconfig type 2.");
                    return null;
                }

                var connectionConfig = configObject["Connection"];
                var tableName = configObject["TableName"]?.ToString();

                if (connectionConfig == null || tableName == null)
                {
                    LogDebug("Connection or TableName is missing in dbconfig for type 2.");
                    return null;
                }

                return new DbConfig
                {
                    DbHost = connectionConfig["Host"]?.ToString(),
                    DbUser = connectionConfig["User"]?.ToString(),
                    DbPassword = connectionConfig["Password"]?.ToString(),
                    DbName = connectionConfig["Database"]?.ToString(),
                    DbPort = connectionConfig["Port"]?.ToString(),
                    Name = tableName
                };
            }
            catch (Exception ex)
            {
                LogDebug($"Error loading dbconfig for type 2 from {configFilePath}: {ex.Message}");
                return null;
            }
        }

        private DbConfig? LoadDbConfigForType3(string configFilePath)
        {
            if (!File.Exists(configFilePath))
            {
                LogDebug("databases.cfg not found at: " + configFilePath);
                return null;
            }

            try
            {
                var lines = File.ReadAllLines(configFilePath);
                var isLevelsRanksSection = false;
                var dbConfig = new DbConfig();

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("\"levels_ranks\""))
                    {
                        isLevelsRanksSection = true;
                        continue;
                    }

                    if (isLevelsRanksSection)
                    {
                        if (trimmedLine.StartsWith("{") || trimmedLine.StartsWith("}"))
                        {
                            continue;
                        }

                        var keyValue = trimmedLine.Split(new[] { '\"', '\"' }, StringSplitOptions.RemoveEmptyEntries);
                        if (keyValue.Length >= 2)
                        {
                            switch (keyValue[0].Trim())
                            {
                                case "host":
                                    dbConfig.DbHost = keyValue[1];
                                    break;
                                case "user":
                                    dbConfig.DbUser = keyValue[1];
                                    break;
                                case "pass":
                                    dbConfig.DbPassword = keyValue[1];
                                    break;
                                case "database":
                                    dbConfig.DbName = keyValue[1];
                                    break;
                                case "port":
                                    dbConfig.DbPort = keyValue[1];
                                    break;
                            }
                        }
                    }
                }

                return dbConfig;
            }
            catch (Exception ex)
            {
                LogDebug($"Error reading databases.cfg from {configFilePath}: {ex.Message}");
                return null;
            }
        }

        private string GetConfigFilePathForType(int type)
        {
            var parentDirectory = Directory.GetParent(ModuleDirectory)?.Parent?.FullName ?? "";
            var csgoDirectory = Directory.GetParent(parentDirectory)?.Parent?.Parent?.FullName ?? "";

            return type switch
            {
                1 => Path.Combine(parentDirectory, "plugins/RanksPoints/dbconfig.json"),
                2 => Path.Combine(parentDirectory, "configs/plugins/RanksCore/ranks.json"),
                3 => Path.Combine(csgoDirectory, "addons/configs/databases.cfg"),
                _ => "",
            };
        }

        [GameEventHandler]
        public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            Task.Run(() => CounterStrikeSharp.API.Server.NextFrame(async () =>
            {
                await UpdatePlayerInfoAsync();
            }));

            return HookResult.Continue;
        }

        [GameEventHandler]
        public HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
        {
            var player = @event.Userid;

            if (!IsPlayerValid(player)) return HookResult.Continue;

            if (player!.AuthorizedSteamID != null)
            {
                InitPlayerSession(player, player.AuthorizedSteamID);
            }
            else
            {
                LogDebug("Player's AuthorizedSteamID is null. Player session initialization skipped.");
            }

            return HookResult.Continue;
        }

        [GameEventHandler]
        public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            var player = @event.Userid;
            if (!IsPlayerValid(player)) return HookResult.Continue;

            PlayerList.Remove(player!.Slot);
            LogDebug($"Player disconnected: {player.UserId}");
            return HookResult.Continue;
        }

        private string ConvertToSteam2(long steamId64)
        {
            long steamId3 = steamId64 - SteamId64Base;
            int y = (int)(steamId3 % 2);
            long z = steamId3 / 2;

            return $"STEAM_1:{y}:{z}";
        }

        private int CalculatePlayTime(PlayerInfo playerInfo)
        {
            if (playerInfo == null)
            {
                LogDebug("Player info is null.");
                return 0;
            }

            LogDebug($"Calculating play time for player: {playerInfo.Name}");
            var currentTime = DateTime.Now;
            LogDebug($"Current time: {currentTime}, SessionStartTime: {playerInfo.SessionStartTime}");

            if (currentTime < playerInfo.SessionStartTime)
            {
                LogDebug($"Current time is earlier than the session start time for {playerInfo.Name}.");
                return 0;
            }

            TimeSpan timeSpent = currentTime - playerInfo.SessionStartTime;
            if (timeSpent.TotalSeconds > int.MaxValue)
            {
                LogDebug($"Play time overflow for {playerInfo.Name}.");
                return int.MaxValue;
            }

            int playTimeInSeconds = (int)timeSpent.TotalSeconds;
            LogDebug($"Play time for {playerInfo.Name}: {playTimeInSeconds} seconds");
            return playTimeInSeconds;
        }

        private void LogDebug(string message)
        {
            if (isDebugMode)
            {
                Console.WriteLine($"{DateTime.Now} | {LogPrefix} {message}");
            }
        }

        private async Task UpdatePlayerInfoAsync()
        {
            LogDebug("Updating player info for all players...");

            if (!PlayerList.Any())
            {
                LogDebug("No players on the server.");
                await Task.Run(() => CounterStrikeSharp.API.Server.NextFrame(() => SendServerInfoAsync().Wait()));
                return;
            }

            var allPlayersJson = new List<object>();

            foreach (var playerSlot in PlayerList.Keys.ToList())
            {
                CCSPlayerController? player = null;
                var resetEvent = new ManualResetEvent(false);

                await Task.Run(() => CounterStrikeSharp.API.Server.NextFrame(() =>
                {
                    player = Utilities.GetPlayerFromSlot(playerSlot);
                    resetEvent.Set();
                }));

                resetEvent.WaitOne();

                if (player == null || !IsPlayerValid(player)) continue;

                var playerInfo = PlayerList[playerSlot];
                await Task.Run(() => CounterStrikeSharp.API.Server.NextFrame(() => UpdatePlayerStats(player, playerInfo)));
                LogPlayerInfo(playerInfo);

                var playerJson = GetPlayersJson(playerInfo);
                allPlayersJson.AddRange(playerJson);
            }

            await SendServerInfoAsync(allPlayersJson);
            LogDebug("All player info updated.");
        }

        private void UpdatePlayerStats(CCSPlayerController player, PlayerInfo playerInfo)
        {
            playerInfo.Name = player.PlayerName;
            playerInfo.Kills = player.ActionTrackingServices?.MatchStats.Kills;
            playerInfo.Deaths = player.ActionTrackingServices?.MatchStats.Deaths;
            playerInfo.Assists = player.ActionTrackingServices?.MatchStats.Assists;
            playerInfo.Headshots = player.ActionTrackingServices?.MatchStats.HeadShotKills;
        }

        private void LogPlayerInfo(PlayerInfo playerInfo)
        {
            LogDebug($"Player info - Name: {playerInfo.Name}, SteamID: {playerInfo.SteamId}, Kills: {playerInfo.Kills}, Deaths: {playerInfo.Deaths}, Assists: {playerInfo.Assists}, Headshots: {playerInfo.Headshots}");
        }

        private static (int ctScore, int terroristScore) GetTeamsScore()
        {
            var teamEntities = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
            var ctScore = teamEntities.FirstOrDefault(t => t.Teamname == TeamCT)?.Score ?? 0;
            var terroristScore = teamEntities.FirstOrDefault(t => t.Teamname == TeamTerrorist)?.Score ?? 0;

            return (ctScore, terroristScore);
        }

        private async Task SendServerInfoAsync(List<object>? playersJson = null)
        {
            LogDebug("Preparing to send server info...");
            if (string.IsNullOrEmpty(Server) || string.IsNullOrEmpty(Url) || string.IsNullOrEmpty(Password))
            {
                return;
            }

            int scoreCt = 0;
            int scoreT = 0;

            if (!PlayerList.Any())
            {
                LogDebug("No players on the server. Skipping team score retrieval and data send.");
                (scoreCt, scoreT) = GetTeamsScore();
                var jsonDataEmpty = new { score_ct = scoreCt, score_t = scoreT, players = playersJson ?? new List<object>() };
                var jsonEmptyString = System.Text.Json.JsonSerializer.Serialize(jsonDataEmpty);
                await PostData(jsonEmptyString);
                LogDebug("Data send when server is empty: " + jsonEmptyString);
                return;
            }

            var resetEvent = new ManualResetEvent(false);

            await Task.Run(() => CounterStrikeSharp.API.Server.NextFrame(() =>
            {
                (scoreCt, scoreT) = GetTeamsScore();
                resetEvent.Set();
            }));

            resetEvent.WaitOne();

            var jsonData = new { score_ct = scoreCt, score_t = scoreT, players = playersJson ?? new List<object>() };
            var jsonString = System.Text.Json.JsonSerializer.Serialize(jsonData);

            await PostData(jsonString);
            LogDebug("Server info sent.");
        }

        private List<object> GetPlayersJson(PlayerInfo playerinfo)
        {
            var playTime = CalculatePlayTime(playerinfo);
            int rank = GetRankFromDatabaseAsync(playerinfo.SteamId2).Result;
            var playerJson = new
            {
                name = playerinfo.Name ?? "Unknown",
                steamid = playerinfo?.SteamId,
                kills = playerinfo?.Kills,
                assists = playerinfo?.Assists,
                death = playerinfo?.Deaths,
                headshots = playerinfo?.Headshots,
                rank,
                playtime = playTime
            };

            return new List<object> { playerJson };
        }

        private async Task PostData(string jsonData)
        {
            LogDebug("Sending data to server...");
            var requestUri = $"{Url}/app/modules/module_block_main_monitoring_rating/forward/js_controller.php?server={Server}&password={Password}";

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(jsonData, Encoding.UTF8, "application/json")
            };

            try
            {
                var response = await httpClient.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

                LogDebug($"Response status: {response.StatusCode}. Data sent. Response: {responseContent}");

                if (!response.IsSuccessStatusCode)
                {
                    LogDebug("Error when sending a request: " + responseContent);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Exception in HTTP request: {ex.Message}");
            }
        }

        public static bool IsPlayerValid(CCSPlayerController? player)
        {
            return player is
            {
                Pawn.IsValid: true,
                IsBot: false,
                IsHLTV: false
            };
        }

        [GeneratedRegex("\"([^\"]+)\"\\s+\"([^\"]+)\"")]
        private static partial Regex ConfigLineRegex();
    }

    public class PlayerInfo
    {
        public int? UserId { get; set; }
        public string? SteamId { get; set; }
        public string? SteamId2 { get; set; }
        public string? Name { get; set; }
        public int? Kills { get; set; } = 0;
        public int? Deaths { get; set; } = 0;
        public int? Assists { get; set; } = 0;
        public int? Headshots { get; set; } = 0;
        public DateTime SessionStartTime { get; set; }
    }

    public class DbConfig
    {
        [Required]
        public string? DbHost { get; set; }
        [Required]
        public string? DbUser { get; set; }
        [Required]
        public string? DbPassword { get; set; }
        [Required]
        public string? DbName { get; set; }
        [Required]
        public string? DbPort { get; set; }
        [Required]
        public string? Name { get; set; }
    }

    public class AlternativeConfig
    {
        [Required]
        public string? TableName { get; set; }
        [Required]
        public ConnectionConfig? Connection { get; set; }
    }

    public class ConnectionConfig
    {
        [Required]
        public string? Host { get; set; }
        [Required]
        public string? Database { get; set; }
        [Required]
        public string? User { get; set; }
        [Required]
        public string? Password { get; set; }
        public int Port { get; set; }
    }
}
