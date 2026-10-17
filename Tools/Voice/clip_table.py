"""Transcribe downloaded voice lines and print them sorted by length (for picking replacements).

usage: <venv python> clip_table.py <source dir>
  Reads <source dir>/dl/*.ogg, writes <source dir>/clips.json ({name: {"s": seconds, "text": ...}}).
  Needs the tools venv (setup_venv.bat): faster-whisper, running on the CPU.
  Transcripts are cached; delete clips.json to redo them.
"""
import glob
import json
import os
import struct
import sys


def ogg_len(f):
    b = open(f, 'rb').read()
    rate = struct.unpack('<I', b[b.index(b'\x01vorbis') + 12:][:4])[0]
    last = b.rindex(b'OggS')
    gran, = struct.unpack('<q', b[last + 6:last + 14])
    return round(gran / rate, 2)


def load16k(f):
    import av
    import numpy as np
    with av.open(f) as c:
        r = av.AudioResampler(format='s16', layout='mono', rate=16000)
        pcm = [fr.to_ndarray().ravel() for frame in c.decode(audio=0) for fr in r.resample(frame)]
        pcm += [fr.to_ndarray().ravel() for fr in r.resample(None)]
    return np.concatenate(pcm).astype(np.float32) / 32768.0


if __name__ == '__main__':
    d = sys.argv[1]
    out = os.path.join(d, 'clips.json')
    res = json.load(open(out)) if os.path.exists(out) else {}
    files = sorted(glob.glob(os.path.join(d, 'dl', '*.ogg')))
    todo = [f for f in files if os.path.basename(f)[:-4] not in res]
    if todo:
        from faster_whisper import WhisperModel
        m = WhisperModel('small.en', device='cpu', compute_type='int8')
        for f in todo:
            segs, _ = m.transcribe(load16k(f), beam_size=5)
            res[os.path.basename(f)[:-4]] = {'s': ogg_len(f), 'text': ' '.join(s.text.strip() for s in segs)}
        json.dump(res, open(out, 'w'), indent=1)
    for k, v in sorted(res.items(), key=lambda x: x[1]['s']):
        print(f'{v["s"]:5.2f}s  {k:40} {v["text"]}')
