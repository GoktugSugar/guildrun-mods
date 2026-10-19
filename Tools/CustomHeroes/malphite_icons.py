"""Malphite's portrait cut-out (from the .glb render) and icons (head close-up from his League splash).

  python malphite_icons.py <malphite_render.png> <splash_hd.jpg> <Portraits dir>

Writes _source.png (the render cropped to his outline), Square_Portrait_Malphite.png and Malphite_round.png
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
k = s.width / 1200                                   # box picked on a 1200 px wide preview: crest, eyes and jaw
box = [int(v * k) for v in (645, 265, 935, 555)]
sq = s.crop(box).resize((256, 256), Image.LANCZOS)
sq.save(os.path.join(out, 'Square_Portrait_Malphite.png'))
mask = Image.new('L', (1024, 1024), 0)
ImageDraw.Draw(mask).ellipse((0, 0, 1023, 1023), fill=255)
rd = sq.copy(); rd.putalpha(mask.resize((256, 256), Image.LANCZOS))
rd.save(os.path.join(out, 'Malphite_round.png'))
print('outline', (l, t, r, b), 'icons from box', box)
