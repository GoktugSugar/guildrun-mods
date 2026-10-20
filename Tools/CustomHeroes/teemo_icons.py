"""Teemo's portrait cut-out (from the .glb render) and icons (head close-up from his League splash (Data Dragon Teemo_0.jpg)).

  python teemo_icons.py <teemo_render.png> <splash_hd.jpg> <Portraits dir>

Writes _source.png (the render cropped to his outline), Square_Portrait_Teemo.png and Teemo_round.png
(256x256, the round one masked to a circle like Freddy's).
"""
import sys, os
from PIL import Image, ImageDraw

render, splash, out = sys.argv[1], sys.argv[2], sys.argv[3]
a = Image.open(render).convert('RGBA')
l, t, r, b = a.getchannel('A').getbbox()
m = 20
a.crop((max(l - m, 0), max(t - m, 0), min(r + m, a.width), min(b + m, a.height))).save(os.path.join(out, '_source.png'))

s = Image.open(splash).convert('RGBA')
k = s.width / 1215                                   # box picked on the 1215 px Data Dragon splash: hat, eyes and grin
box = [int(v * k) for v in (445, 15, 855, 425)]
sq = s.crop(box).resize((256, 256), Image.LANCZOS)
sq.save(os.path.join(out, 'Square_Portrait_Teemo.png'))
mask = Image.new('L', (1024, 1024), 0)
ImageDraw.Draw(mask).ellipse((0, 0, 1023, 1023), fill=255)
rd = sq.copy(); rd.putalpha(mask.resize((256, 256), Image.LANCZOS))
rd.save(os.path.join(out, 'Teemo_round.png'))
print('outline', (l, t, r, b), 'icons from box', box)
