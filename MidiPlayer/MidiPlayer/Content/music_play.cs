
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


    //internal class MidiPlayerSetting : ModSetting
    //{
    //    public override string Name => "MidiPlayer";
    //    public override string Title => "MIDI多轨播放器设置";

    //    public override UIElement GetUI()
    //    {
    //        // 直接用你原本的 get3 配合你的嵌套 UI 列表
    //        return UIBuild.get3(musicplay.GetUI());
    //    }
    //}
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
        public static GetSetReset<float> SemitoneShift = new GetSetReset<float>(0f, 0f);

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
                .SkilCMDBuild("semitone", SemitoneShift)
            };
        }

        public static List<UIElement> GetUI()
        {
            return new List<UIElement>()
            {
                UIBuild.get1(Enable, LoopPlay, (s) => bool.Parse(s), "<bool> 自动循环播放 MIDI 曲目", "Images/Extra_19", "MIDI多轨播放器-总开关"),
                UIBuild.get6(SongIndex, int.Parse, "<int> 播放歌曲序号", "Images/Extra_19", "MIDI歌曲序号"),
                UIBuild.get6(SemitoneShift, (s) => float.Parse(s, CultureInfo.InvariantCulture), "<float> 全局半音偏移调整", "Images/Extra_19", "MIDI半音调整")
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

                if (state.FrameTimer >= nextNote.delayFrames)
                {
                    if (state.IsDrum)
                    {
                        PerformDrumSound(player, nextNote.pitch);
                    }
                    else
                    {
                        float rawShift = SemitoneShift.val / 12.0f;
                        float clampedShift = MathHelper.Clamp(rawShift, -1.0f, 1.0f);
                        float adjustedPitch = nextNote.pitch + clampedShift;

                        PerformInstrumentSound(player, state.InstType, adjustedPitch);
                    }

                    state.FrameTimer -= nextNote.delayFrames;
                    state.NoteIndex++;
                    playedThisFrame++;

                    if (playedThisFrame >= 8) break;

                    if (state.NoteIndex < state.Track.Notes.Count && state.Track.Notes[state.NoteIndex].delayFrames > 0)
                    {
                        break;
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

            var trackDefinitions = new (string suffix, InstrumentType inst, bool isDrum)[]
            {
                ("", InstrumentType.Harp, false),
                ("_guitar", InstrumentType.TheAxe, false),
                ("_bell", InstrumentType.Bell, false),
                ("_rain_song", InstrumentType.RainSong, false),
                ("_drums", InstrumentType.Harp, true)
            };

            foreach (var def in trackDefinitions)
            {
                string fileName = $"song_{index}{def.suffix}.json";
                string filePath = Path.Combine(SongsDirPath, fileName);

                if (File.Exists(filePath))
                {
                    MidiTrack parsedTrack = ParseJsonTrack(filePath, def.isDrum);
                    if (parsedTrack != null && parsedTrack.Notes.Count > 0)
                    {
                        _activeTracks.Add(new TrackPlayerState
                        {
                            Track = parsedTrack,
                            InstType = def.inst,
                            IsDrum = def.isDrum
                        });
                        Main.NewText($"[MIDI播放器] 已加载音轨: {fileName} ({parsedTrack.Notes.Count} 音符)", 100, 255, 100);
                    }
                }
            }
        }

        public static void PerformInstrumentSound(Player player, InstrumentType instType, float pitch)
        {
            if (player == null || !player.active || player.dead) return;

            float safePitch = MathHelper.Clamp(pitch, -1f, 1f);
            int itemId = GetItemIdForInstrument(instType);

            // 1. 本地播放：通过反射调用原生 SoundEngine，带上与发包完全一致的原始 pitch
            PlayVanillaPacket58SoundLocal(player, itemId, safePitch);

            // 2. 联机广播：完美的 5 -> 58 -> 5 手持伪装发包，保证其他客户端能听见并识别乐器
            if (Main.netMode == 1)
            {
                SendInstrumentNetworkHandshake(player, itemId, safePitch);
            }
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
                // 1. 处理拥有独立播放方法的特殊乐器 (雨歌等吉他类、架子鼓)
                // 对应原版：if (type7 == 4372 || type7 == 4057 || type7 == 4715) ...
                if (itemId == 4372 || itemId == 4057 || itemId == 4715) // 4372 为雨歌 Rain Song
                {
                    player.PlayGuitarChord(pitch);
                    return;
                }

                if (itemId == 4673) // 4673 为架子鼓
                {
                    player.PlayDrums(pitch);
                    return;
                }

                // 2. 处理普通原版乐器 (竖琴、铃铛、吉他斧)
                // 核心：像原版底层一样，通过修改全局变量来控制音高
                Main.musicPitch = pitch;

                // 使用逆向代码中明确指定的 LegacySoundStyle
                Terraria.Audio.LegacySoundStyle style = SoundID.Item26; // 默认: 竖琴 

                if (itemId == 507) // ITEM_ID_BELL
                {
                    style = SoundID.Item35;
                }
                else if (itemId == 1305) // ITEM_ID_THE_AXE
                {
                    style = SoundID.Item47;
                }

                // 3. 严格使用原版逆向代码中的 PlaySound 签名
                Terraria.Audio.SoundEngine.PlaySound(style, player.position, 0f, 1f);

                // 4. 播放完成后，务必重置该全局变量，防止导致游戏内其他音效走调
                Main.musicPitch = 0f;
            }
            catch
            {
                // 异常吞咽，保持与原逻辑一致
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

        private static MidiTrack ParseJsonTrack(string filePath, bool isDrum)
        {
            try
            {
                string jsonContent = File.ReadAllText(filePath);
                MidiTrack track = new MidiTrack();

                Match titleMatch = Regex.Match(jsonContent, @"""Title""\s*:\s*""([^""]+)""");
                if (titleMatch.Success) track.Title = titleMatch.Groups[1].Value;

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

