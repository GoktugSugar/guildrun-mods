"""Normalize every clip in a Voice folder to the same speech loudness; rewrites them as 16-bit WAV.

usage: <venv python> normalize.py <Voice dir> [target_db=-23] [peak_db=-3]
Downloaded voice lines are usually mastered 4-12 dB hotter than Guildrun's, and shouts are much louder than
calm lines; -23 dB speech RMS with a -3 dB peak ceiling sits right with the game's own VO.
"""
import glob
import os
import sys
import wave


def _load(f):
    import av
    import numpy as np
    with av.open(f) as c:
        rate = c.streams.audio[0].rate
        r = av.AudioResampler(format='flt', layout='mono', rate=rate)
        pcm = [fr.to_ndarray().ravel() for frame in c.decode(audio=0) for fr in r.resample(frame)]
        pcm += [fr.to_ndarray().ravel() for fr in r.resample(None)]
    return np.concatenate(pcm).astype(np.float64), rate


def _speech_rms(x, rate):
    import numpy as np
    n = max(1, rate // 50)
    w = x[: len(x) // n * n].reshape(-1, n)
    e = np.sqrt((w ** 2).mean(1) + 1e-12)
    loud = e[e > e.max() * 0.1]
    return 20 * np.log10(np.sqrt((loud ** 2).mean()) + 1e-12)


def run(root, target=-23.0, peak=-3.0):
    import numpy as np
    for f in sorted(glob.glob(os.path.join(root, '*', '*.ogg')) + glob.glob(os.path.join(root, '*', '*.mp3'))):
        x, rate = _load(f)
        before, pk = _speech_rms(x, rate), 20 * np.log10(np.abs(x).max() + 1e-12)
        gain = min(target - before, peak - pk)
        y = np.clip(x * 10 ** (gain / 20), -1, 1)
        out = os.path.splitext(f)[0] + '.wav'
        with wave.open(out, 'wb') as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
            w.writeframes((y * 32767).astype('<i2').tobytes())
        os.remove(f)
        print(f'{before:6.1f} dB -> {before + gain:6.1f} dB  {os.path.relpath(out, root)}')


if __name__ == '__main__':
    run(sys.argv[1], *(float(a) for a in sys.argv[2:4]))
