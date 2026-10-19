"""Build a custom hero's Voice folders from a mapping, then loudness-normalize them.

usage: <venv python> build_voice.py <source dir> <hero dir> <mapping.json> [target_lufs=-23]
  target_lufs: loudness every clip gets (LUFS, see normalize.py).
  mapping.json: {"SKILL_VO": ["xscream_cut", ...], "PURCHASE_VO": [...], ...}
  Slot names are the event suffixes the game plays: event:/SFX/COMB/HEROES/<Hero>/<Hero>_<slot>
  (SKILL_VO, PURCHASE_VO, DEATH_VO, RANKUP_B_VO, RANKUP_A_VO, RANKUP_S_VO; check the log for others).
  Clip names are the files in <source dir>/dl without .ogg (see clip_table.py).
  Writes <hero dir>/Assets/Voice/<slot>/*.wav (16-bit, all at -23 LUFS) and Voice/mapping.txt.
"""
import json
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import normalize  # noqa: E402

if __name__ == '__main__':
    src, hero, mp = sys.argv[1:4]
    target = float(sys.argv[4]) if len(sys.argv) > 4 else -23.0
    mapping = json.load(open(mp))
    clips = json.load(open(os.path.join(src, 'clips.json'))) if os.path.exists(os.path.join(src, 'clips.json')) else {}
    voice = os.path.join(hero, 'Assets', 'Voice')
    shutil.rmtree(voice, ignore_errors=True)
    lines = []
    for slot, names in mapping.items():
        os.makedirs(os.path.join(voice, slot))
        lines.append(slot)
        for n in names:
            shutil.copy(os.path.join(src, 'dl', n + '.ogg'), os.path.join(voice, slot, n + '.ogg'))
            c = clips.get(n, {})
            lines.append(f'    {c.get("s", 0):.2f}s  "{c.get("text", "?")}"  ({n})')
    open(os.path.join(voice, 'mapping.txt'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    normalize.run(voice, target)
    print('\n'.join(lines))
