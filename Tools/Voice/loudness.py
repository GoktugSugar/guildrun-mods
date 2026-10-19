"""Loudness of voice clips in LUFS (ITU-R BS.1770: K-weighting, 400 ms blocks, absolute -70 and relative -10 LU
gates), the measure that follows how loud speech sounds. Short shouts (< 400 ms) are measured as one block.

usage: <venv python> loudness.py <dir or files...>        prints each clip, then the median per slot code
  The game's own lines (VO.bank, extracted with vgmstream-cli) are named HERO_<code>: AA = ability, OD = death,
  OP = purchase, OR = rank-up.
"""
import glob
import os
import re
import sys

import numpy as np


def load(f):
    import av
    with av.open(f) as c:
        rate = c.streams.audio[0].rate
        r = av.AudioResampler(format='flt', layout='mono', rate=rate)
        pcm = [fr.to_ndarray().ravel() for frame in c.decode(audio=0) for fr in r.resample(frame)]
        pcm += [fr.to_ndarray().ravel() for fr in r.resample(None)]
    return np.concatenate(pcm).astype(np.float64), rate


def k_weight(x, rate):
    """BS.1770 pre-filter (high shelf) + RLB high-pass, coefficients computed for any sample rate."""
    from scipy.signal import lfilter
    # high shelf
    f0, G, Q = 1681.974450955533, 3.999843853973347, 0.7071752369554196
    K = np.tan(np.pi * f0 / rate); Vh = 10 ** (G / 20); Vb = Vh ** 0.4996667741545416
    a0 = 1 + K / Q + K * K
    b = [(Vh + Vb * K / Q + K * K) / a0, 2 * (K * K - Vh) / a0, (Vh - Vb * K / Q + K * K) / a0]
    a = [1, 2 * (K * K - 1) / a0, (1 - K / Q + K * K) / a0]
    x = lfilter(b, a, x)
    # high pass
    f0, Q = 38.13547087602444, 0.5003270373238773
    K = np.tan(np.pi * f0 / rate)
    a0 = 1 + K / Q + K * K
    b = [1, -2, 1]
    a = [1, 2 * (K * K - 1) / a0, (1 - K / Q + K * K) / a0]
    return lfilter(b, a, x)


def lufs(x, rate):
    y = k_weight(x, rate)
    n, hop = int(0.4 * rate), int(0.1 * rate)
    if len(y) < n:
        blocks = np.array([np.mean(y ** 2)])
    else:
        blocks = np.array([np.mean(y[i:i + n] ** 2) for i in range(0, len(y) - n + 1, hop)])
    l = -0.691 + 10 * np.log10(blocks + 1e-15)
    blocks = blocks[l > -70]
    if len(blocks) == 0:
        return -70.0
    rel = -0.691 + 10 * np.log10(blocks.mean()) - 10
    l = -0.691 + 10 * np.log10(blocks + 1e-15)
    blocks = blocks[l > rel]
    return float(-0.691 + 10 * np.log10(blocks.mean() + 1e-15))


def files(args):
    out = []
    for a in args:
        out += sorted(glob.glob(os.path.join(a, '**', '*.wav'), recursive=True) + glob.glob(os.path.join(a, '**', '*.ogg'), recursive=True)) if os.path.isdir(a) else [a]
    return out


if __name__ == '__main__':
    by = {}
    for f in files(sys.argv[1:]):
        x, rate = load(f)
        v, pk = lufs(x, rate), 20 * np.log10(np.abs(x).max() + 1e-12)
        m = re.match(r'[A-Z]+_([A-Z]{2})', os.path.basename(f))
        code = m.group(1) if m else os.path.basename(os.path.dirname(f))
        by.setdefault(code, []).append(v)
        print(f'{v:6.1f} LUFS  peak {pk:5.1f} dB  {len(x) / rate:5.2f}s  {os.path.relpath(f, sys.argv[1]) if os.path.isdir(sys.argv[1]) else f}')
    print()
    allv = [v for vs in by.values() for v in vs]
    for k, vs in sorted(by.items()):
        print(f'{k:12s} n={len(vs):3d}  median {np.median(vs):6.1f}  min {min(vs):6.1f}  max {max(vs):6.1f}')
    print(f'{"ALL":12s} n={len(allv):3d}  median {np.median(allv):6.1f}  p10 {np.percentile(allv, 10):6.1f}  p90 {np.percentile(allv, 90):6.1f}')
