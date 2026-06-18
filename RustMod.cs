using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Game.Rust.Cui;
using Rust;
using Steamworks;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RustMod", "RustMod", "1.33b")]
    [Description("RustMod Service")]
    public class RustMod : RustPlugin
    {
        private const string HeaderToken = "X-Rustmod-Token";
        private const string HeaderServerUid = "X-Rustmod-Server-Uid";
        private const string ReportLayer = "RustMod.ReportPanel";

        private Configuration _config;
        private readonly List<EventEnvelope> _eventQueue = new List<EventEnvelope>();
        private readonly List<PluginReportDto> _reportQueue = new List<PluginReportDto>();
        private readonly List<PluginDrawingDto> _drawingQueue = new List<PluginDrawingDto>();
        private readonly List<string> _drawingDestroyQueue = new List<string>();
        private readonly Dictionary<string, ActiveBanEntry> _activeBans = new Dictionary<string, ActiveBanEntry>();
        private readonly Dictionary<string, ActiveMuteEntry> _activeMutes = new Dictionary<string, ActiveMuteEntry>();
        private readonly HashSet<string> _activeCheckNotices = new HashSet<string>();
        private readonly Dictionary<string, TeamChangeHint> _pendingTeamChanges = new Dictionary<string, TeamChangeHint>();
        private readonly Dictionary<ulong, double> _reportCooldowns = new Dictionary<ulong, double>();
        private bool _isSending;
        private bool _isSendingReports;
        private bool _isSendingDrawings;
        private bool _isSendingDrawingDeletes;
        private bool _isPullingCommands;
        private bool _isPullingBans;
        private bool _isPullingChecks;
        private bool _isPullingMutes;

        #region Config

        private class Configuration
        {
            [JsonProperty("API base URL (without trailing slash), DO NOT CHANGE: https://rustmod.com/api/plugin")]
            public string api_base_url = "https://rustmod.com/api/plugin";

            [JsonProperty("Plugin token DO NOT CHANGE")]
            public string token = "change-me-very-long-token";

            [JsonProperty("Verification code (optional, for quick verify command without args)")]
            public string verification_code = "";

            [JsonProperty("Server UID (optional; leave empty for auto endpoint/host UID)")]
            public string server_uid = "";

            [JsonProperty("Flush interval in seconds")]
            public float flush_interval_seconds = 5f;

            [JsonProperty("Heartbeat interval in seconds")]
            public float heartbeat_interval_seconds = 30f;

            [JsonProperty("Max events per request")]
            public int max_events_per_request = 100;

            [JsonProperty("Max queue size (drop oldest if overflow)")]
            public int max_queue_size = 5000;

            [JsonProperty("Players snapshot interval in seconds")]
            public float players_state_interval_seconds = 15f;

            [JsonProperty("Commands poll interval in seconds")]
            public float commands_poll_interval_seconds = 2f;

            [JsonProperty("Bans snapshot interval in seconds")]
            public float bans_snapshot_interval_seconds = 10f;

            [JsonProperty("Ban enforcement interval in seconds")]
            public float ban_enforce_interval_seconds = 2f;

            [JsonProperty("Mutes snapshot interval in seconds")]
            public float mutes_snapshot_interval_seconds = 10f;

            [JsonProperty("Checks snapshot interval in seconds")]
            public float checks_snapshot_interval_seconds = 10f;

            [JsonProperty("Request timeout in seconds")]
            public float request_timeout_seconds = 10f;

            [JsonProperty("[Reports] Chat commands")]
            public List<string> report_ui_commands = new List<string> { "report", "reports" };

            [JsonProperty("[Reports] Reasons")]
            public List<string> report_ui_reasons = new List<string> { "Cheat", "Macros", "Abuse" };

            [JsonProperty("[Reports] Cooldown between reports (seconds)")]
            public int report_ui_cooldown = 240;

            [JsonProperty("[Reports] Auto-parse reports from F7")]
            public bool report_ui_auto_parse = true;

            [JsonProperty("Debug logs")]
            public bool debug = false;

            public static Configuration CreateDefault()
            {
                return new Configuration();
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = Configuration.CreateDefault();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<Configuration>();
            }
            catch
            {
                PrintWarning("Config is invalid. Creating a new one with defaults.");
                LoadDefaultConfig();
            }

            NormalizeConfig();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config);
        }

        private void NormalizeConfig()
        {
            var defaults = Configuration.CreateDefault();

            if (_config == null)
            {
                _config = defaults;
            }

            if (string.IsNullOrWhiteSpace(_config.api_base_url))
            {
                _config.api_base_url = defaults.api_base_url;
            }

            if (string.IsNullOrWhiteSpace(_config.verification_code) && !string.IsNullOrWhiteSpace(defaults.verification_code))
            {
                _config.verification_code = defaults.verification_code.Trim();
            }

            if (_config.flush_interval_seconds < 1f)
            {
                _config.flush_interval_seconds = 1f;
            }

            if (_config.heartbeat_interval_seconds < 5f)
            {
                _config.heartbeat_interval_seconds = 5f;
            }

            if (_config.max_events_per_request < 1)
            {
                _config.max_events_per_request = 1;
            }

            if (_config.max_queue_size < _config.max_events_per_request)
            {
                _config.max_queue_size = _config.max_events_per_request;
            }

            if (_config.request_timeout_seconds < 1f)
            {
                _config.request_timeout_seconds = 1f;
            }

            if (_config.report_ui_cooldown < 0)
            {
                _config.report_ui_cooldown = 0;
            }

            _config.report_ui_reasons = (_config.report_ui_reasons ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            if (_config.report_ui_reasons.Count == 0)
            {
                _config.report_ui_reasons = new List<string> { "Cheat", "Macros", "Abuse" };
            }

            if (_config.players_state_interval_seconds < 2f)
            {
                _config.players_state_interval_seconds = 2f;
            }

            if (_config.checks_snapshot_interval_seconds < 2f)
            {
                _config.checks_snapshot_interval_seconds = 2f;
            }

            if (_config.commands_poll_interval_seconds < 1f)
            {
                _config.commands_poll_interval_seconds = 1f;
            }

            if (_config.bans_snapshot_interval_seconds < 2f)
            {
                _config.bans_snapshot_interval_seconds = 2f;
            }

            if (_config.ban_enforce_interval_seconds < 1f)
            {
                _config.ban_enforce_interval_seconds = 1f;
            }

            if (_config.mutes_snapshot_interval_seconds < 2f)
            {
                _config.mutes_snapshot_interval_seconds = 2f;
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Reason.Unknown"] = "Unknown reason",
                ["Kick.Admin"] = "Kicked by administration",
                ["Kick.Admin.WithReason"] = "Kicked by administration. Reason: %REASON%",
                ["Ban.Kick.Temp"] = "You are banned until %TIME% (UTC+3). Reason: %REASON%",
                ["Ban.Kick.Perm"] = "You are banned permanently. Reason: %REASON%",
                ["Mute.Active.Temp"] = "Chat mute until %TIME% (UTC+3). Reason: %REASON%",
                ["Mute.Active.Perm"] = "Chat mute is active. Reason: %REASON%",
                ["Mute.Active.Toast"] = "Chat is blocked while mute is active.",
                ["Mute.Issued.Toast"] = "You received a mute.",
                ["Mute.Issued.Chat.Temp"] = "You are muted.<size=12>\n- reason: %REASON%\n- until: %TIME%</size>",
                ["Mute.Issued.Chat.Perm"] = "You are muted.<size=12>\n- reason: %REASON%\n- duration: permanently</size>",
                ["Mute.Removed.Toast"] = "Your mute has been removed.",
                ["Mute.Removed.Chat"] = "Your chat mute has been removed.",
                ["Report.Cooldown"] = "You can send the next report in %SECONDS% sec.",
                ["Report.Sent"] = "Report sent.",
                ["Report.Usage"] = "Usage: /report <player or steamid> <reason> [message]",
                ["Report.Reasons"] = "Available reasons: %REASONS%",
                ["Report.TargetNotFound"] = "Player not found.",
                ["Report.ReasonInvalid"] = "Unknown report reason.",
                ["Report.Panel.Title"] = "Report player",
                ["Report.Panel.Subtitle"] = "Select who you want to report",
                ["Report.Panel.Search"] = "Search",
                ["Report.Panel.NoPlayers"] = "No players found",
                ["Report.Panel.ReasonTitle"] = "Select report reason",
                ["Report.Panel.ReasonSubtitle"] = "Player: %PLAYER%",
                ["Report.Panel.RecentCheck"] = "recently checked",
                ["Report.Panel.OpenHint"] = "Use /report to open the report panel.",
                ["Check.Text"] = "<color=#c6bdb4><size=32><b>YOU ARE SUMMONED FOR A CHECK-UP</b></size></color>\n<color=#958D85>You have <color=#c6bdb4><b>3 minutes</b></color> to write your Discord in chat and accept the friend request.\n\nWhile the check is active, your chat messages are visible only to the moderator.</color>",
                ["Time.Permanent"] = "permanently",
            }, this, "en");
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Reason.Unknown"] = "Неизвестная причина",
                ["Kick.Admin"] = "Вы кикнуты администрацией",
                ["Kick.Admin.WithReason"] = "Вы кикнуты администрацией. Причина: %REASON%",
                ["Ban.Kick.Temp"] = "Вы забанены до %TIME% (МСК). Причина: %REASON%",
                ["Ban.Kick.Perm"] = "Вы забанены навсегда. Причина: %REASON%",
                ["Check.Text"] = "<color=#c6bdb4><size=32><b>ВЫ ВЫЗВАНЫ НА ПРОВЕРКУ</b></size></color>\n<color=#958D85>У вас есть <color=#c6bdb4><b>3 минуты</b></color>, чтобы написать Discord в чат и принять заявку в друзья.\n\nВо время проверки ваши сообщения из чата видит только проверяющий.</color>",
                ["Mute.Active.Temp"] = "Чат заблокирован до %TIME% (МСК). Причина: %REASON%",
                ["Mute.Active.Perm"] = "Чат заблокирован. Причина: %REASON%",
                ["Mute.Active.Toast"] = "У вас активная блокировка чата",
                ["Mute.Issued.Toast"] = "Вы были заблокированы в чате",
                ["Mute.Issued.Chat.Temp"] = "Вы были заблокированы в чате:<size=12>\n- Причина: %REASON%\n- До: %TIME%</size>",
                ["Mute.Issued.Chat.Perm"] = "Вы были заблокированы в чате: <size=12>\n- Причина: %REASON%\n- Срок: Навсегда</size>",
                ["Mute.Removed.Toast"] = "Блокировка чата снята.",
                ["Mute.Removed.Chat"] = "Блокировка чата снята.",
                ["Time.Permanent"] = "навсегда",
                ["Report.Cooldown"] = "Следующий репорт можно отправить через %SECONDS% сек.",
                ["Report.Sent"] = "Репорт отправлен.",
                ["Report.Usage"] = "Использование: /report <игрок или steamid> <причина> [сообщение]",
                ["Report.Reasons"] = "Доступные причины: %REASONS%",
                ["Report.TargetNotFound"] = "Игрок не найден.",
                ["Report.ReasonInvalid"] = "Неизвестная причина репорта.",
            }, this, "ru");
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            lang.SetServerLanguage("ru");

            if (_config == null)
            {
                LoadDefaultConfig();
                NormalizeConfig();
                SaveConfig();
            }

            RegisterReportCommands();
        }

        private void OnServerInitialized()
        {
            if (string.IsNullOrWhiteSpace(_config.token) || _config.token.Contains("change-me"))
            {
                PrintWarning("Plugin token is not configured yet. Use rustmod.verify <verification-code> to link this server and fetch the token automatically.");
            }

            timer.Every(_config.flush_interval_seconds, FlushQueue);
            timer.Every(1f, FlushReportQueue);
            timer.Every(Math.Max(1f, _config.flush_interval_seconds), FlushDrawingQueue);
            timer.Every(Math.Max(1f, _config.flush_interval_seconds), FlushDrawingDestroyQueue);
            timer.Every(_config.heartbeat_interval_seconds, SendHeartbeat);
            timer.Every(_config.players_state_interval_seconds, SnapshotPlayersState);
            timer.Every(_config.commands_poll_interval_seconds, PullCommands);
            timer.Every(_config.bans_snapshot_interval_seconds, PullBansSnapshot);
            timer.Every(_config.checks_snapshot_interval_seconds, PullChecksSnapshot);
            timer.Every(_config.mutes_snapshot_interval_seconds, PullMutesSnapshot);
            timer.Every(_config.ban_enforce_interval_seconds, CycleBanEnforcement);

            EnqueueEvent("server_initialized", null, null, new Dictionary<string, object>
            {
                ["hostname"] = ConVar.Server.hostname ?? string.Empty,
                ["map"] = SteamServer.MapName ?? ConVar.Server.level ?? string.Empty,
            });

            SendHeartbeat();
            timer.Once(1f, FlushQueue);
            timer.Once(2f, FlushDrawingQueue);
            timer.Once(2f, FlushDrawingDestroyQueue);
            timer.Once(2f, PullCommands);
            timer.Once(2f, PullBansSnapshot);
            timer.Once(2f, PullChecksSnapshot);
            timer.Once(2f, PullMutesSnapshot);
        }

        private void Unload()
        {
            FlushQueue();
            FlushReportQueue();
            FlushDrawingQueue();
            FlushDrawingDestroyQueue();
        }

        #endregion

        #region Hooks

        private void OnNewSave(string saveName)
        {
            EnqueueEvent("server_wipe", null, null, new Dictionary<string, object>
            {
                ["save_name"] = saveName ?? string.Empty,
            });
            FlushQueue();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            if (TryEnforceBan(player.UserIDString))
            {
                EnqueueEvent("ban_kick", player.UserIDString, null, new Dictionary<string, object>
                {
                    ["reason"] = "active_ban",
                });
                return;
            }

            if (_activeCheckNotices.Contains(player.UserIDString))
            {
                DrawCheckNotice(player);
            }

            EnqueueEvent("player_connected", player.UserIDString, null, new Dictionary<string, object>
            {
                ["name"] = player.displayName ?? string.Empty,
                ["ip"] = StripIpPort(player.Connection?.ipaddress),
            });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
            {
                return;
            }

            DestroyReportUi(player);
            CuiHelper.DestroyUi(player, "RP_PrivateLayer");

            EnqueueEvent("player_disconnected", player.UserIDString, null, new Dictionary<string, object>
            {
                ["name"] = player.displayName ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
            });
        }

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null)
            {
                return null;
            }

            if (channel != ConVar.Chat.ChatChannel.Global &&
                channel != ConVar.Chat.ChatChannel.Team &&
                channel != ConVar.Chat.ChatChannel.Local)
            {
                return null;
            }

            if (_activeCheckNotices.Contains(player.UserIDString))
            {
                var safeMessage = (message ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(safeMessage))
                {
                    SendCheckChatMessage(player, safeMessage);
                    SendReply(player, $"<size=13>[Secure Chat] Вы: {safeMessage}</size>");
                }

                return false;
            }

            if (TryBlockMutedChat(player))
            {
                return false;
            }

            EnqueueEvent("chat_message", player.UserIDString, null, new Dictionary<string, object>
            {
                ["channel"] = channel.ToString().ToLowerInvariant(),
                ["name"] = player.displayName ?? string.Empty,
                ["message"] = message ?? string.Empty,
            });

            return null;
        }

        private void CanAssignBed(BasePlayer player, SleepingBag bag, ulong targetPlayerId)
        {
            if (player == null || bag == null || targetPlayerId == 0)
            {
                return;
            }

            var targetId = targetPlayerId.ToString();
            var target = BasePlayer.Find(targetId) ?? BasePlayer.FindSleeping(targetId);

            EnqueueEvent("sleeping_bag_assigned", player.UserIDString, targetId, new Dictionary<string, object>
            {
                ["initiator_steam_id"] = player.UserIDString,
                ["initiator_name"] = player.displayName ?? string.Empty,
                ["target_steam_id"] = targetId,
                ["target_name"] = target?.displayName ?? string.Empty,
                ["position"] = FormatPosition(bag.transform.position),
                ["are_teammates"] = ArePlayersTeammates(player.userID, targetPlayerId),
            });
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity?.net == null)
            {
                return;
            }

            if (entity is ISignage || entity is PaintedItemStorageEntity)
            {
                EnqueueDrawingDestroy(entity.net.ID.Value.ToString());
            }
        }

        private void OnImagePost(BasePlayer player, string url, bool raw, ISignage signage, uint textureIndex)
        {
            TryQueueSignageDrawing(player, signage, textureIndex, 0);
        }

        private void OnSignUpdated(ISignage signage, BasePlayer player, int textureIndex = 0)
        {
            TryQueueSignageDrawing(player, signage, (uint)textureIndex, 0);
        }

        private void OnItemPainted(PaintedItemStorageEntity entity, Item item, BasePlayer player, byte[] image)
        {
            if (entity == null || player == null || image == null || image.Length == 0 || entity.net == null)
            {
                return;
            }

            QueueDrawingUpload(
                player,
                entity.net.ID.Value.ToString(),
                entity.ShortPrefabName,
                entity.transform.position,
                image,
                new Dictionary<string, object>
                {
                    ["item_shortname"] = item?.info?.shortname ?? string.Empty,
                    ["item_uid"] = item?.uid.Value ?? 0,
                }
            );
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            var player = plan?.GetOwnerPlayer();
            var signage = go?.ToBaseEntity() as ISignage;

            if (player == null || signage == null)
            {
                return;
            }

            NextTick(() => TryQueueSignageDrawing(player, signage, 0, 0));
        }

        private void OnPlayerReported(BasePlayer reporter, string targetName, string targetId, string subject, string message, string type)
        {
            if (reporter == null || string.IsNullOrWhiteSpace(targetId))
            {
                return;
            }

            if (!_config.report_ui_auto_parse || !CanSendReport(reporter, false))
            {
                return;
            }

            var target = BasePlayer.Find(targetId) ?? BasePlayer.FindSleeping(targetId);
            SendPlayerReport(
                initiator: reporter,
                targetSteamId: targetId,
                targetName: target != null ? (target.displayName ?? targetId) : (targetName ?? targetId),
                reason: type,
                message: message,
                source: "f7",
                reportType: type,
                subject: subject,
                wasCheckedRecently: false,
                targetOnline: target != null && target.IsConnected
            );
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null)
            {
                return;
            }

            var initiator = info?.InitiatorPlayer;
            var type = "player_death";
            var steamId = player.UserIDString;
            var targetSteamId = (string)null;

            if (initiator != null && initiator != player)
            {
                type = "player_kill";
                steamId = initiator.UserIDString;
                targetSteamId = player.UserIDString;
            }

            EnqueueEvent(type, steamId, targetSteamId, new Dictionary<string, object>
            {
                ["victim_name"] = player.displayName ?? string.Empty,
                ["initiator_name"] = initiator?.displayName ?? string.Empty,
                ["weapon"] = GetWeaponName(info),
                ["distance"] = info?.ProjectileDistance ?? 0f,
                ["is_headshot"] = info?.isHeadshot ?? false,
                ["game_time"] = DateTime.UtcNow.ToString("HH:mm:ss"),
            });
        }

        private void OnTeamKick(RelationshipManager.PlayerTeam team, BasePlayer player, ulong target)
        {
            if (player == null)
            {
                return;
            }

            SetTeamChange(player.UserIDString, "kick", player.UserIDString, target.ToString());
            SetTeamChange(target.ToString(), "kicked", player.UserIDString, target.ToString());
        }

        private void OnTeamDisband(RelationshipManager.PlayerTeam team)
        {
            if (team?.members == null)
            {
                return;
            }

            foreach (var member in team.members)
            {
                SetTeamChange(member.ToString(), "disband", member.ToString(), member.ToString());
            }
        }

        private void OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            SetTeamChange(player.UserIDString, "leave", player.UserIDString, player.UserIDString);

            if (team?.members != null && team.members.Count == 1)
            {
                SetTeamChange(team.members[0].ToString(), "alone", player.UserIDString, team.members[0].ToString());
            }
        }

        #endregion

        #region Commands

        [ConsoleCommand("rustmod.flush")]
        private void CmdFlush(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin)
            {
                return;
            }

            FlushQueue();
            Puts($"Manual flush requested. Queue size: {_eventQueue.Count}");
        }

        [ConsoleCommand("rustmod.heartbeat")]
        private void CmdHeartbeat(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin)
            {
                return;
            }

            SendHeartbeat();
            Puts("Manual heartbeat requested.");
        }

        [ConsoleCommand("rustmod.drawings.flush")]
        private void CmdDrawingsFlush(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin)
            {
                return;
            }

            FlushDrawingQueue();
            FlushDrawingDestroyQueue();
            Puts($"Manual drawings flush requested. Upload queue: {_drawingQueue.Count}, destroy queue: {_drawingDestroyQueue.Count}");
        }

        [ConsoleCommand("rustmod.bans.sync")]
        private void CmdBansSync(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin)
            {
                return;
            }

            PullBansSnapshot();
            CycleBanEnforcement();
            Puts("Manual bans sync requested.");
        }

        [ConsoleCommand("rustmod.verify")]
        private void CmdVerify(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin)
            {
                return;
            }

            var code = arg.Args != null && arg.Args.Length > 0 ? arg.Args[0] : _config.verification_code;
            if (string.IsNullOrWhiteSpace(code))
            {
                Puts("Usage: rustmod.verify <verification-code>");
                return;
            }

            SendVerification(code.Trim());
        }

        [ConsoleCommand("rustmod.report.page")]
        private void CmdReportPage(ConsoleSystem.Arg arg)
        {
            var player = GetArgPlayer(arg);
            if (player == null)
            {
                return;
            }

            var page = arg.Args != null && arg.Args.Length > 0 ? ToInt(arg.Args[0], 0) : 0;
            var search = NormalizeReportSearch(JoinArgs(arg.Args, 1));
            DrawReportInterface(player, page, search);
        }

        [ConsoleCommand("rustmod.report.search")]
        private void CmdReportSearch(ConsoleSystem.Arg arg)
        {
            var player = GetArgPlayer(arg);
            if (player == null)
            {
                return;
            }

            DrawReportInterface(player, 0, NormalizeReportSearch(JoinArgs(arg.Args, 0)));
        }

        [ConsoleCommand("rustmod.report.pick")]
        private void CmdReportPick(ConsoleSystem.Arg arg)
        {
            var player = GetArgPlayer(arg);
            if (player == null || arg.Args == null || arg.Args.Length == 0)
            {
                return;
            }

            var targetId = arg.Args[0];
            var tileIndex = arg.Args.Length > 1 ? ToInt(arg.Args[1], 0) : 0;
            var page = arg.Args.Length > 2 ? ToInt(arg.Args[2], 0) : 0;
            var search = NormalizeReportSearch(JoinArgs(arg.Args, 3));
            DrawReportReasonOverlay(player, targetId, tileIndex, page, search);
        }

        [ConsoleCommand("rustmod.report.send")]
        private void CmdReportSend(ConsoleSystem.Arg arg)
        {
            var player = GetArgPlayer(arg);
            if (player == null || arg.Args == null || arg.Args.Length < 2)
            {
                return;
            }

            if (!CanSendReport(player, true))
            {
                return;
            }

            var target = BasePlayer.Find(arg.Args[0]) ?? BasePlayer.FindSleeping(arg.Args[0]);
            if (target == null)
            {
                SendReply(player, GetMessage(player.UserIDString, "Report.TargetNotFound"));
                DrawReportInterface(player, 0, string.Empty);
                return;
            }

            var reasonIndex = ToInt(arg.Args[1], -1);
            if (reasonIndex < 0 || _config.report_ui_reasons == null || reasonIndex >= _config.report_ui_reasons.Count)
            {
                SendReply(player, GetMessage(player.UserIDString, "Report.ReasonInvalid"));
                return;
            }

            var reason = _config.report_ui_reasons[reasonIndex];
            SendPlayerReport(player, target, reason, string.Empty, "ui", reason, string.Empty, false);
            DestroyReportUi(player);
            SendReply(player, GetMessage(player.UserIDString, "Report.Sent"));
        }

        [ConsoleCommand("rustmod.report.close")]
        private void CmdReportClose(ConsoleSystem.Arg arg)
        {
            var player = GetArgPlayer(arg);
            if (player == null)
            {
                return;
            }

            DestroyReportUi(player);
        }

        private void CmdChatReport(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (args == null || args.Length == 0)
            {
                DrawReportInterface(player, 0, string.Empty);
                return;
            }

            if (args.Length < 2)
            {
                SendReply(player, GetMessage(player.UserIDString, "Report.Usage"));
                SendReply(player, GetMessage(player.UserIDString, "Report.Reasons").Replace("%REASONS%", string.Join(", ", _config.report_ui_reasons)));
                return;
            }

            if (!CanSendReport(player, true))
            {
                return;
            }

            var target = FindReportTarget(player, args[0]);
            if (target == null)
            {
                SendReply(player, GetMessage(player.UserIDString, "Report.TargetNotFound"));
                return;
            }

            var reason = ResolveReportReason(args[1]);
            if (string.IsNullOrWhiteSpace(reason))
            {
                SendReply(player, GetMessage(player.UserIDString, "Report.ReasonInvalid"));
                SendReply(player, GetMessage(player.UserIDString, "Report.Reasons").Replace("%REASONS%", string.Join(", ", _config.report_ui_reasons)));
                return;
            }

            var message = args.Length > 2 ? string.Join(" ", args.Skip(2).ToArray()) : string.Empty;
            SendPlayerReport(player, target, reason, message, "command", string.Empty, string.Empty, false);
            SendReply(player, GetMessage(player.UserIDString, "Report.Sent"));
        }

        #endregion

        #region Queue + API

        private class ServerSnapshot
        {
            public string uid;
            public string name;
            public string hostname;
            public string ip;
            public int port;
            public string map;
            public string description;
            public string plugin_version;
            public string game_version;
            public string protocol;
            public int online;
            public int max_players;
            public Dictionary<string, object> meta;
        }

        private class EventEnvelope
        {
            public string type;
            public long happened_at;
            public string steam_id;
            public string target_steam_id;
            public Dictionary<string, object> data;
        }

        private class HeartbeatRequest
        {
            public string verification_code;
            public ServerSnapshot server;
        }

        private class EventsRequest
        {
            public ServerSnapshot server;
            public List<EventEnvelope> events;
        }

        private class ReportsRequest
        {
            public ServerSnapshot server;
            public List<PluginReportDto> reports;
        }

        private class DrawingsRequest
        {
            public ServerSnapshot server;
            public List<PluginDrawingDto> drawings;
        }

        private class DrawingDestroyRequest
        {
            public ServerSnapshot server;
            public List<string> net_ids;
            public string steam_id;
            public string reason;
        }

        private class VerifyRequest
        {
            public string verification_code;
            public ServerSnapshot server;
        }

        private class VerifyResponse
        {
            public bool ok;
            public bool verified;
            public string project_slug;
            public string project_token;
            public string server_token;
            public long server_id;
            public string verified_at;
        }

        private class PluginReportDto
        {
            public string initiator_steam_id;
            public string initiator_name;
            public string target_steam_id;
            public string target_name;
            public List<string> sub_targets_steam_ids;
            public string reason;
            public string message;
            public string source;
            public string report_type;
            public string subject;
            public bool was_checked_recently;
            public long happened_at;
            public Dictionary<string, object> metadata;
        }

        private class PluginDrawingDto
        {
            public string steam_id;
            public string player_name;
            public string net_id;
            public string entity_type;
            public string position;
            public string square;
            public string image_base64;
            public long happened_at;
            public Dictionary<string, object> metadata;
        }

        private class CommandsPullResponse
        {
            public bool ok;
            public List<CommandTask> commands = new List<CommandTask>();
        }

        private class CommandTask
        {
            public long id;
            public string type;
            public JObject payload;
        }

        private class CommandsAckRequest
        {
            public List<CommandTaskResult> results = new List<CommandTaskResult>();
        }

        private class CommandTaskResult
        {
            public long id;
            public string status;
            public string message;
            public Dictionary<string, object> response;
        }

        private class ActiveBanEntry
        {
            public long id;
            public string group_uid;
            public string scope;
            public string steam_id;
            public string player_name;
            public string reason;
            public int duration_minutes;
            public long expires_at;
        }

        private class ActiveMuteEntry
        {
            public long id;
            public string group_uid;
            public string scope;
            public string steam_id;
            public string player_name;
            public string reason;
            public int duration_minutes;
            public long expires_at;
        }

        private class BansSnapshotResponse
        {
            public bool ok;
            public List<ActiveBanSnapshotItem> bans = new List<ActiveBanSnapshotItem>();
        }

        private class ActiveBanSnapshotItem
        {
            public long id;
            public string group_uid;
            public string scope;
            public string steam_id;
            public string player_name;
            public string reason;
            public int duration_minutes;
            public string expires_at;
        }

        private class MutesSnapshotResponse
        {
            public bool ok;
            public List<ActiveMuteSnapshotItem> mutes = new List<ActiveMuteSnapshotItem>();
        }

        private class ChecksSnapshotResponse
        {
            public bool ok;
            public List<ActiveCheckSnapshotItem> checks = new List<ActiveCheckSnapshotItem>();
        }

        private class ActiveCheckSnapshotItem
        {
            public long id;
            public string steam_id;
            public string player_name;
            public string started_at;
            public string contact_received_at;
        }

        private class ActiveMuteSnapshotItem
        {
            public long id;
            public string group_uid;
            public string scope;
            public string steam_id;
            public string player_name;
            public string reason;
            public int duration_minutes;
            public string expires_at;
        }

        private class TeamChangeHint
        {
            public string type;
            public string actor_steam_id;
            public string target_steam_id;
        }

        private void EnqueueEvent(string type, string steamId, string targetSteamId, Dictionary<string, object> data)
        {
            _eventQueue.Add(new EventEnvelope
            {
                type = type,
                happened_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                steam_id = steamId,
                target_steam_id = targetSteamId,
                data = data ?? new Dictionary<string, object>(),
            });

            if (_eventQueue.Count > _config.max_queue_size)
            {
                var overflow = _eventQueue.Count - _config.max_queue_size;
                _eventQueue.RemoveRange(0, overflow);
                DebugLog($"Queue overflow. Dropped {overflow} oldest events.");
            }

            if (_eventQueue.Count >= _config.max_events_per_request)
            {
                FlushQueue();
            }
        }

        private void SendHeartbeat()
        {
            if (!HasConfiguredToken())
            {
                SendBootstrapHeartbeat();
                return;
            }

            var requestBody = new HeartbeatRequest
            {
                server = BuildServerSnapshot(),
            };

            var url = BuildUrl("/heartbeat");
            var body = JsonConvert.SerializeObject(requestBody);

            webrequest.Enqueue(
                url,
                body,
                (code, response) =>
                {
                    if (code >= 200 && code < 300)
                    {
                        DebugLog("Heartbeat success.");
                        return;
                    }

                    if (HandleAuthenticationFailure(code, response, "heartbeat"))
                    {
                        return;
                    }

                    DebugLog($"Heartbeat failed (HTTP {code}): {response}");
                },
                this,
                RequestMethod.POST,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void SendBootstrapHeartbeat()
        {
            var requestBody = new HeartbeatRequest
            {
                verification_code = string.IsNullOrWhiteSpace(_config.verification_code) ? null : _config.verification_code.Trim(),
                server = BuildServerSnapshot(),
            };

            var url = BuildUrl("/bootstrap");
            var body = JsonConvert.SerializeObject(requestBody);

            webrequest.Enqueue(
                url,
                body,
                (code, response) =>
                {
                    if (code >= 200 && code < 300)
                    {
                        DebugLog("Bootstrap heartbeat success.");
                        return;
                    }

                    DebugLog($"Bootstrap heartbeat failed (HTTP {code}): {response}");
                },
                this,
                RequestMethod.POST,
                BuildVerificationHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void FlushQueue()
        {
            if (!HasConfiguredToken() || _isSending || _eventQueue.Count == 0)
            {
                return;
            }

            var take = Math.Min(_config.max_events_per_request, _eventQueue.Count);
            var batch = _eventQueue.Take(take).ToList();
            _eventQueue.RemoveRange(0, take);

            var requestBody = new EventsRequest
            {
                server = BuildServerSnapshot(),
                events = batch,
            };

            var url = BuildUrl("/events");
            var body = JsonConvert.SerializeObject(requestBody);
            _isSending = true;

            webrequest.Enqueue(
                url,
                body,
                (code, response) =>
                {
                    _isSending = false;

                    if (code >= 200 && code < 300)
                    {
                        DebugLog($"Flush success. Sent {batch.Count} events.");

                        if (_eventQueue.Count > 0)
                        {
                            timer.Once(0.1f, FlushQueue);
                        }

                        return;
                    }

                    _eventQueue.InsertRange(0, batch);
                    DebugLog($"Flush failed (HTTP {code}). Restored {batch.Count} events. Response: {response}");
                },
                this,
                RequestMethod.POST,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void FlushReportQueue()
        {
            if (!HasConfiguredToken() || _isSendingReports || _reportQueue.Count == 0)
            {
                return;
            }

            var batch = _reportQueue.ToList();
            _reportQueue.Clear();

            var requestBody = new ReportsRequest
            {
                server = BuildServerSnapshot(),
                reports = batch,
            };

            var url = BuildUrl("/reports");
            var body = JsonConvert.SerializeObject(requestBody);
            _isSendingReports = true;

            webrequest.Enqueue(
                url,
                body,
                (code, response) =>
                {
                    _isSendingReports = false;

                    if (code >= 200 && code < 300)
                    {
                        DebugLog($"Reports flush success. Sent {batch.Count} reports.");
                        return;
                    }

                    _reportQueue.InsertRange(0, batch);
                    DebugLog($"Reports flush failed (HTTP {code}). Restored {batch.Count} reports. Response: {response}");
                },
                this,
                RequestMethod.POST,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void EnqueueDrawingUpload(PluginDrawingDto drawing)
        {
            if (drawing == null || string.IsNullOrWhiteSpace(drawing.net_id) || string.IsNullOrWhiteSpace(drawing.image_base64))
            {
                return;
            }

            _drawingQueue.RemoveAll(item => item != null && item.net_id == drawing.net_id);
            _drawingQueue.Add(drawing);

            if (_drawingQueue.Count > _config.max_queue_size)
            {
                var overflow = _drawingQueue.Count - _config.max_queue_size;
                _drawingQueue.RemoveRange(0, overflow);
                DebugLog($"Drawing queue overflow. Dropped {overflow} oldest items.");
            }

            if (_drawingQueue.Count >= Math.Max(1, _config.max_events_per_request / 2))
            {
                FlushDrawingQueue();
            }
        }

        private void FlushDrawingQueue()
        {
            if (!HasConfiguredToken() || _isSendingDrawings || _drawingQueue.Count == 0)
            {
                return;
            }

            var take = Math.Min(Math.Max(1, _config.max_events_per_request / 2), _drawingQueue.Count);
            var batch = _drawingQueue.Take(take).ToList();
            _drawingQueue.RemoveRange(0, take);

            var requestBody = new DrawingsRequest
            {
                server = BuildServerSnapshot(),
                drawings = batch,
            };

            var body = JsonConvert.SerializeObject(requestBody);
            var url = BuildUrl("/drawings");
            _isSendingDrawings = true;

            webrequest.Enqueue(
                url,
                body,
                (code, response) =>
                {
                    _isSendingDrawings = false;

                    if (code >= 200 && code < 300)
                    {
                        DebugLog($"Drawings flush success. Sent {batch.Count} items.");
                        if (_drawingQueue.Count > 0)
                        {
                            timer.Once(0.1f, FlushDrawingQueue);
                        }

                        return;
                    }

                    _drawingQueue.InsertRange(0, batch);
                    PrintWarning($"Drawings flush failed (HTTP {code}). Response: {response}");
                    DebugLog($"Drawings flush failed (HTTP {code}). Restored {batch.Count} items. Response: {response}");
                },
                this,
                RequestMethod.POST,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void EnqueueDrawingDestroy(string netId, string steamId = null, string reason = "entity_killed")
        {
            if (!string.IsNullOrWhiteSpace(netId))
            {
                if (!_drawingDestroyQueue.Contains(netId))
                {
                    _drawingDestroyQueue.Add(netId);
                }
            }

            if (_drawingDestroyQueue.Count > _config.max_queue_size)
            {
                var overflow = _drawingDestroyQueue.Count - _config.max_queue_size;
                _drawingDestroyQueue.RemoveRange(0, overflow);
            }

            if (_drawingDestroyQueue.Count >= Math.Max(1, _config.max_events_per_request))
            {
                FlushDrawingDestroyQueue();
            }
        }

        private void FlushDrawingDestroyQueue()
        {
            if (!HasConfiguredToken() || _isSendingDrawingDeletes || _drawingDestroyQueue.Count == 0)
            {
                return;
            }

            var batch = _drawingDestroyQueue.Take(_config.max_events_per_request).ToList();
            _drawingDestroyQueue.RemoveRange(0, batch.Count);

            var requestBody = new DrawingDestroyRequest
            {
                server = BuildServerSnapshot(),
                net_ids = batch,
                reason = "entity_killed",
            };

            var body = JsonConvert.SerializeObject(requestBody);
            var url = BuildUrl("/drawings");
            _isSendingDrawingDeletes = true;

            webrequest.Enqueue(
                url,
                body,
                (code, response) =>
                {
                    _isSendingDrawingDeletes = false;

                    if (code >= 200 && code < 300)
                    {
                        if (_drawingDestroyQueue.Count > 0)
                        {
                            timer.Once(0.1f, FlushDrawingDestroyQueue);
                        }

                        return;
                    }

                    _drawingDestroyQueue.InsertRange(0, batch);
                    DebugLog($"Drawing delete flush failed (HTTP {code}). Restored {batch.Count} items. Response: {response}");
                },
                this,
                RequestMethod.DELETE,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void SendVerification(string code)
        {
            var requestBody = new VerifyRequest
            {
                verification_code = code,
                server = BuildServerSnapshot(),
            };

            var url = BuildUrl("/verify");
            var body = JsonConvert.SerializeObject(requestBody);

            webrequest.Enqueue(
                url,
                body,
                (httpCode, response) =>
                {
                    if (httpCode >= 200 && httpCode < 300)
                    {
                        VerifyResponse verifyResponse = null;

                        try
                        {
                            verifyResponse = JsonConvert.DeserializeObject<VerifyResponse>(response);
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"Verify response parse failed: {ex.Message} ({response})");
                        }

                        if (verifyResponse != null)
                        {
                            var nextToken = !string.IsNullOrWhiteSpace(verifyResponse.server_token)
                                ? verifyResponse.server_token
                                : verifyResponse.project_token;

                            if (!string.IsNullOrWhiteSpace(nextToken))
                            {
                                _config.token = nextToken.Trim();
                            }
                        }

                        if (_config.verification_code == code)
                        {
                            _config.verification_code = string.Empty;
                        }

                        SaveConfig();
                        Puts("Project verification completed successfully. Token saved to config.");

                        SendHeartbeat();
                        timer.Once(1f, FlushQueue);
                        timer.Once(2f, PullCommands);
                        timer.Once(2f, PullBansSnapshot);
                        timer.Once(2f, PullMutesSnapshot);
                        return;
                    }

                    Puts($"Project verification failed (HTTP {httpCode}): {response}");
                },
                this,
                RequestMethod.POST,
                BuildVerificationHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void SnapshotPlayersState()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null)
                {
                    continue;
                }

                var teamMembers = GetTeamMembers(player);
                var data = new Dictionary<string, object>
                {
                    ["name"] = player.displayName ?? string.Empty,
                    ["ip"] = StripIpPort(player.Connection?.ipaddress),
                    ["ping"] = player.Connection != null ? Network.Net.sv.GetAveragePing(player.Connection) : 0,
                    ["connected"] = player.IsConnected,
                    ["status"] = player.IsAlive() ? "active" : "dead",
                    ["health"] = player.Health(),
                    ["team_members"] = teamMembers,
                    ["team_size"] = teamMembers.Count,
                };

                TeamChangeHint hint;
                if (_pendingTeamChanges.TryGetValue(player.UserIDString, out hint) && hint != null)
                {
                    data["team_change_type"] = hint.type ?? string.Empty;
                    data["team_change_actor_steam_id"] = hint.actor_steam_id ?? string.Empty;
                    data["team_change_target_steam_id"] = hint.target_steam_id ?? string.Empty;
                    _pendingTeamChanges.Remove(player.UserIDString);
                }

                EnqueueEvent("player_state", player.UserIDString, null, data);
            }
        }

        private void PullCommands()
        {
            if (!HasConfiguredToken() || _isPullingCommands)
            {
                return;
            }

            _isPullingCommands = true;
            var url = BuildUrl("/commands/pull");

            webrequest.Enqueue(
                url,
                string.Empty,
                (code, response) =>
                {
                    _isPullingCommands = false;

                    if (code < 200 || code >= 300)
                    {
                        if (HandleAuthenticationFailure(code, response, "commands/pull"))
                        {
                            return;
                        }

                        DebugLog($"Commands pull failed (HTTP {code}): {response}");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        return;
                    }

                    CommandsPullResponse pull;
                    try
                    {
                        pull = JsonConvert.DeserializeObject<CommandsPullResponse>(response);
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Commands pull parse failed: {ex.Message} ({response})");
                        return;
                    }

                    if (pull?.commands == null || pull.commands.Count == 0)
                    {
                        return;
                    }

                    var results = new List<CommandTaskResult>(pull.commands.Count);
                    foreach (var task in pull.commands)
                    {
                        results.Add(ProcessCommandTask(task));
                    }

                    AckCommands(results);
                },
                this,
                RequestMethod.GET,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void PullBansSnapshot()
        {
            if (!HasConfiguredToken() || _isPullingBans)
            {
                return;
            }

            _isPullingBans = true;
            var url = BuildUrl("/bans/snapshot");

            webrequest.Enqueue(
                url,
                string.Empty,
                (code, response) =>
                {
                    _isPullingBans = false;

                    if (code < 200 || code >= 300)
                    {
                        if (HandleAuthenticationFailure(code, response, "bans/snapshot"))
                        {
                            return;
                        }

                        DebugLog($"Bans snapshot failed (HTTP {code}): {response}");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        return;
                    }

                    BansSnapshotResponse snapshot;
                    try
                    {
                        snapshot = JsonConvert.DeserializeObject<BansSnapshotResponse>(response);
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Bans snapshot parse failed: {ex.Message} ({response})");
                        return;
                    }

                    if (snapshot?.bans == null)
                    {
                        return;
                    }

                    var next = new Dictionary<string, ActiveBanEntry>();
                    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    foreach (var item in snapshot.bans)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.steam_id))
                        {
                            continue;
                        }

                        var expiresAtUnix = ParseExpiresAtUnix(item.expires_at, item.duration_minutes);
                        if (expiresAtUnix > 0 && expiresAtUnix <= nowUnix)
                        {
                            continue;
                        }

                        next[item.steam_id] = new ActiveBanEntry
                        {
                            id = item.id,
                            group_uid = item.group_uid ?? string.Empty,
                            scope = item.scope ?? "local",
                            steam_id = item.steam_id,
                            player_name = item.player_name ?? string.Empty,
                            reason = string.IsNullOrWhiteSpace(item.reason) ? "Banned" : item.reason,
                            duration_minutes = item.duration_minutes,
                            expires_at = expiresAtUnix,
                        };
                    }

                    _activeBans.Clear();
                    foreach (var pair in next)
                    {
                        _activeBans[pair.Key] = pair.Value;
                    }
                },
                this,
                RequestMethod.GET,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void PullMutesSnapshot()
        {
            if (!HasConfiguredToken() || _isPullingMutes)
            {
                return;
            }

            _isPullingMutes = true;
            var url = BuildUrl("/mutes/snapshot");

            webrequest.Enqueue(
                url,
                string.Empty,
                (code, response) =>
                {
                    _isPullingMutes = false;

                    if (code < 200 || code >= 300)
                    {
                        if (HandleAuthenticationFailure(code, response, "mutes/snapshot"))
                        {
                            return;
                        }

                        DebugLog($"Mutes snapshot failed (HTTP {code}): {response}");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        return;
                    }

                    MutesSnapshotResponse snapshot;
                    try
                    {
                        snapshot = JsonConvert.DeserializeObject<MutesSnapshotResponse>(response);
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Mutes snapshot parse failed: {ex.Message} ({response})");
                        return;
                    }

                    if (snapshot?.mutes == null)
                    {
                        return;
                    }

                    var next = new Dictionary<string, ActiveMuteEntry>();
                    var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    foreach (var item in snapshot.mutes)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.steam_id))
                        {
                            continue;
                        }

                        var expiresAtUnix = ParseExpiresAtUnix(item.expires_at, item.duration_minutes);
                        if (expiresAtUnix > 0 && expiresAtUnix <= nowUnix)
                        {
                            continue;
                        }

                        next[item.steam_id] = new ActiveMuteEntry
                        {
                            id = item.id,
                            group_uid = item.group_uid ?? string.Empty,
                            scope = item.scope ?? "local",
                            steam_id = item.steam_id,
                            player_name = item.player_name ?? string.Empty,
                            reason = string.IsNullOrWhiteSpace(item.reason) ? "Muted" : item.reason,
                            duration_minutes = item.duration_minutes,
                            expires_at = expiresAtUnix,
                        };
                    }

                    _activeMutes.Clear();
                    foreach (var pair in next)
                    {
                        _activeMutes[pair.Key] = pair.Value;
                    }
                },
                this,
                RequestMethod.GET,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void PullChecksSnapshot()
        {
            if (!HasConfiguredToken() || _isPullingChecks)
            {
                return;
            }

            _isPullingChecks = true;
            var url = BuildUrl("/checks/snapshot");

            webrequest.Enqueue(
                url,
                string.Empty,
                (code, response) =>
                {
                    _isPullingChecks = false;

                    if (code < 200 || code >= 300)
                    {
                        if (HandleAuthenticationFailure(code, response, "checks/snapshot"))
                        {
                            return;
                        }

                        DebugLog($"Checks snapshot failed (HTTP {code}): {response}");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        return;
                    }

                    ChecksSnapshotResponse snapshot;
                    try
                    {
                        snapshot = JsonConvert.DeserializeObject<ChecksSnapshotResponse>(response);
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Checks snapshot parse failed: {ex.Message} ({response})");
                        return;
                    }

                    if (snapshot?.checks == null)
                    {
                        return;
                    }

                    var next = new HashSet<string>();
                    foreach (var item in snapshot.checks)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.steam_id))
                        {
                            continue;
                        }

                        next.Add(item.steam_id);
                    }

                    SyncCheckNoticeStates(next);
                },
                this,
                RequestMethod.GET,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void CycleBanEnforcement()
        {
            foreach (var player in BasePlayer.activePlayerList.ToList())
            {
                if (player == null)
                {
                    continue;
                }

                TryEnforceBan(player.UserIDString);
            }

            foreach (var connection in ConnectionAuth.m_AuthConnection.ToList())
            {
                if (connection == null)
                {
                    continue;
                }

                TryEnforceBan(connection.userid.ToString());
            }

            var queue = ServerMgr.Instance?.connectionQueue;
            if (queue == null)
            {
                return;
            }

            foreach (var joining in queue.joining.ToList())
            {
                if (joining == null)
                {
                    continue;
                }

                TryEnforceBan(joining.userid.ToString());
            }

            foreach (var queued in queue.queue.ToList())
            {
                if (queued == null)
                {
                    continue;
                }

                TryEnforceBan(queued.userid.ToString());
            }
        }

        private CommandTaskResult ProcessCommandTask(CommandTask task)
        {
            var result = new CommandTaskResult
            {
                id = task.id,
                status = "success",
                message = null,
                response = new Dictionary<string, object>(),
            };

            try
            {
                var payload = task.payload ?? new JObject();
                var steamId = payload.Value<string>("steam_id") ?? string.Empty;

                if (task.type == "ban_add" || task.type == "ban")
                {
                    var reason = payload.Value<string>("reason") ?? "Banned";
                    var playerName = payload.Value<string>("player_name") ?? string.Empty;
                    var scope = payload.Value<string>("scope") ?? "local";
                    var durationMinutes = payload.Value<int?>("duration_minutes") ?? 0;
                    var expiresAtIso = payload.Value<string>("expires_at") ?? string.Empty;
                    var expiresAtUnix = ParseExpiresAtUnix(expiresAtIso, durationMinutes);

                    if (string.IsNullOrWhiteSpace(steamId))
                    {
                        throw new Exception("steam_id is missing for ban_add command");
                    }

                    var activeBan = new ActiveBanEntry
                    {
                        id = payload.Value<long?>("ban_id") ?? task.id,
                        group_uid = payload.Value<string>("group_uid") ?? string.Empty,
                        scope = scope,
                        steam_id = steamId,
                        player_name = playerName,
                        reason = reason,
                        duration_minutes = durationMinutes,
                        expires_at = expiresAtUnix,
                    };

                    UpsertActiveBan(activeBan);
                    var kicked = TryEnforceBan(steamId);

                    EnqueueEvent("ban_created", steamId, null, new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["player_name"] = playerName,
                        ["scope"] = scope,
                        ["duration_minutes"] = durationMinutes,
                        ["expires_at"] = expiresAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix).ToString("O") : string.Empty,
                        ["group_uid"] = activeBan.group_uid,
                    });

                    result.response["cached"] = true;
                    result.response["kicked"] = kicked;
                    return result;
                }

                if (task.type == "ban_remove" || task.type == "unban")
                {
                    if (string.IsNullOrWhiteSpace(steamId))
                    {
                        throw new Exception("steam_id is missing for ban_remove command");
                    }

                    RemoveActiveBan(steamId);

                    EnqueueEvent("ban_removed", steamId, null, new Dictionary<string, object>
                    {
                        ["group_uid"] = payload.Value<string>("group_uid") ?? string.Empty,
                        ["scope"] = payload.Value<string>("scope") ?? "local",
                    });

                    result.response["removed"] = true;
                    return result;
                }

                if (task.type == "mute_add")
                {
                    var reason = payload.Value<string>("reason") ?? "Muted";
                    var playerName = payload.Value<string>("player_name") ?? string.Empty;
                    var scope = payload.Value<string>("scope") ?? "local";
                    var durationMinutes = payload.Value<int?>("duration_minutes") ?? 0;
                    var expiresAtIso = payload.Value<string>("expires_at") ?? string.Empty;
                    var expiresAtUnix = ParseExpiresAtUnix(expiresAtIso, durationMinutes);

                    if (string.IsNullOrWhiteSpace(steamId))
                    {
                        throw new Exception("steam_id is missing for mute_add command");
                    }

                    var activeMute = new ActiveMuteEntry
                    {
                        id = payload.Value<long?>("mute_id") ?? task.id,
                        group_uid = payload.Value<string>("group_uid") ?? string.Empty,
                        scope = scope,
                        steam_id = steamId,
                        player_name = playerName,
                        reason = reason,
                        duration_minutes = durationMinutes,
                        expires_at = expiresAtUnix,
                    };

                    UpsertActiveMute(activeMute);
                    NotifyPlayerAboutMute(activeMute);

                    EnqueueEvent("mute_created", steamId, null, new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["player_name"] = playerName,
                        ["scope"] = scope,
                        ["duration_minutes"] = durationMinutes,
                        ["expires_at"] = expiresAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix).ToString("O") : string.Empty,
                        ["group_uid"] = activeMute.group_uid,
                    });

                    result.response["cached"] = true;
                    return result;
                }

                if (task.type == "mute_remove")
                {
                    if (string.IsNullOrWhiteSpace(steamId))
                    {
                        throw new Exception("steam_id is missing for mute_remove command");
                    }

                    RemoveActiveMute(steamId);
                    NotifyPlayerAboutMuteRemoval(steamId);

                    EnqueueEvent("mute_removed", steamId, null, new Dictionary<string, object>
                    {
                        ["group_uid"] = payload.Value<string>("group_uid") ?? string.Empty,
                        ["scope"] = payload.Value<string>("scope") ?? "local",
                        ["revoked_reason"] = payload.Value<string>("revoked_reason") ?? string.Empty,
                    });

                    result.response["removed"] = true;
                    return result;
                }

                if (task.type == "kick_player")
                {
                    if (string.IsNullOrWhiteSpace(steamId))
                    {
                        throw new Exception("steam_id is missing for kick_player command");
                    }

                    var reason = payload.Value<string>("reason") ?? "Kicked by administration";
                    var kicked = CloseConnection(steamId, BuildKickMessage(steamId, reason));
                    if (!kicked)
                    {
                        throw new Exception("player is not connected");
                    }

                    EnqueueEvent("player_kicked", steamId, null, new Dictionary<string, object>
                    {
                        ["reason"] = reason,
                        ["player_name"] = payload.Value<string>("player_name") ?? string.Empty,
                    });

                    result.response["kicked"] = true;
                    return result;
                }

                if (task.type == "drawing_delete")
                {
                    var netId = payload.Value<string>("net_id") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(netId))
                    {
                        throw new Exception("net_id is missing for drawing_delete command");
                    }

                    var removed = TryDeleteDrawingEntity(netId);
                    result.response["removed"] = removed;
                    result.response["already_missing"] = !removed;
                    return result;
                }

                if (task.type == "check_notice_set")
                {
                    if (string.IsNullOrWhiteSpace(steamId))
                    {
                        throw new Exception("steam_id is missing for check_notice_set command");
                    }

                    var value = payload.Value<bool?>("value") ?? false;
                    SetCheckNoticeState(steamId, value);
                    result.response["notice"] = value;
                    return result;
                }

                if (task.type == "check_chat_message")
                {
                    if (string.IsNullOrWhiteSpace(steamId))
                    {
                        throw new Exception("steam_id is missing for check_chat_message command");
                    }

                    var messageText = payload.Value<string>("message") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(messageText))
                    {
                        throw new Exception("message is missing for check_chat_message command");
                    }

                    var authorName = payload.Value<string>("author_name") ?? "РЎРѕС‚СЂСѓРґРЅРёРє";
                    var target = BasePlayer.Find(steamId);
                    if (target == null || !target.IsConnected)
                    {
                        throw new Exception("player is offline");
                    }

                    SendReply(target, $"<size=13>[Secure Chat] <color=#AAFF55>{authorName}</color>: {messageText}</size>");
                    result.response["delivered"] = true;
                    return result;
                }

                if (task.type == "chat_broadcast")
                {
                    var authorTag = payload.Value<string>("author_tag") ?? "@site";
                    var messageText = payload.Value<string>("message") ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(messageText))
                    {
                        throw new Exception("message is missing for chat_broadcast command");
                    }

                    var formattedMessage = $"<size=12><color=#ffffffB3>Сообщение от {authorTag}:</color></size>\n<color=#AAFF55>{messageText}</color>";
                    var delivered = 0;

                    foreach (var player in BasePlayer.activePlayerList)
                    {
                        if (player == null || !player.IsConnected)
                        {
                            continue;
                        }

                        SendReply(player, formattedMessage);
                        delivered++;
                    }

                    result.response["delivered"] = delivered;
                    return result;
                }

                throw new Exception($"Unknown command type: {task.type}");
            }
            catch (Exception ex)
            {
                result.status = "failed";
                result.message = ex.Message;
                result.response["exception"] = ex.ToString();
            }

            return result;
        }

        private void AckCommands(List<CommandTaskResult> results)
        {
            if (results == null || results.Count == 0)
            {
                return;
            }

            var url = BuildUrl("/commands/ack");
            var body = JsonConvert.SerializeObject(new CommandsAckRequest
            {
                results = results,
            });

            webrequest.Enqueue(
                url,
                body,
                (code, response) =>
                {
                    if (code < 200 || code >= 300)
                    {
                        DebugLog($"Commands ack failed (HTTP {code}): {response}");
                        return;
                    }

                    // Keep local cache aligned with backend state after command processing.
                    PullBansSnapshot();
                    PullMutesSnapshot();
                },
                this,
                RequestMethod.POST,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private bool TryEnforceBan(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return false;
            }

            var over = Interface.Oxide.CallHook("RustMod_CanIgnoreBan", steamId);
            if (over != null)
            {
                return false;
            }

            ActiveBanEntry ban;
            if (!TryGetActiveBan(steamId, out ban))
            {
                return false;
            }

            var message = BuildBanKickMessage(steamId, ban);
            return CloseConnection(steamId, message);
        }

        private bool TryBlockMutedChat(BasePlayer player)
        {
            if (player == null || string.IsNullOrWhiteSpace(player.UserIDString))
            {
                return false;
            }

            var over = Interface.Oxide.CallHook("RustMod_CanIgnoreMute", player.UserIDString);
            if (over != null)
            {
                return false;
            }

            ActiveMuteEntry mute;
            if (!TryGetActiveMute(player.UserIDString, out mute))
            {
                return false;
            }

            ShowToast(player, GetMessage(player.UserIDString, "Mute.Active.Toast"), ToastType.Error);
            SendReply(player, BuildMuteBlockMessage(player.UserIDString, mute));
            return true;
        }

        private bool TryGetActiveBan(string steamId, out ActiveBanEntry ban)
        {
            if (!_activeBans.TryGetValue(steamId, out ban))
            {
                return false;
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ban.expires_at > 0 && ban.expires_at <= nowUnix)
            {
                _activeBans.Remove(steamId);
                ban = null;
                return false;
            }

            return true;
        }

        private void UpsertActiveBan(ActiveBanEntry ban)
        {
            if (ban == null || string.IsNullOrWhiteSpace(ban.steam_id))
            {
                return;
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ban.expires_at > 0 && ban.expires_at <= nowUnix)
            {
                _activeBans.Remove(ban.steam_id);
                return;
            }

            _activeBans[ban.steam_id] = ban;
        }

        private void RemoveActiveBan(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return;
            }

            _activeBans.Remove(steamId);
        }

        private bool TryGetActiveMute(string steamId, out ActiveMuteEntry mute)
        {
            if (!_activeMutes.TryGetValue(steamId, out mute))
            {
                return false;
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (mute.expires_at > 0 && mute.expires_at <= nowUnix)
            {
                _activeMutes.Remove(steamId);
                mute = null;
                return false;
            }

            return true;
        }

        private void UpsertActiveMute(ActiveMuteEntry mute)
        {
            if (mute == null || string.IsNullOrWhiteSpace(mute.steam_id))
            {
                return;
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (mute.expires_at > 0 && mute.expires_at <= nowUnix)
            {
                _activeMutes.Remove(mute.steam_id);
                return;
            }

            _activeMutes[mute.steam_id] = mute;
        }

        private void RemoveActiveMute(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return;
            }

            _activeMutes.Remove(steamId);
        }

        private List<string> GetTeamMembers(BasePlayer player)
        {
            var result = new List<string>();
            if (player == null)
            {
                return result;
            }

            result.Add(player.UserIDString);

            var team = RelationshipManager.ServerInstance?.FindPlayersTeam(player.userID);
            if (team?.members == null)
            {
                return result;
            }

            foreach (var member in team.members)
            {
                var steamId = member.ToString();
                if (!result.Contains(steamId))
                {
                    result.Add(steamId);
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private bool ArePlayersTeammates(ulong initiatorId, ulong targetId)
        {
            if (initiatorId == 0 || targetId == 0 || initiatorId == targetId)
            {
                return false;
            }

            var team = RelationshipManager.ServerInstance?.FindPlayersTeam(initiatorId);
            return team?.members?.Contains(targetId) ?? false;
        }

        private void SetTeamChange(string steamId, string type, string actorSteamId, string targetSteamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return;
            }

            _pendingTeamChanges[steamId] = new TeamChangeHint
            {
                type = type ?? string.Empty,
                actor_steam_id = actorSteamId ?? string.Empty,
                target_steam_id = targetSteamId ?? string.Empty,
            };
        }

        private static long ParseExpiresAtUnix(string expiresAtIso, int durationMinutes)
        {
            if (!string.IsNullOrWhiteSpace(expiresAtIso))
            {
                DateTimeOffset parsed;
                if (DateTimeOffset.TryParse(expiresAtIso, out parsed))
                {
                    return parsed.ToUnixTimeSeconds();
                }
            }

            if (durationMinutes > 0)
            {
                return DateTimeOffset.UtcNow.AddMinutes(durationMinutes).ToUnixTimeSeconds();
            }

            return 0;
        }

        private void NotifyPlayerAboutMute(ActiveMuteEntry mute)
        {
            if (mute == null || string.IsNullOrWhiteSpace(mute.steam_id))
            {
                return;
            }

            var player = BasePlayer.Find(mute.steam_id);
            if (player == null || !player.IsConnected)
            {
                return;
            }

            ShowToast(player, GetMessage(player.UserIDString, "Mute.Issued.Toast"), ToastType.Error);
            SendReply(player, BuildMuteBlockMessage(player.UserIDString, mute));
        }

        private void NotifyPlayerAboutMuteRemoval(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return;
            }

            var player = BasePlayer.Find(steamId);
            if (player == null || !player.IsConnected)
            {
                return;
            }

            ShowToast(player, GetMessage(player.UserIDString, "Mute.Removed.Toast"), ToastType.Info);
            SendReply(player, GetMessage(player.UserIDString, "Mute.Removed.Chat"));
        }

        private void SetCheckNoticeState(string steamId, bool value)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return;
            }

            if (value)
            {
                _activeCheckNotices.Add(steamId);
                var player = BasePlayer.Find(steamId);
                if (player != null && player.IsConnected)
                {
                    DrawCheckNotice(player);
                }

                return;
            }

            _activeCheckNotices.Remove(steamId);
            var target = BasePlayer.Find(steamId);
            if (target != null && target.IsConnected)
            {
                CuiHelper.DestroyUi(target, "RP_PrivateLayer");
            }
        }

        private void SyncCheckNoticeStates(HashSet<string> activeSteamIds)
        {
            if (activeSteamIds == null)
            {
                activeSteamIds = new HashSet<string>();
            }

            var toHide = _activeCheckNotices.Where(steamId => !activeSteamIds.Contains(steamId)).ToList();
            foreach (var steamId in toHide)
            {
                SetCheckNoticeState(steamId, false);
            }

            foreach (var steamId in activeSteamIds)
            {
                SetCheckNoticeState(steamId, true);
            }
        }

        private void DrawCheckNotice(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
            {
                return;
            }

            var checkLayer = "RP_PrivateLayer";
            CuiHelper.DestroyUi(player, checkLayer);

            var text = GetMessage(player.UserIDString, "Check.Text");
            if (string.IsNullOrWhiteSpace(text) || text == "Check.Text")
            {
                text = "<color=#c6bdb4><size=32><b>ВЫ ВЫЗВАНЫ НА ПРОВЕРКУ</b></size></color>\n<color=#958d85>У вас есть <color=#c6bdb4><b>3 минуты</b></color>, чтобы написать Discord в чат и принять заявку в друзья.\n\nВсе сообщения из чата во время проверки видит только проверяющий.</color>";
            }

            var container = new CuiElementContainer();
            container.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0 0.5", AnchorMax = "1 1", OffsetMin = "-500 -500", OffsetMax = "500 500" },
                Button = { Color = HexToRustFormat("#1C1C1C"), Sprite = "assets/content/ui/gameui/attackheli/compass/ui.soft.radial.png" },
                Text = { Text = string.Empty, Align = TextAnchor.MiddleCenter },
            }, "Under", checkLayer);
            container.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMax = "0 0" },
                Text = { Text = text, Align = TextAnchor.MiddleCenter, Font = "robotocondensed-regular.ttf", FontSize = 16 },
            }, checkLayer);

            CuiHelper.AddUi(player, container);

            var effect = new Effect("assets/bundled/prefabs/fx/invite_notice.prefab", player, 0, Vector3.zero, Vector3.zero);
            EffectNetwork.Send(effect, player.Connection);
        }

        private void SendCheckChatMessage(BasePlayer player, string message)
        {
            var url = BuildUrl("/checks/messages");
            var body = JsonConvert.SerializeObject(new
            {
                server = BuildServerSnapshot(),
                steam_id = player.UserIDString,
                message = message,
            });

            webrequest.Enqueue(
                url,
                body,
                (code, response) =>
                {
                    if (code < 200 || code >= 300)
                    {
                        DebugLog($"Check chat send failed (HTTP {code}): {response}");
                    }
                },
                this,
                RequestMethod.POST,
                BuildHeaders(),
                _config.request_timeout_seconds
            );
        }

        private void TryQueueSignageDrawing(BasePlayer player, ISignage signage, uint textureIndex, int attempt)
        {
            if (player == null || signage == null)
            {
                return;
            }

            var entity = signage as BaseEntity;
            if (entity?.net == null)
            {
                return;
            }

            var imageBytes = ReadSignagePngBytes(signage, textureIndex);
            if (imageBytes == null || imageBytes.Length == 0)
            {
                if (attempt < 5)
                {
                    timer.Once(0.35f, () => TryQueueSignageDrawing(player, signage, textureIndex, attempt + 1));
                    return;
                }

                DebugLog($"Drawing bytes are empty after retries. net_id={entity.net.ID.Value} texture_index={textureIndex} prefab={entity.ShortPrefabName}");
                return;
            }

            QueueDrawingUpload(
                player,
                entity.net.ID.Value.ToString(),
                entity.ShortPrefabName,
                entity.transform.position,
                imageBytes,
                new Dictionary<string, object>
                {
                    ["texture_index"] = textureIndex,
                }
            );
        }

        private byte[] ReadSignagePngBytes(ISignage signage, uint textureIndex)
        {
            var entity = signage as BaseEntity;
            if (signage == null || entity?.net == null)
            {
                return null;
            }

            var crcs = signage.GetTextureCRCs();
            if (crcs == null || textureIndex >= crcs.Length)
            {
                return null;
            }

            var crc = crcs[textureIndex];
            if (crc == 0)
            {
                return null;
            }

            return FileStorage.server.Get(crc, FileStorage.Type.png, signage.NetworkID, textureIndex);
        }

        private void QueueDrawingUpload(BasePlayer player, string netId, string entityType, Vector3 position, byte[] pngBytes, Dictionary<string, object> metadata = null)
        {
            if (player == null || string.IsNullOrWhiteSpace(netId) || pngBytes == null || pngBytes.Length == 0)
            {
                return;
            }

            EnqueueDrawingUpload(new PluginDrawingDto
            {
                steam_id = player.UserIDString,
                player_name = player.displayName ?? string.Empty,
                net_id = netId,
                entity_type = entityType ?? string.Empty,
                position = FormatPosition(position),
                square = FormatMapSquare(position),
                image_base64 = Convert.ToBase64String(pngBytes),
                happened_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                metadata = metadata ?? new Dictionary<string, object>(),
            });
        }

        private string FormatMapSquare(Vector3 position)
        {
            var worldSize = Mathf.Max(1000f, TerrainMeta.Size.x);
            var normalizedX = Mathf.Clamp01((position.x + worldSize / 2f) / worldSize);
            var normalizedZ = Mathf.Clamp01(1f - ((position.z + worldSize / 2f) / worldSize));
            var gridSize = 26;

            var xIndex = Mathf.Clamp(Mathf.FloorToInt(normalizedX * gridSize), 0, gridSize - 1);
            var zIndex = Mathf.Clamp(Mathf.FloorToInt(normalizedZ * gridSize), 0, gridSize - 1);
            var letter = ((char)('A' + xIndex)).ToString();

            return $"{letter}{zIndex + 1}";
        }

        private bool TryDeleteDrawingEntity(string netId)
        {
            if (string.IsNullOrWhiteSpace(netId))
            {
                return false;
            }

            uint value;
            if (!uint.TryParse(netId, out value))
            {
                return false;
            }

            var entity = BaseNetworkable.serverEntities?.Find(new NetworkableId(value)) as BaseEntity;
            if (entity == null || entity.IsDestroyed)
            {
                return false;
            }

            entity.Kill();
            return true;
        }

        private string BuildBanKickMessage(string playerId, ActiveBanEntry ban)
        {
            var reason = ResolveReason(playerId, ban.reason);
            if (ban.expires_at > 0)
            {
                return GetMessage(playerId, "Ban.Kick.Temp")
                    .Replace("%TIME%", FormatAbsoluteTime(ban.expires_at))
                    .Replace("%REASON%", reason);
            }

            return GetMessage(playerId, "Ban.Kick.Perm").Replace("%REASON%", reason);
        }

        private string BuildMuteBlockMessage(string playerId, ActiveMuteEntry mute)
        {
            var reason = ResolveReason(playerId, mute.reason);
            if (mute.expires_at > 0)
            {
                return GetMessage(playerId, "Mute.Issued.Chat.Temp")
                    .Replace("%REASON%", reason)
                    .Replace("%TIME%", FormatAbsoluteTime(mute.expires_at));
            }

            return GetMessage(playerId, "Mute.Issued.Chat.Perm").Replace("%REASON%", reason);
        }

        private string BuildKickMessage(string playerId, string reason)
        {
            return string.IsNullOrWhiteSpace(reason)
                ? GetMessage(playerId, "Kick.Admin")
                : GetMessage(playerId, "Kick.Admin.WithReason").Replace("%REASON%", reason);
        }

        private bool CloseConnection(string steamId, string reason)
        {
            var player = BasePlayer.Find(steamId);
            if (player != null && player.IsConnected)
            {
                player.Kick(reason);
                return true;
            }

            var connection = ConnectionAuth.m_AuthConnection.Find(v => v.userid.ToString() == steamId);
            if (connection != null)
            {
                Network.Net.sv.Kick(connection, reason);
                return true;
            }

            var queue = ServerMgr.Instance?.connectionQueue;
            if (queue != null)
            {
                var joining = queue.joining.Find(v => v.userid.ToString() == steamId);
                if (joining != null)
                {
                    Network.Net.sv.Kick(joining, reason);
                    return true;
                }

                var queued = queue.queue.Find(v => v.userid.ToString() == steamId);
                if (queued != null)
                {
                    Network.Net.sv.Kick(queued, reason);
                    return true;
                }
            }

            return false;
        }

        private enum ToastType
        {
            Error = 1,
            Info = 2,
        }

        private void ShowToast(BasePlayer player, string text, ToastType type)
        {
            if (player == null || !player.IsConnected || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var effect = new Effect("assets/bundled/prefabs/fx/notice/item.select.fx.prefab", player, 0, Vector3.zero, Vector3.zero);
            EffectNetwork.Send(effect, player.Connection);
            player.Command("gametip.showtoast", (int) type, text, 1);
        }

        private void DrawReportInterface(BasePlayer player, int page, string search)
        {
            if (player == null || !player.IsConnected)
            {
                return;
            }

            const int lineAmount = 6;
            const int perPage = 18;
            const float lineMargin = 8f;
            var size = (700f - lineMargin * lineAmount) / lineAmount;

            page = Math.Max(page, 0);
            search = NormalizeReportSearch(search);

            var list = BasePlayer.activePlayerList
                .Where(v => v != null)
                .OrderBy(v => v.displayName ?? string.Empty)
                .ToList();

            var filtered = list
                .Where(v => string.IsNullOrWhiteSpace(search)
                    || (v.displayName ?? string.Empty).ToLowerInvariant().Contains(search.ToLowerInvariant())
                    || v.UserIDString.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var currentPage = filtered
                .Skip(page * perPage)
                .Take(perPage)
                .ToList();

            if (currentPage.Count == 0 && page > 0 && string.IsNullOrEmpty(search))
            {
                DrawReportInterface(player, page - 1, search);
                return;
            }

            DestroyReportUi(player);

            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                CursorEnabled = true,
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMax = "0 0" },
                Image = { Color = "0 0 0 0.8", Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat" }
            }, "Overlay", ReportLayer, ReportLayer);

            container.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMax = "0 0" },
                Button = { Color = HexToRustFormat("#343434"), Sprite = "assets/content/ui/ui.background.transparent.radial.psd", Close = ReportLayer },
                Text = { Text = string.Empty }
            }, ReportLayer);

            container.Add(new CuiPanel
            {
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-368 -200", OffsetMax = "368 142" },
                Image = { Color = "1 0 0 0" }
            }, ReportLayer, ReportLayer + ".C", ReportLayer + ".C");

            container.Add(new CuiPanel
            {
                RectTransform = { AnchorMin = "1 0", AnchorMax = "1 1", OffsetMin = "-36 0", OffsetMax = "0 0" },
                Image = { Color = "0 0 1 0" }
            }, ReportLayer + ".C", ReportLayer + ".R");

            var canGoNext = filtered.Count > (page + 1) * perPage;
            var prevPage = Math.Max(page - 1, 0);
            var nextPage = canGoNext ? page + 1 : page;

            container.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.5", OffsetMin = "0 0", OffsetMax = "0 -4" },
                Button = { Color = HexToRustFormat(canGoNext ? "#D0C6BD4D" : "#D0C6BD33"), Command = $"rustmod.report.page {nextPage} {EscapeConsoleArg(search)}" },
                Text = { Text = "↓", Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", FontSize = 24, Color = HexToRustFormat(canGoNext ? "#D0C6BD" : "#D0C6BD4D") }
            }, ReportLayer + ".R", ReportLayer + ".RD");

            container.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0 0.5", AnchorMax = "1 1", OffsetMin = "0 4", OffsetMax = "0 0" },
                Button = { Color = HexToRustFormat(page == 0 ? "#D0C6BD33" : "#D0C6BD4D"), Command = $"rustmod.report.page {prevPage} {EscapeConsoleArg(search)}" },
                Text = { Text = "↑", Align = TextAnchor.MiddleCenter, Font = "robotocondensed-bold.ttf", FontSize = 24, Color = HexToRustFormat(page == 0 ? "#D0C6BD4D" : "#D0C6BD") }
            }, ReportLayer + ".R", ReportLayer + ".RU");

            container.Add(new CuiPanel
            {
                RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-250 8", OffsetMax = "0 43" },
                Image = { Color = HexToRustFormat("#D0C6BD33") }
            }, ReportLayer + ".C", ReportLayer + ".S");

            container.Add(new CuiElement
            {
                Parent = ReportLayer + ".S",
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = string.IsNullOrEmpty(search) ? GetMessage(player.UserIDString, "Report.Panel.Search") : search,
                        FontSize = 14,
                        Font = "robotocondensed-regular.ttf",
                        Color = HexToRustFormat("#D0C6BD80"),
                        Align = TextAnchor.MiddleLeft,
                        Command = "rustmod.report.search",
                        NeedsKeyboard = true,
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "10 0", OffsetMax = "-10 0" }
                }
            });

            container.Add(new CuiPanel
            {
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0.5 1", OffsetMin = "0 7", OffsetMax = "0 47" },
                Image = { Color = "0.8 0.8 0.8 0" }
            }, ReportLayer + ".C", ReportLayer + ".LT");

            container.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "0 0" },
                Text = { Text = GetMessage(player.UserIDString, "Report.Panel.Title"), Font = "robotocondensed-bold.ttf", Color = HexToRustFormat("#D0C6BD"), FontSize = 24, Align = TextAnchor.UpperLeft }
            }, ReportLayer + ".LT");

            container.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "0 0" },
                Text = { Text = filtered.Count == 0 ? GetMessage(player.UserIDString, "Report.Panel.NoPlayers") : GetMessage(player.UserIDString, "Report.Panel.Subtitle"), Font = "robotocondensed-regular.ttf", Color = HexToRustFormat("#D0C6BD4D"), FontSize = 14, Align = TextAnchor.LowerLeft }
            }, ReportLayer + ".LT");

            container.Add(new CuiPanel
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "-40 0" },
                Image = { Color = "0 1 0 0" }
            }, ReportLayer + ".C", ReportLayer + ".L");

            for (var y = 0; y < Math.Max((int)Math.Ceiling(currentPage.Count / (double)lineAmount), 3); y++)
            {
                for (var x = 0; x < lineAmount; x++)
                {
                    var tileIndex = y * lineAmount + x;
                    var target = currentPage.ElementAtOrDefault(tileIndex);
                    var offsetMin = $"{x * size + lineMargin * x} -{(y + 1) * size + lineMargin * y}";
                    var offsetMax = $"{(x + 1) * size + lineMargin * x} -{y * size + lineMargin * y}";

                    if (target == null)
                    {
                        container.Add(new CuiPanel
                        {
                            RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = offsetMin, OffsetMax = offsetMax },
                            Image = { Color = HexToRustFormat("#D0C6BD33") }
                        }, ReportLayer + ".L");
                        continue;
                    }

                    var tileName = ReportLayer + "." + target.UserIDString;
                    container.Add(new CuiPanel
                    {
                        RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = offsetMin, OffsetMax = offsetMax },
                        Image = { Color = HexToRustFormat("#D0C6BD33") }
                    }, ReportLayer + ".L", tileName);

                    container.Add(new CuiElement
                    {
                        Parent = tileName,
                        Components =
                        {
                            new CuiRawImageComponent { SteamId = target.UserIDString, Sprite = "assets/icons/loading.png" },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMax = "0 0" }
                        }
                    });

                    container.Add(new CuiPanel
                    {
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "0 0" },
                        Image = { Sprite = "assets/content/ui/ui.background.transparent.linear.psd", Color = HexToRustFormat("#282828f2") }
                    }, tileName);

                    container.Add(new CuiLabel
                    {
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "6 16", OffsetMax = "0 0" },
                        Text = { Text = CropName(target.displayName ?? target.UserIDString, 14), Align = TextAnchor.LowerLeft, Font = "robotocondensed-bold.ttf", FontSize = 13, Color = HexToRustFormat("#D0C6BD") }
                    }, tileName);

                    container.Add(new CuiLabel
                    {
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "6 5", OffsetMax = "0 0" },
                        Text = { Text = target.UserIDString, Align = TextAnchor.LowerLeft, Font = "robotocondensed-regular.ttf", FontSize = 10, Color = HexToRustFormat("#D0C6BD80") }
                    }, tileName);

                    container.Add(new CuiButton
                    {
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "0 0" },
                        Button = { Color = "0 0 0 0", Command = $"rustmod.report.pick {target.UserIDString} {tileIndex} {page} {EscapeConsoleArg(search)}" },
                        Text = { Text = string.Empty }
                    }, tileName);
                }
            }

            CuiHelper.AddUi(player, container);
        }

        private void DrawReportReasonOverlay(BasePlayer player, string targetId, int tileIndex, int page, string search)
        {
            if (player == null || !player.IsConnected)
            {
                return;
            }

            var target = BasePlayer.Find(targetId) ?? BasePlayer.FindSleeping(targetId);
            if (target == null)
            {
                SendReply(player, GetMessage(player.UserIDString, "Report.TargetNotFound"));
                DrawReportInterface(player, page, search);
                return;
            }

            const int lineAmount = 6;
            const float lineMargin = 8f;
            var size = (700f - lineMargin * lineAmount) / lineAmount;
            var y = Math.Max(tileIndex, 0) / lineAmount;
            var x = Math.Max(tileIndex, 0) % lineAmount;
            var min = $"{x * size + lineMargin * x} -{(y + 1) * size + lineMargin * y}";
            var max = $"{(x + 1) * size + lineMargin * x} -{y * size + lineMargin * y}";
            var leftAlign = x >= 3;

            CuiHelper.DestroyUi(player, ReportLayer + ".T");

            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = min, OffsetMax = max },
                Image = { Color = "0 0 0 1" }
            }, ReportLayer + ".L", ReportLayer + ".T");

            container.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "-500 -500", OffsetMax = "500 500" },
                Button = { Close = ReportLayer + ".T", Color = "0 0 0 1", Sprite = "assets/content/ui/gameui/attackheli/compass/ui.soft.radial.png" },
                Text = { Text = string.Empty }
            }, ReportLayer + ".T");

            container.Add(new CuiButton
            {
                RectTransform = { AnchorMin = $"{(leftAlign ? -1 : 2)} 0", AnchorMax = $"{(leftAlign ? -2 : 3)} 1", OffsetMin = "-500 -500", OffsetMax = "500 500" },
                Button = { Close = ReportLayer + ".T", Color = HexToRustFormat("#343434"), Sprite = "assets/content/ui/gameui/attackheli/compass/ui.soft.radial.png" },
                Text = { Text = string.Empty }
            }, ReportLayer + ".T");

            container.Add(new CuiButton
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "-1111111 -1111111", OffsetMax = "1111111 1111111" },
                Button = { Close = ReportLayer + ".T", Color = "0 0 0 0.5", Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat" },
                Text = { Text = string.Empty }
            }, ReportLayer + ".T");

            container.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = leftAlign ? "0 0" : "1 0", AnchorMax = leftAlign ? "0 1" : "1 1", OffsetMin = leftAlign ? "-350 0" : "20 0", OffsetMax = leftAlign ? "-20 -5" : "350 -5" },
                Text = { Text = GetMessage(player.UserIDString, "Report.Panel.ReasonTitle"), Font = "robotocondensed-bold.ttf", Color = HexToRustFormat("#D0C6BD"), FontSize = 24, Align = leftAlign ? TextAnchor.UpperRight : TextAnchor.UpperLeft }
            }, ReportLayer + ".T");

            container.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = leftAlign ? "0 0" : "1 0", AnchorMax = leftAlign ? "0 1" : "1 1", OffsetMin = leftAlign ? "-250 0" : "20 0", OffsetMax = leftAlign ? "-20 -35" : "250 -35" },
                Text = { Text = GetMessage(player.UserIDString, "Report.Panel.ReasonSubtitle").Replace("%PLAYER%", "<b>" + (target.displayName ?? target.UserIDString) + "</b>"), Font = "robotocondensed-regular.ttf", Color = HexToRustFormat("#D0C6BD80"), FontSize = 14, Align = leftAlign ? TextAnchor.UpperRight : TextAnchor.UpperLeft }
            }, ReportLayer + ".T");

            container.Add(new CuiElement
            {
                Parent = ReportLayer + ".T",
                Components =
                {
                    new CuiRawImageComponent { SteamId = target.UserIDString, Sprite = "assets/icons/loading.png" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMax = "0 0" }
                }
            });

            for (var i = 0; i < (_config.report_ui_reasons?.Count ?? 0); i++)
            {
                var offXMin = (20 + (i * 5)) + i * 80;
                var offXMax = 20 + (i * 5) + (i + 1) * 80;
                container.Add(new CuiButton
                {
                    RectTransform = { AnchorMin = $"{(leftAlign ? 0 : 1)} 0", AnchorMax = $"{(leftAlign ? 0 : 1)} 0", OffsetMin = $"{(leftAlign ? -offXMax : offXMin)} 15", OffsetMax = $"{(leftAlign ? -offXMin : offXMax)} 45" },
                    Button = { FadeIn = 0.4f + i * 0.2f, Color = HexToRustFormat("#D0C6BD4D"), Command = $"rustmod.report.send {target.UserIDString} {i}" },
                    Text = { FadeIn = 0.4f + i * 0.2f, Text = _config.report_ui_reasons[i], Align = TextAnchor.MiddleCenter, Color = HexToRustFormat("#D0C6BD"), Font = "robotocondensed-bold.ttf", FontSize = 16 }
                }, ReportLayer + ".T");
            }

            CuiHelper.AddUi(player, container);
        }

        private void DestroyReportUi(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            CuiHelper.DestroyUi(player, ReportLayer);
        }

        private static BasePlayer GetArgPlayer(ConsoleSystem.Arg arg)
        {
            return arg?.Connection?.player as BasePlayer;
        }

        private static int ToInt(string raw, int fallback)
        {
            int value;
            return int.TryParse(raw, out value) ? value : fallback;
        }

        private static string JoinArgs(string[] args, int startIndex)
        {
            if (args == null || startIndex >= args.Length)
            {
                return string.Empty;
            }

            return string.Join(" ", args.Skip(startIndex).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray()).Trim();
        }

        private static string EscapeConsoleArg(string value)
        {
            value = NormalizeReportSearch(value);
            return string.IsNullOrWhiteSpace(value) ? string.Empty : "\"" + value.Replace("\"", string.Empty) + "\"";
        }

        private static string NormalizeReportSearch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            value = value.Trim();
            if (string.Equals(value, "\"\"", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return value.Trim('"').Trim();
        }

        private static string CropName(string value, int maxLength)
        {
            value = string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
            return value.Length > maxLength ? value.Substring(0, maxLength - 2) + ".." : value;
        }

        private static string HexToRustFormat(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return "1 1 1 1";
            }

            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6 && hex.Length != 8)
            {
                return "1 1 1 1";
            }

            byte a = 255;
            if (hex.Length == 8)
            {
                a = Convert.ToByte(hex.Substring(6, 2), 16);
            }

            var r = Convert.ToByte(hex.Substring(0, 2), 16);
            var g = Convert.ToByte(hex.Substring(2, 2), 16);
            var b = Convert.ToByte(hex.Substring(4, 2), 16);

            return $"{r / 255f:0.###} {g / 255f:0.###} {b / 255f:0.###} {a / 255f:0.###}";
        }

        private void RegisterReportCommands()
        {
            if (_config?.report_ui_commands == null)
            {
                return;
            }

            foreach (var alias in _config.report_ui_commands
                         .Where(v => !string.IsNullOrWhiteSpace(v))
                         .Select(v => v.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cmd.AddChatCommand(alias, this, nameof(CmdChatReport));
            }

        }

        private bool CanSendReport(BasePlayer player, bool notify)
        {
            if (player == null)
            {
                return false;
            }

            var now = CurrentTime();
            double cooldownUntil;
            if (_reportCooldowns.TryGetValue(player.userID, out cooldownUntil) && cooldownUntil > now)
            {
                if (notify)
                {
                    SendReply(player, GetMessage(player.UserIDString, "Report.Cooldown")
                        .Replace("%SECONDS%", Math.Ceiling(cooldownUntil - now).ToString("0")));
                }

                return false;
            }

            return true;
        }

        private void TouchReportCooldown(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            _reportCooldowns[player.userID] = CurrentTime() + _config.report_ui_cooldown;
        }

        private BasePlayer FindReportTarget(BasePlayer initiator, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            raw = raw.Trim();

            var byId = BasePlayer.Find(raw) ?? BasePlayer.FindSleeping(raw);
            if (byId != null)
            {
                return byId;
            }

            var needle = raw.ToLowerInvariant();
            return BasePlayer.activePlayerList.FirstOrDefault(v => (v.displayName ?? string.Empty).ToLowerInvariant().Contains(needle));
        }

        private string ResolveReportReason(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || _config?.report_ui_reasons == null)
            {
                return null;
            }

            var needle = raw.Trim();
            return _config.report_ui_reasons.FirstOrDefault(v => string.Equals(v, needle, StringComparison.OrdinalIgnoreCase));
        }

        private void SendPlayerReport(BasePlayer initiator, BasePlayer target, string reason, string message, string source, string reportType, string subject, bool wasCheckedRecently)
        {
            SendPlayerReport(
                initiator: initiator,
                targetSteamId: target?.UserIDString,
                targetName: target?.displayName,
                reason: reason,
                message: message,
                source: source,
                reportType: reportType,
                subject: subject,
                wasCheckedRecently: wasCheckedRecently,
                targetOnline: target != null && target.IsConnected
            );
        }

        private void SendPlayerReport(BasePlayer initiator, string targetSteamId, string targetName, string reason, string message, string source, string reportType, string subject, bool wasCheckedRecently, bool targetOnline)
        {
            if (initiator == null || string.IsNullOrWhiteSpace(targetSteamId))
            {
                return;
            }

            QueueReport(new PluginReportDto
            {
                initiator_steam_id = initiator.UserIDString,
                initiator_name = initiator.displayName ?? string.Empty,
                target_steam_id = targetSteamId,
                target_name = targetName ?? targetSteamId,
                sub_targets_steam_ids = new List<string>(),
                reason = string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason,
                message = message ?? string.Empty,
                source = source ?? "unknown",
                report_type = reportType ?? string.Empty,
                subject = subject ?? string.Empty,
                was_checked_recently = wasCheckedRecently,
                happened_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                metadata = new Dictionary<string, object>
                {
                    ["initiator_online"] = initiator.IsConnected,
                    ["target_online"] = targetOnline,
                },
            });

            TouchReportCooldown(initiator);
        }

        private void QueueReport(PluginReportDto report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.initiator_steam_id))
            {
                return;
            }

            _reportQueue.Add(report);

            if (_reportQueue.Count >= 20)
            {
                FlushReportQueue();
            }
        }

        private string GetMessage(string playerId, string key)
        {
            return lang.GetMessage(key, this, playerId);
        }

        private string ResolveReason(string playerId, string reason)
        {
            return string.IsNullOrWhiteSpace(reason)
                ? GetMessage(playerId, "Reason.Unknown")
                : reason;
        }

        private static string FormatAbsoluteTime(long unixSeconds)
        {
            return DateTimeOffset
                .FromUnixTimeSeconds(unixSeconds)
                .ToOffset(TimeSpan.FromHours(3))
                .ToString("dd.MM.yyyy HH:mm");
        }

        private static string FormatPosition(Vector3 position)
        {
            return $"({position.x:0.##}, {position.y:0.##}, {position.z:0.##})";
        }

        private static double CurrentTime()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private Dictionary<string, string> BuildHeaders()
        {
            return new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                [HeaderToken] = _config.token ?? string.Empty,
                [HeaderServerUid] = BuildServerUid(),
            };
        }

        private bool HasConfiguredToken()
        {
            return !string.IsNullOrWhiteSpace(_config?.token) && !_config.token.Contains("change-me");
        }

        private bool HandleAuthenticationFailure(int code, string response, string endpoint)
        {
            if (code != 401 && code != 403 && code != 404 && code != 409)
            {
                return false;
            }

            var hasToken = !string.IsNullOrWhiteSpace(_config?.token) && !_config.token.Contains("change-me");
            if (!hasToken)
            {
                return false;
            }

            _config.token = "change-me-very-long-token";
            SaveConfig();

            var hint = string.IsNullOrWhiteSpace(_config.verification_code)
                ? "Install a freshly downloaded RustMod.cs for the new onboarding slot, then run rustmod.verify <code>."
                : "Run rustmod.verify <code> again or wait for bootstrap after reinstalling the personalized plugin.";

            PrintWarning($"RustMod token was rejected on {endpoint} (HTTP {code}). Falling back to bootstrap mode. {hint}");
            DebugLog($"Authentication failed on {endpoint} (HTTP {code}). Token reset. Response: {response}");

            timer.Once(1f, SendBootstrapHeartbeat);

            return true;
        }

        private Dictionary<string, string> BuildVerificationHeaders()
        {
            return new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                [HeaderServerUid] = BuildServerUid(),
            };
        }

        private string BuildUrl(string path)
        {
            var baseUrl = (_config.api_base_url ?? string.Empty).Trim().TrimEnd('/');
            var endpoint = path.StartsWith("/") ? path : "/" + path;

            return baseUrl + endpoint;
        }

        private ServerSnapshot BuildServerSnapshot()
        {
            var queue = ServerMgr.Instance?.connectionQueue;
            var waiting = (queue?.queue?.Count ?? 0) + (queue?.joining?.Count ?? 0);
            var serverUid = BuildServerUid();

            return new ServerSnapshot
            {
                uid = serverUid,
                name = ConVar.Server.hostname ?? string.Empty,
                hostname = ConVar.Server.hostname ?? string.Empty,
                ip = ConVar.Server.ip ?? string.Empty,
                port = ConVar.Server.port,
                map = SteamServer.MapName ?? ConVar.Server.level ?? string.Empty,
                description = (ConVar.Server.description ?? string.Empty).Trim(),
                plugin_version = Version.ToString(),
                game_version = Protocol.printable.ToString(),
                protocol = Protocol.printable.ToString(),
                online = BasePlayer.activePlayerList.Count + waiting,
                max_players = ConVar.Server.maxplayers,
                meta = new Dictionary<string, object>
                {
                    ["branch"] = ConVar.Server.branch ?? string.Empty,
                    ["queue_waiting"] = waiting,
                    ["reserved_slots"] = queue?.ReservedCount ?? 0,
                },
            };
        }

        private string BuildServerUid()
        {
            if (!string.IsNullOrWhiteSpace(_config.server_uid))
            {
                return NormalizeServerUid(_config.server_uid);
            }

            var port = ConVar.Server.port;
            var ip = ConVar.Server.ip ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0")
            {
                return NormalizeServerUid($"endpoint:{ip}:{port}");
            }

            var hostname = ConVar.Server.hostname ?? "server";
            return NormalizeServerUid($"host:{hostname}:{port}");
        }

        #endregion

        #region Utils

        private static string StripIpPort(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                return string.Empty;
            }

            var idx = ipAddress.LastIndexOf(':');
            if (idx <= 0)
            {
                return ipAddress;
            }

            return ipAddress.Substring(0, idx);
        }

        private static string GetWeaponName(HitInfo info)
        {
            if (info?.Weapon != null && !string.IsNullOrWhiteSpace(info.Weapon.ShortPrefabName))
            {
                return info.Weapon.ShortPrefabName;
            }

            if (info?.WeaponPrefab != null && !string.IsNullOrWhiteSpace(info.WeaponPrefab.ShortPrefabName))
            {
                return info.WeaponPrefab.ShortPrefabName;
            }

            if (info?.WeaponPrefab != null && !string.IsNullOrWhiteSpace(info.WeaponPrefab.name))
            {
                return info.WeaponPrefab.name;
            }

            return "unknown";
        }

        private static string NormalizeServerUid(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "server";
            }

            var normalizedChars = value
                .Trim()
                .ToLowerInvariant()
                .Select(ch =>
                {
                    if ((ch >= 'a' && ch <= 'z') ||
                        (ch >= '0' && ch <= '9') ||
                        ch == '-' ||
                        ch == '_' ||
                        ch == ':' ||
                        ch == '.')
                    {
                        return ch;
                    }

                    return '-';
                })
                .ToArray();

            var normalized = new string(normalizedChars).Trim('-');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "server";
            }

            return normalized.Length > 120 ? normalized.Substring(0, 120) : normalized;
        }

        private void DebugLog(string message)
        {
            if (_config.debug)
            {
                Puts("[debug] " + message);
            }
        }

        #endregion
    }
}
