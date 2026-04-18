using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("GameZoneReportUI", "GameZoneOne", "1.4.0")]
    [Description("In-game player reports with CUI, environment snapshot, Discord webhook and optional collector.")]
    public class GameZoneReportUI : RustPlugin
    {
        private const string UiRoot = "GZReportUI.Root";
        private const string UiDropdown = "GZReportUI.Dropdown";
        private const string UiShotUrlsBorder = "GZReportUI.ShotUrlsBorder";
        private const string UiShotUrlsFill = "GZReportUI.ShotUrlsFill";
        private const string UiDetailsBorder = "GZReportUI.DetailsBorder";
        private const string UiDetailsFill = "GZReportUI.DetailsFill";
        private const string PermUse = "gamezonereportui.use";
        private const string PermAdmin = "gamezonereportui.admin";

        private const string InputBorderColor = "0.32 0.55 0.82 0.92";
        private const string InputFillColor = "0.07 0.08 0.11 1";
        private const string InputTextColor = "0.93 0.94 0.97 1";

        private PluginConfig _config;
        private readonly Dictionary<ulong, ReportDraft> _drafts = new Dictionary<ulong, ReportDraft>();
        private readonly Dictionary<ulong, double> _cooldownUntil = new Dictionary<ulong, double>();

        private ReportHistoryFile _reportHistory;
        private const string HistoryDataPath = "GameZoneReportUI/report_history";

        private sealed class ReportDraft
        {
            public string Category = string.Empty;
            public ulong? TargetSteamId;
            public string TargetName = string.Empty;
            public string Details = string.Empty;
            public string ScreenshotUrls = string.Empty;
            public int PlayerPage;
            public bool ReasonDropdownOpen;
        }

        private sealed class ReportHistoryFile
        {
            public List<ReportHistoryEntry> Entries = new List<ReportHistoryEntry>();
        }

        private sealed class ReportHistoryEntry
        {
            public string TraceId;
            public string UtcIso;
            public string ReporterName;
            public string ReporterId;
            public string Category;
            public string TargetShort;
            public string DetailsPreview;
        }

        private sealed class PluginConfig
        {
            [JsonProperty("Open Command")]
            public string OpenCommand = "report";

            [JsonProperty("Discord Webhook URL (empty = disabled)")]
            public string DiscordWebhookUrl = string.Empty;

            [JsonProperty("Optional: Collector URL (e.g. MongoBridge)")]
            public string CollectorUrl = string.Empty;

            [JsonProperty("Optional: Collector API Key")]
            public string CollectorApiKey = string.Empty;

            [JsonProperty("Optional: Server ID (Collector)")]
            public string ServerId = string.Empty;

            [JsonProperty("Optional: Instance Name (Collector)")]
            public string InstanceName = string.Empty;

            [JsonProperty("Send Collector Event")]
            public bool SendCollectorEvent = true;

            [JsonProperty("Report Categories (Dropdown)")]
            public List<string> Categories = new List<string>
            {
                "Cheating / Hacking",
                "Griefing / RDM",
                "Insults / Toxic",
                "Bug / Technical",
                "Other"
            };

            [JsonProperty("Players Per Page (Online Selection)")]
            public int PlayersPerPage = 6;

            [JsonProperty("Minimum Description Length")]
            public int MinDetailsLength = 15;

            [JsonProperty("Cooldown Seconds")]
            public float CooldownSeconds = 120f;

            [JsonProperty("Max Description Characters")]
            public int MaxDetailsChars = 500;

            [JsonProperty("Max Evidence URL Characters")]
            public int MaxScreenshotUrlChars = 600;

            [JsonProperty("Enable Surrounding Snapshot on Submit")]
            public bool EnableSurroundSnapshot = true;

            [JsonProperty("Snapshot Radius (meters)")]
            public float SnapshotRadius = 50f;

            [JsonProperty("Snapshot: Max Items Per Inventory Container")]
            public int SnapshotMaxItemsPerContainer = 48;

            [JsonProperty("Snapshot: Max Vehicle / Mount Entries")]
            public int SnapshotMaxVehicles = 40;

            [JsonProperty("Save Snapshot as JSON File (oxide/data)")]
            public bool SaveSnapshotDataFile = true;

            [JsonProperty("Require Target Player")]
            public bool RequireTargetPlayer = false;

            [JsonProperty("Auto-Grant Use Permission to Oxide Default Group")]
            public bool AutoGrantUseToDefaultGroup = true;

            [JsonProperty("Admin: Disable Report List and /reportadmin")]
            public bool DisableAdminReportFeatures = false;

            [JsonProperty("Admin: Max History Entries")]
            public int MaxReportHistoryEntries = 40;

            [JsonProperty("History In-Memory Only (no oxide/data)")]
            public bool ReportHistoryMemoryOnly = false;
        }

        private sealed class EventActorDto
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("steamId")] public string SteamId;
            [JsonProperty("authLevel")] public int? AuthLevel;
        }

        private sealed class RustEventDto
        {
            [JsonProperty("eventType")] public string EventType;
            [JsonProperty("eventCategory")] public string EventCategory;
            [JsonProperty("serverId")] public string ServerId;
            [JsonProperty("instanceName")] public string InstanceName;
            [JsonProperty("timestamp")] public string Timestamp;
            [JsonProperty("source")] public string Source;
            [JsonProperty("traceId")] public string TraceId;
            [JsonProperty("actor")] public EventActorDto Actor;
            [JsonProperty("target")] public EventActorDto Target;
            [JsonProperty("payload")] public Dictionary<string, object> Payload;
            [JsonProperty("raw")] public object Raw;
        }

        private sealed class DiscordEmbedImage
        {
            [JsonProperty("url")] public string Url;
        }

        private sealed class DiscordEmbedFooter
        {
            [JsonProperty("text")] public string Text;
        }

        private sealed class DiscordEmbed
        {
            [JsonProperty("title")] public string Title;
            [JsonProperty("color")] public int Color;
            [JsonProperty("fields")] public List<DiscordField> Fields;
            [JsonProperty("timestamp")] public string Timestamp;
            [JsonProperty("image", NullValueHandling = NullValueHandling.Ignore)]
            public DiscordEmbedImage Image;
            [JsonProperty("footer", NullValueHandling = NullValueHandling.Ignore)]
            public DiscordEmbedFooter Footer;
        }

        private sealed class DiscordField
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("value")] public string Value;
            [JsonProperty("inline")] public bool Inline;
        }

        private sealed class DiscordWebhookBody
        {
            [JsonProperty("embeds")] public List<DiscordEmbed> Embeds;
        }

        #region Lang

        private string T(string key, string userId = null, params object[] args)
        {
            var msg = lang.GetMessage(key, this, userId);
            return args.Length > 0 ? string.Format(msg, args) : msg;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                // UI
                ["UI.Title"]              = "Report Player",
                ["UI.CategoryLabel"]      = "Report Reason",
                ["UI.CategoryPlaceholder"] = "— select —",
                ["UI.TargetRequired"]     = "Reported Player (required)",
                ["UI.TargetOptional"]     = "Reported Player (optional)",
                ["UI.ClearSelection"]     = "Clear Selection",
                ["UI.Page"]               = "Page {0}/{1}",
                ["UI.EvidenceLabel"]      = "Evidence: Image/Video Links (optional)",
                ["UI.EvidenceHint"]       = "Note: The server cannot take screenshots. Upload them to Discord and paste the links here.",
                ["UI.DescriptionLabel"]   = "Description (min. {0} chars)",
                ["UI.SnapshotHint"]       = "On submit, a surrounding snapshot (~{0} m) with players, inventory and vehicles will be captured.",
                ["UI.Submit"]             = "Submit",
                ["UI.DropdownTitle"]      = "Select Reason",
                // Chat
                ["Chat.NoPermission"]        = "<color=#ff6b6b>No permission for /{0}.</color>",
                ["Chat.UseWithoutArgs"]      = "<color=#aaaaaa>Use /{0} without text — the form opens in the UI.</color>",
                ["Chat.NoDestination"]       = "<color=#ff6b6b>Report system: Neither webhook nor collector configured (see GameZoneReportUI.json).</color>",
                ["Chat.Cooldown"]            = "<color=#ff6b6b>Cooldown: ~{0} seconds remaining.</color>",
                ["Chat.SelectCategory"]      = "<color=#ff6b6b>Please select a report reason.</color>",
                ["Chat.SelectTarget"]        = "<color=#ff6b6b>Please select a reported player.</color>",
                ["Chat.DescriptionTooShort"] = "<color=#ff6b6b>Description too short (minimum {0} characters).</color>",
                ["Chat.Submitted"]           = "<color=#7bed9f>Report submitted. Thank you!</color>",
                ["Chat.AdminNoReports"]      = "<color=#aaaaaa>No reports stored yet.</color>",
                ["Chat.AdminHeader"]         = "<color=#7bed9f>Latest reports (max. {0}):</color>",
                ["Chat.AdminEntry"]          = "<color=#aaaaaa>   Affected:</color> {0} — {1}",
                ["Chat.AdminDisabled"]       = "<color=#ff6b6b>Report admin list is disabled (config).</color>",
                ["Chat.AdminNoPermission"]   = "<color=#ff6b6b>No permission (gamezonereportui.admin or admin).</color>",
                // Discord embed
                ["Discord.EmbedTitle"]       = "New In-Game Report",
                ["Discord.TraceField"]       = "Trace / File",
                ["Discord.ServerField"]      = "Server",
                ["Discord.ReporterField"]    = "Reporter",
                ["Discord.ReasonField"]      = "Reason",
                ["Discord.AffectedField"]    = "Affected",
                ["Discord.SnapshotField"]    = "Surrounding Snapshot",
                ["Discord.EvidenceField"]    = "Evidence Links",
                ["Discord.DescriptionField"] = "Description",
                // Snapshot
                ["Snapshot.Summary"]         = "Players in radius: {0}, vehicles/mounts: {1}",
                ["Snapshot.Error"]           = "Error: {0}",
            }, this);
        }

        #endregion

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
            }
            catch
            {
                PrintWarning("Config could not be read, loading defaults.");
                _config = new PluginConfig();
            }

            if (_config.Categories == null || _config.Categories.Count == 0)
                _config.Categories = new PluginConfig().Categories;

            if (_config.PlayersPerPage < 3) _config.PlayersPerPage = 3;
            if (_config.PlayersPerPage > 20) _config.PlayersPerPage = 20;
            if (_config.SnapshotRadius < 5f) _config.SnapshotRadius = 5f;
            if (_config.SnapshotRadius > 300f) _config.SnapshotRadius = 300f;
            if (_config.SnapshotMaxItemsPerContainer < 8) _config.SnapshotMaxItemsPerContainer = 8;
            if (_config.SnapshotMaxVehicles < 5) _config.SnapshotMaxVehicles = 5;
            if (_config.MaxReportHistoryEntries < 5) _config.MaxReportHistoryEntries = 5;
            if (_config.MaxReportHistoryEntries > 500) _config.MaxReportHistoryEntries = 500;
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            var cmd = string.IsNullOrWhiteSpace(_config.OpenCommand) ? "report" : _config.OpenCommand.Trim();
            AddCovalenceCommand(cmd, nameof(CmdOpenReport));
        }

        private void OnServerInitialized()
        {
            if (_config.AutoGrantUseToDefaultGroup)
            {
                const string defaultGroup = "default";
                if (!permission.GroupExists(defaultGroup))
                {
                    PrintWarning(
                        $"GameZoneReportUI: Oxide group '{defaultGroup}' not found — players may lack {PermUse}. Create it or grant the permission manually.");
                }
                else if (!permission.GroupHasPermission(defaultGroup, PermUse))
                {
                    permission.GrantGroupPermission(defaultGroup, PermUse, this);
                    Puts($"GameZoneReportUI: Granted {PermUse} to group '{defaultGroup}' (players can use /{_config.OpenCommand ?? "report"}).");
                }
            }

            var hasDiscord = !string.IsNullOrWhiteSpace(_config.DiscordWebhookUrl);
            var hasCollector = _config.SendCollectorEvent && !string.IsNullOrWhiteSpace(_config.CollectorUrl);
            if (!hasDiscord && !hasCollector)
            {
                PrintWarning(
                    "GameZoneReportUI: Neither Discord Webhook nor Collector URL configured — reports cannot be delivered. Edit GameZoneReportUI.json.");
            }

            LoadReportHistory();
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(p, UiRoot);
                CuiHelper.DestroyUi(p, UiDropdown);
            }

            _drafts.Clear();
            _cooldownUntil.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _drafts.Remove(player.userID);
            _cooldownUntil.Remove(player.userID);
        }

        private void CmdOpenReport(IPlayer iPlayer, string command, string[] args)
        {
            var player = iPlayer?.Object as BasePlayer;
            if (player == null) return;

            if (!permission.UserHasPermission(player.UserIDString, PermUse) && !player.IsAdmin)
            {
                player.ChatMessage(T("Chat.NoPermission", player.UserIDString, _config.OpenCommand ?? "report"));
                return;
            }

            if (args != null && args.Length > 0)
                player.ChatMessage(T("Chat.UseWithoutArgs", player.UserIDString, _config.OpenCommand ?? "report"));

            OpenOrRefreshUi(player, true);
        }

        private void OpenOrRefreshUi(BasePlayer player, bool resetDraft)
        {
            if (!_drafts.TryGetValue(player.userID, out var draft))
                _drafts[player.userID] = draft = new ReportDraft();

            if (resetDraft)
            {
                draft.Category = _config.Categories.Count > 0 ? _config.Categories[0] : string.Empty;
                draft.TargetSteamId = null;
                draft.TargetName = string.Empty;
                draft.Details = string.Empty;
                draft.ScreenshotUrls = string.Empty;
                draft.PlayerPage = 0;
                draft.ReasonDropdownOpen = false;
            }

            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.DestroyUi(player, UiDropdown);

            var uid = player.UserIDString;
            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.1 0.97" },
                RectTransform = { AnchorMin = "0.18 0.06", AnchorMax = "0.82 0.94" },
                CursorEnabled = true
            }, "Overlay", UiRoot);

            c.Add(new CuiLabel
            {
                Text = { Text = T("UI.Title", uid), FontSize = 22, Align = TextAnchor.MiddleLeft, Color = "0.95 0.95 1 1" },
                RectTransform = { AnchorMin = "0.03 0.91", AnchorMax = "0.55 0.98" }
            }, UiRoot);

            c.Add(new CuiButton
            {
                Button = { Color = "0.45 0.15 0.15 0.95", Command = "reportui.close", FadeIn = 0f },
                RectTransform = { AnchorMin = "0.9 0.92", AnchorMax = "0.97 0.98" },
                Text = { Text = "X", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UiRoot);

            // Category dropdown
            c.Add(new CuiLabel
            {
                Text = { Text = T("UI.CategoryLabel", uid), FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.75 0.78 0.9 1" },
                RectTransform = { AnchorMin = "0.03 0.84", AnchorMax = "0.28 0.89" }
            }, UiRoot);

            var catLabel = string.IsNullOrEmpty(draft.Category) ? T("UI.CategoryPlaceholder", uid) : draft.Category;
            if (catLabel.Length > 42) catLabel = catLabel.Substring(0, 40) + "…";

            c.Add(new CuiButton
            {
                Button = { Color = "0.2 0.22 0.3 0.95", Command = "reportui.ddtoggle", FadeIn = 0f },
                RectTransform = { AnchorMin = "0.28 0.835", AnchorMax = "0.92 0.895" },
                Text = { Text = catLabel + "   ▼", FontSize = 13, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" }
            }, UiRoot);

            // Target player
            var targetLabel = _config.RequireTargetPlayer
                ? T("UI.TargetRequired", uid)
                : T("UI.TargetOptional", uid);

            c.Add(new CuiLabel
            {
                Text = { Text = targetLabel, FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.75 0.78 0.9 1" },
                RectTransform = { AnchorMin = "0.03 0.745", AnchorMax = "0.55 0.8" }
            }, UiRoot);

            c.Add(new CuiButton
            {
                Button = { Color = "0.25 0.25 0.3 0.95", Command = "reportui.clearplayer", FadeIn = 0f },
                RectTransform = { AnchorMin = "0.58 0.75", AnchorMax = "0.78 0.795" },
                Text = { Text = T("UI.ClearSelection", uid), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UiRoot);

            var online = BasePlayer.activePlayerList
                .Where(p => p != null && p.userID != player.userID)
                .OrderBy(p => p.displayName)
                .ToList();

            int perPage = _config.PlayersPerPage;
            int pages = Mathf.Max(1, Mathf.CeilToInt(online.Count / (float)perPage));
            if (draft.PlayerPage >= pages) draft.PlayerPage = pages - 1;
            if (draft.PlayerPage < 0) draft.PlayerPage = 0;

            var slice = online.Skip(draft.PlayerPage * perPage).Take(perPage).ToList();
            float py = 0.68f;
            foreach (var other in slice)
            {
                bool pick = draft.TargetSteamId == other.userID;
                c.Add(new CuiButton
                {
                    Button =
                    {
                        Color = pick ? "0.3 0.35 0.55 0.95" : "0.18 0.19 0.24 0.95",
                        Command = "reportui.pick " + other.userID,
                        FadeIn = 0f
                    },
                    RectTransform = { AnchorMin = $"0.03 {py:F3}", AnchorMax = $"0.48 {py + 0.048f:F3}" },
                    Text =
                    {
                        Text = other.displayName + "  (" + other.userID + ")",
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = "1 1 1 1"
                    }
                }, UiRoot);
                py -= 0.052f;
            }

            if (pages > 1)
            {
                c.Add(new CuiButton
                {
                    Button = { Color = "0.22 0.22 0.28 0.95", Command = "reportui.ppage -1", FadeIn = 0f },
                    RectTransform = { AnchorMin = "0.5 0.68", AnchorMax = "0.56 0.73" },
                    Text = { Text = "◀", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, UiRoot);
                c.Add(new CuiButton
                {
                    Button = { Color = "0.22 0.22 0.28 0.95", Command = "reportui.ppage 1", FadeIn = 0f },
                    RectTransform = { AnchorMin = "0.58 0.68", AnchorMax = "0.64 0.73" },
                    Text = { Text = "▶", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, UiRoot);
                c.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = T("UI.Page", uid, draft.PlayerPage + 1, pages),
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = "0.8 0.8 0.85 1"
                    },
                    RectTransform = { AnchorMin = "0.66 0.68", AnchorMax = "0.95 0.73" }
                }, UiRoot);
            }

            // Evidence links
            c.Add(new CuiLabel
            {
                Text = { Text = T("UI.EvidenceLabel", uid), FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.75 0.78 0.9 1" },
                RectTransform = { AnchorMin = "0.03 0.555", AnchorMax = "0.55 0.6" }
            }, UiRoot);

            c.Add(new CuiLabel
            {
                Text = { Text = T("UI.EvidenceHint", uid), FontSize = 11, Align = TextAnchor.UpperLeft, Color = "0.65 0.68 0.75 1" },
                RectTransform = { AnchorMin = "0.52 0.52", AnchorMax = "0.97 0.6" }
            }, UiRoot);

            c.Add(new CuiPanel
            {
                Image = { Color = InputBorderColor },
                RectTransform = { AnchorMin = "0.026 0.456", AnchorMax = "0.974 0.548" }
            }, UiRoot, UiShotUrlsBorder);
            c.Add(new CuiPanel
            {
                Image = { Color = InputFillColor },
                RectTransform = { AnchorMin = "0.006 0.08", AnchorMax = "0.994 0.92" }
            }, UiShotUrlsBorder, UiShotUrlsFill);
            c.Add(new CuiElement
            {
                Name = "ShotUrlsInput",
                Parent = UiShotUrlsFill,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Align = TextAnchor.UpperLeft,
                        CharsLimit = _config.MaxScreenshotUrlChars,
                        Command = "reportui.shoturls ",
                        FontSize = 12,
                        IsPassword = false,
                        Text = draft.ScreenshotUrls ?? string.Empty,
                        NeedsKeyboard = true,
                        Color = InputTextColor
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.03 0.08", AnchorMax = "0.97 0.92" }
                }
            });

            // Description
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = T("UI.DescriptionLabel", uid, _config.MinDetailsLength),
                    FontSize = 14,
                    Align = TextAnchor.MiddleLeft,
                    Color = "0.75 0.78 0.9 1"
                },
                RectTransform = { AnchorMin = "0.03 0.405", AnchorMax = "0.7 0.445" }
            }, UiRoot);

            c.Add(new CuiPanel
            {
                Image = { Color = InputBorderColor },
                RectTransform = { AnchorMin = "0.026 0.138", AnchorMax = "0.974 0.398" }
            }, UiRoot, UiDetailsBorder);
            c.Add(new CuiPanel
            {
                Image = { Color = InputFillColor },
                RectTransform = { AnchorMin = "0.006 0.04", AnchorMax = "0.994 0.96" }
            }, UiDetailsBorder, UiDetailsFill);
            c.Add(new CuiElement
            {
                Name = "DetailsInput",
                Parent = UiDetailsFill,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Align = TextAnchor.UpperLeft,
                        CharsLimit = _config.MaxDetailsChars,
                        Command = "reportui.details ",
                        FontSize = 13,
                        IsPassword = false,
                        Text = draft.Details ?? string.Empty,
                        NeedsKeyboard = true,
                        Color = InputTextColor
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.02 0.04", AnchorMax = "0.98 0.96" }
                }
            });

            if (_config.EnableSurroundSnapshot)
            {
                c.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = T("UI.SnapshotHint", uid,
                            _config.SnapshotRadius.ToString(CultureInfo.InvariantCulture)),
                        FontSize = 11,
                        Align = TextAnchor.MiddleLeft,
                        Color = "0.55 0.7 0.55 1"
                    },
                    RectTransform = { AnchorMin = "0.03 0.08", AnchorMax = "0.97 0.125" }
                }, UiRoot);
            }

            c.Add(new CuiButton
            {
                Button = { Color = "0.2 0.45 0.28 0.95", Command = "reportui.submit", FadeIn = 0f },
                RectTransform = { AnchorMin = "0.03 0.02", AnchorMax = "0.32 0.075" },
                Text = { Text = T("UI.Submit", uid), FontSize = 15, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UiRoot);

            CuiHelper.AddUi(player, c);

            if (draft.ReasonDropdownOpen)
                DrawReasonDropdown(player);
        }

        private void DrawReasonDropdown(BasePlayer player)
        {
            var uid = player.UserIDString;
            var dc = new CuiElementContainer();

            dc.Add(new CuiPanel
            {
                Image = { Color = "0.05 0.05 0.08 0.92" },
                RectTransform = { AnchorMin = "0.32 0.35", AnchorMax = "0.68 0.82" },
                CursorEnabled = true
            }, "Overlay", UiDropdown);

            dc.Add(new CuiLabel
            {
                Text = { Text = T("UI.DropdownTitle", uid), FontSize = 16, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.04 0.88", AnchorMax = "0.7 0.97" }
            }, UiDropdown);

            dc.Add(new CuiButton
            {
                Button = { Color = "0.35 0.15 0.15 0.95", Command = "reportui.ddclose", FadeIn = 0f },
                RectTransform = { AnchorMin = "0.82 0.88", AnchorMax = "0.96 0.97" },
                Text = { Text = "×", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UiDropdown);

            float y = 0.82f;
            foreach (var cat in _config.Categories)
            {
                var safe = Uri.EscapeDataString(cat);
                dc.Add(new CuiButton
                {
                    Button = { Color = "0.18 0.2 0.26 0.95", Command = "reportui.cat " + safe, FadeIn = 0f },
                    RectTransform = { AnchorMin = $"0.04 {y - 0.09f:F3}", AnchorMax = $"0.96 {y:F3}" },
                    Text =
                    {
                        Text = cat.Length > 48 ? cat.Substring(0, 46) + "…" : cat,
                        FontSize = 13,
                        Align = TextAnchor.MiddleLeft,
                        Color = "1 1 1 1"
                    }
                }, UiDropdown);
                y -= 0.095f;
                if (y < 0.12f) break;
            }

            CuiHelper.AddUi(player, dc);
        }

        [ConsoleCommand("reportui.close")]
        private void CmdUiClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.DestroyUi(player, UiDropdown);
        }

        [ConsoleCommand("reportui.ddtoggle")]
        private void CmdDdToggle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !CanUse(player)) return;
            if (!_drafts.TryGetValue(player.userID, out var d))
                _drafts[player.userID] = d = new ReportDraft();
            d.ReasonDropdownOpen = !d.ReasonDropdownOpen;
            OpenOrRefreshUi(player, false);
        }

        [ConsoleCommand("reportui.ddclose")]
        private void CmdDdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !CanUse(player)) return;
            if (!_drafts.TryGetValue(player.userID, out var d))
                _drafts[player.userID] = d = new ReportDraft();
            d.ReasonDropdownOpen = false;
            OpenOrRefreshUi(player, false);
        }

        [ConsoleCommand("reportui.cat")]
        private void CmdCat(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 1 || !CanUse(player)) return;

            var cat = Uri.UnescapeDataString(string.Join(" ", arg.Args));
            if (!_drafts.TryGetValue(player.userID, out var d))
                _drafts[player.userID] = d = new ReportDraft();
            d.Category = cat;
            d.ReasonDropdownOpen = false;
            OpenOrRefreshUi(player, false);
        }

        [ConsoleCommand("reportui.pick")]
        private void CmdPick(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 1 || !CanUse(player)) return;
            if (!ulong.TryParse(arg.Args[0], out var sid)) return;

            var target = BasePlayer.activePlayerList.FirstOrDefault(p => p != null && p.userID == sid);
            if (!_drafts.TryGetValue(player.userID, out var d))
                _drafts[player.userID] = d = new ReportDraft();
            d.TargetSteamId = sid;
            d.TargetName = target != null ? target.displayName : sid.ToString();
            OpenOrRefreshUi(player, false);
        }

        [ConsoleCommand("reportui.clearplayer")]
        private void CmdClearPlayer(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !CanUse(player)) return;
            if (!_drafts.TryGetValue(player.userID, out var d))
                _drafts[player.userID] = d = new ReportDraft();
            d.TargetSteamId = null;
            d.TargetName = string.Empty;
            OpenOrRefreshUi(player, false);
        }

        [ConsoleCommand("reportui.ppage")]
        private void CmdPlayerPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 1 || !CanUse(player)) return;
            if (!int.TryParse(arg.Args[0], out var delta)) return;
            if (!_drafts.TryGetValue(player.userID, out var d))
                _drafts[player.userID] = d = new ReportDraft();
            d.PlayerPage += delta;
            if (d.PlayerPage < 0) d.PlayerPage = 0;
            OpenOrRefreshUi(player, false);
        }

        [ConsoleCommand("reportui.details")]
        private void CmdDetails(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !CanUse(player)) return;
            if (!_drafts.TryGetValue(player.userID, out var d))
                _drafts[player.userID] = d = new ReportDraft();
            d.Details = arg.Args != null && arg.Args.Length > 0 ? string.Join(" ", arg.Args) : string.Empty;
        }

        [ConsoleCommand("reportui.shoturls")]
        private void CmdShotUrls(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !CanUse(player)) return;
            if (!_drafts.TryGetValue(player.userID, out var d))
                _drafts[player.userID] = d = new ReportDraft();
            d.ScreenshotUrls = arg.Args != null && arg.Args.Length > 0 ? string.Join(" ", arg.Args) : string.Empty;
        }

        [ConsoleCommand("reportui.submit")]
        private void CmdSubmit(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !CanUse(player)) return;

            var uid = player.UserIDString;

            if (string.IsNullOrWhiteSpace(_config.DiscordWebhookUrl) &&
                (!_config.SendCollectorEvent || string.IsNullOrWhiteSpace(_config.CollectorUrl)))
            {
                player.ChatMessage(T("Chat.NoDestination", uid));
                return;
            }

            var now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_cooldownUntil.TryGetValue(player.userID, out var until) && now < until)
            {
                player.ChatMessage(T("Chat.Cooldown", uid, Mathf.CeilToInt((float)(until - now))));
                return;
            }

            if (!_drafts.TryGetValue(player.userID, out var d))
                _drafts[player.userID] = d = new ReportDraft();

            if (string.IsNullOrWhiteSpace(d.Category))
            {
                player.ChatMessage(T("Chat.SelectCategory", uid));
                return;
            }

            if (_config.RequireTargetPlayer && !d.TargetSteamId.HasValue)
            {
                player.ChatMessage(T("Chat.SelectTarget", uid));
                return;
            }

            var details = (d.Details ?? string.Empty).Trim();
            if (details.Length < _config.MinDetailsLength)
            {
                player.ChatMessage(T("Chat.DescriptionTooShort", uid, _config.MinDetailsLength));
                return;
            }

            var traceId = Guid.NewGuid().ToString("N");
            Dictionary<string, object> snapshot = null;
            if (_config.EnableSurroundSnapshot)
            {
                try
                {
                    snapshot = BuildSurroundSnapshot(player);
                }
                catch (Exception ex)
                {
                    PrintWarning($"Snapshot failed: {ex.Message}");
                    snapshot = new Dictionary<string, object> { ["error"] = ex.Message };
                }

                if (_config.SaveSnapshotDataFile && snapshot != null)
                    TryPersistSnapshot(traceId, snapshot);
            }

            var shotUrls = (d.ScreenshotUrls ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(_config.DiscordWebhookUrl))
                SendDiscordWebhook(player, d, details, traceId, snapshot, shotUrls);

            if (_config.SendCollectorEvent && !string.IsNullOrWhiteSpace(_config.CollectorUrl))
                SendCollectorReport(player, d, details, traceId, snapshot, shotUrls);

            _cooldownUntil[player.userID] = now + _config.CooldownSeconds;
            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.DestroyUi(player, UiDropdown);
            _drafts.Remove(player.userID);
            PushReportHistory(player, d, details, traceId);
            player.ChatMessage(T("Chat.Submitted", uid));
        }

        private void LoadReportHistory()
        {
            try
            {
                _reportHistory = Interface.Oxide.DataFileSystem.ReadObject<ReportHistoryFile>(HistoryDataPath);
                if (_reportHistory?.Entries == null)
                    _reportHistory = new ReportHistoryFile();
            }
            catch
            {
                _reportHistory = new ReportHistoryFile();
            }
        }

        private void PushReportHistory(BasePlayer reporter, ReportDraft d, string details, string traceId)
        {
            if (_config.DisableAdminReportFeatures) return;

            try
            {
                if (_reportHistory == null) LoadReportHistory();

                var targetShort = d.TargetSteamId.HasValue
                    ? (string.IsNullOrEmpty(d.TargetName) ? d.TargetSteamId.ToString() : d.TargetName)
                    : "—";
                var prev = details.Length > 120 ? details.Substring(0, 117) + "..." : details;
                _reportHistory.Entries.Insert(0, new ReportHistoryEntry
                {
                    TraceId = traceId,
                    UtcIso = DateTime.UtcNow.ToString("o"),
                    ReporterName = reporter.displayName,
                    ReporterId = reporter.UserIDString,
                    Category = d.Category,
                    TargetShort = targetShort,
                    DetailsPreview = prev
                });

                var max = Mathf.Clamp(_config.MaxReportHistoryEntries, 5, 500);
                while (_reportHistory.Entries.Count > max)
                    _reportHistory.Entries.RemoveAt(_reportHistory.Entries.Count - 1);

                if (!_config.ReportHistoryMemoryOnly)
                    Interface.Oxide.DataFileSystem.WriteObject(HistoryDataPath, _reportHistory);
            }
            catch (Exception ex)
            {
                PrintWarning($"Report history error: {ex.Message}");
            }
        }

        [ChatCommand("reportadmin")]
        private void CmdReportAdmin(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var uid = player.UserIDString;

            if (_config.DisableAdminReportFeatures)
            {
                player.ChatMessage(T("Chat.AdminDisabled", uid));
                return;
            }

            if (!player.IsAdmin && !permission.UserHasPermission(uid, PermAdmin))
            {
                player.ChatMessage(T("Chat.AdminNoPermission", uid));
                return;
            }

            if (_reportHistory == null) LoadReportHistory();

            var n = 15;
            if (args != null && args.Length > 0 && int.TryParse(args[0], out var parsed))
                n = Mathf.Clamp(parsed, 5, 30);

            var list = _reportHistory?.Entries;
            if (list == null || list.Count == 0)
            {
                player.ChatMessage(T("Chat.AdminNoReports", uid));
                return;
            }

            player.ChatMessage(T("Chat.AdminHeader", uid, n));
            var take = Mathf.Min(n, list.Count);
            for (var i = 0; i < take; i++)
            {
                var e = list[i];
                player.ChatMessage(
                    $"<color=#cccccc>{i + 1}.</color> <color=#ffd93d>{e.TraceId}</color> | {e.Category} | from {e.ReporterName}");
                var det = e.DetailsPreview ?? string.Empty;
                if (det.Length > 220) det = det.Substring(0, 217) + "...";
                player.ChatMessage(T("Chat.AdminEntry", uid, e.TargetShort, det));
            }
        }

        private bool CanUse(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermUse));
        }

        private List<object> SerializeContainer(ItemContainer c, int maxItems)
        {
            var list = new List<object>();
            if (c == null || c.itemList == null) return list;

            var n = 0;
            foreach (var it in c.itemList)
            {
                if (it == null || it.info == null) continue;
                if (n++ >= maxItems) break;
                list.Add(new Dictionary<string, object>
                {
                    ["shortname"] = it.info.shortname,
                    ["amount"] = it.amount,
                    ["skin"] = it.skin,
                    ["displayName"] = it.name ?? string.Empty,
                    ["position"] = it.position
                });
            }

            return list;
        }

        private Dictionary<string, object> BuildSurroundSnapshot(BasePlayer reporter)
        {
            var center = reporter.transform.position;
            var r = _config.SnapshotRadius;
            var maxI = _config.SnapshotMaxItemsPerContainer;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var players = new List<object>();
            var vehicles = new List<object>();
            var budget = _config.SnapshotMaxVehicles;
            var seenVehicleIds = new HashSet<ulong>();

            void TryAddPlayer(BasePlayer p, string sourceTag)
            {
                if (p == null || p.IsNpc) return;
                var dist = Vector3.Distance(p.transform.position, center);
                if (dist > r + 0.01f) return;
                if (!seen.Add(p.UserIDString)) return;

                var held = p.GetHeldEntity();
                players.Add(new Dictionary<string, object>
                {
                    ["steamId"] = p.UserIDString,
                    ["displayName"] = p.displayName,
                    ["source"] = sourceTag,
                    ["distanceM"] = Math.Round(dist, 2),
                    ["health"] = Math.Round(p.health, 1),
                    ["isAdmin"] = p.IsAdmin,
                    ["belt"] = SerializeContainer(p.inventory?.containerBelt, maxI),
                    ["wear"] = SerializeContainer(p.inventory?.containerWear, maxI),
                    ["main"] = SerializeContainer(p.inventory?.containerMain, maxI),
                    ["heldEntity"] = held != null && !held.IsDestroyed ? held.ShortPrefabName : string.Empty
                });
            }

            foreach (var p in BasePlayer.activePlayerList) TryAddPlayer(p, "active");
            foreach (var p in BasePlayer.sleepingPlayerList) TryAddPlayer(p, "sleeping");

            AppendVisEntities(center, r, vehicles, ref budget, seenVehicleIds);
            return new Dictionary<string, object>
            {
                ["capturedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["radiusM"] = r,
                ["center"] = new Dictionary<string, object>
                {
                    ["x"] = Math.Round(center.x, 2),
                    ["y"] = Math.Round(center.y, 2),
                    ["z"] = Math.Round(center.z, 2)
                },
                ["playerCountInRadius"] = players.Count,
                ["players"] = players,
                ["vehiclesAndMounts"] = vehicles
            };
        }

        private void AppendVisEntities(Vector3 center, float radius, List<object> sink, ref int budget,
            HashSet<ulong> seenNetIds)
        {
            TryAppendVis(center, radius, "vehicle", sink, ref budget, seenNetIds, (List<ModularCar> l) => Vis.Entities(center, radius, l));
            TryAppendVis(center, radius, "vehicle", sink, ref budget, seenNetIds, (List<BasicCar> l) => Vis.Entities(center, radius, l));
            TryAppendVis(center, radius, "vehicle", sink, ref budget, seenNetIds, (List<Bike> l) => Vis.Entities(center, radius, l));
            TryAppendVis(center, radius, "vehicle", sink, ref budget, seenNetIds, (List<Snowmobile> l) => Vis.Entities(center, radius, l));
            TryAppendVis(center, radius, "boat", sink, ref budget, seenNetIds, (List<BaseBoat> l) => Vis.Entities(center, radius, l));
            TryAppendVis(center, radius, "mount", sink, ref budget, seenNetIds, (List<RidableHorse> l) => Vis.Entities(center, radius, l));
            TryAppendVis(center, radius, "heli", sink, ref budget, seenNetIds, (List<BaseHelicopter> l) => Vis.Entities(center, radius, l),
                e => !(e is PatrolHelicopter));
        }

        private delegate void VisPopulateDelegate<T>(List<T> list) where T : BaseEntity;

        private void TryAppendVis<T>(Vector3 center, float radius, string kind, List<object> sink, ref int budget,
            HashSet<ulong> seenNetIds, VisPopulateDelegate<T> populate, Func<T, bool> extraFilter = null) where T : BaseEntity
        {
            if (budget <= 0) return;

            var list = Pool.Get<List<T>>();
            try
            {
                populate(list);
                foreach (var e in list)
                {
                    if (budget <= 0) break;
                    if (e == null || e.IsDestroyed) continue;
                    if (extraFilter != null && !extraFilter(e)) continue;

                    var nid = e.net?.ID.Value ?? 0UL;
                    if (nid == 0UL || !seenNetIds.Add(nid)) continue;

                    sink.Add(new Dictionary<string, object>
                    {
                        ["kind"] = kind,
                        ["prefab"] = e.ShortPrefabName,
                        ["netId"] = nid,
                        ["distanceM"] = Math.Round(Vector3.Distance(e.transform.position, center), 2),
                        ["ownerId"] = e.OwnerID
                    });
                    budget--;
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref list);
            }
        }

        private void TryPersistSnapshot(string traceId, Dictionary<string, object> snapshot)
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject($"GameZoneReportUI/snapshots/{traceId}", snapshot);
            }
            catch (Exception ex)
            {
                PrintWarning($"Snapshot file could not be saved: {ex.Message}");
            }
        }

        private string SummarizeSnapshot(Dictionary<string, object> snapshot)
        {
            if (snapshot == null) return "—";

            if (snapshot.TryGetValue("error", out var err))
                return T("Snapshot.Error", null, err);

            var pc = snapshot.TryGetValue("playerCountInRadius", out var pco) ? pco : "?";
            var vc = 0;
            if (snapshot.TryGetValue("vehiclesAndMounts", out var vobj) && vobj is List<object> vl)
                vc = vl.Count;

            return T("Snapshot.Summary", null, pc, vc);
        }

        private static string FirstHttpImageUrl(string urlsBlock)
        {
            if (string.IsNullOrWhiteSpace(urlsBlock)) return null;

            foreach (var line in urlsBlock.Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return t.Length > 2048 ? t.Substring(0, 2048) : t;
                }
            }

            return null;
        }

        private void SendDiscordWebhook(BasePlayer reporter, ReportDraft d, string details, string traceId,
            Dictionary<string, object> snapshot, string shotUrls)
        {
            var truncated = details.Length > 900 ? details.Substring(0, 897) + "..." : details;
            var targetLine = d.TargetSteamId.HasValue
                ? (string.IsNullOrEmpty(d.TargetName) ? d.TargetSteamId.ToString() : d.TargetName + " (`" + d.TargetSteamId + "`)")
                : "—";

            var snapLine = SummarizeSnapshot(snapshot);
            var urlsShort = string.IsNullOrWhiteSpace(shotUrls) ? "—"
                : (shotUrls.Length > 900 ? shotUrls.Substring(0, 897) + "..." : shotUrls);

            var serverLabel = string.IsNullOrWhiteSpace(_config.InstanceName) && string.IsNullOrWhiteSpace(_config.ServerId)
                ? "—"
                : (_config.InstanceName + (string.IsNullOrWhiteSpace(_config.ServerId) ? "" : " / " + _config.ServerId)).Trim(' ', '/');

            var fields = new List<DiscordField>
            {
                new DiscordField { Name = T("Discord.TraceField"),    Value = "`" + traceId + "`", Inline = false },
                new DiscordField { Name = T("Discord.ServerField"),   Value = serverLabel, Inline = true },
                new DiscordField { Name = T("Discord.ReporterField"), Value = reporter.displayName + " (`" + reporter.UserIDString + "`)", Inline = true },
                new DiscordField { Name = T("Discord.ReasonField"),   Value = d.Category, Inline = true },
                new DiscordField { Name = T("Discord.AffectedField"), Value = targetLine, Inline = false },
                new DiscordField { Name = T("Discord.SnapshotField"), Value = snapLine, Inline = false },
                new DiscordField { Name = T("Discord.EvidenceField"), Value = urlsShort, Inline = false },
                new DiscordField { Name = T("Discord.DescriptionField"), Value = truncated, Inline = false }
            };

            var embed = new DiscordEmbed
            {
                Title = T("Discord.EmbedTitle"),
                Color = 0xE67E22,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Fields = fields
            };

            if (_config.SaveSnapshotDataFile)
            {
                embed.Footer = new DiscordEmbedFooter
                {
                    Text = "Snapshot: oxide/data/GameZoneReportUI/snapshots/" + traceId + ".json"
                };
            }

            var img = FirstHttpImageUrl(shotUrls);
            if (!string.IsNullOrEmpty(img))
                embed.Image = new DiscordEmbedImage { Url = img };

            var body = new DiscordWebhookBody { Embeds = new List<DiscordEmbed> { embed } };
            var json = JsonConvert.SerializeObject(body);
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            webrequest.Enqueue(_config.DiscordWebhookUrl, json, (code, response) =>
            {
                if (code < 200 || code >= 300)
                    PrintWarning($"Discord Webhook: HTTP {code} {response}");
            }, this, RequestMethod.POST, headers);
        }

        private void SendCollectorReport(BasePlayer reporter, ReportDraft d, string details, string traceId,
            Dictionary<string, object> snapshot, string shotUrls)
        {
            var argsList = new List<string> { d.Category, details };
            if (d.TargetSteamId.HasValue)
                argsList.Insert(0, d.TargetName ?? d.TargetSteamId.ToString());

            var payload = new Dictionary<string, object>
            {
                ["command"] = "report",
                ["args"] = argsList.ToArray(),
                ["category"] = d.Category,
                ["details"] = details,
                ["targetSteamId"] = d.TargetSteamId?.ToString() ?? string.Empty,
                ["targetName"] = d.TargetName ?? string.Empty,
                ["traceId"] = traceId,
                ["screenshotUrls"] = shotUrls ?? string.Empty,
                ["snapshot"] = snapshot,
                ["uiVersion"] = "GameZoneReportUI/1.4.0"
            };

            var actor = new EventActorDto
            {
                Id = reporter.UserIDString,
                Name = reporter.displayName,
                SteamId = reporter.UserIDString,
                AuthLevel = reporter.net?.connection?.authLevel != null ? (int?)reporter.net.connection.authLevel : 0
            };

            EventActorDto targetDto = null;
            if (d.TargetSteamId.HasValue)
            {
                targetDto = new EventActorDto
                {
                    Id = d.TargetSteamId.ToString(),
                    Name = d.TargetName ?? string.Empty,
                    SteamId = d.TargetSteamId.ToString(),
                    AuthLevel = null
                };
            }

            var ev = new RustEventDto
            {
                EventType = "report",
                EventCategory = "moderation",
                ServerId = _config.ServerId,
                InstanceName = _config.InstanceName,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Source = Name,
                TraceId = traceId,
                Actor = actor,
                Target = targetDto,
                Payload = payload,
                Raw = snapshot
            };

            var json = JsonConvert.SerializeObject(ev);
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["x-api-key"] = string.IsNullOrEmpty(_config.CollectorApiKey) ? string.Empty : _config.CollectorApiKey
            };

            webrequest.Enqueue(_config.CollectorUrl, json, (code, response) =>
            {
                if (code < 200 || code >= 300)
                    PrintWarning($"Collector Report: HTTP {code} {response}");
            }, this, RequestMethod.POST, headers);
        }
    }
}
