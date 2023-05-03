using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Memory;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace ARealmRecorded;

public unsafe class Game
{
    private static readonly string replayFolder = Path.Combine(Framework.Instance()->UserPath, "replay");
    private static readonly string autoRenamedFolder = Path.Combine(replayFolder, "autorenamed");
    private static readonly string archiveZip = Path.Combine(replayFolder, "archive.zip");
    private static readonly string deletedFolder = Path.Combine(replayFolder, "deleted");
    private static Structures.FFXIVReplay.ReplayFile* loadedReplay = null;

    public static string LastSelectedReplay { get; private set; }
    private static Structures.FFXIVReplay.Header lastSelectedHeader;

    private static int quickLoadChapter = -1;
    private static int seekingChapter = 0;
    private static uint seekingOffset = 0;

    private static int currentRecordingSlot = -1;
    private static readonly Regex bannedFolderCharacters = new("[\\\\\\/:\\*\\?\"\\<\\>\\|\u0000-\u001F]");

    private static readonly HashSet<uint> whitelistedContentTypes = new() { 1, 2, 3, 4, 5, 9, 28, 29, 30 }; // 22 Event, 26 Eureka, 27 Carnivale

    private static List<(FileInfo, Structures.FFXIVReplay.ReplayFile)> replayList;
    public static List<(FileInfo, Structures.FFXIVReplay.ReplayFile)> ReplayList
    {
        get => replayList ?? GetReplayList();
        set => replayList = value;
    }

    private const int RsfSize = 0x48;
    private const ushort RsfOpcode = 0xF002;
    private static readonly List<byte[]> rsfBuffer = new();
    private const ushort RsvOpcode = 0xF001;
    private static readonly List<byte[]> rsvBuffer = new();
    private const ushort DeltaOpCode = 0xF003;

    public static Dictionary<ushort, ushort> OpCodeDictionary = null;

    private static readonly Memory.Replacer alwaysRecordReplacer = new("24 06 3C 02 75 08 48 8B CB E8", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeRecordReadyToastReplacer = new("BA CB 07 00 00 48 8B CF E8", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeProcessingLimitReplacer = new("41 FF C6 E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0F 84", new byte[] { 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeProcessingLimitReplacer2 = new("77 57 48 8B 0D ?? ?? ?? ?? 33 C0", new byte[] { 0x90, 0x90 }, true);
    private static readonly Memory.Replacer forceFastForwardReplacer = new("0F 83 ?? ?? ?? ?? 0F B7 47 02 4C 8D 47 0C", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
    private static readonly Memory.Replacer fixP8Replacer = new("73 ?? 8B 52 08 48 8D 0D", new byte[] { 0xEB }, true);
    private static readonly Memory.Replacer hideSelfNameReplacer = new("74 38 41 3B 1F", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer hideSelfNameReplacer2 = new("0F 85 ?? ?? ?? ?? 48 ?? ?? ?? ?? E8 ?? ?? ?? ?? F6 05", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);

    // mov rcx, r14 -> xor rcx, rcx
    public static readonly Memory.Replacer replaceLocalPlayerNameReplacer = new(DalamudApi.SigScanner.ScanModule("F6 05 ?? ?? ?? ?? 04 74 ?? 45 33 C0 33 D2 49 8B CE") + 14, new byte[] { 0x48, 0x31, 0xC9 }, ARealmRecorded.Config.EnableHideOwnName);

    [Signature("48 8D 0D ?? ?? ?? ?? 88 44 24 24", ScanType = ScanType.StaticAddress)]
    public static Structures.FFXIVReplay* ffxivReplay;

    [Signature("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? EB 0E", ScanType = ScanType.StaticAddress)]
    private static byte* waymarkToggle; // Actually a uint, but only seems to use the first 2 bits

    [Signature("89 1D ?? ?? ?? ?? 40 84 FF", ScanType = ScanType.StaticAddress)]
    private static int* delta0;

    [Signature("89 15 ?? ?? ?? ?? EB 1E", ScanType = ScanType.StaticAddress)]
    private static int* delta4;

    [Signature("03 05 ?? ?? ?? ?? 03 C3", ScanType = ScanType.StaticAddress)] //Global = delta0+0x8 CN = delta0+0xC
    private static int* deltaC;

    public static bool InPlayback => (ffxivReplay->playbackControls & 4) != 0;
    public static bool IsPaused => (ffxivReplay->playbackControls & 8) != 0;
    public static bool IsSavingPackets => (ffxivReplay->status & 4) != 0;
    public static bool IsRecording => (ffxivReplay->status & 0x74) == 0x74;
    public static bool IsLoadingChapter => ffxivReplay->selectedChapter < 0x40;

    public static bool IsWaymarkVisible => (*waymarkToggle & 2) == 0;

    [Signature("?? ?? 00 00 01 75 74 85 FF 75 07 E8")]
    public static short contentDirectorOffset;

    [Signature("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 74 5D")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, void> beginRecording;
    public static void BeginRecording() => beginRecording(ffxivReplay, 1);

    [Signature("E8 ?? ?? ?? ?? 84 C0 74 8D 48 8B CE")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, byte> setChapter;
    private static byte SetChapter(byte chapter) => setChapter(ffxivReplay, chapter);

    //[Signature("E9 ?? ?? ?? ?? 48 83 4B 70 04")]
    //private static delegate* unmanaged<Structures.FFXIVReplay*, byte, byte> addRecordingChapter;
    //public static bool AddRecordingChapter(byte type) => addRecordingChapter(ffxivReplay, type) != 0;

    //[Signature("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 24 06 3C 04 75 5D 83 B9")]
    //private static delegate* unmanaged<Structures.FFXIVReplay*, void> resetPlayback;
    //public static void ResetPlayback() => resetPlayback(ffxivReplay);

    [Signature("48 89 5C 24 10 57 48 81 EC 70 04 00 00")]
    private static delegate* unmanaged<IntPtr, void> displaySelectedDutyRecording;
    public static void DisplaySelectedDutyRecording(IntPtr agent) => displaySelectedDutyRecording(agent);

    private delegate void InitializeRecordingDelegate(Structures.FFXIVReplay* ffxivReplay);
    [Signature("40 55 57 48 8D 6C 24 B1 48 81 EC 98 00 00 00", DetourName = "InitializeRecordingDetour")]
    private static Hook<InitializeRecordingDelegate> InitializeRecordingHook;
    private static void InitializeRecordingDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        var id = ffxivReplay->initZonePacket.contentFinderCondition;
        if (id == 0) return;

        var contentFinderCondition = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.ContentFinderCondition>()?.GetRow(id);
        if (contentFinderCondition == null) return;

        var contentType = contentFinderCondition.ContentType.Row;
        if (!whitelistedContentTypes.Contains(contentType)) return;

        FixNextReplaySaveSlot();
        InitializeRecordingHook.Original(ffxivReplay);
        BeginRecording();

        var header = ffxivReplay->replayHeader;
        header.localCID = 0;
        ffxivReplay->replayHeader = header;

        if (contentDirectorOffset > 0)
            ContentDirectorTimerUpdateHook?.Enable();

        FlushRsvRsfBuffers(); // TODO: Look into potential issue with packets received from The Unending Journey being added to replays
    }

    private delegate byte RequestPlaybackDelegate(Structures.FFXIVReplay* ffxivReplay, byte slot);
    [Signature("48 89 5C 24 08 57 48 83 EC 30 F6 81 ?? ?? ?? ?? 04", DetourName = "RequestPlaybackDetour")] // E8 ?? ?? ?? ?? EB 2B 48 8B CB 89 53 2C (+0x14)
    private static Hook<RequestPlaybackDelegate> RequestPlaybackHook;
    public static byte RequestPlaybackDetour(Structures.FFXIVReplay* ffxivReplay, byte slot)
    {
        var customSlot = slot == 100;
        Structures.FFXIVReplay.Header prevHeader = new();

        if (customSlot)
        {
            slot = 0;
            prevHeader = ffxivReplay->savedReplayHeaders[0];
            ffxivReplay->savedReplayHeaders[0] = lastSelectedHeader;
        }
        else
        {
            LastSelectedReplay = null;
        }

        var ret = RequestPlaybackHook.Original(ffxivReplay, slot);
        if (customSlot)
            ffxivReplay->savedReplayHeaders[0] = prevHeader;

        return ret;
    }

    private delegate void BeginPlaybackDelegate(Structures.FFXIVReplay* ffxivReplay, byte canEnter);
    [Signature("E8 ?? ?? ?? ?? 0F B7 17 48 8B CB", DetourName = "BeginPlaybackDetour")]
    private static Hook<BeginPlaybackDelegate> BeginPlaybackHook;
    private static void BeginPlaybackDetour(Structures.FFXIVReplay* ffxivReplay, byte allowed)
    {
        BeginPlaybackHook.Original(ffxivReplay, allowed);
        if (allowed == 0) return;

        UnloadReplay();

        if (string.IsNullOrEmpty(LastSelectedReplay))
            LoadReplay(ffxivReplay->currentReplaySlot);
        else
            LoadReplay(LastSelectedReplay);
    }

    [Signature("E8 ?? ?? ?? ?? F6 83 ?? ?? ?? ?? 04 74 38 F6 83 ?? ?? ?? ?? 01", DetourName = "PlaybackUpdateDetour")]
    private static Hook<InitializeRecordingDelegate> PlaybackUpdateHook;
    private static void PlaybackUpdateDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        PlaybackUpdateHook.Original(ffxivReplay);

        UpdateAutoRename();

        if (IsRecording && ffxivReplay->chapters[0]->type == 1) // For some reason the barrier dropping in dungeons is 5, but in trials it's 1
            ffxivReplay->chapters[0]->type = 5;

        if (!InPlayback) return;

        SetConditionFlag(ConditionFlag.OccupiedInCutSceneEvent, false);

        if (loadedReplay == null) return;

        ffxivReplay->dataLoadType = 0;
        ffxivReplay->dataOffset = 0;

        if (quickLoadChapter < 2) return;

        var seekedTime = ffxivReplay->chapters[seekingChapter]->ms / 1000f;
        if (seekedTime > ffxivReplay->seek) return;

        DoQuickLoad();
    }

    private delegate Structures.FFXIVReplay.ReplayDataSegment* GetReplayDataSegmentDelegate(Structures.FFXIVReplay* ffxivReplay);
    [Signature("40 53 48 83 EC 20 8B 81 90 00 00 00")]
    private static Hook<GetReplayDataSegmentDelegate> GetReplayDataSegmentHook;
    public static Structures.FFXIVReplay.ReplayDataSegment* GetReplayDataSegmentDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        // Needs to be here to prevent infinite looping
        if (seekingOffset > 0 && seekingOffset <= ffxivReplay->overallDataOffset)
        {
            forceFastForwardReplacer.Disable();
            seekingOffset = 0;
        }

        // Absurdly hacky, but it works
        if (!ARealmRecorded.Config.EnableQuickLoad || ARealmRecorded.Config.MaxSeekDelta <= 100 || ffxivReplay->seekDelta >= ARealmRecorded.Config.MaxSeekDelta)
            removeProcessingLimitReplacer2.Disable();
        else
            removeProcessingLimitReplacer2.Enable();

        return loadedReplay == null ? GetReplayDataSegmentHook.Original(ffxivReplay) : loadedReplay->GetDataSegment((uint)ffxivReplay->overallDataOffset);
    }

    private delegate void OnSetChapterDelegate(Structures.FFXIVReplay* ffxivReplay, byte chapter);
    [Signature("48 89 5C 24 08 57 48 83 EC 30 48 8B D9 0F B6 FA 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 24", DetourName = "OnSetChapterDetour")]
    private static Hook<OnSetChapterDelegate> OnSetChapterHook;
    private static void OnSetChapterDetour(Structures.FFXIVReplay* ffxivReplay, byte chapter)
    {
        OnSetChapterHook.Original(ffxivReplay, chapter);

        if (!ARealmRecorded.Config.EnableQuickLoad || chapter <= 0 || ffxivReplay->chapters.length < 2) return;

        quickLoadChapter = chapter;
        seekingChapter = -1;
        DoQuickLoad();
    }

    private delegate byte ExecuteCommandDelegate(uint clientTrigger, int param1, int param2, int param3, int param4);
    [Signature("E8 ?? ?? ?? ?? 8D 43 0A")]
    private static Hook<ExecuteCommandDelegate> ExecuteCommandHook;
    private static byte ExecuteCommandDetour(uint clientTrigger, int param1, int param2, int param3, int param4)
    {
        if (!InPlayback || clientTrigger is 201 or 1981) return ExecuteCommandHook.Original(clientTrigger, param1, param2, param3, param4); // Block GPose and Idle Camera from sending packets
        if (clientTrigger == 315) // Mimic GPose and Idle Camera ConditionFlag for plugin compatibility
            SetConditionFlag(ConditionFlag.WatchingCutscene, param1 != 0);
        return 0;
    }

    private delegate byte DisplayRecordingOnDTRBarDelegate(nint agent);
    [Signature("E8 ?? ?? ?? ?? 44 0F B6 C0 BA 4F 00 00 00", DetourName = "DisplayRecordingOnDTRBarDetour")]
    private static Hook<DisplayRecordingOnDTRBarDelegate> DisplayRecordingOnDTRBarHook;
    private static byte DisplayRecordingOnDTRBarDetour(IntPtr agent) => (byte)(DisplayRecordingOnDTRBarHook.Original(agent) != 0
        || ARealmRecorded.Config.EnableRecordingIcon && IsRecording && DalamudApi.PluginInterface.UiBuilder.ShouldModifyUi ? 1 : 0);

    private delegate void ContentDirectorTimerUpdateDelegate(IntPtr contentDirector);
    [Signature("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 0F 84 ?? ?? ?? ?? A8 08", DetourName = "ContentDirectorTimerUpdateDetour")]
    private static Hook<ContentDirectorTimerUpdateDelegate> ContentDirectorTimerUpdateHook;
    private static void ContentDirectorTimerUpdateDetour(IntPtr contentDirector)
    {
        if ((*(byte*)(contentDirector + contentDirectorOffset) & 12) == 12)
        {
            ffxivReplay->status |= 64;
            ContentDirectorTimerUpdateHook.Disable();
        }

        ContentDirectorTimerUpdateHook.Original(contentDirector);
    }

    private delegate IntPtr EventBeginDelegate(IntPtr a1, IntPtr a2);
    [Signature("40 55 53 57 41 55 41 57 48 8D 6C 24 C9")]
    private static Hook<EventBeginDelegate> EventBeginHook;
    private static IntPtr EventBeginDetour(IntPtr a1, IntPtr a2) => !InPlayback || ConfigModule.Instance()->GetIntValue(ConfigOption.CutsceneSkipIsContents) == 0 ? EventBeginHook.Original(a1, a2) : IntPtr.Zero;

    public unsafe delegate long RsvReceiveDelegate(IntPtr a1);
    [Signature("44 8B 09 4C 8D 41 34",DetourName = nameof(RsvReceiveDetour))]
    //public unsafe delegate long RsvReceiveDelegate(IntPtr a1, IntPtr a2, IntPtr a3, uint size);   //a2:Key[0x30] a3:Value a1:const
    //[Signature("E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC 48 8B 11",DetourName = nameof(RsvReceiveDetour))]
    private static Hook<RsvReceiveDelegate> RsvReceiveHook;
    private static long RsvReceiveDetour(IntPtr a1)
    {
        PluginLog.Debug("Received a RSV packet,");
        var size = *(int*)a1;   //Value size
        var length = size + 0x4 + 0x30;     //package size
        RsvBuffer.Add(MemoryHelper.ReadRaw(a1, length));
        var ret = RsvReceiveHook.Original(a1);
        PluginLog.Debug($"RSV:RET = {ret:X},Num of received:{RsvBuffer.Count}");
        return ret;
    }

    public unsafe delegate long RsfReceiveDelegate(IntPtr a1);
    [Signature("48 8B 11 4C 8D 41 08", DetourName = nameof(RsfReceiveDetour))]
    //public unsafe delegate long RsfReceiveDelegate(IntPtr a1, ulong a2, IntPtr a3);        //a1:const a2:Key a2:Value
    //[Signature("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 40 48 83 B9 ?? ?? ?? ?? ?? 49 8B F0", DetourName = nameof(RsfReceiveDetour))]
    private static Hook<RsfReceiveDelegate> RsfReceiveHook;
    private static long RsfReceiveDetour(IntPtr a1)
    {
        PluginLog.Debug("Received a RSF packet");
        RsfBuffer.Add(MemoryHelper.ReadRaw(a1, RsfSize));
        var ret = RsfReceiveHook.Original(a1);
        PluginLog.Debug($"RSF:RET = {ret:X},Num of received:{RsvBuffer.Count}");
        return ret;
    }

    public unsafe delegate uint RecordPacketDelegate(Structures.FFXIVReplay* replayModule, uint targetId, ushort opcode, IntPtr data, ulong length);
    [Signature("E8 ?? ?? ?? ?? 84 C0 74 60 33 C0", DetourName = nameof(RecordPacketDetour))]
    private static Hook<RecordPacketDelegate> RecordPacketHook;
    private static uint RecordPacketDetour(Structures.FFXIVReplay* replayModule, uint targetId, ushort opcode, IntPtr data, ulong length) {
        //Remove player's names here
        //PluginLog.Debug($"Received:0x{opcode:X},Length:{length}");
        switch (opcode) {

        }
        return RecordPacketHook.Original(replayModule,targetId,opcode,data,length);
    }

    private unsafe delegate uint DispatchPacketDelegate(Structures.FFXIVReplay* replayModule, IntPtr header, IntPtr data);
    [Signature("E8 ?? ?? ?? ?? 80 BB ?? ?? ?? ?? ?? 77 93",DetourName = nameof(DispatchPacketDetour))]
    private static Hook<DispatchPacketDelegate> DispatchPacketHook;
    private static unsafe uint DispatchPacketDetour(Structures.FFXIVReplay* replayModule, nint header, nint data)
    {
        var opcode = *(ushort*)header;
        PluginLog.Debug($"Catched:0x{opcode:X}");
        switch (opcode) {
            case RsvOpcde:
                //ReadRsv();
                RsvReceiveHook.Original(data);
                break;
            case RsfOpcde:
                //ReadRsf();
                RsfReceiveHook.Original(data);
                break;
            case DeltaOpCode:
                UpdateDelta(data);
                break;
            default:
                if (OpCodeDictionary is null) break;
                *(ushort*)header = UpdateOpCode(opcode);
                PluginLog.Information($"changed {opcode:X} to {UpdateOpCode(opcode):X}");
                break;
        }
        var result = DispatchPacketHook.Original(replayModule, header, data);
        return result;
    }

    private static ushort UpdateOpCode(ushort opCode)
    {
        if (OpCodeDictionary is null) return opCode;
        if (OpCodeDictionary.TryGetValue(opCode, out var result)) return result;
        PluginLog.Error($"Error when updating OpCode 0x{opCode:X}");
        return opCode;
    }

    private static void UpdateDelta(nint delta)
    {
        //delta4 = delta0 + deltaC + *delta
        PluginLog.Warning($"Old Delta = {*delta4:X} - {*delta0:X} - {*deltaC:X} = {*delta4 - *delta0 - *deltaC:X}");
        if (*delta4 - *delta0 - *deltaC == *(int*)delta) return;
        *delta4 = *delta0 + *deltaC + *(int*)delta;
        PluginLog.Warning($"New Delta = {*(int*)delta:X}");
    }


    public delegate byte RsvReceiveDelegate(nint data);
    [Signature("44 8B 09 4C 8D 41 34", DetourName = nameof(RsvReceiveDetour))]
    private static Hook<RsvReceiveDelegate> RsvReceiveHook;
    private static byte RsvReceiveDetour(nint data)
    {
        var size = *(int*)data; // Value size
        var length = size + 0x4 + 0x30; // Package size
        rsvBuffer.Add(MemoryHelper.ReadRaw(data, length));
        return RsvReceiveHook.Original(data);
    }

    public delegate byte RsfReceiveDelegate(nint data);
    [Signature("48 8B 11 4C 8D 41 08", DetourName = nameof(RsfReceiveDetour))]
    private static Hook<RsfReceiveDelegate> RsfReceiveHook;
    private static byte RsfReceiveDetour(nint data)
    {
        rsfBuffer.Add(MemoryHelper.ReadRaw(data, RsfSize));
        return RsfReceiveHook.Original(data);
    }

    private static void FlushRsvRsfBuffers()
    {
        if (IsSavingPackets)
        {
            //PluginLog.Debug($"Recording {rsfBuffer.Count} RSF packets");
            foreach (var rsf in rsfBuffer)
            {
                fixed (byte* data = rsf)
                    RecordPacket(ffxivReplay, 0xE000_0000, RsfOpcode, (nint)data, (ulong)rsf.Length);
            }

            //PluginLog.Debug($"Recording {rsvBuffer.Count} RSV packets");
            foreach (var rsv in rsvBuffer)
            {
                fixed (byte* data = rsv)
                    RecordPacket(ffxivReplay, 0xE000_0000, RsvOpcode, (nint)data, (ulong)rsv.Length);
            }
        }

        rsfBuffer.Clear();
        rsvBuffer.Clear();
    }

    [Signature("E8 ?? ?? ?? ?? 84 C0 74 60 33 C0")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, uint, ushort, nint, ulong, uint> recordPacket;
    public static void RecordPacket(Structures.FFXIVReplay* replayModule, uint targetId, ushort opcode, nint data, ulong length) => recordPacket(replayModule, targetId, opcode, data, length);

    private delegate uint DispatchPacketDelegate(Structures.FFXIVReplay* replayModule, nint header, nint data);
    [Signature("E8 ?? ?? ?? ?? 80 BB ?? ?? ?? ?? ?? 77 93", DetourName = nameof(DispatchPacketDetour))]
    private static Hook<DispatchPacketDelegate> DispatchPacketHook;
    private static uint DispatchPacketDetour(Structures.FFXIVReplay* replayModule, nint header, nint data)
    {
        var opcode = *(ushort*)header;
        //PluginLog.Debug($"Dispatch:0x{opcode:X}");
        switch (opcode) {
            case RsvOpcode:
                RsvReceiveHook.Original(data);
                break;
            case RsfOpcode:
                RsfReceiveHook.Original(data);
                break;
        }
        return DispatchPacketHook.Original(replayModule, header, data);
    }

    public static string GetReplaySlotName(int slot) => $"FFXIV_{DalamudApi.ClientState.LocalContentId:X16}_{slot:D3}.dat";

    private static void UpdateAutoRename()
    {
        switch (IsRecording)
        {
            case true when currentRecordingSlot < 0:
                currentRecordingSlot = ffxivReplay->nextReplaySaveSlot;
                break;
            case false when currentRecordingSlot >= 0:
                AutoRenameReplay();
                currentRecordingSlot = -1;
                SetSavedReplayCIDs(DalamudApi.ClientState.LocalContentId);
                break;
        }
    }

    public static bool LoadReplay(int slot) => LoadReplay(Path.Combine(replayFolder, GetReplaySlotName(slot)));

    public static bool LoadReplay(string path)
    {
        var newReplay = ReadReplay(path);
        if (newReplay == null) return false;

        if (loadedReplay != null)
            Marshal.FreeHGlobal((IntPtr)loadedReplay);

        loadedReplay = newReplay;
        ffxivReplay->replayHeader = loadedReplay->header;
        ffxivReplay->chapters = loadedReplay->chapters;
        ffxivReplay->dataLoadType = 0;

        ARealmRecorded.Config.LastLoadedReplay = path;
        return true;
    }

    public static bool UnloadReplay()
    {
        if (loadedReplay == null) return false;
        Marshal.FreeHGlobal((IntPtr)loadedReplay);
        loadedReplay = null;
        return true;
    }

    public static Structures.FFXIVReplay.ReplayFile* ReadReplay(string path)
    {
        var ptr = IntPtr.Zero;
        var allocated = false;

        try
        {
            using var fs = File.OpenRead(path);

            ptr = Marshal.AllocHGlobal((int)fs.Length);
            allocated = true;

            _ = fs.Read(new Span<byte>((void*)ptr, (int)fs.Length));
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed to read replay {path}\n{e}");

            if (allocated)
            {
                Marshal.FreeHGlobal(ptr);
                ptr = IntPtr.Zero;
            }
        }

        return (Structures.FFXIVReplay.ReplayFile*)ptr;
    }

    public static Structures.FFXIVReplay.ReplayFile? ReadReplayHeaderAndChapters(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var size = sizeof(Structures.FFXIVReplay.Header) + sizeof(Structures.FFXIVReplay.ChapterArray);
            var bytes = new byte[size];
            if (fs.Read(bytes, 0, size) != size)
                return null;
            fixed (byte* ptr = &bytes[0])
                return *(Structures.FFXIVReplay.ReplayFile*)ptr;
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed to read replay header and chapters {path}\n{e}");
            return null;
        }
    }

    public static void FixNextReplaySaveSlot()
    {
        if (ARealmRecorded.Config.MaxAutoRenamedReplays <= 0 && !ffxivReplay->savedReplayHeaders[ffxivReplay->nextReplaySaveSlot].IsLocked) return;

        for (byte i = 0; i < 3; i++)
        {
            if (i != 2)
            {
                var header = ffxivReplay->savedReplayHeaders[i];
                if (header.IsLocked) continue;
            }

            ffxivReplay->nextReplaySaveSlot = i;
            return;
        }
    }

    public static byte FindPreviousChapterType(byte chapter, byte type)
    {
        for (byte i = chapter; i > 0; i--)
            if (ffxivReplay->chapters[i]->type == type) return i;
        return 0;
    }

    public static byte FindPreviousChapterType(byte type) => FindPreviousChapterType(GetCurrentChapter(), type);

    public static byte FindNextChapterType(byte chapter, byte type)
    {
        for (byte i = (byte)(chapter + 1); i < ffxivReplay->chapters.length; i++)
            if (ffxivReplay->chapters[i]->type == type) return i;
        return 0;
    }

    public static byte FindNextChapterType(byte type) => FindNextChapterType(GetCurrentChapter(), type);

    public static byte GetPreviousStartChapter(byte chapter)
    {
        var foundPreviousStart = false;
        for (byte i = chapter; i > 0; i--)
        {
            if (ffxivReplay->chapters[i]->type != 2) continue;

            if (foundPreviousStart)
                return i;
            foundPreviousStart = true;
        }
        return 0;
    }

    public static byte GetPreviousStartChapter() => GetPreviousStartChapter(GetCurrentChapter());

    public static byte FindPreviousChapterFromTime(uint ms)
    {
        for (byte i = (byte)(ffxivReplay->chapters.length - 1); i > 0; i--)
            if (ffxivReplay->chapters[i]->ms <= ms) return i;
        return 0;
    }

    public static byte GetCurrentChapter() => FindPreviousChapterFromTime((uint)(ffxivReplay->seek * 1000));

    public static Structures.FFXIVReplay.ReplayDataSegment* FindNextDataSegment(uint ms, out uint offset)
    {
        offset = 0;

        Structures.FFXIVReplay.ReplayDataSegment* segment;
        while ((segment = loadedReplay->GetDataSegment(offset)) != null)
        {
            if (segment->ms >= ms) return segment;
            offset += segment->Length;
        }

        return null;
    }

    public static void JumpToChapter(byte chapter)
    {
        var jumpChapter = ffxivReplay->chapters[chapter];
        if (jumpChapter == null) return;
        ffxivReplay->overallDataOffset = jumpChapter->offset;
        ffxivReplay->seek = jumpChapter->ms / 1000f;
    }

    public static void JumpToTime(uint ms)
    {
        var segment = FindNextDataSegment(ms, out var offset);
        if (segment == null) return;
        ffxivReplay->overallDataOffset = offset;
        ffxivReplay->seek = segment->ms / 1000f;
    }

    public static void JumpToTimeBeforeChapter(byte chapter, uint ms)
    {
        var jumpChapter = ffxivReplay->chapters[chapter];
        if (jumpChapter == null) return;
        JumpToTime(jumpChapter->ms > ms ? jumpChapter->ms - ms : 0);
    }

    public static void SeekToTime(uint ms)
    {
        if (IsLoadingChapter) return;

        var prevChapter = FindPreviousChapterFromTime(ms);
        var segment = FindNextDataSegment(ms, out var offset);
        if (segment == null) return;

        seekingOffset = offset;
        forceFastForwardReplacer.Enable();
        if (ffxivReplay->seek * 1000 < segment->ms && prevChapter == GetCurrentChapter())
            OnSetChapterHook.Original(ffxivReplay, prevChapter);
        else
            SetChapter(prevChapter);
    }

    public static void ReplaySection(byte from, byte to)
    {
        if (from != 0 && ffxivReplay->overallDataOffset < ffxivReplay->chapters[from]->offset)
            JumpToChapter(from);

        seekingChapter = to;
        if (seekingChapter >= quickLoadChapter)
            quickLoadChapter = -1;
    }

    public static void DoQuickLoad()
    {
        if (seekingChapter < 0)
        {
            ReplaySection(0, 1);
            return;
        }

        var nextEvent = FindNextChapterType((byte)seekingChapter, 4);
        if (nextEvent != 0 && nextEvent < quickLoadChapter - 1)
        {
            var nextCountdown = FindNextChapterType(nextEvent, 1);
            if (nextCountdown == 0 || nextCountdown > nextEvent + 2)
                nextCountdown = (byte)(nextEvent + 1);
            ReplaySection(nextEvent, nextCountdown);
            return;
        }

        ReplaySection(GetPreviousStartChapter((byte)quickLoadChapter), (byte)quickLoadChapter);
    }

    public static List<(FileInfo, Structures.FFXIVReplay.ReplayFile)> GetReplayList()
    {
        try
        {
            var directory = new DirectoryInfo(replayFolder);

            var renamedDirectory = new DirectoryInfo(autoRenamedFolder);
            if (!renamedDirectory.Exists)
            {
                if (ARealmRecorded.Config.MaxAutoRenamedReplays > 0)
                    renamedDirectory.Create();
                else
                    renamedDirectory = null;
            }

            var list = (from file in directory.GetFiles().Concat(renamedDirectory?.GetFiles() ?? Array.Empty<FileInfo>())
                    where file.Extension == ".dat"
                    let replay = ReadReplayHeaderAndChapters(file.FullName)
                    where replay is { header.IsValid: true }
                    select (file, replay.Value)
                ).ToList();

            replayList = list;
        }
        catch
        {
            replayList = new();
        }

        return replayList;
    }

    public static void RenameReplay(FileInfo file, string name)
    {
        try
        {
            file.MoveTo(Path.Combine(replayFolder, $"{name}.dat"));
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to rename replay\n{e}");
        }
    }

    public static void AutoRenameReplay()
    {
        if (ARealmRecorded.Config.MaxAutoRenamedReplays <= 0)
        {
            GetReplayList();
            return;
        }

        try
        {
            var fileName = GetReplaySlotName(currentRecordingSlot);
            var (file, _) = GetReplayList().First(t => t.Item1.Name == fileName);

            var name = $"{bannedFolderCharacters.Replace(ffxivReplay->contentTitle.ToString(), string.Empty)} {DateTime.Now:yyyy.MM.dd HH.mm.ss}";
            file.MoveTo(Path.Combine(autoRenamedFolder, $"{name}.dat"));

            var renamedFiles = new DirectoryInfo(autoRenamedFolder).GetFiles().Where(f => f.Extension == ".dat").ToList();
            while (renamedFiles.Count > ARealmRecorded.Config.MaxAutoRenamedReplays)
            {
                DeleteReplay(renamedFiles.OrderBy(f => f.CreationTime).First());
                renamedFiles = new DirectoryInfo(autoRenamedFolder).GetFiles().Where(f => f.Extension == ".dat").ToList();
            }

            GetReplayList();
            ffxivReplay->savedReplayHeaders[currentRecordingSlot] = new Structures.FFXIVReplay.Header();
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to rename replay\n{e}");
        }
    }

    public static void DeleteReplay(FileInfo file)
    {
        try
        {
            if (ARealmRecorded.Config.MaxDeletedReplays > 0)
            {
                var deletedDirectory = new DirectoryInfo(deletedFolder);
                if (!deletedDirectory.Exists)
                    deletedDirectory.Create();

                file.MoveTo(Path.Combine(deletedFolder, file.Name), true);

                var deletedFiles = deletedDirectory.GetFiles().Where(f => f.Extension == ".dat").ToList();
                while (deletedFiles.Count > ARealmRecorded.Config.MaxDeletedReplays)
                {
                    deletedFiles.OrderBy(f => f.CreationTime).First().Delete();
                    deletedFiles = deletedDirectory.GetFiles().Where(f => f.Extension == ".dat").ToList();
                }
            }
            else
            {
                file.Delete();
            }

            GetReplayList();
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to delete replay\n{e}");
        }
    }

    public static void ArchiveReplays()
    {
        var archivableReplays = ReplayList.Where(t => !t.Item2.header.IsPlayable && t.Item1.Directory?.Name == "replay").ToArray();
        if (archivableReplays.Length == 0) return;

        var restoreBackup = true;

        try
        {
            using (var zipFileStream = new FileStream(archiveZip, FileMode.OpenOrCreate))
            using (var zipFile = new ZipArchive(zipFileStream, ZipArchiveMode.Update))
            {
                var expectedEntryCount = zipFile.Entries.Count;
                if (expectedEntryCount > 0)
                {
                    var prevPosition = zipFileStream.Position;
                    zipFileStream.Position = 0;
                    using var zipBackupFileStream = new FileStream($"{archiveZip}.BACKUP", FileMode.Create);
                    zipFileStream.CopyTo(zipBackupFileStream);
                    zipFileStream.Position = prevPosition;
                }

                foreach (var (file, _) in archivableReplays)
                {
                    zipFile.CreateEntryFromFile(file.FullName, file.Name);
                    expectedEntryCount++;
                }

                if (zipFile.Entries.Count != expectedEntryCount)
                    throw new IOException($"Number of archived replays was unexpected (Expected: {expectedEntryCount}, Actual: {zipFile.Entries.Count}) after archiving, restoring backup!");
            }

            restoreBackup = false;

            foreach (var (file, _) in archivableReplays)
                file.Delete();
        }
        catch (Exception e)
        {

            if (restoreBackup)
            {
                try
                {
                    using var zipBackupFileStream = new FileStream($"{archiveZip}.BACKUP", FileMode.Open);
                    using var zipFileStream = new FileStream(archiveZip, FileMode.Create);
                    zipBackupFileStream.CopyTo(zipFileStream);
                }
                catch { }
            }

            ARealmRecorded.PrintError($"Failed to archive replays\n{e}");
        }

        GetReplayList();
    }

    public static void SetDutyRecorderMenuSelection(nint agent, byte slot)
    {
        *(byte*)(agent + 0x2C) = slot;
        *(byte*)(agent + 0x2A) = 1;
        DisplaySelectedDutyRecording(agent);
    }

    public static void SetDutyRecorderMenuSelection(IntPtr agent, string path, Structures.FFXIVReplay.Header header)
    {
        header.localCID = DalamudApi.ClientState.LocalContentId;

        OpCodeDictionary = null;
        if (header.replayVersion != ffxivReplay->replayVersion)
        {
            PluginLog.Warning($"Found different version : target = {header.replayVersion},System = {ffxivReplay->replayVersion}");
            OpCodeDictionary = OpCode.Compare(header.replayVersion, ffxivReplay->replayVersion);
        }
        
        if (OpCodeDictionary is not null) 
            header.replayVersion = ffxivReplay->replayVersion;

        lastSelectedReplay = path;
        lastSelectedHeader = header;
        var prevHeader = ffxivReplay->savedReplayHeaders[0];
        ffxivReplay->savedReplayHeaders[0] = header;
        SetDutyRecorderMenuSelection(agent, 0);
        ffxivReplay->savedReplayHeaders[0] = prevHeader;
        *(byte*)(agent + 0x2C) = 100;
    }

    public static void CopyReplayIntoSlot(nint agent, FileInfo file, Structures.FFXIVReplay.Header header, byte slot)
    {
        if (slot > 2) return;

        try
        {
            file.CopyTo(Path.Combine(replayFolder, GetReplaySlotName(slot)), true);
            header.localCID = DalamudApi.ClientState.LocalContentId;
            ffxivReplay->savedReplayHeaders[slot] = header;
            SetDutyRecorderMenuSelection(agent, slot);
            GetReplayList();
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to copy replay to slot {slot + 1}\n{e}");
        }
    }

    public static void SetSavedReplayCIDs(ulong cID)
    {
        if (ffxivReplay->savedReplayHeaders == null) return;

        for (int i = 0; i < 3; i++)
        {
            var header = ffxivReplay->savedReplayHeaders[i];
            if (!header.IsValid) continue;
            header.localCID = cID;
            ffxivReplay->savedReplayHeaders[i] = header;
        }
    }

    public static void OpenReplayFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = replayFolder,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public static void ToggleWaymarks() => *waymarkToggle ^= 2;

    public static void SetConditionFlag(ConditionFlag flag, bool b) => *(bool*)(DalamudApi.Condition.Address + (int)flag) = b;

    [Conditional("DEBUG")]
    public static void ReadPackets(string path)
    {
        var replay = ReadReplay(path);
        if (replay == null) return;

        var opcodeCount = new Dictionary<uint, uint>();
        var opcodeLengths = new Dictionary<uint, uint>();

        var offset = 0u;
        var totalPackets = 0u;

        Structures.FFXIVReplay.ReplayDataSegment* segment;
        while ((segment = replay->GetDataSegment(offset)) != null)
        {
            opcodeCount.TryGetValue(segment->opcode, out var count);
            opcodeCount[segment->opcode] = ++count;

            opcodeLengths[segment->opcode] = segment->dataLength;
            offset += segment->Length;
            ++totalPackets;
        }

        Marshal.FreeHGlobal((IntPtr)replay);

        PluginLog.Information("-------------------");
        PluginLog.Information($"Opcodes inside: {path} (Total: [{opcodeCount.Count}] {totalPackets})");
        foreach (var (opcode, count) in opcodeCount)
            PluginLog.Information($"[{opcode:X}] {count} ({opcodeLengths[opcode]})");
        PluginLog.Information("-------------------");
    }

    // 48 89 5C 24 08 57 48 83 EC 20 33 FF 48 8B D9 89 39 48 89 79 08 ctor
    // E8 ?? ?? ?? ?? 48 8D 8B 48 0B 00 00 E8 ?? ?? ?? ?? 48 8D 8B 38 0B 00 00 dtor
    // 40 53 48 83 EC 20 80 A1 ?? ?? ?? ?? F3 Initialize
    // 40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 75 09 Update
    // 48 83 EC 38 0F B6 91 ?? ?? ?? ?? 0F B6 C2 RequestEndPlayback
    // E8 ?? ?? ?? ?? EB 10 41 83 78 04 00 EndPlayback
    // 48 89 5C 24 10 55 48 8B EC 48 81 EC 80 00 00 00 48 8B 05 Something to do with loading
    // E8 ?? ?? ?? ?? 3C 40 73 4A GetCurrentChapter
    // F6 81 ?? ?? ?? ?? 04 74 11 SetTimescale (No longer used by anything)
    // 40 53 48 83 EC 20 F3 0F 10 81 ?? ?? ?? ?? 48 8B D9 F3 0F 10 0D SetSoundTimescale1? Doesn't seem to work (Last function)
    // E8 ?? ?? ?? ?? 44 0F B6 D8 C7 03 02 00 00 00 Function handling the UI buttons

    public static void Initialize()
    {
        // TODO change back to static whenever support is added
        //SignatureHelper.Initialise(typeof(Game));
        SignatureHelper.Initialise(new Game());
        InitializeRecordingHook.Enable();
        PlaybackUpdateHook.Enable();
        RequestPlaybackHook.Enable();
        BeginPlaybackHook.Enable();
        GetReplayDataSegmentHook.Enable();
        OnSetChapterHook.Enable();
        ExecuteCommandHook.Enable();
        DisplayRecordingOnDTRBarHook.Enable();
        EventBeginHook.Enable();
        RsvReceiveHook.Enable();
        RsfReceiveHook.Enable();
        DispatchPacketHook.Enable();

        RsvReceiveHook.Enable();
        RsfReceiveHook.Enable();
        RecordPacketHook.Enable();
        DispatchPacketHook.Enable();

        waymarkToggle += 0x48;

        SetSavedReplayCIDs(DalamudApi.ClientState.LocalContentId);

        if (InPlayback && ffxivReplay->fileStream != IntPtr.Zero && *(long*)ffxivReplay->fileStream == 0)
            LoadReplay(ARealmRecorded.Config.LastLoadedReplay);
    }

    public static void Dispose()
    {
        InitializeRecordingHook?.Dispose();
        PlaybackUpdateHook?.Dispose();
        RequestPlaybackHook?.Dispose();
        BeginPlaybackHook?.Dispose();
        GetReplayDataSegmentHook?.Dispose();
        OnSetChapterHook?.Dispose();
        ExecuteCommandHook?.Dispose();
        DisplayRecordingOnDTRBarHook?.Dispose();
        ContentDirectorTimerUpdateHook?.Dispose();
        EventBeginHook?.Dispose();
        RsvReceiveHook?.Dispose();
        RsfReceiveHook?.Dispose();
        DispatchPacketHook?.Dispose();

        RsvReceiveHook?.Dispose();
        RsfReceiveHook?.Dispose();
        RecordPacketHook?.Dispose();
        DispatchPacketHook?.Dispose();

        if (ffxivReplay != null)
            SetSavedReplayCIDs(0);

        if (loadedReplay == null) return;

        if (InPlayback)
        {
            ffxivReplay->playbackControls |= 8; // Pause
            ARealmRecorded.PrintError("Plugin was unloaded, playback will be broken if the plugin or replay is not reloaded.");
        }

        Marshal.FreeHGlobal((IntPtr)loadedReplay);
        loadedReplay = null;
    }
}
