#!/usr/bin/env python3
"""Builds the menu font of the add-on: the game's own Teko with Russian letters added.

  mkfont.py <Teko[wght].ttf> <SofiaSansExtraCondensed[wght].ttf> <out.ttf> [--weight N] [--squeeze X]

Why: RepoRPG draws its menus with the game's font, Teko, and Teko has no Cyrillic. Every Russian letter then comes
from whatever font Windows offers as a substitute - half as wide again as Teko, so captions run out of their buttons.
The mod's layout is made for a narrow font.

What it does:
  * Teko at its default weight (Light, 300 - what the game shows) stays exactly as it is: every glyph, every metric,
    the line height. Latin letters, digits and punctuation look the way they do without the add-on.
  * The Cyrillic letters (and the numero sign) come from Sofia Sans Extra Condensed - a narrow sans with the same
    x-height as Teko. They are taken at a weight whose stems are as thick as Teko's once the letters are squeezed
    a little (--squeeze, 0.90 by default), and the capitals are lowered to the height of Teko's capitals and digits.
  * Nothing else is touched; characters neither font has (stars, arrows) keep coming from the system as before.

Both fonts are under the SIL Open Font License 1.1, and so is the result (mod/font/OFL.txt). Sources:
  https://github.com/google/fonts/tree/main/ofl/teko                        Teko[wght].ttf
  https://github.com/google/fonts/tree/main/ofl/sofiasansextracondensed     SofiaSansExtraCondensed[wght].ttf
Needs fontTools (pip install fonttools). The result is kept in the repository (mod/font/RepoRPG_RU.ttf), so building
and packing the add-on needs neither; run this only to change the font:

  python3 -m venv /tmp/venv && /tmp/venv/bin/pip install fonttools
  for f in 'teko/Teko[wght].ttf' 'sofiasansextracondensed/SofiaSansExtraCondensed[wght].ttf'; do
      gh api "repos/google/fonts/contents/ofl/$(python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1]))' "$f")" \
          -H 'Accept: application/vnd.github.raw' > "/tmp/$(basename "$f")"
  done
  /tmp/venv/bin/python tools/mkfont.py '/tmp/Teko[wght].ttf' '/tmp/SofiaSansExtraCondensed[wght].ttf' mod/font/RepoRPG_RU.ttf

Next to the font it writes <out>.check - the widths of a few letters at 100 pixels. The add-on measures the same
letters in the game before it hands the font to the mod: if the numbers differ, the font did not load the way it
should have, and the mod keeps its own.
"""
import argparse, hashlib, os, sys

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.pens.cu2quPen import Cu2QuPen

FAMILY = 'RepoRPG RU Menu'
# Russian, plus the letters of Ukrainian, Belarusian, Serbian, Macedonian and Bulgarian: player names come in all of them
CYRILLIC = list(range(0x0400, 0x0460)) + [0x0490, 0x0491, 0x04AE, 0x04AF, 0x04D8, 0x04D9, 0x04E8, 0x04E9, 0x04A2, 0x04A3, 0x049A, 0x049B, 0x04B0, 0x04B1, 0x04BA, 0x04BB, 0x0492, 0x0493]
EXTRA = [0x2116]                                     # numero sign
CHECK = 'HWЖШДю'                                     # wide letters: a substitute font gives very different numbers


def bounds(font, ch):
    gs = font.getGlyphSet()
    pen = BoundsPen(gs)
    gs[font.getBestCmap()[ord(ch)]].draw(pen)
    return pen.bounds


def stem(font, ch):
    b = bounds(font, ch)
    return b[2] - b[0]


def russian_forms(font):
    """glyph -> the glyph the font shows instead of it in Russian text (GSUB, script cyrl, language RUS, feature locl)"""
    table = font['GSUB'].table
    out = {}
    for sr in table.ScriptList.ScriptRecord:
        if sr.ScriptTag != 'cyrl': continue
        for lr in sr.Script.LangSysRecord:
            if lr.LangSysTag.strip() != 'RUS': continue
            for fi in lr.LangSys.FeatureIndex:
                rec = table.FeatureList.FeatureRecord[fi]
                if rec.FeatureTag != 'locl': continue
                for li in rec.Feature.LookupListIndex:
                    for st in table.LookupList.Lookup[li].SubTable:
                        if hasattr(st, 'ExtSubTable'): st = st.ExtSubTable
                        if hasattr(st, 'mapping'): out.update(st.mapping)
    return out


def is_upper(cp):
    c = chr(cp)
    return c.isupper() or cp in EXTRA


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('teko'); ap.add_argument('sofia'); ap.add_argument('out')
    ap.add_argument('--weight', type=float, default=0, help='weight of the Cyrillic source (0 = the one whose stems match Teko)')
    ap.add_argument('--squeeze', type=float, default=0.90, help='horizontal scale of the Cyrillic letters')
    ap.add_argument('--quiet', action='store_true')
    a = ap.parse_args()

    base = instancer.instantiateVariableFont(TTFont(a.teko), {'wght': 300}, inplace=False)
    want = stem(base, 'I')                                             # 68 units
    cap, xh = bounds(base, 'H')[3], bounds(base, 'x')[3]

    src = TTFont(a.sofia)
    weight = a.weight
    if not weight:
        one = TTFont(a.sofia)                                          # only the letter I: quick to try at many weights
        sub = subset.Subsetter(subset.Options(layout_features=[], notdef_outline=True))
        sub.populate(unicodes=[0x49])
        sub.subset(one)
        lo, hi = 300.0, 800.0                                          # the stems grow with the weight: bisect
        for _ in range(12):
            mid = (lo + hi) / 2
            if stem(instancer.instantiateVariableFont(one, {'wght': mid}, inplace=False), 'I') * a.squeeze < want: lo = mid
            else: hi = mid
        weight = round((lo + hi) / 2)
    donor = instancer.instantiateVariableFont(src, {'wght': weight}, inplace=False)
    assert base['head'].unitsPerEm == donor['head'].unitsPerEm == 1000
    cap_scale = cap / bounds(donor, 'Н')[3]
    low_scale = xh / bounds(donor, 'х')[3]

    dcmap, dset = dict(donor.getBestCmap()), donor.getGlyphSet()
    # Sofia Sans is a Bulgarian typeface: on their own its small letters have the Bulgarian shapes (a "т" like m, a "д"
    # like g). The shapes Russian is printed with are what the font's own "locl" feature puts in for Russian text.
    russian = russian_forms(donor)
    assert len(russian) >= 20, 'the Russian letter forms were not found in the source font'
    for cp in list(dcmap):
        if dcmap[cp] in russian: dcmap[cp] = russian[dcmap[cp]]
    glyf, hmtx = base['glyf'], base['hmtx']
    taken = set(base.getGlyphOrder())
    added = {}
    for cp in CYRILLIC + EXTRA:
        if cp not in dcmap: continue
        assert cp not in base.getBestCmap(), 'Teko already has U+%04X' % cp
        name = 'uni%04X' % cp
        assert name not in taken, name
        sy = cap_scale if is_upper(cp) else low_scale
        rec = DecomposingRecordingPen(dset)
        dset[dcmap[cp]].draw(rec)
        pen = TTGlyphPen(None)
        rec.replay(TransformPen(Cu2QuPen(pen, 1.0), (a.squeeze, 0, 0, sy, 0, 0)))
        g = pen.glyph()
        g.recalcBounds(glyf)
        glyf[name] = g
        hmtx[name] = (int(round(dset[dcmap[cp]].width * a.squeeze)), g.xMin if g.numberOfContours else 0)
        taken.add(name)
        added[cp] = name
    order = list(glyf.glyphOrder)
    base.setGlyphOrder(order)
    for table in base['cmap'].tables:
        if table.isUnicode():
            for cp, name in added.items(): table.cmap[cp] = name
    base['maxp'].numGlyphs = len(order)
    if base['post'].formatType == 2.0: base['post'].extraNames = []

    for t in ('GSUB', 'GPOS', 'GDEF', 'STAT', 'DSIG'):          # shaping tables: the game's menus do not use them
        if t in base: del base[t]

    name = base['name']
    version = 'Version 1.0; Teko Light + Cyrillic of Sofia Sans Extra Condensed (wght %g, x %.2f)' % (weight, a.squeeze)
    notice = ('Copyright 2023 The Teko Project Authors (https://www.github.com/googlefonts/teko). '
              'Copyright 2019 The Sofia Sans Project Authors (https://github.com/lettersoup/Sofia-Sans). '
              'Put together for the RepoRPG_RU add-on.')
    for rec in list(name.names):
        if rec.nameID in (1, 3, 4, 6, 16, 17, 2, 5, 0, 21, 22, 25) or rec.nameID >= 256: name.removeNames(nameID=rec.nameID)
    for nid, text in ((0, notice), (1, FAMILY), (2, 'Regular'), (3, FAMILY + ' ' + version), (4, FAMILY), (5, version), (6, FAMILY.replace(' ', '') + '-Regular')):
        name.setName(text, nid, 3, 1, 0x409)
        name.setName(text, nid, 1, 0, 0)
    os2 = base['OS/2']
    os2.usWeightClass = 400                                          # one face, called Regular: nothing looks for a "bold" next to it
    os2.fsSelection = (os2.fsSelection & ~0x21) | 0x40
    base['head'].macStyle = 0
    os2.ulUnicodeRange1 |= 1 << 9                                    # Cyrillic
    os2.ulCodePageRange1 |= 1 << 2

    # the same file from the same sources, whenever it is built
    base['head'].created = base['head'].modified = 3786825600      # 2024-01-01
    base.recalcTimestamp = False
    base.save(a.out)

    done = TTFont(a.out)
    ok = bounds(done, 'H')[3] == cap and done['hhea'].ascent == TTFont(a.teko)['hhea'].ascent and done['hhea'].descent == TTFont(a.teko)['hhea'].descent
    assert ok, 'the metrics of Teko changed'
    cm, hm = done.getBestCmap(), done['hmtx']
    with open(a.out + '.check', 'w', encoding='utf-8', newline='\n') as f:
        f.write('# Widths of a few letters of %s at 100 pixels, written by tools/mkfont.py.\n' % os.path.basename(a.out))
        f.write('# Before the add-on hands the font to the mod it asks the game for the same widths: if they differ,\n')
        f.write('# the game did not read this file (it put a system font in its place) and the mod keeps its own font.\n')
        f.write('# The family name is the one the font is found by when it is installed in the system.\n')
        f.write('family\t%s\n' % FAMILY)
        f.write('# letter <TAB> size <TAB> width\n')
        for ch in CHECK: f.write('U+%04X\t100\t%d\n' % (ord(ch), int(round(hm[cm[ord(ch)]][0] / 10.0))))
    if not a.quiet:
        up = [hm[cm[c]][0] for c in range(0x410, 0x430)]; lo = [hm[cm[c]][0] for c in range(0x430, 0x450)]
        lat = [hm[cm[ord(c)]][0] for c in 'ABCDEFGHIJKLMNOPQRSTUVWXYZ']; latl = [hm[cm[ord(c)]][0] for c in 'abcdefghijklmnopqrstuvwxyz']
        print('weight %g, squeeze %.2f: stems %d (Teko %d), capitals x%.3f, small letters x%.3f' % (weight, a.squeeze, stem(done, 'І'), want, cap_scale, low_scale))
        print('%d glyphs added; average width: capitals %.0f (Latin %.0f), small %.0f (Latin %.0f) of 1000' % (len(added), sum(up) / len(up), sum(lat) / len(lat), sum(lo) / len(lo), sum(latl) / len(latl)))
        print('%s: %d bytes, sha256 %s' % (a.out, os.path.getsize(a.out), hashlib.sha256(open(a.out, 'rb').read()).hexdigest()[:16]))


if __name__ == '__main__':
    main()
