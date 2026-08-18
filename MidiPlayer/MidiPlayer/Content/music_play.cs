using CommandHelp;
using Microsoft.Xna.Framework;
using Skil.Utils;
using Skil.Utils.quickBuild;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using tContentPatch;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.Drawing;
using Terraria.ID;
using Terraria.UI;

namespace Skil.Content
{
    public enum InstrumentType
    {
        Harp = 0,
        TheAxe = 1,
        Bell = 2,
        RainSong = 3
    }

    public class MidiNote
    {
        public int delayFrames { get; set; }
        public float pitch { get; set; }
    }

    public class MidiTrack
    {
        public string Title { get; set; }
        public string Instrument { get; set; }
        public List<MidiNote> Notes { get; set; } = new List<MidiNote>();
    }

    public class TrackPlayerState
    {
        public MidiTrack Track;
        public int NoteIndex = 0;
        public int FrameTimer = 0;
        public InstrumentType InstType;
        public bool IsDrum = false;

        public void Reset()
        {
            NoteIndex = 0;
            FrameTimer = 0;
        }
    }

    public class musicplay : PatchPlayer
    {
        private const int ITEM_ID_HARP = 508;
        private const int ITEM_ID_THE_AXE = 1305;
        private const int ITEM_ID_BELL = 507;
        private const int ITEM_ID_RAIN_SONG = 4057;
        private const int ITEM_ID_DRUMSTICK = 4673;

        public static GetSetReset<bool> Enable = new GetSetReset<bool>();
        public static GetSetReset<int> SongIndex = new GetSetReset<int>(13, 13);
        public static GetSetReset<bool> LoopPlay = new GetSetReset<bool>(true, true);

        // 修改1: 将显示特效逻辑改为 bool 开关
        public static GetSetReset<bool> ShowVisuals = new GetSetReset<bool>(true, true);

        public static GetSetReset<float> SemitoneShift = new GetSetReset<float>(0f, 0f);
        public static GetSetReset<float> CustomBPM = new GetSetReset<float>(0f, 0f); // 0表示默认

        // 修改2: 增加各个乐器的单独快捷开关
        public static GetSetReset<bool> EnableHarp = new GetSetReset<bool>(true, true);
        public static GetSetReset<bool> EnableGuitar = new GetSetReset<bool>(true, true);
        public static GetSetReset<bool> EnableBell = new GetSetReset<bool>(true, true);
        public static GetSetReset<bool> EnableRainSong = new GetSetReset<bool>(true, true);
        public static GetSetReset<bool> EnableDrum = new GetSetReset<bool>(true, true);

        private static int _currentSongIdx = -1;
        private static List<TrackPlayerState> _activeTracks = new List<TrackPlayerState>();

        private static string SongsDirPath => Path.Combine(Main.SavePath, "MidiSongs");

        public static List<CommandObject> GetCO()
        {
            return new List<CommandObject>()
            {
                CommandBuild.get3("skil_instrument", Enable)
                .SkilCMDBuild("song", SongIndex)
                .SkilCMDBuild("loop", LoopPlay)
                .SkilCMDBuild("visuals", ShowVisuals)
                .SkilCMDBuild("semitone", SemitoneShift)
                .SkilCMDBuild("bpm", CustomBPM)
                // 注册新乐器开关到指令
                .SkilCMDBuild("harp", EnableHarp)
                .SkilCMDBuild("guitar", EnableGuitar)
                .SkilCMDBuild("bell", EnableBell)
                .SkilCMDBuild("rain_song", EnableRainSong)
                .SkilCMDBuild("drum", EnableDrum)
            };
        }

        public static List<UIElement> GetUI()
        {
            return new List<UIElement>()
            {
                UIBuild.get1(Enable, LoopPlay, (s) => bool.Parse(s), "<bool> 自动循环播放 MIDI 曲目", "Images/Extra_19", "MIDI多轨播放器-总开关"),
                
                // 将特效按钮改为 get2 布尔开关
                UIBuild.get2(ShowVisuals, "<bool> 乐器特效开关 (显示悬浮乐器及粒子)", "Images/Extra_19", "MIDI乐器特效开关"),
                
                // 新增五个乐器的单独控制 UI
                UIBuild.get2(EnableDrum, "<bool> 启用/静音 鼓声 (Drum) 音轨", "Images/Extra_19", "音轨开关 - 鼓声 (Drum)"),
                UIBuild.get2(EnableGuitar, "<bool> 启用/静音 吉他 (Guitar) 音轨", "Images/Extra_19", "音轨开关 - 吉他 (Guitar)"),
                UIBuild.get2(EnableBell, "<bool> 启用/静音 铃铛 (Bell) 音轨", "Images/Extra_19", "音轨开关 - 铃铛 (Bell)"),
                UIBuild.get2(EnableRainSong, "<bool> 启用/静音 雨歌 (Rain Song) 音轨", "Images/Extra_19", "音轨开关 - 雨歌 (Rain Song)"),
                UIBuild.get2(EnableHarp, "<bool> 启用/静音 竖琴 (Harp) 音轨", "Images/Extra_19", "音轨开关 - 竖琴 (Harp)"),

                UIBuild.get6(SongIndex, int.Parse, "<int> 播放歌曲序号", "Images/Extra_19", "MIDI歌曲序号"),
                UIBuild.get6(SemitoneShift, (s) => float.Parse(s, CultureInfo.InvariantCulture), "<float> 全局半音偏移调整", "Images/Extra_19", "MIDI半音调整"),
                UIBuild.get6(CustomBPM, (s) => float.Parse(s, CultureInfo.InvariantCulture), "<float> 自定义 BPM (0为默认)", "Images/Extra_19", "MIDI速度BPM")
            };
        }

        public override void UpdatePrefix(Player This, int playerI)
        {
            if (This != Main.LocalPlayer) return;

            if (Enable.val)
            {
                PlayParallelMidiSequence(This);
            }
            else
            {
                ResetAllTracks();
            }
        }

        private static void ResetAllTracks()
        {
            foreach (var trackState in _activeTracks)
            {
                trackState.Reset();
            }
        }

        private static void PlayParallelMidiSequence(Player player)
        {
            if (player == null || !player.active || player.dead) return;

            int targetSongIdx = Math.Max(0, SongIndex.val);

            if (_currentSongIdx != targetSongIdx)
            {
                _currentSongIdx = targetSongIdx;
                LoadAllTracksForSong(targetSongIdx);
                ResetAllTracks();
            }

            if (_activeTracks.Count == 0) return;

            bool allFinished = true;

            foreach (var trackState in _activeTracks)
            {
                bool finished = ProcessSingleTrack(player, trackState);
                if (!finished)
                {
                    allFinished = false;
                }
            }

            if (allFinished && LoopPlay.val)
            {
                ResetAllTracks();
            }
        }

        private static bool ProcessSingleTrack(Player player, TrackPlayerState state)
        {
            if (state.Track == null || state.Track.Notes == null || state.Track.Notes.Count == 0) return true;
            if (state.NoteIndex >= state.Track.Notes.Count) return true;

            state.FrameTimer++;
            int playedThisFrame = 0;

            while (state.NoteIndex < state.Track.Notes.Count)
            {
                MidiNote nextNote = state.Track.Notes[state.NoteIndex];

                int targetDelay = nextNote.delayFrames;
                if (targetDelay > 0 && CustomBPM.val > 0f)
                {
                    targetDelay = Math.Max(1, (int)Math.Round(targetDelay * (120f / CustomBPM.val)));
                }

                if (state.FrameTimer >= targetDelay)
                {
                    // 在执行播放前，判断该乐器的开关是否处于开启状态
                    if (state.IsDrum)
                    {
                        if (EnableDrum.val)
                        {
                            PerformDrumSound(player, nextNote.pitch);
                        }
                    }
                    else
                    {
                        bool shouldPlay = false;
                        switch (state.InstType)
                        {
                            case InstrumentType.Harp: shouldPlay = EnableHarp.val; break;
                            case InstrumentType.TheAxe: shouldPlay = EnableGuitar.val; break;
                            case InstrumentType.Bell: shouldPlay = EnableBell.val; break;
                            case InstrumentType.RainSong: shouldPlay = EnableRainSong.val; break;
                        }

                        if (shouldPlay)
                        {
                            float rawShift = SemitoneShift.val / 12.0f;
                            float clampedShift = MathHelper.Clamp(rawShift, -1.0f, 1.0f);
                            float adjustedPitch = nextNote.pitch + clampedShift;

                            PerformInstrumentSound(player, state.InstType, adjustedPitch);
                        }
                    }

                    state.FrameTimer -= targetDelay;
                    state.NoteIndex++;
                    playedThisFrame++;

                    if (playedThisFrame >= 8) break;

                    if (state.NoteIndex < state.Track.Notes.Count)
                    {
                        int nextTargetDelay = state.Track.Notes[state.NoteIndex].delayFrames;
                        if (nextTargetDelay > 0 && CustomBPM.val > 0f)
                        {
                            nextTargetDelay = Math.Max(1, (int)Math.Round(nextTargetDelay * (120f / CustomBPM.val)));
                        }

                        if (nextTargetDelay > 0)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    break;
                }
            }

            return state.NoteIndex >= state.Track.Notes.Count;
        }

        private static void LoadAllTracksForSong(int index)
        {
            _activeTracks.Clear();

            if (!Directory.Exists(SongsDirPath)) return;

            string[] filePaths = Directory.GetFiles(SongsDirPath, $"song_{index}*.json");

            foreach (string filePath in filePaths)
            {
                string fileName = Path.GetFileName(filePath);
                MidiTrack parsedTrack = ParseJsonTrack(filePath, out string instStr);

                if (parsedTrack != null && parsedTrack.Notes.Count > 0)
                {
                    var (instType, isDrum) = GetInstrumentTypeFromString(instStr, fileName);
                    _activeTracks.Add(new TrackPlayerState
                    {
                        Track = parsedTrack,
                        InstType = instType,
                        IsDrum = isDrum
                    });
                    Main.NewText($"[MIDI播放器] 已加载音轨: {fileName} ({parsedTrack.Notes.Count} 音符, 乐器: {instStr})", 100, 255, 100);
                }
            }
        }

        private static (InstrumentType instType, bool isDrum) GetInstrumentTypeFromString(string instStr, string fileName)
        {
            if (string.IsNullOrEmpty(instStr))
            {
                string lowerName = fileName.ToLowerInvariant();
                if (lowerName.Contains("drum")) return (InstrumentType.Harp, true);
                if (lowerName.Contains("guitar")) return (InstrumentType.TheAxe, false);
                if (lowerName.Contains("bell")) return (InstrumentType.Bell, false);
                if (lowerName.Contains("rain_song")) return (InstrumentType.RainSong, false);
                return (InstrumentType.Harp, false);
            }

            string lower = instStr.ToLowerInvariant();
            if (lower.Contains("drum"))
            {
                return (InstrumentType.Harp, true);
            }
            if (lower == "guitar")
            {
                return (InstrumentType.TheAxe, false);
            }
            if (lower == "bell")
            {
                return (InstrumentType.Bell, false);
            }
            if (lower == "rain_song" || lower == "rainsong")
            {
                return (InstrumentType.RainSong, false);
            }
            if (lower == "harp")
            {
                return (InstrumentType.Harp, false);
            }

            return (InstrumentType.Harp, false);
        }

        public static void PerformInstrumentSound(Player player, InstrumentType instType, float pitch)
        {
            if (player == null || !player.active || player.dead) return;

            float safePitch = MathHelper.Clamp(pitch, -1f, 1f);
            int itemId = GetItemIdForInstrument(instType);

            PlayVanillaPacket58SoundLocal(player, itemId, safePitch);

            if (Main.netMode == 1)
            {
                SendInstrumentNetworkHandshake(player, itemId, safePitch);
            }

            PerformInstrumentVisualEffects(player, itemId);
        }

        public static void PerformDrumSound(Player player, float drumPitch)
        {
            if (player == null || !player.active || player.dead) return;

            float safePitch = MathHelper.Clamp(drumPitch, -1f, 1f);
            float range = MathHelper.Clamp((safePitch + 1.0f) / 2.0f, 0.0f, 0.99f);
            player.PlayDrums(range);

            if (Main.netMode == 1)
            {
                SendInstrumentNetworkHandshake(player, ITEM_ID_DRUMSTICK, safePitch);
            }

            PerformInstrumentVisualEffects(player, ITEM_ID_DRUMSTICK);
        }

        /// <summary>
        /// 核心视觉特效：平稳的头顶扇形乐器悬浮贴图 + 同步生成的 7 号 Particle 粒子
        /// </summary>
        private static void PerformInstrumentVisualEffects(Player player, int itemId)
        {
            if (player == null || !player.active || player.dead) return;
            // 判断修改为对 bool 进行读取
            if (!ShowVisuals.val) return;

            Vector2 baseHeadPos = player.Center + new Vector2(0f, -38f);
            Vector2 offset = GetInstrumentFanOffset(itemId);

            // 1. 计算带有随机抖动的最终目标坐标（作为起点）
            Vector2 shakeOffset = new Vector2(Main.rand.NextFloat() * 3f - 1.5f, Main.rand.NextFloat() * 3f - 1.5f);
            Vector2 finalPos = baseHeadPos + offset + shakeOffset;

            // 粒子特效 1：物品悬浮贴图
            ParticleOrchestrator.RequestParticleSpawn(
                clientOnly: false, // 允许服务器广播给所有联机玩家
                ParticleOrchestraType.ItemTransfer,
                new ParticleOrchestraSettings
                {
                    PositionInWorld = finalPos,    // 起点：设为抖动后的精准坐标
                    MovementVector = Vector2.Zero, // 终点偏移量：必须为零！这样终点就等于起点，实现原地定点静止
                    UniqueInfoPiece = itemId       // 传入物品ID，渲染对应的乐器贴图
                }
            );

            // 粒子特效 2：参考 skil22 的方式，使用 ParticleOrchestrator 真正生成 7 号 Particle 粒子
            Vector2 particleVel = new Vector2(Main.rand.NextFloat() * 2f - 1f, Main.rand.NextFloat() * 1f - 1.5f);
            ParticleOrchestrator.RequestParticleSpawn(
                clientOnly: false,
                (ParticleOrchestraType)7,
                new ParticleOrchestraSettings
                {
                    PositionInWorld = finalPos,
                    MovementVector = particleVel,
                    UniqueInfoPiece = 1000 // 默认按 1.0 比例缩放 (scale * 1000f)
                }
            );
        }

        /// <summary>
        /// 定义头顶扇形排列的精确坐标偏移（已扩大间距）：
        /// 最左边：铃铛 | 左边：Guitar | 中间：竖琴 | 右边：鼓 | 最右边：雨歌
        /// </summary>
        private static Vector2 GetInstrumentFanOffset(int itemId)
        {
            if (itemId == ITEM_ID_BELL)        // 最左边：铃铛
                return new Vector2(-80f, -10f);
            if (itemId == ITEM_ID_THE_AXE)      // 左边：Guitar
                return new Vector2(-40f, -22f);
            if (itemId == ITEM_ID_HARP)        // 中间：竖琴
                return new Vector2(0f, -32f);
            if (itemId == ITEM_ID_DRUMSTICK)   // 右边：鼓
                return new Vector2(40f, -22f);
            if (itemId == ITEM_ID_RAIN_SONG)   // 最右边：雨歌
                return new Vector2(80f, -10f);

            return new Vector2(0f, -32f); // 默认兜底：中间
        }

        private static int GetItemIdForInstrument(InstrumentType type)
        {
            switch (type)
            {
                case InstrumentType.TheAxe: return ITEM_ID_THE_AXE;
                case InstrumentType.Bell: return ITEM_ID_BELL;
                case InstrumentType.RainSong: return ITEM_ID_RAIN_SONG;
                case InstrumentType.Harp:
                default: return ITEM_ID_HARP;
            }
        }

        private static void PlayVanillaPacket58SoundLocal(Player player, int itemId, float pitch)
        {
            try
            {
                if (itemId == 4372 || itemId == 4057 || itemId == 4715)
                {
                    player.PlayGuitarChord(pitch);
                    return;
                }

                if (itemId == 4673)
                {
                    player.PlayDrums(pitch);
                    return;
                }

                Main.musicPitch = pitch;

                LegacySoundStyle style = SoundID.Item26;

                if (itemId == 507)
                {
                    style = SoundID.Item35;
                }
                else if (itemId == 1305)
                {
                    style = SoundID.Item47;
                }

                SoundEngine.PlaySound(style, player.position, 0f, 1f);

                Main.musicPitch = 0f;
            }
            catch
            {
            }
        }

        private static void SendInstrumentNetworkHandshake(Player player, int itemId, float pitch)
        {
            try
            {
                Item netItem = new Item();
                netItem.SetDefaults(itemId);

                Item realHeldItem = player.inventory[player.selectedItem];

                player.inventory[player.selectedItem] = netItem;
                NetMessage.SendData(5, -1, -1, null, player.whoAmI, player.selectedItem);

                NetMessage.SendData(58, -1, -1, null, player.whoAmI, pitch);

                player.inventory[player.selectedItem] = realHeldItem;
                NetMessage.SendData(5, -1, -1, null, player.whoAmI, player.selectedItem);
            }
            catch
            {
            }
        }

        private static MidiTrack ParseJsonTrack(string filePath, out string instrumentStr)
        {
            instrumentStr = "harp";
            try
            {
                string jsonContent = File.ReadAllText(filePath);
                MidiTrack track = new MidiTrack();

                Match titleMatch = Regex.Match(jsonContent, @"""Title""\s*:\s*""([^""]+)""");
                if (titleMatch.Success) track.Title = titleMatch.Groups[1].Value;

                Match instMatch = Regex.Match(jsonContent, @"""Instrument""\s*:\s*""([^""]+)""");
                if (instMatch.Success)
                {
                    track.Instrument = instMatch.Groups[1].Value;
                    instrumentStr = track.Instrument;
                }

                bool isDrum = instrumentStr.ToLowerInvariant().Contains("drum");
                string pitchKey = isDrum ? "drumPitch" : "pitch";
                string pattern = $@"""delayFrames""\s*:\s*(-?\d+)[\s\S]*?""{pitchKey}""\s*:\s*(-?\d+(?:\.\d+)?)";
                MatchCollection matches = Regex.Matches(jsonContent, pattern);

                foreach (Match m in matches)
                {
                    if (int.TryParse(m.Groups[1].Value, out int delay) &&
                        float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float pVal))
                    {
                        track.Notes.Add(new MidiNote { delayFrames = delay, pitch = pVal });
                    }
                }

                return track;
            }
            catch
            {
                return null;
            }
        }
    }
}
