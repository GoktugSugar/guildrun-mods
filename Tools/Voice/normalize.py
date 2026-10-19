"""Normalize every clip in a Voice folder to the same loudness; rewrites them as 16-bit WAV.

usage: <venv python> normalize.py <Voice dir> [target_lufs=-23] [ceiling_db=-1]
Loudness is LUFS (ITU-R BS.1770, see loudness.py), which follows how loud a line sounds, so a scream and a calm
line end up equally loud. Every clip gets exactly the target; peaks that would go over the ceiling are tamed by a
soft limiter instead of lowering the whole clip (the old peak cap left shouts several dB quieter than talk).

Level: the game's own VO files measure about -14 LUFS (VO.bank, median of 307 lines), but they play through FMOD
events with their own volume; our clips play straight on the VO bus at -23 LUFS, the level tuned in game with Freddy.
"""
import glob
import os
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from loudness import load, lufs  # noqa: E402


def limit(y, rate, ceiling_db):
    """Soft limiter: fast attack, 80 ms release gain reduction where |y| would exceed the ceiling."""
    c = 10 ** (ceiling_db / 20)
    if np.abs(y).max() <= c:
        return y
    look = int(0.002 * rate)                                         # 2 ms lookahead
    need = np.minimum(1.0, c / np.maximum(np.abs(y), 1e-9))
    need = np.minimum.reduce([np.roll(need, -i) for i in range(look + 1)])
    rel = np.exp(-1.0 / (0.08 * rate))
    g = np.empty_like(need)
    cur = 1.0
    for i, n in enumerate(need):                                     # instant down, smooth back up
        cur = n if n < cur else n + (cur - n) * rel
        g[i] = cur
    return np.clip(y * g, -c, c)


def run(root, target=-23.0, ceiling=-1.0):
    for f in sorted(glob.glob(os.path.join(root, '*', '*.ogg')) + glob.glob(os.path.join(root, '*', '*.mp3')) +
                    glob.glob(os.path.join(root, '*', '*.wav'))):
        x, rate = load(f)
        before = lufs(x, rate)
        y = limit(x * 10 ** ((target - before) / 20), rate, ceiling)
        after = lufs(y, rate)
        y = limit(y * 10 ** ((target - after) / 20), rate, ceiling)  # limiting took a little off: once more
        out = os.path.splitext(f)[0] + '.wav'
        if f != out:
            os.remove(f)
        with wave.open(out, 'wb') as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
            w.writeframes((y * 32767).astype('<i2').tobytes())
        print(f'{before:6.1f} -> {lufs(y, rate):6.1f} LUFS  peak {20 * np.log10(np.abs(y).max() + 1e-12):5.1f} dB  {os.path.relpath(out, root)}')


if __name__ == '__main__':
    run(sys.argv[1], *(float(a) for a in sys.argv[2:4]))
