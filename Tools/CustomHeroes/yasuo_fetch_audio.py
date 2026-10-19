"""Downloads a champion's original (base skin) English voice lines from the League of Legends wiki
(<Champion>/LoL/Audio page; Yasuo by default) into <out dir>/dl and writes <out dir>/clips.json ({name: {"s": seconds, "text": ...}}).

  python yasuo_fetch_audio.py <out dir> [Champion]

The page's wikitext comes from api.php (the HTML pages sit behind a bot check); the transcript is the text next to
each file. The Fandom file CDN refuses Python's HTTP client, so the files come through curl with browser headers.
Local names: Yasuo_Original_Q3_1.ogg -> q3_1.ogg, Yasuo_Select.ogg -> select.ogg.
"""
import json, os, re, struct, subprocess, sys, urllib.parse, urllib.request

UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36'
WIKI = 'https://leagueoflegends.fandom.com'
CHAMP = sys.argv[2] if len(sys.argv) > 2 else 'Yasuo'
PAGE = CHAMP + '/LoL/Audio'
# Sections of the base skin worth having (skip champion-specific encounters, jokes about items, sound effects, etc.).
KEEP = re.compile(r'^' + CHAMP + r'_(Select|Ban|Original_(Move|MoveFirst|Taunt|Joke|Laugh|BasicAttack|Attack|Q[123]?|W|E|EDash|R|Death)_\d+)\.ogg$')


def api(params):
    params = dict(params, format='json')
    url = WIKI + '/api.php?' + urllib.parse.urlencode(params)
    return json.load(urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=30))


def lines(wikitext):
    """(wiki file, transcript) for every bullet whose first file is a kept base-skin clip."""
    out = []
    for ln in wikitext.splitlines():
        if not ln.startswith('*'): continue
        m = re.search(r'\{\{sm2\|([^|}]+)', ln)
        if not m or not KEEP.match(m.group(1)): continue
        text = re.sub(r'\{\{sm2\|[^}]*\}\}', '', ln[1:])
        text = text.replace("'''", '').replace("''", '').strip()
        text = re.sub(r'\[\[(?:[^|\]]*\|)?([^\]]*)\]\]', r'\1', text).replace('—', '; ')
        out.append((m.group(1), text.strip('"').strip()))
    return out


def local_name(f):
    n = f[:-4]
    n = n[len(CHAMP + '_Original_'):] if n.startswith(CHAMP + '_Original_') else n[len(CHAMP + '_'):]
    return n.lower()


def ogg_len(path):
    b = open(path, 'rb').read()
    last = b.rindex(b'OggS')
    gran, = struct.unpack('<q', b[last + 6:last + 14])
    if b'\x01vorbis' in b:
        rate = struct.unpack('<I', b[b.index(b'\x01vorbis') + 12:][:4])[0]
        return round(gran / rate, 2)
    if b'OpusHead' in b:
        pre = struct.unpack('<H', b[b.index(b'OpusHead') + 10:][:2])[0]
        return round((gran - pre) / 48000, 2)
    return 0.0


if __name__ == '__main__':
    root = sys.argv[1]
    out = os.path.join(root, 'dl')
    os.makedirs(out, exist_ok=True)
    wt = api({'action': 'parse', 'page': PAGE, 'prop': 'wikitext'})['parse']['wikitext']['*']
    found = lines(wt)
    seen, items = set(), []
    for f, t in found:
        if f not in seen: seen.add(f); items.append((f, t))
    urls = {}
    for i in range(0, len(items), 50):
        r = api({'action': 'query', 'titles': '|'.join('File:' + f for f, _ in items[i:i + 50]), 'prop': 'imageinfo', 'iiprop': 'url'})
        for p in r['query']['pages'].values():
            if 'imageinfo' in p: urls[p['title'][5:].replace(' ', '_')] = p['imageinfo'][0]['url']
    clips_path = os.path.join(root, 'clips.json')
    clips = json.load(open(clips_path, encoding='utf-8')) if os.path.exists(clips_path) else {}
    for f, text in items:
        url = urls.get(f.replace(' ', '_'))
        if not url: print('missing on wiki:', f); continue
        name = local_name(f)
        path = os.path.join(out, name + '.ogg')
        if not os.path.exists(path):
            subprocess.run(['curl', '-s', '-L', '-o', path, '-H', f'User-Agent: {UA}', '-H', f'Referer: {WIKI}/',
                            '-H', 'Accept: audio/webm,audio/ogg,audio/*;q=0.9,*/*;q=0.5', url], check=True)
        if open(path, 'rb').read(4) != b'OggS':
            print('not an ogg (blocked?):', f); os.remove(path); continue
        s = ogg_len(path)
        clips[name] = {'s': s, 'text': text}
        print(f'{name:16s} {s:5.2f}s  {text}')
    json.dump(dict(sorted(clips.items())), open(clips_path, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
    print(len(clips), 'clips ->', clips_path)
