using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;

namespace ARealmRecorded;

public static unsafe class PluginUI
{
    public static readonly float[] presetSpeeds = { 0.5f, 1, 2, 5, 10, 20 };

    private static bool needSort = true;
    private static bool showPluginSettings = false;
    private static int editingReplay = -1;
    private static string editingName = string.Empty;

    private static bool loadingPlayback = false;
    private static bool loadedPlayback = true;

    private static bool showReplaySettings = false;
    private static bool showDebug = false;

    private static float lastSeek = 0;
    private static readonly Stopwatch lastSeekChange = new();

    private static readonly Regex displayNameRegex = new("(.+)[ _]\\d{4}\\.");

    public static void Draw()
    {
        DrawExpandedDutyRecorderMenu();
        DrawExpandedPlaybackControls();
    }

    public static void DrawExpandedDutyRecorderMenu()
    {
        if (DalamudApi.GameGui.GameUiHidden) return;

        var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("ContentsReplaySetting", 1);
        if (addon == null || !addon->IsVisible || (addon->Flags & 16) == 0) return;

        var agent = DalamudApi.GameGui.FindAgentInterface((nint)addon);
        if (agent == nint.Zero) return;

        //var units = AtkStage.GetSingleton()->RaptureAtkUnitManager->AtkUnitManager.FocusedUnitsList;
        //var count = units.Count;
        //if (count > 0 && (&units.AtkUnitEntries)[count - 1] != addon) return;

        var addonW = addon->RootNode->GetWidth() * addon->Scale;
        var addonH = (addon->RootNode->GetHeight() - 11) * addon->Scale;
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(new(addon->X + addonW, addon->Y));
        ImGui.SetNextWindowSize(new Vector2(500 * ImGuiHelpers.GlobalScale, addonH));
        ImGui.Begin("Expanded Duty Recorder", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);

        if (ImGui.IsWindowAppearing())
            showPluginSettings = false;

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.SyncAlt.ToIconString()))
        {
            Game.GetReplayList();
            needSort = true;
        }
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.FolderOpen.ToIconString()))
            Game.OpenReplayFolder();
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.FileArchive.ToIconString()))
        {
            Game.ArchiveReplays();
            needSort = true;
        }
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Archive saved unplayable replays.");
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.Cog.ToIconString()))
            showPluginSettings ^= true;
#if DEBUG
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.ExclamationTriangle.ToIconString()))
            Game.ReadPackets(Game.LastSelectedReplay);
#endif
        ImGui.PopFont();

        if (!showPluginSettings)
            DrawReplaysTable(agent);
        else
            DrawPluginSettings();
    }

    public static void DrawReplaysTable(nint agent)
    {
        if (!ImGui.BeginTable("ReplaysTable", 2, ImGuiTableFlags.Sortable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var sortspecs = ImGui.TableGetSortSpecs();
        if (sortspecs.SpecsDirty || needSort || ImGui.IsWindowAppearing())
        {
            if (sortspecs.Specs.ColumnIndex == 0)
            {
                // Date
                Game.ReplayList = sortspecs.Specs.SortDirection == ImGuiSortDirection.Ascending
                    ? Game.ReplayList.OrderByDescending(t => t.Item2.header.IsPlayable).ThenBy(t => t.Item2.header.timestamp).ToList()
                    : Game.ReplayList.OrderByDescending(t => t.Item2.header.IsPlayable).ThenByDescending(t => t.Item2.header.timestamp).ToList();
            }
            else
            {
                // Name
                Game.ReplayList = sortspecs.Specs.SortDirection == ImGuiSortDirection.Ascending
                    ? Game.ReplayList.OrderByDescending(t => t.Item2.header.IsPlayable).ThenBy(t => t.Item1.Name).ToList()
                    : Game.ReplayList.OrderByDescending(t => t.Item2.header.IsPlayable).ThenByDescending(t => t.Item1.Name).ToList();
            }
            sortspecs.SpecsDirty = false;
            needSort = false;
        }

        for (int i = 0; i < Game.ReplayList.Count; i++)
        {
            var (file, replay) = Game.ReplayList[i];
            var header = replay.header;
            var path = file.FullName;
            var fileName = file.Name;
            var displayName = displayNameRegex.Match(fileName) is { Success: true } match ? match.Groups[1].Value : fileName[..fileName.LastIndexOf('.')];
            var isPlayable = replay.header.IsPlayable;
            var autoRenamed = file.Directory?.Name == "autorenamed";

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (!isPlayable)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
            ImGui.TextUnformatted(DateTimeOffset.FromUnixTimeSeconds(header.timestamp).LocalDateTime.ToString(CultureInfo.CurrentCulture));
            if (!isPlayable)
                ImGui.PopStyleColor();
            ImGui.TableNextColumn();

            if (editingReplay != i)
            {
                if (!isPlayable)
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
                if (ImGui.Selectable(autoRenamed ? $"◯ {displayName}##{path}" : $"{displayName}##{path}", path == Game.LastSelectedReplay && *(byte*)(agent + 0x2C) == 100, ImGuiSelectableFlags.SpanAllColumns))
                    Game.SetDutyRecorderMenuSelection(agent, path, header);
                if (!isPlayable)
                    ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                {
                    // TODO: This could probably be a function on the chapter structure itself
                    var pulls = 0;
                    var longestPull = TimeSpan.Zero;
                    for (int j = 0; j < replay.chapters.length; j++)
                    {
                        var chapter = replay.chapters[j];
                        if (chapter->type != 2) continue;

                        if (j < replay.chapters.length - 1)
                        {
                            var nextChapter = replay.chapters[j + 1];
                            if (nextChapter->type == 1)
                            {
                                chapter = nextChapter;
                                j++;
                            }
                        }

                        var nextStartMS = header.ms;
                        for (int k = j + 1; k < replay.chapters.length; k++)
                        {
                            var nextStart = replay.chapters[k];
                            if (nextStart->type != 2) continue;
                            nextStartMS = nextStart->ms;
                            break;
                        }

                        var ms = (int)(nextStartMS - chapter->ms);
                        if (ms > 30_000)
                            pulls++;

                        var timeSpan = new TimeSpan(0, 0, 0, 0, ms);
                        if (timeSpan > longestPull)
                            longestPull = timeSpan;
                    }

                    ImGui.BeginTooltip();
                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

                    ImGui.TextUnformatted($"Duty: {header.ContentFinderCondition?.Name.ToDalamudString()}");
                    if ((header.info & 4) != 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextUnformatted(" ");
                        ImGui.SameLine();
                        ImGui.PushFont(UiBuilder.IconFont);
                        ImGui.TextColored(new Vector4(0, 1, 0, 1), FontAwesomeIcon.Check.ToIconString());
                        ImGui.PopFont();
                    }

                    var foundPlayer = false;
                    ImGui.TextUnformatted("Party:");
                    foreach (var row in header.ClassJobs.OrderBy(row => row.UIPriority))
                    {
                        ImGui.SameLine();
                        if (!foundPlayer && row == header.LocalPlayerClassJob)
                        {
                            ImGui.TextUnformatted($" «{row.Abbreviation}»");
                            foundPlayer = true;
                        }
                        else
                        {
                            ImGui.TextUnformatted($" {row.Abbreviation}");
                        }
                    }

                    ImGui.TextUnformatted($"Length: {new TimeSpan(0, 0, 0, 0, (int)header.ms):hh':'mm':'ss}");
                    if (longestPull > TimeSpan.Zero)
                    {
                        ImGui.TextUnformatted($"Number of Pulls: {pulls}");
                        ImGui.TextUnformatted($"Longest Pull: {longestPull:hh':'mm':'ss}");
                    }

                    ImGui.PopStyleVar();
                    ImGui.EndTooltip();
                }

                if (ImGui.BeginPopupContextItem())
                {
                    for (byte j = 0; j < 3; j++)
                    {
                        if (!ImGui.Selectable($"Copy to slot #{j + 1}")) continue;
                        Game.CopyReplayIntoSlot(agent, file, header, j);
                        needSort = true;
                    }

                    if (ImGui.Selectable("Delete"))
                    {
                        Game.DeleteReplay(file);
                        needSort = true;
                    }

                    ImGui.EndPopup();
                }

                if (!ImGui.IsItemHovered() || !ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) continue;

                editingReplay = i;
                editingName = fileName[..fileName.LastIndexOf('.')];
            }
            else
            {
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputText("##SetName", ref editingName, 64, ImGuiInputTextFlags.AutoSelectAll);

                if (ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive())
                    ImGui.SetKeyboardFocusHere(-1);

                if (!ImGui.IsItemDeactivated()) continue;

                editingReplay = -1;

                if (ImGui.IsItemDeactivatedAfterEdit())
                    Game.RenameReplay(file, editingName);
            }
        }
        ImGui.EndTable();
    }

    public static void DrawPluginSettings()
    {
        ImGui.BeginChild("PluginSettings", Vector2.Zero, true);

        var save = false;

        save |= ImGui.Checkbox("Enable Recording Icon", ref ARealmRecorded.Config.EnableRecordingIcon);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enables the game's recording icon next to the world / time information (Server info bar).");

        save |= ImGui.InputInt("Max Replays", ref ARealmRecorded.Config.MaxAutoRenamedReplays);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Max number of replays to keep in the autorenamed folder.");

        save |= ImGui.InputInt("Max Deleted Replays", ref ARealmRecorded.Config.MaxDeletedReplays);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Max number of replays to keep in the deleted folder.");

        if (save)
            ARealmRecorded.Config.Save();

        ImGui.EndChild();
    }

    public static void DrawExpandedPlaybackControls()
    {
        if (DalamudApi.GameGui.GameUiHidden || DalamudApi.Condition[ConditionFlag.WatchingCutscene]) return;

        if (!Game.InPlayback)
        {
            loadingPlayback = false;
            loadedPlayback = false;
            return;
        }

        if (!loadedPlayback)
        {
            if (Game.ffxivReplay->seek != lastSeek)
            {
                lastSeek = Game.ffxivReplay->seek;
                lastSeekChange.Restart();
            }
            else if (lastSeekChange.ElapsedMilliseconds >= 3_000)
            {
                loadedPlayback = true;
            }

            if (Game.ffxivReplay->u0x6F8 != 0)
                loadingPlayback = true;
            else if (loadingPlayback && Game.ffxivReplay->u0x6F8 == 0)
                loadedPlayback = true;
            return;
        }

        var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("ContentsReplayPlayer", 1);
        if (addon == null) return;

        var addonW = addon->RootNode->GetWidth() * addon->Scale;
        var addonPadding = addon->Scale * 8;
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(new(addon->X + addonPadding, addon->Y + addonPadding), ImGuiCond.Always, Vector2.UnitY);
        ImGui.SetNextWindowSize(new Vector2(addonW - addonPadding * 2, 0));
        ImGui.Begin("Expanded Playback", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);

        if (showReplaySettings && !Game.IsLoadingChapter)
        {
            DrawReplaySettings();
            ImGui.Separator();
        }
        else if (showDebug)
        {
            DrawDebug();
            ImGui.Separator();
        }

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Users.ToIconString()))
            Framework.Instance()->GetUiModule()->EnterGPose();
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enters group pose.");

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Video.ToIconString()))
            Framework.Instance()->GetUiModule()->EnterIdleCam(0, DalamudApi.TargetManager.FocusTarget is { } focus ? focus.ObjectId : 0xE0000000);
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enters idle camera on the current focus target.");

        ImGui.SameLine();
        var v = Game.IsWaymarkVisible;
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(v ? FontAwesomeIcon.ToggleOn.ToIconString() : FontAwesomeIcon.ToggleOff.ToIconString()))
            Game.ToggleWaymarks();
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(v ? "Hide waymarks." : "Show waymarks.");
        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.Cog.ToIconString()))
            showReplaySettings ^= true;

        ImGui.SameLine();
        ImGui.Button(FontAwesomeIcon.Skull.ToIconString());
        if (ImGui.BeginPopupContextItem(null, ImGuiPopupFlags.MouseButtonLeft))
        {
            if (ImGui.Selectable(FontAwesomeIcon.DoorOpen.ToIconString()))
                Game.ffxivReplay->overallDataOffset = long.MaxValue;
            ImGui.EndPopup();
        }

#if DEBUG
        ImGui.SameLine();
        if (ImGui.Button(FontAwesomeIcon.ExclamationTriangle.ToIconString()))
            showDebug ^= true;
#endif
        ImGui.PopFont();

        var seek = Game.ffxivReplay->seek;
        if (!Game.IsPaused || Game.IsLoadingChapter)
        {
            if (seek != lastSeek)
            {
                lastSeek = seek;
                lastSeekChange.Restart();
            }
            else if (lastSeekChange.ElapsedMilliseconds >= 3_000)
            {
                ImGui.SameLine();
                var segment = Game.GetReplayDataSegmentDetour(Game.ffxivReplay);
                ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TabActive));
                if (ImGui.Button("UNSTUCK") && segment != null)
                    Game.ffxivReplay->overallDataOffset += segment->Length;
                ImGui.PopStyleColor();
            }
        }

        ImGui.BeginDisabled(Game.IsLoadingChapter);

        const int restartDelayMS = 12_000;
        var sliderWidth = ImGui.GetContentRegionAvail().X;
        var startMS = Game.ffxivReplay->chapters[0]->ms;
        var seekMS = Math.Max((int)(seek * 1000), (int)startMS);
        var lastStartChapterMS = Game.ffxivReplay->chapters[Game.FindPreviousChapterType(2)]->ms;
        var nextStartChapterMS = Game.ffxivReplay->chapters[Game.FindNextChapterType(2)]->ms;
        if (lastStartChapterMS >= nextStartChapterMS)
            nextStartChapterMS = Game.ffxivReplay->replayHeader.ms + startMS;
        var currentTime = new TimeSpan(0, 0, 0, 0, (int)(seekMS - lastStartChapterMS));
        ImGui.PushItemWidth(sliderWidth);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 4);
        ImGui.SliderInt($"##Time{lastStartChapterMS}", ref seekMS, (int)lastStartChapterMS, (int)nextStartChapterMS - restartDelayMS, currentTime.ToString("hh':'mm':'ss"), ImGuiSliderFlags.NoInput);
        ImGui.PopStyleVar();

        if (ImGui.IsItemHovered())
        {
            var hoveredWidth = ImGui.GetMousePos().X - ImGui.GetItemRectMin().X;
            var hoveredPercent = hoveredWidth / sliderWidth;
            if (hoveredPercent is >= 0.0f and <= 1.0f)
            {
                var hoveredTime = new TimeSpan(0, 0, 0, 0, (int)Math.Min(Math.Max((int)((nextStartChapterMS - lastStartChapterMS - restartDelayMS) * hoveredPercent), 0), nextStartChapterMS - lastStartChapterMS));
                ImGui.SetTooltip(hoveredTime.ToString("hh':'mm':'ss"));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    Game.SeekToTime((uint)hoveredTime.TotalMilliseconds + lastStartChapterMS);
                else if (ARealmRecorded.Config.EnableJumpToTime && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    Game.JumpToTime((uint)hoveredTime.TotalMilliseconds + lastStartChapterMS);
            }
        }

        ImGui.EndDisabled();

        var speed = Game.ffxivReplay->speed;
        if (ImGui.SliderFloat("##Speed", ref speed, 0.05f, 10.0f, "%.2fx", ImGuiSliderFlags.AlwaysClamp))
            Game.ffxivReplay->speed = speed;
        ImGui.PopItemWidth();

        for (int i = 0; i < presetSpeeds.Length; i++)
        {
            if (i != 0)
                ImGui.SameLine();

            var s = presetSpeeds[i];
            if (ImGui.Button($"{s}x"))
                Game.ffxivReplay->speed = s == Game.ffxivReplay->speed ? 1 : s;
        }

        var customSpeed = ARealmRecorded.Config.CustomSpeedPreset;
        ImGui.SameLine();
        ImGui.Dummy(Vector2.Zero);
        ImGui.SameLine();
        if (ImGui.Button($"{customSpeed}x"))
            Game.ffxivReplay->speed = customSpeed == Game.ffxivReplay->speed ? 1 : customSpeed;

        ImGui.End();
    }

    private static void DrawReplaySettings()
    {
        var save = false;

        if (ImGui.Checkbox("Hide Own Name (Requires Replay Restart)", ref ARealmRecorded.Config.EnableHideOwnName))
        {
            Game.replaceLocalPlayerNameReplacer.Toggle();
            save = true;
        }

        save |= ImGui.Checkbox("Enable Quick Chapter Load", ref ARealmRecorded.Config.EnableQuickLoad);

        save |= ImGui.Checkbox("Enable Right Click to Jump to Time", ref ARealmRecorded.Config.EnableJumpToTime);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Doing this WILL result in playback not appearing correctly!");
        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(new Vector4(1, 1, 0, 1), FontAwesomeIcon.ExclamationTriangle.ToIconString());
        ImGui.PopFont();

        if (ARealmRecorded.Config.EnableQuickLoad)
        {
            save |= ImGui.SliderFloat("Loading Speed", ref ARealmRecorded.Config.MaxSeekDelta, 100, 800, "%.f%%");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Can cause issues with some fights that contain arena changes.");
        }

        save |= ImGui.SliderFloat("Speed Preset", ref ARealmRecorded.Config.CustomSpeedPreset, 0.05f, 60, "%.2fx", ImGuiSliderFlags.AlwaysClamp);

        if (save)
            ARealmRecorded.Config.Save();
    }

    [Conditional("DEBUG")]
    private static void DrawDebug()
    {
        var segment = Game.GetReplayDataSegmentDetour(Game.ffxivReplay);
        if (segment == null) return;

        ImGui.TextUnformatted($"Offset: {Game.ffxivReplay->overallDataOffset + sizeof(Structures.FFXIVReplay.Header) + sizeof(Structures.FFXIVReplay.ChapterArray):X}");
        ImGui.TextUnformatted($"Opcode: {segment->opcode:X}");
        ImGui.TextUnformatted($"Data Length: {segment->dataLength}");
        ImGui.TextUnformatted($"Time: {segment->ms / 1000f}");
        ImGui.TextUnformatted($"Object ID: {segment->objectID:X}");
    }
}