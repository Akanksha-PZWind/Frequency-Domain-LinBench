"""Generates assets/linbench.ico and assets/linbench.png: LinBench's response-curve mark on a blue tile
(the tile and colours match TuneMe's icon, so the two programs belong together on a taskbar)."""
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(HERE), 'assets')


def draw(size=512):
    s = size
    img = Image.new('RGBA', (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(s * 0.2)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=(31, 111, 235, 255))
    d.rounded_rectangle([0, int(s * 0.62), s - 1, s - 1], radius=r, fill=(26, 95, 204, 255))
    # the mark of the app header: a frequency-response trace (M3 20 L8 9 L12 15 L16 5 L21 20 on a 24 grid)
    pts = [(3, 20), (8, 9), (12, 15), (16, 5), (21, 20)]
    k, off = s / 24 * 0.78, s * 0.11
    xy = [(off + x * k, off + y * k) for x, y in pts]
    d.line(xy, fill=(255, 255, 255, 255), width=max(2, int(s * 0.075)), joint='curve')
    for x, y in xy:
        rr = s * 0.037
        d.ellipse([x - rr, y - rr, x + rr, y + rr], fill=(255, 255, 255, 255))
    return img


if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    big = draw(512)
    big.save(os.path.join(OUT, 'linbench.png'))
    big.save(os.path.join(OUT, 'linbench.ico'), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print('icon written to', OUT)
