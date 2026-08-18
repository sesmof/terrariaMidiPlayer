import json
import os
import struct
import sys

if sys.stdout.encoding != "utf-8":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if sys.stderr.encoding != "utf-8":
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
CENTER_NOTE = 72       # 泰拉瑞亚中央 C (C5)
GAME_FPS = 60          # Terraria 游戏帧率
DRUM_CHANNEL = 9       # GM 鼓组通道





def get_instrument_type(program_id):
    """
    根据 General MIDI (GM) Program Change 编号分配到 Terraria 对应的乐器轨
    harp rain_song guitar bell drums None
    """
    harp="harp";guitar="guitar";bell="bell";rain_song="rain_song";none=None
    if 0 <= program_id <= 15:
        return harp          # 竖琴 / 钢琴类 (508)  -竖琴
    elif 16 <= program_id <= 23:
        return harp    # 雨歌吉他 / 风琴类 (4057)   -雨哥
    elif 24 <= program_id <= 39:
        return guitar       # 斧吉他 / 吉他/贝斯类 (1305)  -电吉他
    elif program_id in (8, 9, 10, 11, 14, 98, 108, 112):
        return bell          # 铃铛 / 钟琴打击乐 (507)      -铃铛
    elif 40 <= program_id <= 55:
        return bell     # 弦乐 / 弦乐合奏类 (4057)       -雨哥
    elif 56 <= program_id <= 79:
        return harp          # 铜管 / 木管类 (508)          -竖琴
    else:
        return harp          # 默认兜底乐器             -竖琴


def parse_midi_with_tempos_and_instruments(midi_path):
    with open(midi_path, "rb") as f:
        data = f.read()

    ticks_per_quarter = 480
    mthd_idx = data.find(b'MThd')
    if mthd_idx != -1 and len(data) >= mthd_idx + 14:
        division = struct.unpack('>H', data[mthd_idx+12:mthd_idx+14])[0]
        if division > 0 and not (division & 0x8000):
            ticks_per_quarter = division

    raw_notes = []
    tempo_events = []

    track_positions = []
    idx = 0
    while True:
        pos = data.find(b'MTrk', idx)
        if pos == -1:
            break
        track_positions.append(pos)
        idx = pos + 4

    if not track_positions:
        track_positions = [0]

    for pos in track_positions:
        ptr = pos + 8 if data[pos:pos+4] == b'MTrk' else pos
        end_ptr = len(data)

        current_tick = 0
        running_status = None
        channel_programs = {ch: 0 for ch in range(16)}

        while ptr < end_ptr - 3:
            delta_time = 0
            bytes_read = 0
            valid_vlq = False

            while ptr < end_ptr and bytes_read < 4:
                b = data[ptr]
                ptr += 1
                bytes_read += 1
                delta_time = (delta_time << 7) | (b & 0x7F)
                if not (b & 0x80):
                    valid_vlq = True
                    break

            if not valid_vlq:
                ptr = ptr - bytes_read + 1
                continue

            current_tick += delta_time
            if ptr >= end_ptr:
                break

            status_byte = data[ptr]

            if status_byte >= 0x80:
                ptr += 1
                if status_byte < 0xF0:
                    running_status = status_byte
                else:
                    running_status = None
            else:
                if running_status is None:
                    ptr += 1
                    continue
                status_byte = running_status

            event_type = status_byte & 0xF0
            channel = status_byte & 0x0F

            if event_type == 0x90:  # Note On
                if ptr + 2 > end_ptr:
                    break
                note_num, velocity = data[ptr], data[ptr+1]
                ptr += 2
                if velocity > 0:
                    prog_id = channel_programs[channel]
                    raw_notes.append((current_tick, note_num, channel, prog_id))

            elif event_type == 0x80:  # Note Off
                if ptr + 2 > end_ptr:
                    break
                ptr += 2

            elif event_type == 0xC0:  # Program Change
                if ptr + 1 > end_ptr:
                    break
                channel_programs[channel] = data[ptr]
                ptr += 1

            elif event_type in (0xA0, 0xB0, 0xE0):
                if ptr + 2 > end_ptr:
                    break
                ptr += 2

            elif event_type == 0xD0:
                if ptr + 1 > end_ptr:
                    break
                ptr += 1

            elif status_byte == 0xFF:  # Meta Event
                if ptr + 1 > end_ptr:
                    break
                meta_type = data[ptr]
                ptr += 1

                meta_len = 0
                while ptr < end_ptr:
                    b = data[ptr]
                    ptr += 1
                    meta_len = (meta_len << 7) | (b & 0x7F)
                    if not (b & 0x80):
                        break

                if meta_type == 0x51 and meta_len == 3 and ptr + 3 <= end_ptr:
                    mpqn = (data[ptr] << 16) | (data[ptr+1] << 8) | data[ptr+2]
                    tempo_events.append((current_tick, mpqn))

                ptr += meta_len

            elif status_byte in (0xF0, 0xF7):
                sysex_len = 0
                while ptr < end_ptr:
                    b = data[ptr]
                    ptr += 1
                    sysex_len = (sysex_len << 7) | (b & 0x7F)
                    if not (b & 0x80):
                        break
                ptr += sysex_len
            else:
                running_status = None

    return raw_notes, tempo_events, ticks_per_quarter


def build_tempo_converter(tempo_events, tpq):
    tempo_events.sort(key=lambda x: x[0])

    cleaned_tempos = []
    if not tempo_events or tempo_events[0][0] > 0:
        cleaned_tempos.append((0, 500000))

    for t in tempo_events:
        cleaned_tempos.append(t)

    timeline = []
    accumulated_time = 0.0

    for i in range(len(cleaned_tempos)):
        start_tick, mpqn = cleaned_tempos[i]
        seconds_per_tick = (mpqn / 1000000.0) / float(tpq)

        if i < len(cleaned_tempos) - 1:
            next_tick = cleaned_tempos[i+1][0]
            duration = (next_tick - start_tick) * seconds_per_tick
            timeline.append({
                "start_tick": start_tick,
                "end_tick": next_tick,
                "start_time": accumulated_time,
                "spt": seconds_per_tick
            })
            accumulated_time += duration
        else:
            timeline.append({
                "start_tick": start_tick,
                "end_tick": float('inf'),
                "start_time": accumulated_time,
                "spt": seconds_per_tick
            })

    def tick_to_seconds(tick):
        for seg in timeline:
            if seg["start_tick"] <= tick < seg["end_tick"]:
                return seg["start_time"] + (tick - seg["start_tick"]) * seg["spt"]
        return accumulated_time

    return tick_to_seconds


def process_and_export_tracks(midi_path, output_dir, song_id=13, max_notes_per_frame=2):
    print(f"⌛ 正在解析与多音轨拆分: {os.path.basename(midi_path)} ...")

    raw_notes, tempo_events, tpq = parse_midi_with_tempos_and_instruments(midi_path)
    if not raw_notes:
        print("❌ 未在 MIDI 文件中检测到有效音符！")
        return

    get_seconds = build_tempo_converter(tempo_events, tpq)

    # 用字典按乐器分类收集音符: {"harp": [], "guitar": [], "bell": [], "rain_song": [], "drums": []}
    track_events = {
        "harp": [],
        "guitar": [],
        "bell": [],
        "rain_song": [],
        "drums": []
    }

    for tick, midi_pitch, channel, prog_id in raw_notes:
        real_seconds = get_seconds(tick)
        abs_frame = int(round(real_seconds * GAME_FPS))

        if channel == DRUM_CHANNEL:
            drum_pitch_val = round(((midi_pitch % 24) - 12) / 12.0, 4)
            track_events["drums"].append((abs_frame, drum_pitch_val))
        else:
            inst_type = get_instrument_type(prog_id)
            base_center = 48 if (32 <= prog_id <= 39) else CENTER_NOTE

            diff = midi_pitch - base_center
            while diff > 12: diff -= 12
            while diff < -12: diff += 12

            pitch_val = round(max(-1.0, min(1.0, diff / 12.0)), 4)
            if inst_type in track_events:
                track_events[inst_type].append((abs_frame, pitch_val))

    os.makedirs(output_dir, exist_ok=True)

    # 针对单一音轨的数据进行聚类与 JSON 序列化
    def build_single_track_json(events, is_drum=False):
        if not events:
            return []

        buckets = {}
        for abs_frame, p_val in events:
            if abs_frame not in buckets:
                buckets[abs_frame] = []
            buckets[abs_frame].append(p_val)

        formatted_notes = []
        last_frame = 0

        for current_frame in sorted(buckets.keys()):
            pitches_in_frame = sorted(buckets[current_frame])

            # 防爆音限制
            if len(pitches_in_frame) > max_notes_per_frame:
                selected = [pitches_in_frame[0], pitches_in_frame[-1]]
            else:
                selected = pitches_in_frame

            delay = current_frame - last_frame

            for idx, p_val in enumerate(selected):
                actual_delay = delay if idx == 0 else 0
                item = {"delayFrames": actual_delay}
                if is_drum:
                    item["drumPitch"] = p_val
                else:
                    item["pitch"] = p_val
                formatted_notes.append(item)

            last_frame = current_frame

        return formatted_notes

    # 导出各个乐器的独立 JSON 文件
    base_name = os.path.basename(midi_path)
    exported_files = []

    for inst_name, events in track_events.items():
        if not events:
            continue

        is_drum = (inst_name == "drums")
        notes_data = build_single_track_json(events, is_drum=is_drum)

        # 命名格式，已将 drum 改为 drums 与 C# 保持一致
        if inst_name == "harp":
            file_name = f"song_{song_id}.json"
        else:
            file_name = f"song_{song_id}_{inst_name}.json"

        output_path = os.path.join(output_dir, file_name)

        with open(output_path, "w", encoding="utf-8") as f:
            json.dump({
                "Title": f"{base_name} ({inst_name.upper()})",
                "Instrument": inst_name,
                "Notes": notes_data
            }, f, indent=2)

        exported_files.append((file_name, len(notes_data)))

    print(f"✅ 多音轨转换成功！包含转拍节点 {len(tempo_events)} 个。")
    print("📁 生成的独立乐器文件如下：")
    for fname, count in exported_files:
        print(f"   ├─ {fname}: 共 {count} 个音符")


if __name__ == "__main__":
    process_and_export_tracks(
        midi_path=r"C:\Users\sesmof\Downloads\King Crimson — Moonchild [MIDIfind.com].mid",
        output_dir=r"C:\Users\sesmof\Documents\My Games\Terraria\MidiSongs",
        song_id=67 ,
        max_notes_per_frame=3
    )






