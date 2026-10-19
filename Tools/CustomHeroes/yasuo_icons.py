"""Yasuo's square and round icons from his League splash (head close-up).

  python yasuo_icons.py <splash_hd.jpg> <Portraits dir>

Writes Square_Portrait_Yasuo.png and Yasuo_round.png (256x256, the round one masked to a circle like Freddy's).
"""
import sys, os
from PIL import Image, ImageDraw, ImageFilter

src, out = sys.argv[1], sys.argv[2]
a = Image.open(src).convert('RGBA')
s = a.width / 1200                                   # box picked on a 1200 px wide preview
box = [int(v * s) for v in (805, 135, 1065, 395)]
sq = a.crop(box).resize((256, 256), Image.LANCZOS)
sq.save(os.path.join(out, 'Square_Portrait_Yasuo.png'))
mask = Image.new('L', (1024, 1024), 0)
ImageDraw.Draw(mask).ellipse((0, 0, 1023, 1023), fill=255)
mask = mask.resize((256, 256), Image.LANCZOS)
rd = sq.copy(); rd.putalpha(mask)
rd.save(os.path.join(out, 'Yasuo_round.png'))
print('icons from box', box)
