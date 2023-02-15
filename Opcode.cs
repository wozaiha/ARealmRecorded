using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using Dalamud.Logging;
using Dalamud.Plugin.Ipc.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ARealmRecorded;

public unsafe class OpCode
{
    public class OpCodeJson
    {
        public string version { get; set; }
        public string region { get; set; }
        public int ver_id { get; set; }
        public Dictionary<string,List<string>> opcodes { get; set; }

        public Dictionary<string,ushort> NameToOpCode
        {
            get
            {
                var _result = new Dictionary<string, ushort>();
                foreach (var (_, opCodeEntry) in opcodes)
                {
                    _result.Add(opCodeEntry[3],(ushort)Convert.ToInt32(opCodeEntry[1], 16));
                }

                return _result;
            }
        }

        public Dictionary<string, string> NameToLength
        {
            get
            {
                var _result = new Dictionary<string, string>();
                foreach (var (_,opCodeEntry) in opcodes)
                {
                    _result.Add(opCodeEntry[3], opCodeEntry[2]);
                }

                return _result;
            }
        }

        //public Dictionary<ushort, string> OpcodeToName
        //{
        //    get
        //    {
        //        var _result = new Dictionary<ushort, string>();
        //        foreach (var (_, opCodeEntry) in opcodes)
        //        {
        //            _result.Add( (ushort)Convert.ToInt32(opCodeEntry[1], 16),opCodeEntry[3]);
        //        }

        //        return _result;
        //    }
        //}
    }
    public static Dictionary<int,OpCodeJson> OpCodeDic = new ();

    public static OpCodeJson GetOpcode(string path)
    {
        
        if (!File.Exists(path))
        {
            PluginLog.Warning($"File:{path} doesn't exist.");
            return null;
        }
        var json = JsonConvert.DeserializeObject<OpCodeJson>(File.ReadAllText(path));
        PluginLog.Debug($"Json -> Version:{json.region}_{json.version} with {json.opcodes.Count} OpCodes.");
        return json;
    }

    public static Dictionary<ushort, ushort> Compare(int oldVersion, int newVersion)
    {
        var warning = false;
        if (!OpCodeDic.ContainsKey(oldVersion) || !OpCodeDic.ContainsKey(newVersion))
        {
            PluginLog.Warning($"Cant Find version {oldVersion} or {newVersion}");
            return null;
        }
        var result = new Dictionary<ushort, ushort>();
        foreach (var (name,opCode) in OpCodeDic[oldVersion].NameToOpCode)
        {
            if (!OpCodeDic[newVersion].NameToOpCode.TryGetValue(name, out var newOpCode))
            {
                PluginLog.Warning($"Cant find OpCode {name}");
                warning = true;
                continue;
            }

            if (OpCodeDic[newVersion].NameToLength[name] != OpCodeDic[oldVersion].NameToLength[name])
            {
                PluginLog.Warning($"OpCode length dismatch: {name},old {OpCodeDic[oldVersion].NameToLength[name]} & new {OpCodeDic[newVersion].NameToLength[name]}");
                warning = true;
                continue;
            }
            result.Add(opCode, newOpCode);
        }
        PluginLog.Information($"Found dictionary for:{oldVersion} & {newVersion} with {result.Count} entries.");
        //if (warning) return null;
        return result;
    }


    public static void Initialize()
    {
        var files = DalamudApi.PluginInterface.ConfigDirectory.GetFiles("*.json");
        foreach (var file in files)
        {
            var json = GetOpcode(Path.Combine(DalamudApi.PluginInterface.GetPluginConfigDirectory(), file.FullName));
            if (json is null) continue;
            OpCodeDic.TryAdd(json.ver_id, json);
        }

        PluginLog.Debug($"Read {OpCodeDic.Count} opCode files.");
    }

    public static void Dispose()
    {
        
    }
}

