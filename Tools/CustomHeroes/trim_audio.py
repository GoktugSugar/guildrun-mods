"""Cuts a piece out of an audio file and writes it as Opus .ogg (the venv has no Vorbis encoder), with a fade-out.

  <venv python> trim_audio.py <in> <out.ogg> <start s> <end s> [fade s=0.25]
"""
import sys

import av
import numpy as np

if __name__ == '__main__':
    src, dst = sys.argv[1], sys.argv[2]
    start, end = float(sys.argv[3]), float(sys.argv[4])
    fade = float(sys.argv[5]) if len(sys.argv) > 5 else 0.25
    chunks, rate = [], 48000
    with av.open(src) as f:
        stream = f.streams.audio[0]
        res = av.AudioResampler(format='flt', layout='mono', rate=rate)
        for frame in f.decode(stream):
            for r in res.resample(frame):
                chunks.append(r.to_ndarray().reshape(-1))
    pcm = np.concatenate(chunks)
    pcm = pcm[int(start * rate):int(end * rate)].copy()
    n = min(len(pcm), int(fade * rate))
    if n > 0: pcm[-n:] *= np.linspace(1.0, 0.0, n, dtype=np.float32)
    k = min(len(pcm), int(0.005 * rate))                       # 5 ms fade-in against clicks
    if k > 0: pcm[:k] *= np.linspace(0.0, 1.0, k, dtype=np.float32)
    with av.open(dst, 'w', format='ogg') as out:
        st = out.add_stream('libopus', rate=rate)
        st.layout = 'mono'
        frame = av.AudioFrame.from_ndarray(pcm.reshape(1, -1).astype(np.float32), format='flt', layout='mono')
        frame.sample_rate = rate
        for p in st.encode(frame): out.mux(p)
        for p in st.encode(None): out.mux(p)
    print(f'{dst}: {len(pcm) / rate:.2f}s from {start:.2f}-{end:.2f}s of {src}')
