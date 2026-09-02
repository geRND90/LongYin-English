using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MelonLoader;

[assembly: MelonInfo(typeof(LongYinEnglish.MainMod), "LongYin English Core", "0.1.8-test42", "OpenAI")]
[assembly: MelonGame("TppStudio", "LongYinLiZhiZhuan")]

namespace LongYinEnglish
{
    public sealed class MainMod : MelonMod
    {
        private static HarmonyLib.Harmony _harmony;
        private static readonly Dictionary<string,string> Exact = new Dictionary<string,string>(StringComparer.Ordinal);
        private static readonly Dictionary<string,string> FallbackExact = new Dictionary<string,string>(StringComparer.Ordinal);
        private static readonly Dictionary<string,string> BuildingNames = new Dictionary<string,string>(StringComparer.Ordinal);
        private static readonly Dictionary<string,string> NpcNames = new Dictionary<string,string>(StringComparer.Ordinal);
        private static readonly Dictionary<string,string> NpcSurnames = new Dictionary<string,string>(StringComparer.Ordinal);
        private static readonly Dictionary<string,string> NpcGivenChars = new Dictionary<string,string>(StringComparer.Ordinal);
        private static readonly TrieNode TokenRoot = new TrieNode();
        private static readonly Dictionary<char,List<RegexRule>> RegexByTriggerChar = new Dictionary<char,List<RegexRule>>();
        private static readonly List<KeyValuePair<string,string>> Aliases = new List<KeyValuePair<string,string>>();
        private static readonly Dictionary<string,string> TranslationCache = new Dictionary<string,string>(StringComparer.Ordinal);
        private static readonly Dictionary<string,string> FinalizeCache = new Dictionary<string,string>(StringComparer.Ordinal);
        private static readonly object CacheLock = new object();
        private static readonly List<ResizeRule> ResizeRules = new List<ResizeRule>();
        private static readonly Dictionary<long, LayoutBaseline> LayoutBaselines = new Dictionary<long, LayoutBaseline>();
        private static readonly Dictionary<long, DeferredLayoutJob> DeferredLayoutJobs = new Dictionary<long, DeferredLayoutJob>();
        private static readonly Dictionary<long, DeferredTextJob> DeferredTextJobs = new Dictionary<long, DeferredTextJob>();
        private static readonly object LayoutLock = new object();
        private static readonly HashSet<string> PatchedTypes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> DebugSeen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> UnresolvedSeen = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object DebugLock = new object();
        private static int _debugCount;
        private static int _unresolvedCount;
        private static int _retryTicks;
        private static bool _resizerEnabled;
        private static int _perfSlowCount;
        [ThreadStatic] private static bool _postSetTranslationGuard;
        private const int DebugLimit = 240;
        private const int UnresolvedLimit = 2000;
        private const int PerfSlowLimit = 80;
        private const int TranslationCacheLimit = 12000;
        private const int FinalizeCacheLimit = 12000;

        private sealed class TrieNode
        {
            public readonly Dictionary<char,TrieNode> Next = new Dictionary<char,TrieNode>();
            public string Value;
        }
        private sealed class RegexRule
        {
            public Regex Pattern;
            public string Replacement;
            public string Trigger;
        }
        private sealed class ResizeRule
        {
            public string Path;
            public Regex Pattern;
            public bool MatchAll;
            public double? IdealFontSize;
            public double? FontPercentage;
            public bool? AllowWordWrap;
            public bool? AllowAutoSizing;
            public bool? AllowLeftTrimText;
            public double? AdjustX;
            public double? AdjustY;
            public double? AdjustWidth;
            public double? AdjustHeight;
            public double? MinFontSize;
            public double? MaxFontSize;
            public double? LineSpacing;
            public double? CharacterSpacing;
            public double? WordSpacing;
            public string Alignment;
            public string Overflow;
        }
        private sealed class LayoutBaseline
        {
            public bool HasFontSize;
            public double FontSize;
            public bool HasRect;
            public double X;
            public double Y;
            public double Width;
            public double Height;
            public bool HasLocalPosition;
            public double LocalX;
            public double LocalY;
            public double LocalZ;
        }

        private sealed class DeferredLayoutJob
        {
            public object Component;
            public LayoutBaseline Baseline;
            public int Kind;
            public int FramesLeft;
        }

        private sealed class DeferredTextJob
        {
            public object Component;
            public int FramesLeft;
        }

        public override void OnInitializeMelon()
        {
            try
            {
                string dataDir = Path.Combine(Environment.CurrentDirectory, "UserData", "LongYinEnglish");
                int exactCount = LoadTsv(Path.Combine(dataDir, "canonical.tsv"), Exact, false);
                int tokenCount = LoadTsv(Path.Combine(dataDir, "tokens.tsv"), null, true);
                int fallbackCount = LoadTsv(Path.Combine(dataDir, "fallback_exact.tsv"), FallbackExact, false);
                int buildingCount = LoadTsv(Path.Combine(dataDir, "building_names.tsv"), BuildingNames, false);
                int npcNameCount = LoadTsv(Path.Combine(dataDir, "npc_names.tsv"), NpcNames, false);
                int npcSurnameCount = LoadTsv(Path.Combine(dataDir, "npc_surnames.tsv"), NpcSurnames, false);
                int npcGivenCount = LoadTsv(Path.Combine(dataDir, "npc_given_chars.tsv"), NpcGivenChars, false);
                int aliasCount = LoadAliases(Path.Combine(dataDir, "aliases.tsv"));
                int regexCount = LoadRegex(Path.Combine(dataDir, "regex.tsv"));
                int resizerCount = LoadResizerDirectory(Path.Combine(dataDir, "Resizers"));
                _resizerEnabled = resizerCount > 0;

                _harmony = new HarmonyLib.Harmony("openai.longyin.englishcore");
                TryPatchTextType("UnityEngine.UI.Text", null);
                TryPatchTextType("TMPro.TMP_Text", "Unity.TextMeshPro");

                MelonLogger.Msg("LongYin English Core 0.1.8-test42 loaded.");
                MelonLogger.Msg("Canonical data: " + exactCount + " exact, " + tokenCount + " safe token, " + fallbackCount + " fallback exact, " + regexCount + " regex, " + aliasCount + " English aliases, " + buildingCount + " canonical building names, " + npcNameCount + " explicit NPC names, " + npcSurnameCount + " NameData surnames, " + npcGivenCount + " NameData given-name syllables.");
                MelonLogger.Msg("Global UI types patched now: " + PatchedTypes.Count + ". TMP will be retried automatically if its wrapper loads later.");
                MelonLogger.Msg("Unresolved capture limit: " + UnresolvedLimit + " unique entries per session.");
                MelonLogger.Msg("TEST42 safety mode: TEST38/40 performance protections preserved; latest unresolved mixed-output cleanup is context-scoped, NPC display romanization is strengthened, and canonical terminology remains normalized at display time.");
                MelonLogger.Msg(_resizerEnabled
                    ? "DragonHier-style YAML resizer ENABLED: " + resizerCount + " rule(s). Baseline/idempotent mode prevents cumulative shrinking; prefab-suffix matching enabled; map/building labels use stable parent-anchored correction; inventory item names use icon-safe late translation; building action labels are additionally enforced by the separate Building Actions Native Labels mod; the core keeps its compatibility fallback enabled."
                    : "DragonHier-style YAML resizer disabled: no YAML rules found.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("LongYin English Core init failed: " + ex);
            }
        }

        public override void OnUpdate()
        {
            ProcessDeferredTextJobs();
            ProcessDeferredLayoutJobs();

            if (!PatchedTypes.Contains("TMPro.TMP_Text"))
            {
                _retryTicks++;
                if (_retryTicks <= 600 && (_retryTicks % 60)==0)
                    TryPatchTextType("TMPro.TMP_Text", "Unity.TextMeshPro");
            }
        }

        private static void TryPatchTextType(string typeName, string assemblyName)
        {
            if (PatchedTypes.Contains(typeName)) return;
            Type t = FindLoadedType(typeName);
            if (t == null && !string.IsNullOrEmpty(assemblyName))
            {
                TryLoadWrapperAssembly(assemblyName);
                t = FindLoadedType(typeName);
            }
            if (t == null) return;

            int count=0;
            MethodInfo setter = FindTextSetter(t);
            if (setter != null)
            {
                HarmonyLib.HarmonyMethod prefix = new HarmonyLib.HarmonyMethod(typeof(MainMod).GetMethod(nameof(TextSetterPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                prefix.priority = HarmonyLib.Priority.First;
                HarmonyLib.HarmonyMethod postfix = new HarmonyLib.HarmonyMethod(typeof(MainMod).GetMethod(nameof(TextSetterPostfix), BindingFlags.Static | BindingFlags.NonPublic));
                postfix.priority = HarmonyLib.Priority.Last;
                _harmony.Patch(setter, prefix: prefix, postfix: postfix);
                count++;
            }

            MethodInfo onEnable = FindInstanceMethod(t,"OnEnable",0);
            if (onEnable != null)
            {
                HarmonyLib.HarmonyMethod postfix = new HarmonyLib.HarmonyMethod(typeof(MainMod).GetMethod(nameof(TextOnEnablePostfix), BindingFlags.Static | BindingFlags.NonPublic));
                postfix.priority = HarmonyLib.Priority.Last;
                _harmony.Patch(onEnable, postfix: postfix);
                count++;
            }

            if (count>0)
            {
                PatchedTypes.Add(typeName);
                MelonLogger.Msg("Presentation hooks installed for " + typeName + ": " + count + ".");
            }
        }

        private static MethodInfo FindTextSetter(Type t)
        {
            try
            {
                PropertyInfo p = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null)
                {
                    MethodInfo m=p.GetSetMethod(true);
                    if (m!=null) return m;
                }
            }
            catch { }
            Type cur=t;
            while (cur!=null)
            {
                foreach (MethodInfo m in cur.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "set_text") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) return m;
                }
                cur=cur.BaseType;
            }
            return null;
        }

        private static MethodInfo FindInstanceMethod(Type t,string name,int argCount)
        {
            Type cur=t;
            while (cur!=null)
            {
                try
                {
                    foreach (MethodInfo m in cur.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                        if (m.Name==name && m.GetParameters().Length==argCount) return m;
                }
                catch { }
                cur=cur.BaseType;
            }
            return null;
        }

        private static void TextSetterPrefix(object __instance, ref string __0)
        {
            if (_postSetTranslationGuard || string.IsNullOrEmpty(__0)) return;
            try
            {
                string original = __0;
                string path = BuildTransformPath(__instance);

                string nativeBuildingAction;
                if (IsBuildingActionChoicePath(path) && TryGetNativeBuildingActionLabel(original,out nativeBuildingAction))
                {
                    // These three labels participate in vanilla building-management logic. Keep only
                    // Upgrade/Demolish/Move in their original Chinese at all times; everything else translates.
                    __0=nativeBuildingAction;
                    return;
                }

                // ItemIcon names can participate in the game's sprite lookup on some inventory prefabs.
                // Let the original Chinese value reach the real setter first, then translate the visible
                // label in our postfix. This keeps presentation English without changing the lookup key.
                if (IsIconSensitiveItemNamePath(path)) return;

                string translated = TranslateForContext(original,path);
                if (translated != original)
                {
                    __0 = translated;
                    DebugHit(__instance, original, translated);
                }
                if (ContainsCjk(translated)) DebugUnresolved(__instance, original, translated);
            }
            catch { }
        }

        private static void TextSetterPostfix(object __instance)
        {
            if (__instance == null) return;
            try
            {
                if (!_postSetTranslationGuard) ScheduleIconSafeTextTranslation(__instance);
                if (_resizerEnabled) ApplyResizeRules(__instance);
            }
            catch { }
        }

        private static bool IsBuildingActionChoicePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            return path.IndexOf("BuildingUIPanel/BuildingUI/ExtraButtonGrid/",StringComparison.Ordinal)>=0
                && path.EndsWith("/Text",StringComparison.Ordinal);
        }

        private static bool TryGetNativeBuildingActionLabel(string text,out string native)
        {
            native=null;
            if (string.IsNullOrEmpty(text)) return false;
            string value=text.Trim();
            if (value=="升级" || string.Equals(value,"Upgrade",StringComparison.OrdinalIgnoreCase))
            {
                native="升级";
                return true;
            }
            if (value=="拆除" || string.Equals(value,"Demolish",StringComparison.OrdinalIgnoreCase))
            {
                native="拆除";
                return true;
            }
            if (value=="迁移" || value=="移动" || string.Equals(value,"Move",StringComparison.OrdinalIgnoreCase))
            {
                native=value=="移动" ? "移动" : "迁移";
                return true;
            }
            return false;
        }

        private static bool IsIconSensitiveItemNamePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            return path.EndsWith("ItemIcon(Clone)/Name",StringComparison.Ordinal)
                || path.EndsWith("ItemIcon/Name",StringComparison.Ordinal);
        }

        private static void ScheduleIconSafeTextTranslation(object instance)
        {
            if (instance==null) return;
            string path=BuildTransformPath(instance);
            if (!IsIconSensitiveItemNamePath(path)) return;
            long key=GetStableObjectKey(instance);
            lock(LayoutLock)
            {
                DeferredTextJob job;
                if (!DeferredTextJobs.TryGetValue(key,out job))
                {
                    job=new DeferredTextJob();
                    DeferredTextJobs[key]=job;
                }
                job.Component=instance;
                // Wait until the caller that assigned Name.text has fully returned. Some item prefabs
                // resolve their sprite immediately after assigning the original Chinese display name.
                // Two frames keeps that lookup key intact while remaining visually instantaneous.
                job.FramesLeft=2;
            }
        }

        private static void ProcessDeferredTextJobs()
        {
            lock(LayoutLock)
            {
                if (DeferredTextJobs.Count==0) return;
                List<long> remove=null;
                foreach (KeyValuePair<long,DeferredTextJob> kv in DeferredTextJobs)
                {
                    DeferredTextJob job=kv.Value;
                    if (job==null || job.Component==null)
                    {
                        if (remove==null) remove=new List<long>();
                        remove.Add(kv.Key);
                        continue;
                    }
                    job.FramesLeft--;
                    if (job.FramesLeft>0) continue;
                    try { ApplyIconSafePostSetTranslation(job.Component); } catch { }
                    if (remove==null) remove=new List<long>();
                    remove.Add(kv.Key);
                }
                if (remove!=null) for (int i=0;i<remove.Count;i++) DeferredTextJobs.Remove(remove[i]);
            }
        }

        private static void ApplyIconSafePostSetTranslation(object instance)
        {
            string path=BuildTransformPath(instance);
            if (!IsIconSensitiveItemNamePath(path)) return;

            PropertyInfo p=FindProperty(instance.GetType(),"text","Text");
            if (p==null || !p.CanRead || !p.CanWrite || p.PropertyType!=typeof(string)) return;
            string original=null;
            try { original=(string)p.GetValue(instance); } catch { return; }
            if (string.IsNullOrEmpty(original)) return;

            string translated=TranslateForContext(original,path);
            if (translated==original)
            {
                if (ContainsCjk(original)) DebugUnresolved(instance,original,original);
                return;
            }

            try
            {
                _postSetTranslationGuard=true;
                p.SetValue(instance,translated);
            }
            finally { _postSetTranslationGuard=false; }
            DebugHit(instance,original,translated);
            if (ContainsCjk(translated)) DebugUnresolved(instance,original,translated);
        }

        private static void TextOnEnablePostfix(object __instance)
        {
            if (__instance==null) return;
            try
            {
                PropertyInfo p=FindProperty(__instance.GetType(),"text","Text");
                if (p!=null && p.CanRead && p.CanWrite && p.PropertyType==typeof(string))
                {
                    string original=(string)p.GetValue(__instance);
                    if (!string.IsNullOrEmpty(original))
                    {
                        string path=BuildTransformPath(__instance);
                        string nativeBuildingAction;
                        if (IsBuildingActionChoicePath(path) && TryGetNativeBuildingActionLabel(original,out nativeBuildingAction))
                        {
                            if (original!=nativeBuildingAction)
                            {
                                try
                                {
                                    _postSetTranslationGuard=true;
                                    p.SetValue(__instance,nativeBuildingAction);
                                }
                                finally { _postSetTranslationGuard=false; }
                            }
                        }
                        else if (IsIconSensitiveItemNamePath(path))
                        {
                            ScheduleIconSafeTextTranslation(__instance);
                        }
                        else
                        {
                            string translated=TranslateForContext(original,path);
                            if (translated!=original)
                            {
                                p.SetValue(__instance,translated);
                                DebugHit(__instance,original,translated);
                            }
                            if (ContainsCjk(translated)) DebugUnresolved(__instance,original,translated);
                        }
                    }
                }
                if (_resizerEnabled) ApplyResizeRules(__instance);
            }
            catch { }
        }

        private static string TranslateForContext(string input,string path)
        {
            if (string.IsNullOrEmpty(input)) return input;
            long started=Stopwatch.GetTimestamp();
            string result=TranslateForContextCore(input,path);
            long elapsedTicks=Stopwatch.GetTimestamp()-started;
            double elapsedMs=(elapsedTicks*1000.0)/Stopwatch.Frequency;
            if (elapsedMs>=40.0 && _perfSlowCount<PerfSlowLimit)
            {
                _perfSlowCount++;
                MelonLogger.Warning("[PerfSlow "+_perfSlowCount+"/"+PerfSlowLimit+"] "+elapsedMs.ToString("F1")+" ms | len="+input.Length+" | "+OneLine(path,120)+" | "+OneLine(input,90));
            }
            return result;
        }

        private static string TranslateForContextCore(string input,string path)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // The HUD/area history controls contain the ENTIRE accumulated history in one Text value.
            // Translating 7k-15k characters as one regex subject can cause catastrophic backtracking
            // when the day advances. Split these histories into individual event lines; cached old lines
            // then cost almost nothing and a newly appended line is the only expensive work.
            if (IsAccumulatedHistoryPath(path) && input.IndexOf('\n')>=0)
                return TranslateAccumulatedHistory(input,path);

            return TranslateSingleContext(input,path);
        }

        private static bool IsAccumulatedHistoryPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            return path.IndexOf("HudPanel/InfoList/InfoListScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("AreaUIPanel/AreaUIBelow/AreaLog/Log/LogListScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("BattleUIPanel/InfoPanel/InfoUI/InfoScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("HeroDetailPanel/Log/LogListScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0;
        }

        private static string TranslateAccumulatedHistory(string input,string path)
        {
            string normalized=input.Replace("\r\n","\n");
            string[] lines=normalized.Split(new[]{'\n'});
            bool changed=false;
            for (int i=0;i<lines.Length;i++)
            {
                if (lines[i].Length==0) continue;
                string translated=TranslateSingleContext(lines[i],path);
                if (!string.Equals(translated,lines[i],StringComparison.Ordinal)) changed=true;
                lines[i]=translated;
            }
            if (!changed) return input;
            // Do not run FinalizeDisplay over the joined giant history again. Each line was finalized above.
            return string.Join("\n",lines);
        }

        private static string TranslateSingleContext(string input,string path)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // Tiny one-character research abbreviations are safe only on this exact HUD field.
            // Never add these as global tokens: the same characters are common in NPC names and prose.
            string researchLabel;
            if (TryResearchHudAbbreviation(input,path,out researchLabel))
                return researchLabel;

            string readBookGlyph;
            if (TryReadBookGlyph(input,path,out readBookGlyph))
                return readBookGlyph;

            string dialogueRecord;
            if (TryTranslateDialogueRecord(input,path,out dialogueRecord))
            {
                if (ContainsCjk(dialogueRecord)) dialogueRecord=NormalizeNpcMixedTitles(dialogueRecord);
                return FinalizeDisplay(dialogueRecord);
            }

            string nativeBuildingAction;
            if (IsBuildingActionChoicePath(path) && TryGetNativeBuildingActionLabel(input,out nativeBuildingAction))
                return nativeBuildingAction;

            string contextual;
            // Structural templates are global game-language rules. They operate on any NPC/name/value,
            // not on specific screenshots or specific characters.
            if (TryGlobalTemplate(input,out contextual))
                return FinalizeDisplay(contextual);

            string source=input;
            if (IsNpcDisplayPath(path) && ContainsCjk(source))
                source=TranslateNpcNamesInDisplay(source);
            if (IsBuildingLabelPath(path) && ContainsCjk(source))
                source=RepairBuildingLabel(source);

            string translated=Translate(source);
            if (IsSectShortNameContext(path) && ContainsCjk(translated))
                translated=NormalizeSectShortNames(translated);
            if (IsGeneratedLogContext(path))
                translated=NormalizeGeneratedLogMixed(translated);
            if (IsSkillDetailPath(path))
                translated=NormalizeSkillDetailMixed(translated);
            if (IsBattleInfoPath(path))
                translated=NormalizeBattleMixed(translated);
            translated=NormalizeLatestWaveMixed(translated,path);
            if (IsNpcDisplayPath(path) && ContainsCjk(translated))
                translated=NormalizeNpcMixedTitles(translated);
            return FinalizeDisplay(translated);
        }

        private static bool TryReadBookGlyph(string input,string path,out string result)
        {
            result=input;
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            if (path.IndexOf("ReadBookUIPanel/Paper/TextGrid/",StringComparison.Ordinal)<0 || !path.EndsWith("/NameText",StringComparison.Ordinal)) return false;
            if (input.Trim()=="融") { result="Merge"; return true; }
            return false;
        }

        private static bool IsSectShortNameContext(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            return path.IndexOf("HudPanel/InfoList/",StringComparison.Ordinal)>=0
                || path.IndexOf("AreaUIPanel/AreaUIBelow/AreaLog/",StringComparison.Ordinal)>=0
                || path.IndexOf("ContributionExchangePanel/",StringComparison.Ordinal)>=0
                || path.IndexOf("BattleUIPanel/",StringComparison.Ordinal)>=0
                || path.IndexOf("HeroDetailPanel/Log/",StringComparison.Ordinal)>=0
                || path.IndexOf("MonthMissionPanel/",StringComparison.Ordinal)>=0
                || path.IndexOf("RightPopInfoList/",StringComparison.Ordinal)>=0
                || path.IndexOf("BountyPanel/",StringComparison.Ordinal)>=0;
        }

        // Some generated history strings use a sect's two-character short label before a rank
        // instead of the full database name (e.g. 武当<color...>). Keep this contextual so
        // phrases such as 飞龙 inside martial-art names are never globally rewritten as sects.
        private static string NormalizeSectShortNames(string s)
        {
            if (string.IsNullOrEmpty(s) || !ContainsCjk(s)) return s;
            string[,] map=new string[,]
            {
                {"长乐","Changle Gang"}, {"药王","Yaowang Valley"}, {"丐","Beggars Sect"},
                {"飞龙","Flying Dragon Sect"}, {"茅山","Maoshan Sect"}, {"铸剑","Sword Forging Villa"},
                {"五毒","Five Poisons Sect"}, {"阎罗","Yama Palace"}, {"大隐","Dayin Pavilion"},
                {"少林","Shaolin Temple"}, {"武当","Wudang Sect"}, {"霸刀","Badao Sect"},
                {"蓬莱","Penglai Sect"}, {"峨眉","Emei Sect"}, {"崆峒","Kongtong Sect"},
                {"神机","Shenji Sect"}, {"霹雳","Thunderclap Hall"}, {"金刚","Vajra Esoteric Sect"},
                {"天山","Tianshan Sect"}, {"聚义","Alliance of Justice"}, {"黄河","Yellow River Gang"},
                {"八卦","Bagua Sect"}, {"海沙","Haisha Gang"}, {"铁掌","Iron Palm Gang"},
                {"仙霞","Xianxia Sect"}, {"巨鲸","Giant Whale Gang"}, {"金龙","Golden Dragon Gang"},
                {"青城","Qingcheng Sect"}, {"伏牛","Funiu Sect"}
            };
            for (int i=0;i<map.GetLength(0);i++)
            {
                string raw=map[i,0], en=map[i,1];
                if (s.IndexOf(raw,StringComparison.Ordinal)<0) continue;
                if (s==raw) { s=en; continue; }
                s=s.Replace(raw+"<color=",en+" <color=")
                   .Replace(raw+"\n",en+"\n")
                   .Replace(raw+"\r",en+"\r");
            }
            return s;
        }

        private static bool TryResearchHudAbbreviation(string input,string path,out string result)
        {
            result=input;
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            if (path.IndexOf("HudPanel/ForceUI/NowResearch/Text",StringComparison.Ordinal)<0) return false;
            string s=input.Trim();
            if (s=="锻") result="Forging";
            else if (s=="采") result="Extraction";
            else if (s=="生") result="Production";
            else if (s=="灵") result="Agility";
            else if (s=="研") result="Research";
            else if (s=="口") result="Eloquence";
            else if (s=="力") result="Strength";
            else if (s=="长") result="Polearm";
            else return false;
            return true;
        }


        private static bool IsGeneratedLogContext(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            return IsAccumulatedHistoryPath(path)
                || path.IndexOf("RightPopInfoList/RightPopInfoPrefab",StringComparison.Ordinal)>=0
                || path.IndexOf("MonthMissionPanel/",StringComparison.Ordinal)>=0
                || path.IndexOf("BountyPanel/",StringComparison.Ordinal)>=0;
        }

        // Generated world/sect/hero logs are assembled from many fragments by the game.
        // Translate only the leftover grammar markers here, where their meaning is unambiguous,
        // instead of adding dangerous global one-character/two-character tokens.
        private static string NormalizeGeneratedLogMixed(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            s=s.Replace("完成了重要Commission","completed an important commission")
               .Replace("Complete了重要Commission","completed an important commission")
               .Replace("completed了重要Commission","completed an important commission")
               .Replace("completed了重要","completed an important ")
               .Replace("Recruit了民间高手","recruited a local expert")
               .Replace("recruited了民间高手","recruited a local expert")
               .Replace("Desire steal","tried to steal")
               .Replace("Desire Snatching","tried to snatch")
               .Replace("Desire Steal techniques/insights","tried to steal techniques/insights")
               .Replace("进行Exploration","carry out Exploration")
               .Replace("Defense工事","defensive fortifications");

            // Building notifications such as "Xianxia Sect的Mine升为Level 2".
            s=Regex.Replace(s,@"(?<=[A-Za-z0-9\)>])的(?=[A-Za-z<])","'s ",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"升为\s*(?=Level\s*[0-9]+)","was upgraded to ",RegexOptions.CultureInvariant);
            // Conjunctions are safe here because this method only runs on generated logs/notifications.
            s=Regex.Replace(s,@"(?<=[A-Za-z0-9>])并(?=[A-Za-z<])"," and ",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<=[A-Za-z0-9>])而(?=[A-Za-z<])",", while ",RegexOptions.CultureInvariant);

            // Safe generated-stat grammar.
            s=Regex.Replace(s,@"(?<stat>Population|Security|Public Opinion|Defense|Fame|Reputation|Notoriety)\s*增加\s*(?<n>[+-]?[0-9]+)",
                "${stat} increased by ${n}",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<stat>Population|Security|Public Opinion|Defense|Fame|Reputation|Notoriety)\s*减少\s*(?<n>[+-]?[0-9]+)",
                "${stat} decreased by ${n}",RegexOptions.CultureInvariant);

            s=s.Replace("窃取","steal")
               .Replace("爆发","broke out")
               .Replace("由于","because ")
               .Replace("进行","carry out ");

            // Bounty text often leaves the locative 内 attached to an already translated place.
            s=s.Replace("内</b>","</b>");
            return s;
        }

        private static bool IsSkillDetailPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            return path.IndexOf("PopInfoPanel/QuickDetail/SkillDetail/Back/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("PopInfoPanel/QuickDetail/BookDetail/Back/Text",StringComparison.Ordinal)>=0;
        }

        // Skill cards use several one-character Chinese grade/body labels that are unsafe as
        // global tokens. They are safe and deterministic inside a skill-detail card.
        private static string NormalizeSkillDetailMixed(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // Scaling grades: 下 / 中 / 上 / 精 / 极.
            s=Regex.Replace(s,@"(?<open><color(?:=[^>]+)?>)下(?<close></color>)","${open}Low${close}",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<open><color(?:=[^>]+)?>)中(?<close></color>)","${open}Medium${close}",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<open><color(?:=[^>]+)?>)上(?<close></color>)","${open}High${close}",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<open><color(?:=[^>]+)?>)精(?<close></color>)","${open}Superior${close}",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<open><color(?:=[^>]+)?>)极(?<close></color>)","${open}Master${close}",RegexOptions.CultureInvariant);

            // Stance body locations are also one-character labels in the source.
            s=Regex.Replace(s,@"(?<![\u3400-\u9fff])头(?=[0-9])","Head ",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<![\u3400-\u9fff])胸(?=[0-9])","Chest ",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<![\u3400-\u9fff])腹(?=[0-9])","Abdomen ",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<![\u3400-\u9fff])手(?=[0-9])","Hand ",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<![\u3400-\u9fff])足(?=[0-9])","Foot ",RegexOptions.CultureInvariant);

            // Legacy EnglishPatch variants that all refer to the same canonical mechanics.
            s=s.Replace("Strengthen muscles","Tendon Ease")
               .Replace("Wound tendons","Tendon Injury")
               .Replace("Blood loss","Bleed")
               .Replace("Dizziness","Stun")
               .Replace("Blindness","Blind")
               .Replace("Physical Condition","Constitution")
               .Replace("Physical constitution","Constitution")
               .Replace("Sword Skills Potential","Sword Potential")
               .Replace("Offensive stance","Offensive Stance")
               .Replace("Defensive posture","Defensive Stance")
               .Replace("Defensive stance","Defensive Stance")
               .Replace("Upgrade Effects","Upgrade Effect")
               .Replace("Breakthrough effects","Breakthrough Effects")
               .Replace("Training requirements","Training Requirement");

            return NormalizeCanonicalEnglishTerms(s);
        }

        private static string NormalizeCanonicalEnglishTerms(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // 连击 as a percentage is the rate/chance stat. Plain "Combo" remains the action/count.
            if (s.IndexOf("Combo",StringComparison.Ordinal)>=0 && s.IndexOf('%')>=0)
                s=Regex.Replace(s,@"\bCombo(?! Rate)\s*(?=[+-]?\s*(?:[0-9]|\{\{))","Combo Rate ",RegexOptions.CultureInvariant);
            // 灼烧 as a percentage is the proc/status-application rate; the timed status label remains "Burning".
            if (s.IndexOf("Burning",StringComparison.Ordinal)>=0 && s.IndexOf('%')>=0)
                s=Regex.Replace(s,@"\bBurning(?=\s*[+-]?\s*[0-9])","Burn",RegexOptions.CultureInvariant);

            s=Regex.Replace(s,@"升为\s*Level\s*","was upgraded to Level ",RegexOptions.CultureInvariant);
            s=s.Replace("Basic Damage","Base Damage")
               .Replace("Inner Force Consumption","Inner Force Cost")
               .Replace("Inner Force consumption","Inner Force Cost")
               .Replace("Inner Power Consumption","Inner Force Cost")
               .Replace("Damage Bonus","Damage Scaling")
               .Replace("Offensive stance","Offensive Stance")
               .Replace("Attack Stance","Offensive Stance")
               .Replace("Defensive posture","Defensive Stance")
               .Replace("Defensive stance","Defensive Stance")
               .Replace("Defense Stance","Defensive Stance")
               .Replace("Upgrade Effects","Upgrade Effect")
               .Replace("Breakthrough effects","Breakthrough Effects")
               .Replace("Training requirements","Training Requirement")
               .Replace("Breakthrough Effectss","Breakthrough Effects")
               .Replace("Keen Sight","Insight")
               .Replace("Sharp Eye","Insight")
               .Replace("Blood Vitality","Blood Circulation")
               .Replace("Invigorate Blood","Blood Circulation")
               .Replace("Blood Activation","Blood Circulation")
               .Replace("Charge Power","Charge")
               .Replace("Negative Bonus","Negative Status Potency")
               .Replace("Negative Buff","Negative Status Potency")
               .Replace("Negative buffs","Negative Status Potency")
               .Replace("Negative modifier","Negative Status Potency")
               .Replace("Force absorption","Force Deflection")
               .Replace("The Layer ","Layer ");

            // Generated area/sect cards can leave a Chinese numeral after an already translated "Level".
            if (ContainsCjk(s) && s.IndexOf("Level",StringComparison.Ordinal)>=0)
            {
                string[,] levels=new string[,]
                {
                    {"零","0"},{"一","1"},{"二","2"},{"三","3"},{"四","4"},{"五","5"},
                    {"六","6"},{"七","7"},{"八","8"},{"九","9"},{"十","10"}
                };
                for (int i=0;i<levels.GetLength(0);i++)
                {
                    s=s.Replace("Level  "+levels[i,0],"Level "+levels[i,1])
                       .Replace("Level "+levels[i,0],"Level "+levels[i,1])
                       .Replace(levels[i,0]+" Level","Level "+levels[i,1]);
                }
            }

            // Canonical martial-art progression label: Layer N.
            string[,] layers=new string[,]
            {
                {"Chapter Zero","Layer 0"}, {"Zero layer","Layer 0"}, {"The zeroth layer","Layer 0"},
                {"First layer","Layer 1"}, {"The first layer","Layer 1"}, {"First level","Layer 1"},
                {"Second layer","Layer 2"}, {"The second layer","Layer 2"}, {"Second level","Layer 2"},
                {"Third layer","Layer 3"}, {"The third layer","Layer 3"}, {"Third level","Layer 3"},
                {"Fourth layer","Layer 4"}, {"The fourth layer","Layer 4"}, {"Fourth level","Layer 4"},
                {"Fifth layer","Layer 5"}, {"The fifth layer","Layer 5"}, {"Fifth level","Layer 5"},
                {"Sixth layer","Layer 6"}, {"The sixth layer","Layer 6"}, {"Sixth level","Layer 6"},
                {"Seventh layer","Layer 7"}, {"The seventh layer","Layer 7"}, {"Seventh level","Layer 7"},
                {"Eighth layer","Layer 8"}, {"The eighth layer","Layer 8"}, {"Eighth level","Layer 8"},
                {"Ninth layer","Layer 9"}, {"The ninth layer","Layer 9"}, {"Ninth level","Layer 9"},
                {"Tenth layer","Layer 10"}, {"The tenth layer","Layer 10"}, {"Tenth level","Layer 10"},
                {"First Layer","Layer 1"}, {"First Level","Layer 1"},
                {"Second Layer","Layer 2"}, {"Second Level","Layer 2"},
                {"Third Layer","Layer 3"}, {"Third Level","Layer 3"},
                {"Fourth Layer","Layer 4"}, {"Fourth Level","Layer 4"},
                {"Fifth Layer","Layer 5"}, {"Fifth Level","Layer 5"},
                {"Sixth Layer","Layer 6"}, {"Sixth Level","Layer 6"},
                {"Seventh Layer","Layer 7"}, {"Seventh Level","Layer 7"},
                {"Eighth Layer","Layer 8"}, {"Eighth Level","Layer 8"},
                {"Ninth Layer","Layer 9"}, {"Ninth Level","Layer 9"},
                {"Tenth Layer","Layer 10"}, {"Tenth Level","Layer 10"}
            };
            for (int i=0;i<layers.GetLength(0);i++)
                if (s.IndexOf(layers[i,0],StringComparison.Ordinal)>=0) s=s.Replace(layers[i,0],layers[i,1]);

            return s;
        }


        // TEST42: cleanup for mixed strings found in the latest unresolved wave.
        // Every aggressive rule is gated by a specific UI family so short Chinese grammar
        // fragments never become dangerous global tokens.
        private static string NormalizeLatestWaveMixed(string s,string path)
        {
            if (string.IsNullOrEmpty(s)) return s;
            path=NormalizeResizePath(path ?? string.Empty);

            bool generated=IsGeneratedLogContext(path);
            if (generated)
            {
                s=s.Replace("Manufacturing了","crafted ")
                   .Replace("Manufactured了","crafted ")
                   .Replace(" 清理Sect Warehouse，出售了数件闲置Equipment。"," cleared the Sect Warehouse and sold several unused pieces of equipment.")
                   .Replace("Demolish完 Become","was demolished.")
                   .Replace("，并 obtained ",", and obtained ")
                   .Replace(",并 obtained ",", and obtained ")
                   .Replace("，并Put it in the sect's Warehouse",", and placed it in the Sect Warehouse")
                   .Replace(" and Put it in the sect's Warehouse"," and placed it in the Sect Warehouse")
                   .Replace("并Put it in the sect's Warehouse","and placed it in the Sect Warehouse")
                   .Replace("JianghuJianghu","Jianghu");

                s=Regex.Replace(s,@"(?<stat>Max Health|Max Inner Force|Max Stamina|Martial Arts Potential|Fist Potential|Sword Potential|Blade Potential|Polearm Potential|Archery Potential|Qimen Potential|Internal Art Potential|Movement Art Potential|Body Art Potential)\s*增加\s*(?<n>[+-]?[0-9]+)",
                    "${stat} increased by ${n}",RegexOptions.CultureInvariant);

                s=Regex.Replace(s,@"与\s*Martial Arts\s*Prodigy\s*相结交","befriended a Martial Arts Prodigy",RegexOptions.CultureInvariant);
                s=Regex.Replace(s,@"(?<who>[A-Za-z][A-Za-z .'-]+?)近来诸事不顺，?心生退隐Jianghu之意[。.]?",
                    "${who} has had a run of bad luck lately and is considering retiring from the Jianghu.",RegexOptions.CultureInvariant);
                s=Regex.Replace(s,@"(?<who>[A-Za-z][A-Za-z .'-]+?)has had a run of bad luck lately，?心生退隐Jianghu之意[。.]?",
                    "${who} has had a run of bad luck lately and is considering retiring from the Jianghu.",RegexOptions.CultureInvariant);
                s=Regex.Replace(s,@"(?<who>[A-Za-z][A-Za-z .'-]+?)心灰意冷，?formally retired from the Jianghu[。.]?",
                    "${who} became disillusioned and formally retired from the Jianghu.",RegexOptions.CultureInvariant);

                // The old English patch romanized these idioms as if they were names.
                s=s.Replace(" Ji Rentianxiang，"," was blessed with good fortune and ")
                   .Replace(" Ji Xinggaozhao，"," was blessed with good fortune and ")
                   .Replace("，获得Reclusive ExpertDirections，"," and received guidance from a Reclusive Expert, ")
                   .Replace("获得Reclusive ExpertDirections，","received guidance from a Reclusive Expert, ");

                s=Regex.Replace(s,@"寻得Expert所刻石碑，?潜心研读后Increase\s*了(?<stat>[A-Za-z ]+Potential)",
                    "found a stone stele engraved by an expert and, after studying it closely, increased their ${stat}",
                    RegexOptions.CultureInvariant);

                s=s.Replace("held a盛大庆典，周遭民众颇为欢欣鼓舞","held a grand celebration, greatly lifting the spirits of the local people")
                   .Replace("海晏河清，仓禀充实，附近流民Farmer纷纷Go to 定居","enjoyed peace and prosperity; the granaries were full, and nearby refugees and farmers flocked there to settle");

                // Loyalty notifications: "Wei Xuhua对Xianxia Sect's Loyalty+64".
                s=Regex.Replace(s,@"(?<who>[A-Za-z][A-Za-z .'-]+?)对(?<target>[A-Za-z][A-Za-z .'-]+?)'s Loyalty(?<delta>[+-][0-9]+)",
                    "${who}'s Loyalty to ${target} ${delta}",RegexOptions.CultureInvariant);

                s=s.Replace("Xianxia Merit","Xianxia Sect Merit");
            }

            if (path.IndexOf("PopInfoPanel/SimpleText",StringComparison.Ordinal)>=0)
                s=s.Replace("Banquet评分","Banquet Rating");

            if (path.IndexOf("PopInfoPanel/QuickDetail/EventDetail/Back/Text",StringComparison.Ordinal)>=0)
            {
                s=s.Replace("采掘Materials","Mining Materials")
                   .Replace("无名地窖","Unnamed Cellar")
                   .Replace("下山游历","Journey Down the Mountain");
            }

            if (path.IndexOf("PopInfoPanel/QuickDetail/ObstacleDetail/Back/Text",StringComparison.Ordinal)>=0)
                s=s.Replace("花瓶","Vase");

            if (path.IndexOf("MissionUI/WorldEventScrollView/",StringComparison.Ordinal)>=0)
            {
                s=s.Replace("(二)","(II)")
                   .Replace("泰岳巨典","Taiyue Grand Assembly")
                   .Replace("失传Manual","Lost Manual");
            }

            if (path.IndexOf("MeetingPanel/InfoText",StringComparison.Ordinal)>=0)
            {
                s=s.Replace("Sect power 孱弱","Sect Power: Weak")
                   .Replace("The strength of the sect has declined.","Sect Power: Weak")
                   .Replace("Main方针","Main Strategy:")
                   .Replace("Development outline","Development Strategy:")
                   .Replace("This Month's Policy Internal AffairsPreparation","This Month's Policy: Internal Affairs Preparation")
                   .Replace("This Month's Policy Internal Affairs","This Month's Policy: Internal Affairs");
            }
            if (path.IndexOf("MeetingPanel/TalkPanel/",StringComparison.Ordinal)>=0)
                s=s.Replace("Task 和这4000 taelsFunds","task and these 4,000 taels of funding");

            if (path.IndexOf("SurePanel/SureMenu/Text",StringComparison.Ordinal)>=0)
            {
                s=Regex.Replace(s,@"Confirm you want to proceed\?(?<thing>.+?) Upgrade to 二 Is it level\?\s*\nApproximately needs (?<days>[0-9]+) Heavenly time",
                    "Upgrade ${thing} to Level 2?\\nEstimated time: ${days} days.",RegexOptions.CultureInvariant);
            }

            if (path.IndexOf("GameMenuPanel/GameInfoBack/ChapterInfo",StringComparison.Ordinal)>=0)
            {
                s=s.Replace("Sect间 可以攻击资源","Sects may attack resource sites")
                   .Replace("Sect间 可以攻击城镇","Sects may attack towns")
                   .Replace("Sect间 Can attack resources","Sects may attack resource sites")
                   .Replace("Sect间 Can attack towns","Sects may attack towns")
                   .Replace("Sect间 Cannot attack the main den","Sects may not attack headquarters")
                   .Replace("Sect间 Cannot attack headquarters","Sects may not attack headquarters")
                   .Replace("Sect间 不可攻击总舵","Sects may not attack headquarters");
            }

            if (path.IndexOf("PopInfoPanel/SimpleDetail/Layout/Back/Text",StringComparison.Ordinal)>=0)
            {
                s=s.Replace("Agility + Movement Art之和决定BasicDisplacement","Agility + Movement Art together determine Base Movement")
                   .Replace("BasicDisplacement","Base Movement")
                   .Replace("每Martial Arts of each tier都有最佳training count","Each Martial Arts tier has an optimal training count")
                   .Replace("♦with the other person进行Sparring比试","♦Spar with the other person")
                   .Replace("♦获WinAffection+2，落DefeatAffection+1","♦Win: Affection +2; Defeat: Affection +1")
                   .Replace("♦Increase 对方一门Martial Arts Experience，Martial Arts Level 比对方高越多，效果越好",
                            "♦Increase one of the other character's Martial Arts Experience; the greater your level advantage, the better the effect")
                   .Replace("♦Increase 2点Affection","♦Affection +2")
                   .Replace("♦Increase 1点Affection","♦Affection +1")
                   .Replace("♦消耗1点's goodwill","♦Costs 1 Affection")
                   .Replace("♦消耗2点's goodwill","♦Costs 2 Affection")
                   .Replace("♦Healing效果越好，Increase 越多's goodwill","♦The better the healing effect, the more Affection gained")
                   .Replace("♦向对方GiftsIncrease Affection","♦Give gifts to increase Affection")
                   .Replace("♦Equipment越贵重Increase 越多Affection","♦The more valuable the equipment, the more Affection gained")
                   .Replace("♦将Martial ArtsTraining至Layer 10获得 Talent points","♦Train a Martial Art to Layer 10 to gain Talent Points")
                   .Replace("♦将Martial ArtsTraining至Level 10获得 Talent points","♦Train a Martial Art to Layer 10 to gain Talent Points")
                   .Replace("♦ in Seclusion Chamber, spend Talent points unlock Talents","♦Spend Talent Points in the Seclusion Chamber to unlock Talents")
                   .Replace("♦ in Seclusion Chamber, spend  Talent points unlock Talents","♦Spend Talent Points in the Seclusion Chamber to unlock Talents")
                   .Replace("Rumor几名martial artists聚于","Rumor has it that several martial artists have gathered at ")
                   .Replace("Taishan脚下"," at the foot of Mount Tai")
                   .Replace("欲效仿赵点检举办“Taiyue Grand Assembly”","hoping to imitate Zhao Dianjian by holding the “Taiyue Grand Assembly”")
                   .Replace("Jianghu中一时","the Jianghu ")
                   .Replace("隐藏有前朝高手秘密Collection的Manual一本","contains a secret manual hidden away by a master of the former dynasty")
                   .Replace("隐藏有前朝高手秘密收藏的Manual一本","contains a secret manual hidden away by a master of the former dynasty");
                s=Regex.Replace(s,@"(?<=[A-Za-z>])的(?=[A-Za-z])"," ",RegexOptions.CultureInvariant);
            }

            if (path.IndexOf("ClothChoice",StringComparison.Ordinal)>=0)
                s=s.Replace("Prime Disciple Taoist Robe道","Prime Disciple Daoist Robe")
                   .Replace("Prime Disciple Daoist Robe道","Prime Disciple Daoist Robe")
                   .Replace("Taoist Robe道","Daoist Robe")
                   .Replace("Daoist Robe道","Daoist Robe");

            if (path.IndexOf("MissionUI/MailScrollView/",StringComparison.Ordinal)>=0)
            {
                s=s.Replace("Woo弟","Junior Brother Jin")
                   .Replace("欲寻人赌上两把","looking for someone for a few wagers");
            }

            if (path.IndexOf("PlotPanel/RecordScrollView/",StringComparison.Ordinal)>=0
                || path.IndexOf("DebatePanel/",StringComparison.Ordinal)>=0)
            {
                s=s.Replace("能师妹你语无伦次，言不及义","Junior Sister Neng, you're rambling incoherently and making no sense")
                   .Replace("能师妹 Vague and inexact","Junior Sister Neng, vague and inexact")
                   .Replace("能师妹 Can","Junior Sister Neng, can")
                   .Replace("能师妹 A single sentence","Junior Sister Neng, a single sentence")
                   .Replace("能师妹 ","Junior Sister Neng, ")
                   .Replace("我看是Senior Brother Wei你有问题吧","I think you're the one with the problem, Senior Brother Wei")
                   .Replace("是Disciple啊","Ah, Disciple,")
                   .Replace("Ah，是Elder Jin啊！","Ah, Elder Jin!")
                   .Replace("是Elder Jin啊","Ah, Elder Jin!")
                   .Replace("近来Elder Jinreputation has been rising，可谓at the height of their fame",
                            "Elder Jin's reputation has been rising lately and is now at its height")
                   .Replace("JianghuRumor，与我","Rumor has it that ")
                   .Replace("也是evenly matched"," is evenly matched with me")
                   .Replace("故而此次came specially","so I came especially")
                   .Replace("感谢各位同道看 in 我Xiang Yuanba三分薄面上赏光莅临","Thank you all for honoring me, Xiang Yuanba, with your presence")
                   .Replace("感谢各位同道看 in 我","Thank you all for honoring me, ")
                   .Replace("三分薄面上赏光莅临","with your presence")
                   .Replace("Great HeroBlessed by Heaven，All猜中，这大奖可谓实至名归了！恭喜恭喜！",
                            "Great Hero, fortune smiles upon you—you guessed every riddle correctly. This grand prize is well deserved! Congratulations!")
                   .Replace("Let me see, the Great Hero guessed it correctly 5 Next","Let's see... Great Hero, you answered 5 correctly.");
                s=Regex.Replace(s,@"让我看看，大侠总共猜对了(?<n>[0-9]+)次。\s*\n大侠吉人天相，全部猜中，这大奖可谓实至名归了！恭喜恭喜！",
                    "Let's see... Great Hero, you answered ${n} correctly.\\nGreat Hero, fortune smiles upon you—you guessed every riddle correctly. This grand prize is well deserved! Congratulations!",
                    RegexOptions.CultureInvariant);
                s=Regex.Replace(s,@"^一名(?<sect>.+?)(?<rank><color=[^>]+>Sect Leader</color>)\s*(?:in\s*)?has been lingering",
                    "A ${sect}${rank} has been lingering",RegexOptions.CultureInvariant);
                s=Regex.Replace(s,@"^一名(?<sect>.+?)(?<rank><color=[^>]+>掌门</color>)\s*has been lingering",
                    "A ${sect}<color=#FD1430>Sect Leader</color> has been lingering",RegexOptions.CultureInvariant);
            }

            if (path.IndexOf("PlotPanel/InteractGrid/",StringComparison.Ordinal)>=0)
            {
                s=Regex.Replace(s,@"(?<prefix>[A-Za-z][A-Za-z ]+\s)(?<name>[\u3400-\u9fff·.]{2,5})$",delegate(Match m)
                {
                    string v;
                    return TryCanonicalNpcDisplayName(m.Groups["name"].Value,out v)
                        ? m.Groups["prefix"].Value+v : m.Value;
                },RegexOptions.CultureInvariant);
            }

            return s;
        }

        private static bool IsBattleInfoPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            return path.IndexOf("BattleUIPanel/InfoPanel/InfoUI/InfoScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0;
        }

        // Battle history is very formulaic. By the time the general dictionary has run,
        // only a few Chinese grammar markers tend to remain. Fix them here instead of
        // creating dangerous global one-character tokens such as 对 / 点 / 合 / 被.
        private static string NormalizeBattleMixed(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s=s.Replace("。",".").Replace("，",", ");

            s=Regex.Replace(s,
                @"(?<a><color=[^>]+>[^<]+</color>)\s*对\s*(?<b><color=[^>]+>[^<]+</color>)\s*dealt critical damage\s*(?<n>[0-9]+)\s*(?:点|points?)",
                "${a} dealt ${n} critical damage to ${b}",
                RegexOptions.CultureInvariant);
            s=Regex.Replace(s,
                @"(?<a><color=[^>]+>[^<]+</color>)\s*对\s*(?<b><color=[^>]+>[^<]+</color>)\s*dealt\s*(?<n>[0-9]+)\s*(?:点|points?)",
                "${a} dealt ${n} damage to ${b}",
                RegexOptions.CultureInvariant);

            s=Regex.Replace(s,
                @"(?<a><color=[^>]+>[^<]+</color>)'s technique未能Accuracy\s*(?<b><color=[^>]+>[^<]+</color>)",
                "${a}'s technique failed to hit ${b}",
                RegexOptions.CultureInvariant);
            s=Regex.Replace(s,
                @"(?<a><color=[^>]+>[^<]+</color>)\s*触发了\s*",
                "${a} triggered ",
                RegexOptions.CultureInvariant);
            s=Regex.Replace(s,
                @"(?<a><color=[^>]+>[^<]+</color>)\s*激活\s*",
                "${a} activated ",
                RegexOptions.CultureInvariant);

            s=Regex.Replace(s,@"(?<n>[0-9]+)\s*合(?=[\)）\]\s]|$)","${n} rounds",RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<n>[0-9]+)\s*点(?=(?:\(|\[|。|\.|,|$))","${n} damage",RegexOptions.CultureInvariant);

            s=s.Replace("被HelmetPoisoning","Helmet Poisoning ")
               .Replace("被ArmorPoisoning","Armor Poisoning ")
               .Replace("被Footwear Poisoning","Footwear Poisoning ")
               .Replace("被Reflect Reduction","Reflect Reduction ")
               .Replace("被Reflect","Reflected ")
               .Replace("回合"," rounds");

            return s;
        }

        private static string NormalizeNpcMixedTitles(string s)
        {
            if (string.IsNullOrEmpty(s) || !ContainsCjk(s)) return s;

            // Explicitly translated English titles can end up next to a still-Chinese surname
            // after a partial token pass: 孟Great Hero / 井Elder / 朱Sect Leader.
            s=Regex.Replace(s,@"(?<surname>[\u3400-\u9fff]{1,2})(?<title>Great Hero|Vice Sect Leader|Elder|Sect Leader|Young Hero|Heroine|Master)",delegate(Match m)
            {
                string roman;
                if (!TryRomanizeSurnameOnly(m.Groups["surname"].Value,out roman)) return m.Value;
                return m.Groups["title"].Value+" "+roman;
            },RegexOptions.CultureInvariant);

            // Battle banter commonly abbreviates a companion to surname + kinship title.
            s=Regex.Replace(s,@"(?<surname>[\u3400-\u9fff]{1,2}?)(?<rel>师兄|师弟|师姐|师妹|兄|弟|姐|妹)",delegate(Match m)
            {
                string roman;
                if (!TryRomanizeSurnameOnly(m.Groups["surname"].Value,out roman)) return m.Value;
                string rel=m.Groups["rel"].Value;
                string title;
                if (rel=="师兄") title="Senior Brother";
                else if (rel=="师弟") title="Junior Brother";
                else if (rel=="师姐") title="Senior Sister";
                else if (rel=="师妹") title="Junior Sister";
                else if (rel=="兄" || rel=="弟") title="Brother";
                else title="Sister";
                return title+" "+roman;
            },RegexOptions.CultureInvariant);

            return s;
        }

        private static bool TryRomanizeSurnameOnly(string surname,out string roman)
        {
            roman=null;
            if (string.IsNullOrEmpty(surname)) return false;
            string best=null,bestRoman=null;
            foreach (KeyValuePair<string,string> kv in NpcSurnames)
            {
                if (!surname.Equals(kv.Key,StringComparison.Ordinal)) continue;
                if (best==null || kv.Key.Length>best.Length) { best=kv.Key; bestRoman=kv.Value; }
            }
            if (bestRoman==null) return false;
            roman=bestRoman;
            return true;
        }

        private static string FinalizeDisplay(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string cached;
            if (s.Length<=4096)
            {
                lock(CacheLock)
                    if (FinalizeCache.TryGetValue(s,out cached)) return cached;
            }

            string original=s;
            s=NormalizeSpacing(s);
            if (s.IndexOf('<')>=0) s=NormalizeRichTextSpacing(s);
            s=NormalizeCanonicalEnglishTerms(s);
            if (ContainsCjk(s)) s=NormalizeNpcMixedTitles(s);
            s=NormalizeAliases(s);

            if (original.Length<=4096)
            {
                lock(CacheLock)
                {
                    if (FinalizeCache.Count>=FinalizeCacheLimit) FinalizeCache.Clear();
                    FinalizeCache[original]=s;
                }
            }
            return s;
        }


        private static bool TryTranslateDialogueRecord(string input,string path,out string result)
        {
            result=input;
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            if (path.IndexOf("PlotPanel/RecordScrollView/Viewport/Content/Text",StringComparison.Ordinal)<0)
                return false;

            // History records often prepend "Speaker: " to an otherwise canonical line.
            // Translate each block independently so exact/regex rules for the dialogue body can still win.
            string[] blocks=input.Split(new string[]{"\n\n"},StringSplitOptions.None);
            bool changed=false;
            for (int i=0;i<blocks.Length;i++)
            {
                Match m=Regex.Match(blocks[i],@"^(?<speaker>[^:\n]{1,80}):\s*(?<body>[\s\S]+)$",RegexOptions.CultureInvariant);
                if (!m.Success)
                {
                    if (ContainsCjk(blocks[i]))
                    {
                        string blockSource=TranslateNpcNamesInDisplay(blocks[i]);
                        string block=Translate(blockSource);
                        block=NormalizeLatestWaveMixed(block,path);
                        if (ContainsCjk(block)) block=NormalizeNpcMixedTitles(block);
                        if (!string.Equals(block,blocks[i],StringComparison.Ordinal))
                        {
                            blocks[i]=block;
                            changed=true;
                        }
                    }
                    continue;
                }
                string speaker=m.Groups["speaker"].Value;
                if (ContainsCjk(speaker)) speaker=TranslateNpcNamesInDisplay(speaker);
                string bodySource=m.Groups["body"].Value;
                if (ContainsCjk(bodySource)) bodySource=TranslateNpcNamesInDisplay(bodySource);
                string body=Translate(bodySource);
                body=NormalizeLatestWaveMixed(body,path);
                if (ContainsCjk(body)) body=NormalizeNpcMixedTitles(body);
                if (body!=m.Groups["body"].Value || speaker!=m.Groups["speaker"].Value)
                {
                    blocks[i]=speaker+": "+body;
                    changed=true;
                }
            }
            if (!changed) return false;
            result=string.Join("\n\n",blocks);
            return true;
        }

        private static bool TryGlobalTemplate(string input,out string result)
        {
            Match m;
            // "You met <sect><rank><name> (initial favor +N)" notification.
            m=Regex.Match(input,@"^你结识了(?<who>.+?)\(初始好感(?<favor>(?:<color=[^>]+>)?[+\-]?\d+(?:</color>)?)\)$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string who=Translate(TranslateNpcNamesInDisplay(m.Groups["who"].Value));
                string favor=m.Groups["favor"].Value;
                result="Met "+NormalizeRichTextSpacing(who)+" (Initial Favor "+favor+")";
                return true;
            }

            // Generic affinity change notification for any NPC.
            m=Regex.Match(input,@"^(?<who>.+?)对你的(?<favor>(?:<color=[^>]+>)?好感[+\-]?\d+(?:</color>)?)\((?<rate>\d+%)\)$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string who=Translate(TranslateNpcNamesInDisplay(m.Groups["who"].Value));
                string favor=Translate(m.Groups["favor"].Value);
                result=NormalizeRichTextSpacing(who)+" — "+favor+" ("+m.Groups["rate"].Value+")";
                return true;
            }

            // Generic acquisition notice: works for any character and any item wrapped in rich text.
            m=Regex.Match(input,@"^(?<who>.+?)获得了\s*(?<item>.+)$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                result=NormalizeRichTextSpacing(Translate(TranslateNpcNamesInDisplay(m.Groups["who"].Value)))+" obtained "+NormalizeRichTextSpacing(Translate(m.Groups["item"].Value));
                return true;
            }

            // Generic join/recruitment notification used by several sect/NPC events.
            m=Regex.Match(input,@"^(?<who>(?:<color=[^>]+>)?.+?(?:</color>)?)\s*拜入了(?<sect>.+)$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string who=Translate(TranslateNpcNamesInDisplay(m.Groups["who"].Value));
                string sect=Translate(m.Groups["sect"].Value);
                result=NormalizeRichTextSpacing(who)+" joined "+NormalizeRichTextSpacing(sect);
                return true;
            }

            // TEST42 dynamic plot/event templates observed in the latest log.
            m=Regex.Match(input,@"^一名(?<who>.+?)在山门口徘徊已久，\s*\n似乎是为拜访本派掌门而来[。.]?$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string who=NormalizeRichTextSpacing(Translate(m.Groups["who"].Value));
                result="A "+who+" has been lingering by the mountain gate for quite some time.\nIt seems they have come to visit our Sect Leader.";
                return true;
            }

            m=Regex.Match(input,@"^近来(?<player>.+?)声名鹊起，可谓风头正劲。\s*\n江湖传闻，与我(?<target>.+?)也是难分高下，\s*\n故而此次特意前来讨教讨教[！!]$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string player=NormalizeRichTextSpacing(Translate(TranslateNpcNamesInDisplay(m.Groups["player"].Value)));
                string target=NormalizeRichTextSpacing(Translate(TranslateNpcNamesInDisplay(m.Groups["target"].Value)));
                result=player+" has risen to prominence lately and is at the height of their fame.\nRumor has it that you and "+target+" are evenly matched,\nso I came especially to test my skills against you!";
                return true;
            }

            m=Regex.Match(input,@"^让我看看，大侠总共猜对了(?<n>[0-9]+)次。\s*\n(?<tail>[\s\S]+)$",RegexOptions.CultureInvariant);
            if (m.Success && m.Groups["tail"].Value.IndexOf("全部猜中",StringComparison.Ordinal)>=0)
            {
                result="Let's see... Great Hero, you answered "+m.Groups["n"].Value+" correctly.\nGreat Hero, fortune smiles upon you—you guessed every riddle correctly. This grand prize is well deserved! Congratulations!";
                return true;
            }

            // Area/settlement administration log variants.
            m=Regex.Match(input,@"^\[(?:Faction)?(?<date>[0-9.]+)\](?<who>.+?) in (?<area>.+?)加强管理，使该地(?<kind>Population|Public Opinion|Defense)Increase (?<value>[0-9]+) points\.?$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string who=Translate(TranslateNpcNamesInDisplay(m.Groups["who"].Value));
                string area=Translate(m.Groups["area"].Value);
                string kind=m.Groups["kind"].Value;
                result="["+m.Groups["date"].Value+"] "+NormalizeRichTextSpacing(who)+" strengthened administration in "+NormalizeRichTextSpacing(area)+", increasing "+kind+" by "+m.Groups["value"].Value+" points.";
                return true;
            }

            // Basic faction work/food log.
            m=Regex.Match(input,@"^\[Faction(?<date>[0-9.]+)\](?<who>.+?) in (?<area>.+?)辛勤劳作，为Factiongained Food(?<value>[0-9]+)。?$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string who=Translate(TranslateNpcNamesInDisplay(m.Groups["who"].Value));
                string area=Translate(m.Groups["area"].Value);
                result="[Faction "+m.Groups["date"].Value+"] "+NormalizeRichTextSpacing(who)+" worked diligently in "+NormalizeRichTextSpacing(area)+", generating "+m.Groups["value"].Value+" Food for the faction.";
                return true;
            }

            // Sect quest: recruit N disciples. Handles both raw Chinese and the mixed
            // intermediate form produced by older token waves.
            m=Regex.Match(input,@"^(?:门派任务|Sect Quests?|Faction Quests?)\s*\n\s*(?:招募|Recruit)\s*(?<n>[0-9]+)\s*名\s*(?<rank><color=[^>]+>.+?</color>)\s*(?<progress>\([0-9]+/[0-9]+\))?$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string rank=Translate(m.Groups["rank"].Value);
                result="Sect Quest\nRecruit "+m.Groups["n"].Value+" "+NormalizeRichTextSpacing(rank);
                if (m.Groups["progress"].Success) result+="  "+m.Groups["progress"].Value;
                return true;
            }

            // Raw settlement administration log. Keep stat terminology canonical.
            m=Regex.Match(input,@"^(?<prefix>\[(?:世界|门派|他人)?[0-9.]+\])(?<who>.+?)在(?<area>.+?)加强管理，使该地(?<stat>人口|治安|民心|防御)提升(?<value>[0-9]+)点[。.]?$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string prefix=Translate(m.Groups["prefix"].Value);
                string who=Translate(TranslateNpcNamesInDisplay(m.Groups["who"].Value));
                string area=Translate(m.Groups["area"].Value);
                string stat=TranslateLogStat(m.Groups["stat"].Value);
                result=prefix+" "+NormalizeRichTextSpacing(who)+" strengthened administration in "+NormalizeRichTextSpacing(area)+", increasing "+stat+" by "+m.Groups["value"].Value+".";
                return true;
            }

            m=Regex.Match(input,@"^(?<prefix>\[(?:世界|门派|他人)?[0-9.]+\])(?<who>.+?)在(?<area>.+?)暗中破坏，使该地(?<stat>人口|治安|民心|防御)降低(?:了)?(?<value>[0-9]+)点[。.]?$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string prefix=Translate(m.Groups["prefix"].Value);
                string who=Translate(TranslateNpcNamesInDisplay(m.Groups["who"].Value));
                string area=Translate(m.Groups["area"].Value);
                string stat=TranslateLogStat(m.Groups["stat"].Value);
                result=prefix+" "+NormalizeRichTextSpacing(who)+" secretly sabotaged "+NormalizeRichTextSpacing(area)+", reducing "+stat+" by "+m.Groups["value"].Value+".";
                return true;
            }

            // Sect construction logs. These occur in large accumulated histories, so the
            // line-by-line path above makes these cheap and deterministic.
            m=Regex.Match(input,@"^(?<prefix>\[(?:世界|门派|他人)?[0-9.]+\])(?<sect>.+?)近日大兴土木，开始在(?<area>.+?)新建(?<building>.+)$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                result=Translate(m.Groups["prefix"].Value)+" "+NormalizeRichTextSpacing(Translate(m.Groups["sect"].Value))
                    +" began major construction, building a new "+NormalizeRichTextSpacing(Translate(m.Groups["building"].Value))
                    +" in "+NormalizeRichTextSpacing(Translate(m.Groups["area"].Value))+".";
                return true;
            }

            m=Regex.Match(input,@"^(?<prefix>\[(?:世界|门派|他人)?[0-9.]+\])(?<sect>.+?)近日大兴土木，开始修缮升级(?<target>.+?)\((?<level>[零一二三四五六七八九十]+)级\)$",RegexOptions.CultureInvariant);
            if (m.Success)
            {
                result=Translate(m.Groups["prefix"].Value)+" "+NormalizeRichTextSpacing(Translate(m.Groups["sect"].Value))
                    +" began major construction, renovating and upgrading "+NormalizeRichTextSpacing(Translate(m.Groups["target"].Value))
                    +" ("+NormalizeRichTextSpacing(Translate(m.Groups["level"].Value+"级"))+").";
                return true;
            }

            result=input; return false;
        }

        private static string TranslateLogStat(string stat)
        {
            if (stat=="人口") return "Population";
            if (stat=="治安") return "Security";
            if (stat=="民心") return "Public Opinion";
            if (stat=="防御") return "Defense";
            return Translate(stat);
        }

        private static bool IsNpcDisplayPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            // Presentation-only contexts. This never modifies HeroData, save names or lookup keys.
            return path.IndexOf("PopInfoPanel/QuickDetail/HeroDetail/Back/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("RightPopInfoList/RightPopInfoPrefab",StringComparison.Ordinal)>=0
                || path.EndsWith("HeroDetailPanel/Name",StringComparison.Ordinal)
                || path.EndsWith("PlotPanel/LeftFace/Name",StringComparison.Ordinal)
                || path.EndsWith("PlotPanel/RightFace/Name",StringComparison.Ordinal)
                // Area hero cards and hero-selection tabs are presentation-only name labels too.
                // TEST31: procedural NPCs shown here now use the same canonical NameData romanization
                // already used by popups, so e.g. 归斌布 is displayed as Gui Binbu everywhere.
                || path.EndsWith("/HeroName",StringComparison.Ordinal)
                || path.IndexOf("AreaHeroScrollView/Viewport/Content/",StringComparison.Ordinal)>=0 && path.EndsWith("HeroName",StringComparison.Ordinal)
                || path.IndexOf("HeroDetailPanel/HeroTabGrid/",StringComparison.Ordinal)>=0 && path.EndsWith("/Label",StringComparison.Ordinal)
                || path.IndexOf("ForceHeroSettingPanel/HeroList/ScrollView/Viewport/Content/HeroAISettingTab",StringComparison.Ordinal)>=0 && path.EndsWith("/Name",StringComparison.Ordinal)
                || path.IndexOf("PlotPanel/PlotTextBack/PlotText",StringComparison.Ordinal)>=0
                || path.IndexOf("PlotPanel/RecordScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("PlotPanel/InteractGrid/",StringComparison.Ordinal)>=0
                || path.IndexOf("AreaUIPanel/AreaUIBelow/AreaLog/Log/LogListScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("HudPanel/InfoList/InfoListScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("BattleUIPanel/InfoPanel/InfoUI/InfoScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("BattleUIPanel/HeroTalkUIPanel",StringComparison.Ordinal)>=0 && path.EndsWith("/Text",StringComparison.Ordinal)
                || path.EndsWith("BattleUIPanel/NowActiveHero/NameBack/Name",StringComparison.Ordinal)
                || path.IndexOf("BattleUIPanel/ActionBar/",StringComparison.Ordinal)>=0 && path.EndsWith("/Text",StringComparison.Ordinal)
                || path.IndexOf("HeroDetailPanel/Log/LogListScrollView/Viewport/Content/Text",StringComparison.Ordinal)>=0
                || path.IndexOf("BattleUIPanel/PrepareUIPanel",StringComparison.Ordinal)>=0
                || path.IndexOf("MonthMissionPanel/MonthMissionScrollView/Viewport/Content/MonthMissionButton",StringComparison.Ordinal)>=0
                || path.IndexOf("MeetingPanel",StringComparison.Ordinal)>=0
                || path.IndexOf("MissionPanel/MissionUI/MissionScrollView",StringComparison.Ordinal)>=0
                || path.IndexOf("BountyPanel",StringComparison.Ordinal)>=0
                || path.IndexOf("HeroIcon",StringComparison.Ordinal)>=0;
        }

        private static string TranslateNpcNamesInDisplay(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            string direct;
            string trimmed=input.Trim();
            if (TryCanonicalNpcDisplayName(trimmed,out direct))
            {
                int lead=input.Length-input.TrimStart().Length;
                int trail=input.Length-input.TrimEnd().Length;
                return input.Substring(0,lead)+direct+input.Substring(input.Length-trail);
            }

            string s=input;

            // Standalone notification prefixes: generated name followed by a stat/action.
            s=Regex.Replace(s,@"^(?<name>[\u3400-\u9fff·.]{2,5})(?=(?:\s|<|used\b|Shift\b|Silver\b|Fame\b|Merit\b|对|获得|使用|受到|恢复|增加|降低))",delegate(Match m)
            {
                string v;
                return TryCanonicalNpcDisplayName(m.Groups["name"].Value,out v) ? v : m.Value;
            },RegexOptions.CultureInvariant);

            // Notifications often place a generated NPC name directly before a colored stat delta.
            s=Regex.Replace(s,@"(?<![\u3400-\u9fff])(?<name>[\u3400-\u9fff·.]{2,5})(?=<color=)",delegate(Match m)
            {
                string v;
                return TryCanonicalNpcDisplayName(m.Groups["name"].Value,out v) ? v : m.Value;
            },RegexOptions.CultureInvariant);

            // Hero quick-detail/card: the dedicated name line is always treated as a name, never as ordinary vocabulary.
            s=Regex.Replace(s,@"(?<open><size(?:=[^>]+)?>)(?<name>[\u3400-\u9fff·.]{2,10})(?<close></size>)",delegate(Match m)
            {
                string v;
                return TryCanonicalNpcDisplayName(m.Groups["name"].Value,out v)
                    ? m.Groups["open"].Value+v+m.Groups["close"].Value
                    : m.Value;
            },RegexOptions.CultureInvariant);

            // Sect/rank rich text is commonly followed immediately by the NPC name.
            s=Regex.Replace(s,@"(?<prefix></color>)(?<ws>\s*)(?<name>[\u3400-\u9fff·.]{2,10})(?=(?:\s|$|\(|（|<|\\n|,|，|。|\.|!|！|\?|？|在|于|向|从|与|对|被|将|欲|正|已|攻|偷|抢|购买))",delegate(Match m)
            {
                string v;
                if (!TryCanonicalNpcDisplayName(m.Groups["name"].Value,out v)) return m.Value;
                string ws=m.Groups["ws"].Value;
                if (ws.Length==0) ws=" ";
                return m.Groups["prefix"].Value+ws+v;
            },RegexOptions.CultureInvariant);

            // Promotion logs use "将<Name>身份..." and retirement logs put the name
            // immediately before 心灰意冷. These are presentation-only generated-name slots.
            s=Regex.Replace(s,@"(?<prefix>将)(?<name>[\u3400-\u9fff·.]{2,5})(?=身份)",delegate(Match m)
            {
                string v;
                return TryCanonicalNpcDisplayName(m.Groups["name"].Value,out v)
                    ? m.Groups["prefix"].Value+v
                    : m.Value;
            },RegexOptions.CultureInvariant);
            s=Regex.Replace(s,@"(?<name>[\u3400-\u9fff·.]{2,5})(?=心灰意冷)",delegate(Match m)
            {
                string v;
                return TryCanonicalNpcDisplayName(m.Groups["name"].Value,out v) ? v : m.Value;
            },RegexOptions.CultureInvariant);

            // TEST33: generated NPC names also appear inside rich-text log spans and as
            // speaker prefixes. Romanize only CJK-only candidates that pass the canonical
            // NameData surname + given-name syllable resolver.
            s=Regex.Replace(s,@"(?<open><color(?:=[^>]+)?>)(?<name>[\u3400-\u9fff·.]{2,5})(?<close></color>)",delegate(Match m)
            {
                string v;
                return TryCanonicalNpcDisplayName(m.Groups["name"].Value,out v)
                    ? m.Groups["open"].Value+v+m.Groups["close"].Value
                    : m.Value;
            },RegexOptions.CultureInvariant);

            s=Regex.Replace(s,@"(?<![\u3400-\u9fff])(?<name>[\u3400-\u9fff·.]{2,5})(?=\s*[:：])",delegate(Match m)
            {
                string v;
                return TryCanonicalNpcDisplayName(m.Groups["name"].Value,out v) ? v : m.Value;
            },RegexOptions.CultureInvariant);

            // Lines consisting only of an NPC name (hero cards and several popup prefabs).
            string[] lines=s.Split(new string[]{"\r\n","\n"},StringSplitOptions.None);
            bool changed=false;
            for (int i=0;i<lines.Length;i++)
            {
                string v;
                if (TryCanonicalNpcDisplayName(lines[i].Trim(),out v)) { lines[i]=v; changed=true; }
            }
            if (changed) s=string.Join("\n",lines);
            return s;
        }

        private static bool TryCanonicalNpcDisplayName(string raw,out string display)
        {
            display=null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string name=raw.Trim().Replace("·",string.Empty).Replace(".",string.Empty);
            if (NpcNames.TryGetValue(name,out display)) return true;
            return TryRomanizeGeneratedNpcName(name,out display);
        }

        private static bool TryRomanizeGeneratedNpcName(string name,out string display)
        {
            display=null;
            if (string.IsNullOrEmpty(name) || name.Length<2 || name.Length>5 || !ContainsCjk(name)) return false;

            string surname=null, surnameRoman=null;
            // Longest surname wins, so compound surnames such as 司马/欧阳/尉迟 are stable.
            foreach (KeyValuePair<string,string> kv in NpcSurnames)
            {
                if (!name.StartsWith(kv.Key,StringComparison.Ordinal)) continue;
                if (surname==null || kv.Key.Length>surname.Length) { surname=kv.Key; surnameRoman=kv.Value; }
            }
            if (string.IsNullOrEmpty(surname) || surname.Length>=name.Length) return false;

            string given=name.Substring(surname.Length);
            StringBuilder g=new StringBuilder();
            for (int i=0;i<given.Length;i++)
            {
                string syllable;
                if (!NpcGivenChars.TryGetValue(given[i].ToString(),out syllable)) return false;
                if (g.Length==0) g.Append(syllable);
                else g.Append(syllable.ToLowerInvariant());
            }
            display=surnameRoman+" "+g.ToString();
            return true;
        }

        private static bool IsBuildingLabelPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path=NormalizeResizePath(path);
            return path.EndsWith("AreaBuildingUI(Clone)/Back/BuildingName",StringComparison.Ordinal)
                || path.EndsWith("BuildingUIPanel/BuildingUI/Name",StringComparison.Ordinal)
                || path.EndsWith("BuildQuickButtonPrefab(Clone)/Text",StringComparison.Ordinal);
        }

        private static string RepairBuildingLabel(string input)
        {
            string direct;
            if (BuildingNames.TryGetValue(input,out direct)) return direct;
            // Some game UI code abbreviates/mutates Chinese building labels before presentation.
            // Resolve that presentation-only label against the game's complete BuildingData name set.
            string bestKey=null,bestValue=null; int best=99;
            foreach (KeyValuePair<string,string> kv in BuildingNames)
            {
                int d=EditDistanceBounded(input,kv.Key,2);
                if (d<best) { best=d; bestKey=kv.Key; bestValue=kv.Value; if (d==0) break; }
            }
            int allowed=input.Length<=3 ? 1 : 2;
            return best<=allowed && bestValue!=null ? bestValue : input;
        }

        private static int EditDistanceBounded(string a,string b,int cutoff)
        {
            if (Math.Abs(a.Length-b.Length)>cutoff) return cutoff+1;
            int[] prev=new int[b.Length+1], cur=new int[b.Length+1];
            for (int j=0;j<=b.Length;j++) prev[j]=j;
            for (int i=1;i<=a.Length;i++)
            {
                cur[0]=i; int row=cur[0];
                for (int j=1;j<=b.Length;j++)
                {
                    int cost=a[i-1]==b[j-1]?0:1;
                    int v=Math.Min(Math.Min(cur[j-1]+1,prev[j]+1),prev[j-1]+cost);
                    cur[j]=v; if (v<row) row=v;
                }
                if (row>cutoff) return cutoff+1;
                int[] tmp=prev; prev=cur; cur=tmp;
            }
            return prev[b.Length];
        }

        private static string NormalizeRichTextSpacing(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s=Regex.Replace(s,@"(?<=[A-Za-z0-9\)])(?=<color=)"," ");
            s=Regex.Replace(s,@"(?<=</color>)(?=[A-Za-z0-9\u3400-\u9fff])"," ");
            s=Regex.Replace(s,@"(?<=[A-Za-z0-9])(?=<size=)"," ");
            s=Regex.Replace(s,@"(?<=</size>)(?=[A-Za-z0-9\u3400-\u9fff])"," ");
            return s;
        }

        private static string Translate(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            string cached;
            if (input.Length<=4096)
            {
                lock(CacheLock)
                    if (TranslationCache.TryGetValue(input,out cached)) return cached;
            }

            string s=input;
            string v;
            if (Exact.TryGetValue(input,out v)) s=v;
            else if (FallbackExact.TryGetValue(input,out v)) s=v;
            else if (ContainsCjk(input))
            {
                string regexResult;
                if (TryRegex(input,out regexResult)) s=regexResult;
            }

            // Even an exact/autogen translation may still contain Chinese fragments.
            // Run safe dynamic regex rules on the partially translated result too (dates, durations, currency, etc.)
            // before falling back to canonical token replacement.
            for (int pass=0;pass<6 && ContainsCjk(s);pass++)
            {
                string next=s;
                string partialRegex;
                if (TryRegex(next,out partialRegex) && partialRegex!=next) next=partialRegex;
                next=ReplaceTokens(next);
                next=ApplyContextualGrades(input,next);
                if (next==s) break;
                s=next;
            }
            if (input.Length<=4096)
            {
                lock(CacheLock)
                {
                    if (TranslationCache.Count>=TranslationCacheLimit) TranslationCache.Clear();
                    TranslationCache[input]=s;
                }
            }
            return s;
        }

        private static bool TryRegex(string input,out string result)
        {
            // Dynamic templates are intended for one UI sentence/event, not an accumulated history blob.
            // This is a hard safety net against catastrophic regex backtracking on very long strings.
            if (string.IsNullOrEmpty(input) || input.Length>2048) { result=input; return false; }
            HashSet<char> seenChars=new HashSet<char>();
            for (int ci=0;ci<input.Length;ci++)
            {
                char ch=input[ci];
                if (!IsCjk(ch) || !seenChars.Add(ch)) continue;
                List<RegexRule> list;
                if (!RegexByTriggerChar.TryGetValue(ch,out list)) continue;
                for (int i=0;i<list.Count;i++)
                {
                    try
                    {
                        RegexRule rr=list[i];
                        if (rr.Trigger.Length>0 && input.IndexOf(rr.Trigger,StringComparison.Ordinal)<0) continue;
                        if (!rr.Pattern.IsMatch(input)) continue;
                        result=rr.Pattern.Replace(input,rr.Replacement);
                        return true;
                    }
                    catch { }
                }
            }
            result=input; return false;
        }

        private static string ReplaceTokens(string s)
        {
            StringBuilder sb = null;
            int i = 0;
            while (i < s.Length)
            {
                TrieNode node = TokenRoot;
                string best = null;
                int bestLen = 0;
                int j = i;
                while (j < s.Length)
                {
                    TrieNode next;
                    if (!node.Next.TryGetValue(s[j], out next)) break;
                    node = next;
                    j++;
                    if (node.Value != null)
                    {
                        best = node.Value;
                        bestLen = j - i;
                    }
                }
                if (best != null)
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(s.Length + 24);
                        if (i > 0) sb.Append(s, 0, i);
                    }
                    sb.Append(best);
                    i += bestLen;
                }
                else
                {
                    if (sb != null) sb.Append(s[i]);
                    i++;
                }
            }
            return sb == null ? s : sb.ToString();
        }

        private static string ApplyContextualGrades(string original,string translated)
        {
            bool treasure = original.IndexOf("品相",StringComparison.Ordinal)>=0 || original.IndexOf("年代",StringComparison.Ordinal)>=0 ||
                            original.IndexOf("材质",StringComparison.Ordinal)>=0 || original.IndexOf("工艺",StringComparison.Ordinal)>=0 ||
                            translated.IndexOf("Condition",StringComparison.OrdinalIgnoreCase)>=0 || translated.IndexOf("Craftsmanship",StringComparison.OrdinalIgnoreCase)>=0;
            if (treasure)
            {
                translated=ReplaceStandaloneChar(translated,'残',"Broken");
                translated=ReplaceStandaloneChar(translated,'下',"Novice");
                translated=ReplaceStandaloneChar(translated,'中',"Medium");
                translated=ReplaceStandaloneChar(translated,'良',"High");
                translated=ReplaceStandaloneChar(translated,'极',"Master");
                translated=ReplaceStandaloneChar(translated,'珍',"Treasure");
            }
            bool ratings = original.IndexOf('|')>=0 && (original.IndexOf("力道",StringComparison.Ordinal)>=0 || original.IndexOf("内功",StringComparison.Ordinal)>=0 || original.IndexOf("医术",StringComparison.Ordinal)>=0);
            if (ratings)
            {
                translated=ReplaceStandaloneChar(translated,'上',"Advanced");
                translated=ReplaceStandaloneChar(translated,'中',"Intermediate");
                translated=ReplaceStandaloneChar(translated,'下',"Novice");
                translated=ReplaceStandaloneChar(translated,'精',"Expert");
            }
            return translated;
        }

        private static string ReplaceStandaloneChar(string s,char token,string value)
        {
            StringBuilder b=null;
            for (int i=0;i<s.Length;i++)
            {
                if (s[i]!=token) continue;
                bool left=i==0 || !IsCjk(s[i-1]);
                bool right=i==s.Length-1 || !IsCjk(s[i+1]);
                if (!left || !right) continue;
                if (b==null) b=new StringBuilder(s);
                b.Remove(i,1); b.Insert(i,value);
                s=b.ToString();
                i += value.Length-1;
                b=null;
            }
            return s;
        }

        private static string NormalizeAliases(string s)
        {
            if (string.IsNullOrEmpty(s) || Aliases.Count==0) return s;
            HashSet<char> present=new HashSet<char>();
            for (int i=0;i<s.Length;i++) present.Add(char.ToUpperInvariant(s[i]));

            for (int i=0;i<Aliases.Count;i++)
            {
                string from=Aliases[i].Key, to=Aliases[i].Value;
                if (string.IsNullOrEmpty(from) || !present.Contains(char.ToUpperInvariant(from[0]))) continue;
                int pos=0;
                while ((pos=s.IndexOf(from,pos,StringComparison.OrdinalIgnoreCase))>=0)
                {
                    s=s.Substring(0,pos)+to+s.Substring(pos+from.Length);
                    for (int j=0;j<to.Length;j++) present.Add(char.ToUpperInvariant(to[j]));
                    pos+=to.Length;
                }
            }
            return s;
        }

        private static string NormalizeSpacing(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            bool hasDigit=false;
            for (int i=0;i<s.Length;i++) if (char.IsDigit(s[i])) { hasDigit=true; break; }
            if (!hasDigit) return s;

            string[] labels = {
                "Max Health","Max Inner Force","Inner Force Recovery","Carry Weight Limit","Armor","Movement Art",
                "Internal Art","Body Art","Strength","Agility","Constitution","Willpower","Intelligence","Meridians",
                "Fist","Sword","Blade","Polearm","Qimen","Archery","Medicine","Poison","Knowledge","Eloquence",
                "Extraction","Gathering","Forging","Alchemy","Cooking","Health","Inner Force","Stamina","Speed",
                "Weight","Value","Damage Reduction","HP Recovery","Population","Security","Public Opinion","Defense",
                "Appraisal Expertise","Number of uses per battle"
            };
            for (int i=0;i<labels.Length;i++)
            {
                string label=labels[i];
                int pos=0;
                while ((pos=s.IndexOf(label,pos,StringComparison.Ordinal))>=0)
                {
                    int after=pos+label.Length;
                    if (after>=s.Length) break;
                    int numberAt=after;
                    if (s[numberAt]=='+' || s[numberAt]=='-') numberAt++;
                    if (numberAt<s.Length && char.IsDigit(s[numberAt]))
                    {
                        s=s.Insert(after," ");
                        pos=after+1;
                    }
                    else pos=after;
                }
            }
            return s;
        }

        private static int LoadTsv(string path, Dictionary<string,string> target, bool tokens)
        {
            if (!File.Exists(path)) { MelonLogger.Warning("Missing translation data: " + path); return 0; }
            int count = 0;
            foreach (string raw in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#", StringComparison.Ordinal)) continue;
                int tab = raw.IndexOf('\t'); if (tab <= 0) continue;
                string k = Unescape(raw.Substring(0, tab));
                string v = Unescape(raw.Substring(tab + 1));
                if (k.Length == 0) continue;
                if (tokens) AddToken(k, v); else target[k] = v;
                count++;
            }
            return count;
        }

        private static int LoadAliases(string path)
        {
            Dictionary<string,string> d=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            int n=LoadTsv(path,d,false);
            foreach (KeyValuePair<string,string> kv in d) Aliases.Add(kv);
            Aliases.Sort((a,b)=>b.Key.Length.CompareTo(a.Key.Length));
            return n;
        }

        private static int LoadRegex(string path)
        {
            if (!File.Exists(path)) return 0;
            int count=0;
            foreach (string raw in File.ReadLines(path,Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#",StringComparison.Ordinal)) continue;
                int tab1=raw.IndexOf('\t'); if (tab1<=0) continue;
                int tab2=raw.IndexOf('\t',tab1+1); if (tab2<=tab1) continue;
                string p=Unescape(raw.Substring(0,tab1));
                string r=Unescape(raw.Substring(tab1+1,tab2-tab1-1));
                string trigger=Unescape(raw.Substring(tab2+1));
                try
                {
                    RegexRule rr=new RegexRule{Pattern=new Regex(p,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(25)),Replacement=r,Trigger=trigger};
                    if (trigger.Length==0) continue;
                    char key=trigger[0];
                    List<RegexRule> list;
                    if (!RegexByTriggerChar.TryGetValue(key,out list)) { list=new List<RegexRule>(); RegexByTriggerChar[key]=list; }
                    list.Add(rr); count++;
                }
                catch (Exception ex) { MelonLogger.Warning("Skipped invalid regex: "+p+" | "+ex.Message); }
            }
            foreach (KeyValuePair<char,List<RegexRule>> kv in RegexByTriggerChar)
                kv.Value.Sort((a,b)=>b.Trigger.Length.CompareTo(a.Trigger.Length));
            return count;
        }

        private static void AddToken(string key, string value)
        {
            TrieNode node = TokenRoot;
            for (int i=0;i<key.Length;i++)
            {
                TrieNode next;
                if (!node.Next.TryGetValue(key[i], out next)) { next = new TrieNode(); node.Next[key[i]] = next; }
                node = next;
            }
            node.Value = value;
        }

        private static string Unescape(string s)
        {
            StringBuilder b = new StringBuilder(s.Length);
            for (int i=0;i<s.Length;i++)
            {
                if (s[i]=='\\' && i+1<s.Length)
                {
                    char n=s[++i];
                    if (n=='n') b.Append('\n');
                    else if (n=='r') b.Append('\r');
                    else if (n=='t') b.Append('\t');
                    else if (n=='\\') b.Append('\\');
                    else { b.Append('\\'); b.Append(n); }
                }
                else b.Append(s[i]);
            }
            return b.ToString();
        }

        private static void DebugHit(object component, string original, string translated)
        {
            if (_debugCount >= DebugLimit) return;
            string key = original.Length > 180 ? original.Substring(0,180) : original;
            lock (DebugLock)
            {
                if (_debugCount >= DebugLimit || !DebugSeen.Add(key)) return;
                _debugCount++;
            }
            string path = component == null ? string.Empty : BuildTransformPath(component);
            MelonLogger.Msg("[Canonical " + _debugCount + "/" + DebugLimit + "] " + OneLine(original,110) + " => " + OneLine(translated,110) + (path.Length>0 ? " | " + path : string.Empty));
        }

        private static void DebugUnresolved(object component,string original,string translated)
        {
            if (_unresolvedCount>=UnresolvedLimit) return;
            string path=component==null?string.Empty:BuildTransformPath(component);
            string normalizedPath=NormalizeResizePath(path);
            // PlotText is the live typewriter buffer. Logging every growing prefix can consume
            // hundreds of unresolved slots for one sentence. The final dialogue is still captured
            // by RecordScrollView, so suppress only this transient diagnostic noise.
            if (normalizedPath.IndexOf("PlotPanel/PlotTextBack/PlotText",StringComparison.Ordinal)>=0) return;

            // Accumulated HUD/battle histories are rewritten as a whole every time a new event is added.
            // Log the actual unresolved lines, not the giant changing buffer. This preserves useful
            // diagnostics while avoiding hundreds of duplicate entries for the same history.
            if (IsAccumulatedHistoryPath(path) && translated.IndexOf('\n')>=0)
            {
                string[] parts=translated.Replace("\r\n","\n").Split(new[]{'\n'});
                for (int i=0;i<parts.Length && _unresolvedCount<UnresolvedLimit;i++)
                {
                    if (parts[i].Length==0 || !ContainsCjk(parts[i])) continue;
                    LogUnresolvedLine(parts[i],path);
                }
                return;
            }

            LogUnresolvedLine(translated,path);
        }

        private static void LogUnresolvedLine(string translated,string path)
        {
            if (_unresolvedCount>=UnresolvedLimit || string.IsNullOrEmpty(translated)) return;
            string key=translated.Length>180?translated.Substring(0,180):translated;
            lock(DebugLock)
            {
                if (_unresolvedCount>=UnresolvedLimit || !UnresolvedSeen.Add(key)) return;
                _unresolvedCount++;
            }
            MelonLogger.Warning("[Unresolved " + _unresolvedCount + "/" + UnresolvedLimit + "] " + OneLine(translated,130) + (path.Length>0?" | "+path:string.Empty));
        }

        private static string OneLine(string s, int max)
        {
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            return s.Length <= max ? s : s.Substring(0,max) + "...";
        }

        // ---------------- DragonHier-style YAML Resizer ----------------
        // Uses the same YAML concepts as Xyzj2OverLlm/DragonHierOverLlm, but is
        // implemented here with reflection so it can run under MelonLoader IL2CPP.
        // Every application is recalculated from a cached ORIGINAL baseline.
        // This makes repeated set_text/OnEnable calls idempotent and prevents
        // percentage rules from shrinking the same control again and again.
        private static int LoadResizerDirectory(string dir)
        {
            ResizeRules.Clear();
            lock (LayoutLock) { LayoutBaselines.Clear(); DeferredLayoutJobs.Clear(); }
            if (!Directory.Exists(dir)) return 0;
            string[] files=Directory.GetFiles(dir,"*.yaml",SearchOption.TopDirectoryOnly);
            Array.Sort(files,StringComparer.OrdinalIgnoreCase);
            for (int i=0;i<files.Length;i++) LoadResizerYaml(files[i]);
            return ResizeRules.Count;
        }

        private static void LoadResizerYaml(string path)
        {
            ResizeRule current=null;
            foreach (string raw in File.ReadLines(path,Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string line=raw.Trim();
                if (line.StartsWith("#",StringComparison.Ordinal)) continue;
                if (line.StartsWith("- ",StringComparison.Ordinal))
                {
                    if (current!=null) FinalizeResizeRule(current);
                    current=new ResizeRule();
                    line=line.Substring(2).Trim();
                    ParseResizeYamlProperty(current,line);
                }
                else if (current!=null)
                {
                    ParseResizeYamlProperty(current,line);
                }
            }
            if (current!=null) FinalizeResizeRule(current);
        }

        private static void ParseResizeYamlProperty(ResizeRule r,string line)
        {
            int colon=line.IndexOf(':');
            if (colon<=0) return;
            string key=line.Substring(0,colon).Trim();
            string value=UnquoteYaml(line.Substring(colon+1).Trim());
            double n; bool b;
            if (key=="path") r.Path=NormalizeResizePath(value);
            else if (key=="idealFontSize" && TryParseDouble(value,out n)) r.IdealFontSize=n;
            else if (key=="fontPercentage" && TryParseDouble(value,out n)) r.FontPercentage=n;
            else if (key=="allowWordWrap" && bool.TryParse(value,out b)) r.AllowWordWrap=b;
            else if (key=="allowAutoSizing" && bool.TryParse(value,out b)) r.AllowAutoSizing=b;
            else if (key=="allowLeftTrimText" && bool.TryParse(value,out b)) r.AllowLeftTrimText=b;
            else if (key=="adjustX" && TryParseDouble(value,out n)) r.AdjustX=n;
            else if (key=="adjustY" && TryParseDouble(value,out n)) r.AdjustY=n;
            else if (key=="adjustWidth" && TryParseDouble(value,out n)) r.AdjustWidth=n;
            else if (key=="adjustHeight" && TryParseDouble(value,out n)) r.AdjustHeight=n;
            else if (key=="minFontSize" && TryParseDouble(value,out n)) r.MinFontSize=n;
            else if (key=="maxFontSize" && TryParseDouble(value,out n)) r.MaxFontSize=n;
            else if (key=="lineSpacing" && TryParseDouble(value,out n)) r.LineSpacing=n;
            else if (key=="characterSpacing" && TryParseDouble(value,out n)) r.CharacterSpacing=n;
            else if (key=="wordSpacing" && TryParseDouble(value,out n)) r.WordSpacing=n;
            else if (key=="alignment") r.Alignment=value;
            else if (key=="overflow") r.Overflow=value;
            // sampleText is intentionally ignored: it is diagnostic metadata only.
        }

        private static void FinalizeResizeRule(ResizeRule r)
        {
            if (r==null || string.IsNullOrEmpty(r.Path)) return;
            if (r.Path=="/*" || r.Path=="*") r.MatchAll=true;
            else if (r.Path.IndexOf('*')>=0)
            {
                string rx="^"+Regex.Escape(r.Path).Replace("\\*","[^/]*")+"$";
                r.Pattern=new Regex(rx,RegexOptions.CultureInvariant);
            }
            ResizeRules.Add(r);
        }

        private static string NormalizeResizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            p=p.Replace('\\','/').Trim();
            while (p.StartsWith("/",StringComparison.Ordinal) && p!="/*") p=p.Substring(1);
            while (p.EndsWith("/",StringComparison.Ordinal) && p.Length>1) p=p.Substring(0,p.Length-1);
            return p;
        }

        private static string UnquoteYaml(string s)
        {
            if (s.Length>=2 && ((s[0]=='"' && s[s.Length-1]=='"') || (s[0]=='\'' && s[s.Length-1]=='\'')))
            {
                char q=s[0]; s=s.Substring(1,s.Length-2);
                if (q=='"') s=s.Replace("\\\"","\"").Replace("\\n","\n").Replace("\\r","\r").Replace("\\t","\t").Replace("\\\\","\\");
                else s=s.Replace("''","'");
            }
            return s;
        }

        private static bool TryParseDouble(string s,out double v)
        {
            return double.TryParse(s,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out v);
        }

        private static bool ResizeRuleMatches(ResizeRule r,string path)
        {
            if (r.MatchAll) return true;
            if (r.Pattern!=null)
            {
                if (r.Pattern.IsMatch(path)) return true;
                // IL2CPP wrappers sometimes expose a transform chain beginning at the prefab clone.
                // Retry wildcard rules against progressively shorter suffixes.
                int slash=path.IndexOf('/');
                while (slash>=0 && slash+1<path.Length)
                {
                    string suffix=path.Substring(slash+1);
                    if (r.Pattern.IsMatch(suffix)) return true;
                    slash=path.IndexOf('/',slash+1);
                }
                return false;
            }
            if (string.Equals(r.Path,path,StringComparison.Ordinal)) return true;
            if (r.Path.EndsWith("/"+path,StringComparison.Ordinal)) return true;
            if (path.EndsWith("/"+r.Path,StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsBigMapAreaLabelPath(string path)
        {
            return !string.IsNullOrEmpty(path) && NormalizeResizePath(path).EndsWith("BigMapAreaUI(Clone)/AreaUI/AreaName/Label",StringComparison.Ordinal);
        }

        private static bool IsAreaBuildingNamePath(string path)
        {
            return !string.IsNullOrEmpty(path) && NormalizeResizePath(path).EndsWith("AreaBuildingUI(Clone)/Back/BuildingName",StringComparison.Ordinal);
        }

        private static void ApplyParentAnchoredLabel(object component,double leftPad,double rightPad,double topPad,double bottomPad,string alignment,double minSize,double maxSize)
        {
            object rect=GetRectTransform(component); if (rect==null) return;
            object tr=GetPropertyValue(component,"transform","Transform"); if (tr==null) return;
            object parent=GetPropertyValue(tr,"parent","Parent"); if (parent==null) return;
            // Anchor the text to its own banner/background parent. This is independent from world position,
            // so moving a city/building on the map cannot move the text away from its frame.
            SetVector2Property(rect,"anchorMin",0.0,0.0);
            SetVector2Property(rect,"anchorMax",1.0,1.0);
            SetVector2Property(rect,"pivot",0.5,0.5);
            SetVector2Property(rect,"anchoredPosition",(leftPad-rightPad)/2.0,(bottomPad-topPad)/2.0);
            SetVector2Property(rect,"sizeDelta",-(leftPad+rightPad),-(topPad+bottomPad));
            ApplyWordWrap(component,false);
            ApplyAutoSizing(component,true);
            SetNumericProperty(component,minSize,"fontSizeMin","FontSizeMin","resizeTextMinSize","ResizeTextMinSize");
            SetNumericProperty(component,maxSize,"fontSizeMax","FontSizeMax","resizeTextMaxSize","ResizeTextMaxSize");
            ApplyAlignment(component,alignment);
        }

        private static void ApplyResizeRules(object component)
        {
            string path=NormalizeResizePath(BuildTransformPath(component));
            if (path.Length==0 || ResizeRules.Count==0) return;

            bool any=false;
            for (int i=0;i<ResizeRules.Count;i++) if (ResizeRuleMatches(ResizeRules[i],path)) { any=true; break; }
            if (!any) return;

            LayoutBaseline baseline=GetOrCaptureBaseline(component);
            if (baseline==null) return;

            bool touchFont=false;
            double font=baseline.FontSize;
            bool touchRect=false;
            double dx=0,dy=0,dw=0,dh=0;
            bool? wrap=null, auto=null, leftTrim=null;
            double? min=null,max=null,line=null,chars=null,words=null;
            string alignment=null, overflow=null;

            // Files are loaded alphabetically and rules are preserved in file order.
            // Defaults.yaml therefore applies first and zzzGlobalResizer.yaml last,
            // matching the layout convention used by the upstream patch.
            for (int i=0;i<ResizeRules.Count;i++)
            {
                ResizeRule r=ResizeRules[i]; if (!ResizeRuleMatches(r,path)) continue;
                if (r.IdealFontSize.HasValue) { font=r.IdealFontSize.Value; touchFont=true; }
                if (r.FontPercentage.HasValue && r.FontPercentage.Value>0) { font*=r.FontPercentage.Value; touchFont=true; }
                if (r.AdjustX.HasValue) { dx+=r.AdjustX.Value; touchRect=true; }
                if (r.AdjustY.HasValue) { dy+=r.AdjustY.Value; touchRect=true; }
                if (r.AdjustWidth.HasValue) { dw+=r.AdjustWidth.Value; touchRect=true; }
                if (r.AdjustHeight.HasValue) { dh+=r.AdjustHeight.Value; touchRect=true; }
                if (r.AllowWordWrap.HasValue) wrap=r.AllowWordWrap;
                if (r.AllowAutoSizing.HasValue) auto=r.AllowAutoSizing;
                if (r.AllowLeftTrimText.HasValue) leftTrim=r.AllowLeftTrimText;
                if (r.MinFontSize.HasValue) min=r.MinFontSize;
                if (r.MaxFontSize.HasValue) max=r.MaxFontSize;
                if (r.LineSpacing.HasValue) line=r.LineSpacing;
                if (r.CharacterSpacing.HasValue) chars=r.CharacterSpacing;
                if (r.WordSpacing.HasValue) words=r.WordSpacing;
                if (!string.IsNullOrEmpty(r.Alignment)) alignment=r.Alignment;
                if (!string.IsNullOrEmpty(r.Overflow)) overflow=r.Overflow;
            }

            if (touchFont && baseline.HasFontSize) SetNumericProperty(component,font,"fontSize","FontSize");
            if (touchRect && baseline.HasRect) ApplyRectBaseline(component,baseline,dx,dy,dw,dh);
            if (wrap.HasValue) ApplyWordWrap(component,wrap.Value);
            if (auto.HasValue) ApplyAutoSizing(component,auto.Value);
            if (leftTrim.HasValue) ApplyLeftTrim(component,leftTrim.Value);
            if (min.HasValue) SetNumericProperty(component,min.Value,"fontSizeMin","FontSizeMin","resizeTextMinSize","ResizeTextMinSize");
            if (max.HasValue) SetNumericProperty(component,max.Value,"fontSizeMax","FontSizeMax","resizeTextMaxSize","ResizeTextMaxSize");
            if (line.HasValue) SetNumericProperty(component,line.Value,"lineSpacing","LineSpacing");
            if (chars.HasValue) SetNumericProperty(component,chars.Value,"characterSpacing","CharacterSpacing");
            if (words.HasValue) SetNumericProperty(component,words.Value,"wordSpacing","WordSpacing");
            if (!string.IsNullOrEmpty(alignment)) ApplyAlignment(component,alignment);
            if (!string.IsNullOrEmpty(overflow)) ApplyOverflow(component,overflow);

            // Class-wide layout corrections only. TEST9 changes the *localPosition* instead of
            // relying only on anchoredPosition. DragonSong/Unity can rebuild the RectTransform after
            // the text setter runs, which is why the previous anchored Y offsets appeared to do nothing.
            if (IsBigMapAreaLabelPath(path))
            {
                ApplyMapLabelLayout(component,baseline);
                ScheduleDeferredLayout(component,baseline,1);
            }
            else if (IsAreaBuildingNamePath(path))
            {
                ApplyBuildingLabelLayout(component,baseline);
                ScheduleDeferredLayout(component,baseline,2);
            }
        }

        private static void ApplyMapLabelLayout(object component,LayoutBaseline baseline)
        {
            // Big-map city/resource labels are most stable when anchored to their own white banner.
            // This avoids the vertical drift seen after city reloads while still reserving space for the icon on the left.
            ApplyParentAnchoredLabel(component,22.0,4.0,10.0,0.0,"Center",9.0,17.0);
        }

        private static void ApplyBuildingLabelLayout(object component,LayoutBaseline baseline)
        {
            // Building names are anchored to the red banner itself instead of depending on local world-position offsets.
            // That keeps them stable across leave/re-enter cycles where Unity sometimes reuses the same pooled widgets.
            ApplyParentAnchoredLabel(component,18.0,4.0,10.0,0.0,"Center",8.0,14.0);
        }

        private static void ScheduleDeferredLayout(object component,LayoutBaseline baseline,int kind)
        {
            if (component==null || baseline==null) return;
            long key=GetStableObjectKey(component);
            lock(LayoutLock)
            {
                DeferredLayoutJob job;
                if (!DeferredLayoutJobs.TryGetValue(key,out job))
                {
                    job=new DeferredLayoutJob();
                    job.Component=component;
                    job.Baseline=baseline;
                    job.Kind=kind;
                    DeferredLayoutJobs[key]=job;
                }
                else
                {
                    job.Component=component;
                    job.Baseline=baseline;
                    job.Kind=kind;
                }
                // Finite settle window: enough for layout rebuilds, never a permanent per-frame poll.
                job.FramesLeft=120;
            }
        }

        private static void ProcessDeferredLayoutJobs()
        {
            lock(LayoutLock)
            {
                if (DeferredLayoutJobs.Count==0) return;
                List<long> remove=null;
                foreach (KeyValuePair<long,DeferredLayoutJob> kv in DeferredLayoutJobs)
                {
                    DeferredLayoutJob job=kv.Value;
                    try
                    {
                        if (job.Kind==1) ApplyMapLabelLayout(job.Component,job.Baseline);
                        else if (job.Kind==2) ApplyBuildingLabelLayout(job.Component,job.Baseline);
                    }
                    catch
                    {
                        job.FramesLeft=0;
                    }

                    job.FramesLeft--;
                    if (job.FramesLeft<=0)
                    {
                        if (remove==null) remove=new List<long>();
                        remove.Add(kv.Key);
                    }
                }
                if (remove!=null)
                    for (int i=0;i<remove.Count;i++) DeferredLayoutJobs.Remove(remove[i]);
            }
        }

        private static void ApplyLocalBaselineYOffset(object component,LayoutBaseline baseline,double dy)
        {
            if (baseline==null || !baseline.HasLocalPosition) return;
            object rect=GetRectTransform(component); if (rect==null) return;

            double x,y,z;
            if (!TryReadVector3Property(rect,"localPosition",out x,out y,out z))
            {
                x=baseline.LocalX; z=baseline.LocalZ;
            }
            SetVector3Property(rect,"localPosition",x,baseline.LocalY+dy,z);
        }

        private static LayoutBaseline GetOrCaptureBaseline(object component)
        {
            long key=GetStableObjectKey(component);
            lock(LayoutLock)
            {
                LayoutBaseline state;
                if (LayoutBaselines.TryGetValue(key,out state)) return state;
                state=new LayoutBaseline();
                PropertyInfo fp=FindProperty(component.GetType(),"fontSize","FontSize");
                if (fp!=null && fp.CanRead)
                {
                    try { state.FontSize=Convert.ToDouble(fp.GetValue(component),System.Globalization.CultureInfo.InvariantCulture); state.HasFontSize=true; } catch { }
                }
                object rect=GetRectTransform(component);
                double x,y,w,h;
                if (rect!=null && TryReadVector2Property(rect,"anchoredPosition",out x,out y) && TryReadVector2Property(rect,"sizeDelta",out w,out h))
                {
                    state.X=x; state.Y=y; state.Width=w; state.Height=h; state.HasRect=true;
                }
                double lx,ly,lz;
                if (rect!=null && TryReadVector3Property(rect,"localPosition",out lx,out ly,out lz))
                {
                    state.LocalX=lx; state.LocalY=ly; state.LocalZ=lz; state.HasLocalPosition=true;
                }
                LayoutBaselines[key]=state;
                return state;
            }
        }

        private static long GetStableObjectKey(object component)
        {
            try
            {
                object ptr=GetPropertyValue(component,"Pointer","pointer");
                if (ptr is IntPtr)
                {
                    long p=((IntPtr)ptr).ToInt64(); if (p!=0) return p;
                }
            }
            catch { }
            try
            {
                MethodInfo m=FindInstanceMethod(component.GetType(),"GetInstanceID",0);
                if (m!=null) return 0x4000000000000000L | (uint)Convert.ToInt32(m.Invoke(component,null));
            }
            catch { }
            return 0x2000000000000000L | (uint)RuntimeHelpers.GetHashCode(component);
        }

        private static object GetRectTransform(object component)
        {
            object r=GetPropertyValue(component,"rectTransform","RectTransform");
            if (r!=null) return r;
            object t=GetPropertyValue(component,"transform","Transform");
            if (t!=null && t.GetType().Name.IndexOf("RectTransform",StringComparison.OrdinalIgnoreCase)>=0) return t;
            return null;
        }

        private static bool TryReadVector2Property(object obj,string prop,out double x,out double y)
        {
            x=0; y=0;
            PropertyInfo p=FindProperty(obj.GetType(),prop,UpperFirst(prop));
            if (p==null || !p.CanRead) return false;
            try
            {
                object v=p.GetValue(obj); if (v==null) return false;
                return TryReadVector2(v,out x,out y);
            }
            catch { return false; }
        }

        private static bool TryReadVector2(object v,out double x,out double y)
        {
            x=0; y=0; Type t=v.GetType();
            try
            {
                FieldInfo fx=t.GetField("x",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                FieldInfo fy=t.GetField("y",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                if (fx!=null && fy!=null) { x=Convert.ToDouble(fx.GetValue(v)); y=Convert.ToDouble(fy.GetValue(v)); return true; }
                PropertyInfo px=FindProperty(t,"x","X"), py=FindProperty(t,"y","Y");
                if (px!=null && py!=null) { x=Convert.ToDouble(px.GetValue(v)); y=Convert.ToDouble(py.GetValue(v)); return true; }
            }
            catch { }
            return false;
        }

        private static bool TryReadVector3Property(object obj,string prop,out double x,out double y,out double z)
        {
            x=0; y=0; z=0;
            PropertyInfo p=FindProperty(obj.GetType(),prop,UpperFirst(prop));
            if (p==null || !p.CanRead) return false;
            try
            {
                object v=p.GetValue(obj); if (v==null) return false;
                Type t=v.GetType();
                FieldInfo fx=t.GetField("x",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                FieldInfo fy=t.GetField("y",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                FieldInfo fz=t.GetField("z",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                if (fx!=null && fy!=null && fz!=null)
                {
                    x=Convert.ToDouble(fx.GetValue(v)); y=Convert.ToDouble(fy.GetValue(v)); z=Convert.ToDouble(fz.GetValue(v)); return true;
                }
                PropertyInfo px=FindProperty(t,"x","X"), py=FindProperty(t,"y","Y"), pz=FindProperty(t,"z","Z");
                if (px!=null && py!=null && pz!=null)
                {
                    x=Convert.ToDouble(px.GetValue(v)); y=Convert.ToDouble(py.GetValue(v)); z=Convert.ToDouble(pz.GetValue(v)); return true;
                }
            }
            catch { }
            return false;
        }

        private static void SetVector3Property(object obj,string prop,double x,double y,double z)
        {
            PropertyInfo p=FindProperty(obj.GetType(),prop,UpperFirst(prop)); if (p==null || !p.CanWrite) return;
            try
            {
                Type t=p.PropertyType; object v=Activator.CreateInstance(t);
                FieldInfo fx=t.GetField("x",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                FieldInfo fy=t.GetField("y",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                FieldInfo fz=t.GetField("z",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                if (fx!=null && fy!=null && fz!=null)
                {
                    SetNumericMember(fx,v,x); SetNumericMember(fy,v,y); SetNumericMember(fz,v,z); p.SetValue(obj,v); return;
                }
                PropertyInfo px=FindProperty(t,"x","X"), py=FindProperty(t,"y","Y"), pz=FindProperty(t,"z","Z");
                if (px!=null && py!=null && pz!=null && px.CanWrite && py.CanWrite && pz.CanWrite)
                {
                    SetNumericPropertyValue(px,v,x); SetNumericPropertyValue(py,v,y); SetNumericPropertyValue(pz,v,z); p.SetValue(obj,v);
                }
            }
            catch { }
        }

        private static void ApplyRectBaseline(object component,LayoutBaseline b,double dx,double dy,double dw,double dh)
        {
            object rect=GetRectTransform(component); if (rect==null) return;
            SetVector2Property(rect,"anchoredPosition",b.X+dx,b.Y+dy);
            SetVector2Property(rect,"sizeDelta",b.Width+dw,b.Height+dh);
        }

        private static void SetVector2Property(object obj,string prop,double x,double y)
        {
            PropertyInfo p=FindProperty(obj.GetType(),prop,UpperFirst(prop)); if (p==null || !p.CanWrite) return;
            try
            {
                Type t=p.PropertyType; object v=Activator.CreateInstance(t);
                FieldInfo fx=t.GetField("x",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                FieldInfo fy=t.GetField("y",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                if (fx!=null && fy!=null)
                {
                    SetNumericMember(fx,v,x); SetNumericMember(fy,v,y); p.SetValue(obj,v); return;
                }
                PropertyInfo px=FindProperty(t,"x","X"), py=FindProperty(t,"y","Y");
                if (px!=null && py!=null && px.CanWrite && py.CanWrite)
                {
                    SetNumericPropertyValue(px,v,x); SetNumericPropertyValue(py,v,y); p.SetValue(obj,v);
                }
            }
            catch { }
        }

        private static void SetNumericMember(FieldInfo f,object obj,double val)
        {
            Type t=f.FieldType;
            if (t==typeof(float)) f.SetValue(obj,(float)val);
            else if (t==typeof(double)) f.SetValue(obj,val);
            else if (t==typeof(int)) f.SetValue(obj,(int)Math.Round(val));
        }

        private static void SetNumericPropertyValue(PropertyInfo p,object obj,double val)
        {
            Type t=p.PropertyType;
            if (t==typeof(float)) p.SetValue(obj,(float)val);
            else if (t==typeof(double)) p.SetValue(obj,val);
            else if (t==typeof(int)) p.SetValue(obj,(int)Math.Round(val));
        }

        private static string UpperFirst(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0])+s.Substring(1);
        }

        private static void ApplyWordWrap(object component,bool enabled)
        {
            PropertyInfo tmp=FindProperty(component.GetType(),"enableWordWrapping","EnableWordWrapping");
            if (tmp!=null && tmp.CanWrite && tmp.PropertyType==typeof(bool)) { try { tmp.SetValue(component,enabled); return; } catch { } }
            PropertyInfo ugui=FindProperty(component.GetType(),"horizontalOverflow","HorizontalOverflow");
            if (ugui!=null && ugui.CanWrite && ugui.PropertyType.IsEnum)
            {
                try { ugui.SetValue(component,Enum.Parse(ugui.PropertyType,enabled?"Wrap":"Overflow",true)); } catch { }
            }
        }

        private static void ApplyAutoSizing(object component,bool enabled)
        {
            SetBoolProperty(component,enabled,"enableAutoSizing","EnableAutoSizing","resizeTextForBestFit","ResizeTextForBestFit");
        }

        private static void ApplyLeftTrim(object component,bool enabled)
        {
            // Different TMP builds expose left-trim under different names. Ignore safely
            // when the member is unavailable; uploaded LongYin defaults do not require it.
            SetBoolProperty(component,enabled,"enableLeftTrim","EnableLeftTrim","allowLeftTrimText","AllowLeftTrimText");
        }

        private static void SetBoolProperty(object obj,bool value,params string[] names)
        {
            PropertyInfo p=FindProperty(obj.GetType(),names); if (p==null || !p.CanWrite || p.PropertyType!=typeof(bool)) return;
            try { p.SetValue(obj,value); } catch { }
        }

        private static void ApplyAlignment(object component,string value)
        {
            PropertyInfo p=FindProperty(component.GetType(),"alignment","Alignment");
            if (p==null || !p.CanWrite || !p.PropertyType.IsEnum) return;
            string target=value;
            string typeName=p.PropertyType.Name;
            if (typeName.IndexOf("TextAnchor",StringComparison.OrdinalIgnoreCase)>=0)
            {
                if (value.Equals("TopLeft",StringComparison.OrdinalIgnoreCase)) target="UpperLeft";
                else if (value.Equals("TopRight",StringComparison.OrdinalIgnoreCase)) target="UpperRight";
                else if (value.Equals("BottomLeft",StringComparison.OrdinalIgnoreCase)) target="LowerLeft";
                else if (value.Equals("BottomRight",StringComparison.OrdinalIgnoreCase)) target="LowerRight";
                else if (value.Equals("Center",StringComparison.OrdinalIgnoreCase)) target="MiddleCenter";
                else if (value.Equals("Left",StringComparison.OrdinalIgnoreCase)) target="MiddleLeft";
                else if (value.Equals("Right",StringComparison.OrdinalIgnoreCase)) target="MiddleRight";
                else if (value.Equals("Top",StringComparison.OrdinalIgnoreCase)) target="UpperCenter";
                else if (value.Equals("Bottom",StringComparison.OrdinalIgnoreCase)) target="LowerCenter";
            }
            try { p.SetValue(component,Enum.Parse(p.PropertyType,target,true)); } catch { }
        }

        private static void ApplyOverflow(object component,string value)
        {
            PropertyInfo p=FindProperty(component.GetType(),"overflowMode","OverflowMode");
            if (p!=null && p.CanWrite && p.PropertyType.IsEnum)
            {
                try { p.SetValue(component,Enum.Parse(p.PropertyType,value,true)); return; } catch { }
            }
            PropertyInfo h=FindProperty(component.GetType(),"horizontalOverflow","HorizontalOverflow");
            if (h!=null && h.CanWrite && h.PropertyType.IsEnum)
            {
                try { h.SetValue(component,Enum.Parse(h.PropertyType,value.Equals("Overflow",StringComparison.OrdinalIgnoreCase)?"Overflow":"Wrap",true)); } catch { }
            }
        }

        private static void SetNumericProperty(object obj,double val,params string[] names)
        {
            PropertyInfo p=FindProperty(obj.GetType(),names); if (p==null || !p.CanWrite) return; SetPropertyNumeric(p,obj,val);
        }
        private static void SetPropertyNumeric(PropertyInfo p,object obj,double val)
        {
            try
            {
                Type t=p.PropertyType;
                if (t==typeof(int)) p.SetValue(obj,(int)Math.Round(val));
                else if (t==typeof(float)) p.SetValue(obj,(float)val);
                else if (t==typeof(double)) p.SetValue(obj,val);
            }
            catch { }
        }

        private static void TryLoadWrapperAssembly(string simpleName)
        {
            try { Assembly.Load(simpleName); return; } catch { }
            try
            {
                string p=Path.Combine(Environment.CurrentDirectory,"MelonLoader","Il2CppAssemblies",simpleName+".dll");
                if (File.Exists(p)) Assembly.LoadFrom(p);
            }
            catch { }
        }
        private static bool IsAssemblyLoaded(string prefix)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if ((a.GetName().Name ?? string.Empty).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            }
            return false;
        }
        private static Type FindLoadedType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t=asm.GetType(fullName,false,false); if (t!=null) return t; } catch { }
            }
            return null;
        }
        private static PropertyInfo FindProperty(Type t, params string[] names)
        {
            while (t!=null)
            {
                for (int i=0;i<names.Length;i++)
                {
                    try { PropertyInfo p=t.GetProperty(names[i],BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic); if (p!=null) return p; } catch { }
                }
                t=t.BaseType;
            }
            return null;
        }
        private static object GetPropertyValue(object obj, params string[] names)
        {
            if (obj==null) return null;
            PropertyInfo p=FindProperty(obj.GetType(),names); if (p==null || !p.CanRead) return null;
            try { return p.GetValue(obj); } catch { return null; }
        }
        private static string BuildTransformPath(object component)
        {
            object tr=GetPropertyValue(component,"transform","Transform"); if (tr==null) return string.Empty;
            List<string> parts=new List<string>(); int guard=0;
            while (tr!=null && guard++<64)
            {
                string name=Convert.ToString(GetPropertyValue(tr,"name","Name")) ?? string.Empty;
                if (name.Length==0) name="?";
                parts.Add(name.Replace("/","_")); tr=GetPropertyValue(tr,"parent","Parent");
            }
            parts.Reverse(); return string.Join("/",parts.ToArray());
        }
        private static bool IsCjk(char c) { return (c>='\u3400'&&c<='\u4DBF')||(c>='\u4E00'&&c<='\u9FFF'); }
        private static bool ContainsCjk(string s)
        {
            for (int i=0;i<s.Length;i++) if (IsCjk(s[i])) return true;
            return false;
        }
    }
}
