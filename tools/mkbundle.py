#!/usr/bin/env python3
"""Packs the add-on's font into a Unity asset bundle: mod/font/RepoRPG_RU.bundle.

Why a bundle. Unity cannot make a usable font out of a .ttf file at run time: `new Font(path)` gives an object without
a single character (seen in the game, font.txt of 0.2.1), and a font is found by its name only when it is installed
in Windows for all users. A font asset loaded from an asset bundle, on the other hand, is a complete dynamic font with
the .ttf inside it - the same kind of object as the game's own Teko.

How. A bundle is written by the Unity editor, which is not at hand here. So the layout is borrowed from a bundle that
is: UniverseLib (the UI library of UnityExplorer) ships `modern.bundle`, built by Unity 2017.1 with type descriptions
inside, and holding two dynamic fonts. That library asks for it in every game made with Unity 2017 or newer
(UniversalUI.cs: `if (major > 5) UIBundle = LoadBundle("modern")`). Everything in it is thrown away except one font
object with its material and (empty) texture and the bundle's table of contents; the font data, names and line
metrics are replaced by ours. What is left of the template is only the layout Unity 2017.1 gave to those four objects;
the material still points at the engine's built-in font shader (file 'unity default resources', id 10101), as it did.

NOT checked: that Unity 6000.3 (the game) accepts a bundle made this way. Nothing here can run the engine; the test
double (tests/unity/AssetBundleModule.cs) only proves the file is well formed and holds what was meant. The add-on
measures the font it gets and writes the outcome to font.txt, so the game gives the answer.

    template    https://github.com/sinai-dev/UniverseLib  src/Resources/modern.bundle
                gh api -H "Accept: application/vnd.github.raw" repos/sinai-dev/UniverseLib/contents/src/Resources/modern.bundle > modern.bundle
    needs       pip install UnityPy fonttools

usage: mkbundle.py <modern.bundle> <font.ttf> <out.bundle> [--quiet]

The line metrics are the ones Unity's font importer writes for a font at its default size of 16 (checked against
both fonts of the template): ascent, descent and line gap of the 'hhea' table, scaled to 16 pixels per em.
The result is the same byte for byte every time, and it is written uncompressed: nothing to go wrong while loading,
and the tests can find the font inside it.
"""
import hashlib, io, sys

import UnityPy
from fontTools.ttLib import TTFont

ASSET_NAME = 'RepoRPG_RU'                       # Font.name of the loaded object
ASSET_PATH = 'assets/reporpg_ru.ttf'            # its path inside the bundle
BUNDLE_NAME = 'reporpg_ru.font'
TEMPLATE_FONT = 'CONSOLA'
IMPORT_SIZE = 16.0


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    quiet = '--quiet' in sys.argv
    if len(args) != 3:
        sys.exit(__doc__)
    template, ttf_path, out = args
    ttf = open(ttf_path, 'rb').read()
    face = TTFont(io.BytesIO(ttf))
    family = face['name'].getDebugName(1)
    upm = face['head'].unitsPerEm
    hhea = face['hhea']
    scale = IMPORT_SIZE / upm

    env = UnityPy.load(template)
    bundle = env.file
    if len(bundle.files) != 1:
        sys.exit('the template is expected to hold one file, it has %d' % len(bundle.files))
    (old_name, sf), = bundle.files.items()

    font = None
    for pid, obj in sf.objects.items():
        if obj.type.name == 'Font' and obj.read_typetree()['m_Name'] == TEMPLATE_FONT:
            font = obj
    if font is None:
        sys.exit('no font named %s in the template' % TEMPLATE_FONT)
    tree = font.read_typetree()
    material_id, texture_id = tree['m_DefaultMaterial']['m_PathID'], tree['m_Texture']['m_PathID']
    table = sf.objects[1]
    if table.type.name != 'AssetBundle':
        sys.exit('object 1 of the template is not the table of contents')
    keep = {1, font.path_id, material_id, texture_id}
    for pid in list(sf.objects):
        if pid not in keep:
            del sf.objects[pid]
    if len(sf.objects) != 4:
        sys.exit('the font of the template does not have a material and a texture of its own')

    # the font: our file, its family name (Unity looks the face up by it), the metrics of the importer
    tree['m_Name'] = ASSET_NAME
    tree['m_FontData'] = list(ttf)
    tree['m_FontNames'] = [family]
    tree['m_FontSize'] = IMPORT_SIZE
    tree['m_Ascent'] = hhea.ascent * scale
    tree['m_Descent'] = hhea.descent * scale
    tree['m_LineSpacing'] = (hhea.ascent - hhea.descent + hhea.lineGap) * scale
    if tree['m_ConvertCase'] != -2 or tree['m_CharacterRects'] or tree['m_FallbackFonts']:
        sys.exit('the font of the template is not a plain dynamic font')
    font.save_typetree(tree)

    # the table of contents: one asset, and what has to be loaded with it (the text shader of Unity itself, the texture,
    # the material, the font - the order the template has)
    toc = table.read_typetree()
    shader = toc['m_PreloadTable'][0]
    if shader['m_FileID'] != 1 or shader['m_PathID'] != 10101:
        sys.exit('the template does not start its preload table with the built-in text shader')
    ref = lambda pid: {'m_FileID': 0, 'm_PathID': pid}
    toc['m_Name'] = toc['m_AssetBundleName'] = BUNDLE_NAME
    toc['m_PreloadTable'] = [shader, ref(texture_id), ref(material_id), ref(font.path_id)]
    entry = lambda pid: (ASSET_PATH, {'preloadIndex': 0, 'preloadSize': 4, 'asset': ref(pid)})
    toc['m_Container'] = [entry(font.path_id), entry(material_id), entry(texture_id)]
    toc['m_Dependencies'] = []
    table.save_typetree(toc)

    # the inner file gets a name of its own: Unity refuses a bundle whose file has the name of one that is loaded
    # already (the template itself, when UnityExplorer is installed next to the add-on)
    new_name = 'CAB-' + hashlib.md5(('RepoRPG_RU menu font ' + BUNDLE_NAME).encode()).hexdigest()
    sf.name = new_name
    bundle.files = {new_name: sf}
    data = bundle.save(packer='none')
    open(out, 'wb').write(data)

    # read it back
    back = UnityPy.load(out).file
    (name, f2), = back.files.items()
    kinds = sorted(o.type.name for o in f2.objects.values())
    fonts = [o.read_typetree() for o in f2.objects.values() if o.type.name == 'Font']
    ok = (name == new_name and kinds == ['AssetBundle', 'Font', 'Material', 'Texture2D'] and len(fonts) == 1
          and bytes(fonts[0]['m_FontData']) == ttf and fonts[0]['m_FontNames'] == [family] and fonts[0]['m_Name'] == ASSET_NAME)
    if not ok:
        sys.exit('the bundle does not read back as written')
    if not quiet:
        f = fonts[0]
        print('%s: %d bytes, sha256 %s' % (out, len(data), hashlib.sha256(data).hexdigest()))
        print('  Unity %s, file %s' % (f2.unity_version, name))
        print('  font %r, family %r, %d bytes of font data' % (f['m_Name'], family, len(ttf)))
        print('  size %g: line %.4f, ascent %.4f, descent %.4f (the game shows them as %d / %d)'
              % (f['m_FontSize'], f['m_LineSpacing'], f['m_Ascent'], f['m_Descent'], int(f['m_LineSpacing']), int(f['m_Ascent'])))


if __name__ == '__main__':
    main()
