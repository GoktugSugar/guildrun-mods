"""Downloads Freddy's sounds from the FNAF wiki (Freddy Fazbear/Audio page) into <out dir>/dl.

  python freddy_fetch_audio.py <out dir>

The Fandom file CDN refuses Python's HTTP client, so the files come through curl with browser headers.
"""
import json, os, subprocess, sys, urllib.parse, urllib.request

UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36'
WIKI = 'https://freddy-fazbears-pizza.fandom.com'
FILES = {   # local name: wiki file
    'xscream': 'XSCREAM.ogg',
    'freddy_jumpscare': 'FreddyJumpscare.ogg',
    'powerdown': 'Powerdown.ogg',
    'music_box': 'Music box.ogg',
    'laugh_1': 'Laugh Giggle Girl 1d.ogg',
    'laugh_2': 'Laugh Giggle Girl 2d.ogg',
    'didnt_expect_me': "I bet you didn't expect me to show up did ya now.ogg",
    'didnt_invite_me': "What's that you say You didnt invite me Too bad.ogg",
    'sure_was_fun': 'That sure was fun!.ogg',
    'party': "It looks like someone wasn't ready for Freddy to come to the party.ogg",
}

def api(params):
    url = WIKI + '/api.php?' + urllib.parse.urlencode(params)
    return json.load(urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': UA}), timeout=30))

if __name__ == '__main__':
    out = os.path.join(sys.argv[1], 'dl')
    os.makedirs(out, exist_ok=True)
    r = api({'action': 'query', 'titles': '|'.join('File:' + f for f in FILES.values()), 'prop': 'imageinfo', 'iiprop': 'url', 'format': 'json'})
    urls = {}
    for p in r['query']['pages'].values():
        if 'imageinfo' in p: urls[p['title'][5:]] = p['imageinfo'][0]['url']
    for local, title in FILES.items():
        url = urls.get(title) or urls.get(title.replace('_', ' '))
        if not url: print('missing on wiki:', title); continue
        path = os.path.join(out, local + '.ogg')
        subprocess.run(['curl', '-s', '-L', '-o', path, '-H', f'User-Agent: {UA}', '-H', f'Referer: {WIKI}/',
                        '-H', 'Accept: audio/webm,audio/ogg,audio/*;q=0.9,*/*;q=0.5', url], check=True)
        print(f'{local:18s} {os.path.getsize(path):8d} bytes  <- {title}')
