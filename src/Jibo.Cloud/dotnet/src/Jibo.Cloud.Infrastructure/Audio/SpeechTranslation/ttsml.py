#!/usr/bin/env python3
"""Any-language text -> ESML for Jibo's own (English-only) griffin voice, so he speaks other languages in his voice.
eSpeak-NG gives IPA per clause; IPA maps onto the voice's phone set and goes into <phoneme ph='...'>word</phoneme>.
Usage: ttsml.py LANG 'text'  -> ESML.   Self-check: ttsml.py --test
Options (env, for A/B tests): TTSML_LONG=1 doubles long vowels (on by default for LONG_VOWELS);
TTSML_UVULAR=h maps French/German ʁ to h; TTSML_MERGE=1 joins vowel-only words onto the previous word."""
import os, re, subprocess, sys, unicodedata

# Phones are the griffin voice's own set (voices/en_us_world/en_us_world.phones, lowercase), checked against its
# dictionary: aa=father ou=boat uh=cut ah=about ii=beat iy=bit u=boot uo=book e=bet a=bat o=caught ur=bird dt=water.
IPA = [
    ('tʃ', 'tj'), ('dʒ', 'dj'), ('ts', 't s'), ('dz', 'd z'), ('tɕ', 'tj'), ('dʑ', 'dj'), ('ʈʂ', 'tj'), ('tʂ', 'tj'), ('ʂ', 'sh'), ('ʐ', 'zh'),
    ('ɔø', 'oi'), ('ɔʏ', 'oi'), ('ʌʊ', 'au'),  # German eu/äu, Dutch ou/au
    ('aɪ', 'ai'), ('aj', 'ai'), ('eɪ', 'ei'), ('ej', 'ei'), ('oʊ', 'ou'), ('əʊ', 'ou'), ('aʊ', 'au'), ('aw', 'au'),
    ('ɔɪ', 'oi'), ('oɪ', 'oi'), ('oj', 'oi'), ('eu', 'e u'), ('au', 'au'), ('ai', 'ai'), ('ei', 'ei'), ('ou', 'ou'),  # plain diphthongs (Mandarin)
    ('ɑ̃', 'aa n'), ('ɔ̃', 'o n'), ('ɛ̃', 'e n'), ('œ̃', 'uh n'), ('ɐ̃', 'uh n'), ('ẽ', 'e n'), ('ĩ', 'ii n'), ('õ', 'ou n'), ('ũ', 'u n'),
    ('a', 'aa'), ('ä', 'aa'), ('ɑ', 'aa'), ('ɐ', 'uh'), ('æ', 'a'), ('e', 'e'), ('ɛ', 'e'), ('ə', 'ah'), ('ɜ', 'ur'),
    ('i', 'ii'), ('ɪ', 'iy'), ('ɨ', 'iy'), ('o', 'o'),  # pure o: English 'ou' glides (oh-oo) and sounds American
    ('ɔ', 'o'), ('ɒ', 'o'), ('u', 'u'), ('ʊ', 'uo'), ('ɯ', 'u'), ('ʉ', 'u'), ('ɵ', 'uh'),
    ('y', 'u'), ('ʏ', 'uo'), ('ø', 'ur'), ('œ', 'ur'), ('ʌ', 'uh'),
    ('p', 'p'), ('b', 'b'), ('t', 't'), ('d', 'd'), ('k', 'k'), ('q', 'k'), ('c', 'k'), ('ɟ', 'g'), ('g', 'g'), ('ɡ', 'g'), ('m', 'm'), ('n', 'n'), ('ŋ', 'ng'),
    ('ɲ', 'n j'), ('f', 'f'), ('v', 'v'), ('s', 's'), ('z', 'z'), ('ʃ', 'sh'), ('ʒ', 'zh'), ('ɕ', 'sh'), ('ʑ', 'zh'),
    ('θ', 'th'), ('ð', 'd'),  # soft d (Spanish tarde): English 'dh' is the 'th' in 'the'
    ('h', 'h'), ('x', 'h'), ('χ', 'h'), ('ç', 'h'), ('ɸ', 'f'), ('β', 'b'), ('ɣ', 'g'),
    ('l', 'l'), ('ʎ', 'j'),  # Spanish/Italian ll, gl: most speakers say y
    ('ɫ', 'l'), ('ɭ', 'l'),
    ('ɾ', 'dt'), ('r', 'dt'), ('ɽ', 'dt'),  # trilled/tapped r as the tap in 'water', not the American r (English IPA uses ɹ)
    ('ɹ', 'r'), ('ʁ', 'r'), ('ʀ', 'r'), ('w', 'w'), ('ɥ', 'w'), ('ʋ', 'v'), ('j', 'j'),
]
# Where eSpeak's symbol means something else in one language (checked against its output for that language).
LANG_IPA = {
    'ko': [('ɐ', 'aa'), ('o', 'ou')],  # eSpeak writes ㅏ as ɐ (heard as ㅓ); a pure o was heard as ㅓ too
    'pt': [('æ', 'ah'), ('y', 'iy')],  # eSpeak pt: æ = final unstressed a, y = final unstressed e/i
    'ro': [('y', 'iy')],  # eSpeak ro writes â/î as y
    'nl': [('ɣ', 'h')],  # Dutch g is a throat sound, not an English hard g
}
# Rolled/tapped r: the tap in "water" (dt) was heard as d in these languages (Turkish yirmi -> "yutma", German regnen ->
# "weggen"); an English r scored better (de .26->.18, sv .28->.13, tr .22->.16, ro .25->.20). Spanish and Italian keep the
# tap (their own sound, and it scored best); Brazilian Portuguese taps only between vowels.
R_MODE = {'de': 'r', 'sv': 'r', 'tr': 'r', 'ro': 'r', 'pt': 'inter'}
V = 'aeiouyɑɐæɛəɜɪɨɔɒʊɯʉɵøœʌä'
FIXUPS = [
    (None, re.compile('\u0303(?=[ˈˌ]?[mnŋɲ])'), ''),  # nasal vowel then a nasal: one n, not two
    (None, re.compile('ɲ(?![ˈˌ]?[%s])' % V), 'n'),  # ɲ is n + y only before a vowel
    ('ko', re.compile('(tʃ|tɕ|p|t|k|q)h'), r'\1'),  # eSpeak marks Korean aspiration with a plain h
    ('pt', re.compile('ɾə(?=[ˈˌ]?[^%s ˈˌ])' % V), 'ɾ'),  # eSpeak's ɾə before a consonant is one r, not a syllable
]
VOWELS = {'aa', 'uh', 'a', 'e', 'ah', 'ur', 'ii', 'iy', 'ou', 'o', 'u', 'uo', 'ai', 'ei', 'au', 'oi'}
SKIP = set('ˑ̃ʔ‍͡ ̯̩̥̞̝̪̺̻ᵝʰʷ.‿-"0123456789') | {'\u0303'}  # ties, diacritics, glottal stop, Mandarin tone digits
FILLER = 'word'  # the tag's text must be an English dictionary word: unknown text ("ce") is said twice
LONG_VOWELS = {'de', 'ja'}  # doubling long vowels helped these (CER .31->.18, .30->.20) and hurt nl/sv
KEEP = {'jibo', 'jibos', 'jibo’s', "jibo's"}  # his own name stays as he already says it
CLAUSE = re.compile(r'([,.!?;:，。、！？；：…]+)')
PUNCT = {'，': ',', '、': ',', '。': '.', '！': '!', '？': '?', '；': ';', '：': ':', '…': '.'}


def ipa_words(lang, text):
    out = subprocess.run(['espeak-ng', '-q', '--ipa', '-v', lang, text], capture_output=True, text=True, check=True).stdout
    out = re.sub(r'\([a-z\u200d-]+\)', '', out)  # eSpeak's (en)...(ja) language-switch markers
    return out.split()


def arpabet(ipa, lang=''):
    lang = lang.split('-')[0]  # rules are per language; eSpeak's voice may be regional (pt-br)
    long_vowels = os.environ.get('TTSML_LONG') or lang in LONG_VOWELS
    ipa = unicodedata.normalize('NFD', ipa).replace('\u200d', '')  # eSpeak ties affricates with a zero-width joiner
    for only, rx, to in FIXUPS:
        if only in (None, lang): ipa = rx.sub(to, ipa)
    ipa = unicodedata.normalize('NFC', ipa)
    # TTSML_EXTRA='de:ɾ=r,r=r;ko:o=o' (A/B tests): extra symbol mappings for one language.
    extra = [tuple(kv.split('=')) for part in os.environ.get('TTSML_EXTRA', '').split(';') if part.startswith(lang + ':')
             for kv in part.split(':', 1)[1].split(',')]
    table = extra + LANG_IPA.get(lang, []) + IPA
    uvular = os.environ.get('TTSML_UVULAR')
    phones, stress, i = [], 0, 0
    while i < len(ipa):
        c = ipa[i]
        if c == 'ˈ': stress = 1; i += 1; continue
        if c == 'ˌ': stress = 2; i += 1; continue
        if c == 'ː':  # long vowel
            # Only stressed long vowels: an unstressed doubled vowel became an extra syllable (Hallo -> "Hallala").
            if long_vowels and phones and phones[-1][:-1] in VOWELS and (phones[-1][-1] == '1' or lang == 'ja'): phones.append(phones[-1][:-1] + '0')
            i += 1; continue
        if c == 'ʲ':  # palatalized: kʲo -> k j o (Japanese kyo, Russian); dropped before a consonant or at the end
            nxt = ipa[i + 1:].lstrip('ˈˌ')[:1]
            if nxt and any(nxt == k[0] for k, v in table if v.split()[0] in VOWELS): phones.append('j')
            i += 1; continue
        if c == 'ʁ' and uvular: phones.append(uvular); i += 1; continue
        for k, v in table:
            if ipa.startswith(k, i):
                for p in v.split():
                    if p in VOWELS: p += '1' if stress == 1 else '0'; stress = 0 if p[-1] == '1' else stress
                    phones.append(p)
                i += len(k); break
        else:
            i += 1  # diacritic or unknown: drop
    r_mode = os.environ.get('TTSML_R') or R_MODE.get(lang, 'dt')  # dt = tap everywhere, inter = only between vowels, r = English r
    if r_mode != 'dt':
        vow = lambda p: p[:-1] in VOWELS
        phones = [('r' if p == 'dt' and (r_mode == 'r' or not (0 < k < len(phones) - 1 and vow(phones[k - 1]) and vow(phones[k + 1]))) else p)
                  for k, p in enumerate(phones)]
    return ' '.join(phones)


LIMIT = 950  # jibo-tts-service answers 400 (and Jibo stays silent) for ESML of ~1000+ characters


def parts(lang, text):
    if lang in ('cmn', 'zh'):  # Mandarin: pinyin + tones as pitch levels (eSpeak's Mandarin isn't understood)
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__))); import ttsml_zh
        return ttsml_zh.parts(text)
    return parts_espeak(lang, text)


def esml(lang, text):
    return '<speak>' + ' '.join(parts(lang, text)) + '</speak>'


def esml_chunks(lang, text, limit=LIMIT):
    """ESML strings to speak one after another, each under the TTS service's size limit; split after a clause when
    possible, else between words."""
    chunks, cur = [], []
    size = lambda ps: len('<speak>' + ' '.join(ps) + '</speak>')
    for p in parts(lang, text):
        if cur and size(cur + [p]) > limit:
            cut = max((k + 1 for k, q in enumerate(cur) if q[-1] in ',.!?;:'), default=len(cur))
            chunks.append(cur[:cut]); cur = cur[cut:]
            if cur and size(cur + [p]) > limit: chunks.append(cur); cur = []
        cur.append(p)
    if cur: chunks.append(cur)
    return ['<speak>' + ' '.join(c) + '</speak>' for c in chunks]


def parts_espeak(lang, text):
    """Clause by clause (eSpeak stresses words in context, so small words stay unstressed); punctuation kept for pauses
    and question intonation; one phoneme tag per eSpeak word, which also splits Chinese/Japanese into words."""
    parts = []
    for piece in CLAUSE.split(text):
        if not piece.strip(): continue
        if CLAUSE.fullmatch(piece):
            mark = ''.join(PUNCT.get(ch, ch) for ch in piece)
            if parts: parts[-1] += mark[0] if mark[0] in ',.!?;:' else ''
            continue
        for chunk in re.split(r"(\b(?:%s)\b)" % '|'.join(re.escape(k) for k in KEEP), piece, flags=re.I):
            if not chunk.strip(): continue
            if chunk.lower() in KEEP: parts.append(chunk); continue
            phs = [ph for ph in (arpabet(w, lang) for w in ipa_words(lang, chunk)) if ph]
            if os.environ.get('TTSML_MERGE'):  # "va a", "hoy": a lone vowel blends into the word before it
                merged = []
                for ph in phs:
                    if merged and all(p[:-1] in VOWELS for p in ph.split()): merged[-1] += ' ' + ph
                    else: merged.append(ph)
                phs = merged
            parts += ["<phoneme ph='%s'>%s</phoneme>" % (ph, FILLER) for ph in phs]
    return parts


if __name__ == '__main__':
    if sys.argv[1:] == ['--test']:
        assert arpabet('ˈola') == 'o1 l aa0', arpabet('ˈola')
        assert arpabet('bɔ̃ʒˈuʁ') == 'b o0 n zh u1 r', arpabet('bɔ̃ʒˈuʁ')
        assert arpabet('kˌo̞nnit‍ɕˈihä') == 'k o0 n n ii0 tj ii1 h aa0', arpabet('kˌo̞nnit‍ɕˈihä')
        assert arpabet('pˈeɾo') == 'p e1 dt o0' and arpabet('frumˈos') == 'f dt u0 m o1 s'
        assert arpabet('kʲˈoː') == 'k j o1', arpabet('kʲˈoː')  # Japanese kyō keeps its y
        assert arpabet('ˈastəzʲ') == 'aa1 s t ah0 z', arpabet('ˈastəzʲ')
        assert arpabet('ni2 xˈau3') == 'n ii0 h au1', arpabet('ni2 xˈau3')  # tone digits dropped
        assert arpabet('ɡˈuːtən', 'de') == 'g u1 u0 t ah0 n' and arpabet('ɡˈuːtən', 'nl') == 'g u1 t ah0 n'
        assert arpabet('tˈaɾðe') == 't aa1 dt d e0' and arpabet('ʎoβˈeɾ') == 'j o0 b e1 dt'
        assert arpabet('jiɾmˈi', 'tr') == 'j ii0 r m ii1' and arpabet('pˈeɾo', 'es') == 'p e1 dt o0'  # r per language
        assert arpabet('kˈaɾu', 'pt') == 'k aa1 dt u0' and arpabet('mˈaɾ', 'pt-br') == 'm aa1 r'
        assert arpabet('hˈaloː', 'de') == 'h aa1 l o0' and arpabet('ɡˈeːt', 'de') == 'g e1 e0 t'  # unstressed long: once
        assert arpabet('hˈɔøtə', 'de') == 'h oi1 t ah0' and arpabet('ʋɪl', 'nl') == 'v iy0 l' and arpabet('ɣˈaːt', 'nl') == 'h aa1 t'
        assert arpabet('dˈɐŋsinqwˌɐ', 'ko') == 'd aa1 ng s ii0 n k w aa0', arpabet('dˈɐŋsinqwˌɐ', 'ko')
        assert arpabet('tʃhˈumtʃhunˌɯn', 'ko') == 'tj u1 m tj u0 n u0 n', arpabet('tʃhˈumtʃhunˌɯn', 'ko')
        assert arpabet('pˈɔɾətæ', 'pt') == 'p o1 r t ah0' and arpabet('vˈiŋtʃy', 'pt') == 'v ii1 ng tj iy0', arpabet('pˈɔɾətæ', 'pt')
        assert arpabet('dɐ̃ŋsˈar', 'pt') == 'd uh0 ng s aa1 r', arpabet('dɐ̃ŋsˈar', 'pt')
        assert arpabet('bˌɛɲdʑɛ', 'pl') == 'b e0 n dj e0' and arpabet('mˈyɪne', 'ro') == 'm iy1 iy0 n e0', arpabet('mˈyɪne', 'ro')
        ro = esml('ro', 'Bună ziua, ce mai faci? Mă numesc Jibo.')
        assert ro.count('<phoneme') == 7 and "Jibo." in ro and "</phoneme>, <phoneme" in ro and "</phoneme>? <phoneme" in ro, ro
        zh = esml('cmn', '你好，你今天怎么样？')
        assert zh.count('<phoneme') >= 3 and zh.endswith('?</speak>'), zh
        long = 'Hoy va a llover por la tarde, así que lleva un paraguas. ' * 8
        cs = esml_chunks('es', long)
        assert len(cs) > 1 and all(len(c) <= LIMIT for c in cs) and cs[0].endswith('.</speak>'), [len(c) for c in cs]
        assert ' '.join(c[7:-8] for c in cs) == esml('es', long)[7:-8]  # nothing lost or reordered
        assert all(len(c) <= LIMIT for c in esml_chunks('cmn', '我很喜欢和你一起跳舞' * 12))
        print('PASS ttsml'); sys.exit(0)
    print(esml(sys.argv[1], sys.argv[2]))
