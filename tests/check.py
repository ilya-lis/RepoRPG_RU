#!/usr/bin/env python3
"""Scenarios for tests/run.sh. Each scenario lays out a BepInEx folder, starts the imitation host and checks its output."""
import os, shutil, subprocess, sys

RTK = os.environ['RTK']
W = os.environ['W']
REPO = os.getcwd()
ROOT = os.path.join(W, 'BepInEx')
ORIG_DIR = os.path.join(ROOT, 'plugins', 'ExtraCreamyMilk-RepoRPG')
HOME = os.path.join(ROOT, 'plugins', 'ilyalis-RepoRPG_RU')
PATCHERS = os.path.join(ROOT, 'patchers', 'ilyalis-RepoRPG_RU')
COPY = os.path.join(HOME, 'RepoRPG.dll')
CFG = os.path.join(ROOT, 'config', 'RepoRPG_RU.cfg')
GAME = os.path.join(W, 'game')                      # the game's own folder (with a mod manager the profile is elsewhere)
failures = []


def layout(addon=True, original=True):
    shutil.rmtree(ROOT, ignore_errors=True)
    os.makedirs(os.path.join(ROOT, 'config'))
    os.makedirs(ORIG_DIR)
    if original:
        shutil.copy(os.path.join(W, 'fake', 'RepoRPG.dll'), ORIG_DIR)
    if addon:
        os.makedirs(os.path.join(HOME, 'lang'))
        os.makedirs(PATCHERS)
        shutil.copy(os.path.join(REPO, 'build', 'RepoRPG_RU.dll'), HOME)
        shutil.copy(os.path.join(REPO, 'build', 'RepoRPG_RU.Patcher.dll'), PATCHERS)
        shutil.copy(os.path.join(REPO, 'tests', 'fake', 'test.po'), os.path.join(HOME, 'lang'))
        shutil.copy(os.path.join(REPO, 'tests', 'fake', 'test.rules'), os.path.join(HOME, 'RepoRPG_RU.rules'))
        os.makedirs(os.path.join(HOME, 'font'))
        for f in ('RepoRPG_RU.ttf', 'RepoRPG_RU.ttf.check'):
            shutil.copy(os.path.join(REPO, 'mod', 'font', f), os.path.join(HOME, 'font'))
    shutil.rmtree(GAME, ignore_errors=True)
    os.makedirs(GAME)


def launch(*args, **env):
    r = subprocess.run([os.path.join(RTK, 'run.sh'), os.path.join(W, 'bin', 'Host.exe'), ROOT, *args],
                       capture_output=True, text=True, encoding='utf-8', env=dict(os.environ, **env))
    out = r.stdout + r.stderr
    return r.returncode, out.splitlines()


def font_of(lines, key='font='):
    """('RepoRPG_RU', 68) from the line 'font=RepoRPG_RU 68': the font the fake mod got and how wide it drew a Russian caption"""
    for l in lines:
        if l.startswith(key):
            f = l[len(key):].rsplit(' ', 1)
            return (f[0], int(f[1])) if len(f) == 2 and f[1].isdigit() else (l[len(key):], 0)
    return ('', 0)


def font_txt():
    p = os.path.join(ROOT, 'config', 'RepoRPG_RU', 'font.txt')
    return open(p, encoding='utf-8').read() if os.path.isfile(p) else ''


def check(name, cond, detail=''):
    print(('  ok   ' if cond else '  FAIL ') + name + (('  -> ' + str(detail)) if detail and not cond else ''))
    if not cond:
        failures.append(name)


def has(lines, text):
    return any(text == l for l in lines)


def contains(lines, text):
    return any(text in l for l in lines)


def set_cfg(key, value):
    s = open(CFG, encoding='utf-8').read().splitlines()
    s = [(key + ' = ' + value) if l.startswith(key + ' =') else l for l in s]
    open(CFG, 'w', encoding='utf-8').write('\n'.join(s) + '\n')


verbose = '-v' in sys.argv

print('A. original only (baseline)')
layout(addon=False)
code, base = launch()
check('runs', code == 0, base)
check('english', has(base, 'Label:Choose your class') and has(base, 'Btn:Class'))

print('B. add-on installed, first launch')
layout()
code, out = launch()
if verbose:
    print('\n'.join('      ' + l for l in out))
check('runs', code == 0, out)
check('copy written', os.path.isfile(COPY))
check('original untouched', open(os.path.join(ORIG_DIR, 'RepoRPG.dll'), 'rb').read() == open(os.path.join(W, 'fake', 'RepoRPG.dll'), 'rb').read())
check('bumped copy is the one loaded', contains(out, 'Loading [rpgmod.semibotrpg 1.0.100.1] from plugins/ilyalis-RepoRPG_RU/RepoRPG.dll'))
check('original skipped', contains(out, 'Skipping [1.0.100] because a newer version exists (1.0.100.1)'))
check('no failed initializer', not contains(out, 'Failed to run Initializer') and not contains(out, 'PLUGIN FAILED'))
# sentences
check('sentence', has(out, 'Label:Выберите класс'))
check('ternary', has(out, 'Toast:Недостаточно золота') and has(out, 'Label:Недостаточно золота'))
check('concat', has(out, 'Label:Уровень 5'))
check('format with holes', has(out, 'Label:Уровень 3 — 40.5 опыта'))
check('interpolated', has(out, 'Label:Золото: 3'))
# the name comes from the mod's data at run time: it is found inside the already translated sentence
check('concat x3 + runtime term inside', has(out, 'Toast:Снаряжение: Железная пластина — надето'), [l for l in out if l.startswith('Toast:')])
check('string.Concat(3)', has(out, 'Label:Откроется на уровне 7.'))
check('switch bodies', has(out, 'Label:Редкий предмет') and has(out, 'Label:Эпический предмет'))
check('confirm dialog', has(out, 'Ask:Сбросить класс?/Все очки будут возвращены./Сбросить'))
check('closure', has(out, 'Toast:Класс сброшен'))
check('iterator', out.count('Label:Выберите класс') == 2 and has(out, 'Label:Куплено'))
# ids stay ids, captions are translated at display time
check('tab captions (display time)', has(out, 'Btn:Класс') and has(out, 'Btn:Снаряжение') and has(out, 'Btn:Магазин') and has(out, 'Btn:Настройки'))
check('tab id compare intact', has(out, 'tab-is-class=True'))
check('badge lookup by id intact', has(out, 'Label:2'))
check('stat key intact', has(out, 'stat=2'))
check('local shown + compared', has(out, 'Label:Снаряжение') and has(out, 'mode-ok'))
check('direct single word', has(out, 'Label:Класс'))
check('measure sees translated text', has(out, 'w=63'), [l for l in out if l.startswith('w=')])
# must not be translated
check('log untouched', has(out, 'log:Purchased') and contains(out, '[Info   :RepoRPG] Not enough gold'))
check('network untouched', has(out, 'sent=Not enough gold'))
check('notify key untouched, text translated', has(out, 'Notify[shop.fail]:Недостаточно золота'))
check('exception untouched', has(out, 'ex:Not enough gold'))
check('enum parse untouched', has(out, 'enum=Monday'))
check('handshake version untouched', has(out, 'handshake=RPG/1.0.100/proto4'))
check('config: section/key/default kept, description translated',
      has(out, "cfg:General|UiFont|auto|Шрифт меню. «auto» — шрифт игры.") and has(out, 'cfg:HUD|Scale|1|Масштаб интерфейса.')
      and has(out, 'cfg:HUD|Anchor|Left|Где расположен интерфейс.'), [l for l in out if l.startswith('cfg:')])
check('array of names: shown translated, literal search intact', has(out, 'Label:Быстрые ячейки') and has(out, 'Label:Голосование') and has(out, 'idx=1'))
check('array shown only', has(out, 'Label:Удерживайте для бега') and has(out, 'Label:Нажмите для рывка'))
check('id word inside composed caption', has(out, 'state-on') and has(out, 'Label:Режим: Вкл'), [l for l in out if 'Mode' in l or 'Режим' in l])
check('scoped entry', has(out, 'Label:Обзор'))
check('bad placeholder entry rejected', contains(out, '{placeholders} differ'))
# probes: the mod's own string tests keep giving the same answers
check('prefix probe: test intact, text translated when drawn', has(out, 'probe-prefix-ok') and has(out, 'Label:Груз слишком велик для этой двери'))
check('inside probe kept by the translation', has(out, 'probe-inside-ok') and has(out, 'Label:Бонус x2'))
check('bare word probe ignored', has(out, 'Label:Появляется скрытое свойство.') and not has(out, 'never'))
check('bare word probe protected by rule', has(out, 'probe-word-ok') and has(out, 'Label:Подарок от друга'))
check('probe ignored by rule', has(out, 'plugin-seen') and has(out, 'Label:Ждём опоздавшего игрока'))
check('layout probe (". ") works on the translation', has(out, 'Label:Интерфейс: показывает уровень'), [l for l in out if 'HUD' in l or 'Интерфейс' in l])
# display-time fallbacks
check('upper-cased by the mod', has(out, 'Label:ЛЕГЕНДА'), [l for l in out if 'LEGEND' in l.upper() or 'ЛЕГЕНД' in l.upper()])
check('wrapped in rich text', has(out, 'Label:<b>Легенда</b>'))
check('notice: sent in english, shown translated', has(out, 'sent2=Provoked: Rex vs Bob') and has(out, 'Toast:Спровоцирован: Rex против Bob'), [l for l in out if 'Rex' in l])
check('explicit pattern', has(out, 'Toast:Занято: Alice'))
check('pattern argument translated too', has(out, 'Toast:Надето: Железная пластина'), [l for l in out if l.startswith('Toast:')])
check('pattern without placeholder rejected', contains(out, 'a pattern needs at least one'))
check('named placeholder dropped', has(out, 'Label:Убейте монстров: 3'))
check('named placeholder invented -> rejected', has(out, 'Label:Carry 2 items'))
check('glued sentence: every piece replaced in the code', has(out, 'glue1=Вставлено: Rune в Blade'), [l for l in out if l.startswith('glue')])
check('glued sentence with a probed piece: untouched in the code, translated when drawn',
      has(out, 'glue2=Load too big 3 carts wide') and has(out, 'Label:Груз слишком велик: 3 тележек в ширину'), [l for l in out if 'carts' in l or 'тележ' in l])
# stat lines and comparison modes
check('stat line declared free of the tests: replaced in the code, the mod cuts the translation', has(out, 'stat1=+2|к длительности Метки (сек.)'), [l for l in out if l.startswith('stat')])
check('same wording without the rule: kept for the test, translated when drawn',
      has(out, 'unit=s stun') and has(out, 'stat2=+3s stun') and has(out, 'Label:+3 с оглушения'), [l for l in out if 'stun' in l or 'оглуш' in l])
check('StartsWith(.., Ordinal): the caption with another case is not a key', has(out, 'probe-case-ok') and contains(out, 'cap=Снаряжение: 4/'), [l for l in out if l.startswith('cap=')])
check('EndsWith(.., OrdinalIgnoreCase): the text it would stop matching stays', has(out, 'probe-nocase-ok') and has(out, 'cap=Снаряжение: 4/Layer3') and has(out, 'Label:Слой 3'),
      [l for l in out if l.startswith('cap=') or 'Layer' in l or 'Слой' in l])
check('stat line', has(out, 'Label:+5% к макс. здоровью'))
check('[skip] Type::Member*: host-side text untouched in the code (closure included), translated when drawn',
      has(out, 'state=Too many trades are open') and has(out, 'Label:Открыто слишком много обменов'), [l for l in out if 'trades' in l or 'обмен' in l])
check('call-site hook: translated before the mod measure', has(out, 'measure=Широкий') and has(out, 'measure2=Широкий'), [l for l in out if l.startswith('measure')])
check('missing hook reported, not fatal', contains(out, 'точек перевода не найдены'))
# data
data = [l for l in out if l.startswith('data:')]
check('json names/descs', '"name": "Танк"' in data[0] and '"desc": "Держит урон. «Ни шагу назад»"' in data[0] and '"name": "Толстая броня"' in data[0])
check('json ids/tags/requires kept', '"id": "tank"' in data[0] and '"tags": ["Tank", "melee"]' in data[0] and '"requires": "t0"' in data[0])
check('json array of names', '"bases": ["Танк", "Scout"]' in data[0], data[0])
check('json numbers byte-identical', '"cost": 1.50' in data[0] and '"version": 3' in data[0])
check('json with BOM, id/stat kept; a path rule reaches only that path',
      data[1] == 'data:{ "perks": [ { "id": "Tank", "name": "Танк", "stat": "Tank" } ], "givers": [ { "label": "Танк" }, { "label": "Груз, который не поднять одному" } ], "notes": [ { "label": "Tank" } ] }', data[1])
check('data text the mod would no longer recognise by its wording: left as it is', '"desc": "Load too big to lift alone"' in data[0], data[0])
check('free-data: a file no wording test reads is translated all the same', '"label": "Груз, который не поднять одному"' in data[1], data[1])
check('report written', os.path.isfile(os.path.join(ROOT, 'config', 'RepoRPG_RU', 'report.txt')))
check('config created', os.path.isfile(CFG))
# the font of the menus
system = font_of(base)
ours = font_of(out)
check('font: without the add-on the mod picks its own', system[0] == 'Bahnschrift' and system[1] > 100, system)
check('font: the mod gets the add-on\'s font, and a Russian caption is drawn much narrower', ours[0] == 'RepoRPG_RU' and 0 < ours[1] < system[1] * 0.6, (ours, system))
check('font: the same object when the mod asks again', has(out, 'font-again=True'))
check('font: survives a level change', has(out, 'font-after-level=True'))
check('font: nothing made in vain', has(out, 'destroyed='), [l for l in out if l.startswith('destroyed')])
check('font: a method that does not begin by reading the setting is served too', has(out, 'font-hud=RepoRPG_RU'), [l for l in out if l.startswith('font')])
check('font: font.txt says what happened', 'итог: шрифт подключён' in font_txt() and 'Ж (100 пикс.): по файлу 49, в игре 49' in font_txt(), font_txt())
check('font: one line in the log', sum(1 for l in out if 'Шрифт меню: шрифт подключён' in l) == 1, [l for l in out if 'Шрифт' in l])
check('font: font.txt lists the fonts of the game with their metrics (ours marked)', '  * RepoRPG_RU: 32 / 46 / 31; H 38, W 55' in font_txt(), font_txt())
rep = open(os.path.join(ROOT, 'config', 'RepoRPG_RU', 'report.txt'), encoding='utf-8').read()
check('font: report counts the places and the rule without a target', 'спрашивает шрифт у русификатора: 2, правил без цели: 1' in rep and 'font method not found: RpgRuntime::PickFont' in rep, [l for l in rep.splitlines() if 'шрифт' in l or 'font' in l])
check('font: missing rule reported, not fatal', contains(out, 'где мод выбирает шрифт меню'))
first = out

print('C. second launch: nothing to rebuild')
mtime = os.path.getmtime(COPY)
code, out = launch()
check('up to date', contains(out, 'перевод актуален'))
check('copy not rewritten', os.path.getmtime(COPY) == mtime)
check('same result', [l for l in out if not l.startswith('[')] == [l for l in first if not l.startswith('[')])

print('D. rich variant uses its own entry')
code, out = launch('rich')
check('rich text', has(out, 'Label:<b>Недостаточно золота</b>'))

print('E. dictionary edited -> rebuilt')
with open(os.path.join(HOME, 'lang', 'test.po'), 'a', encoding='utf-8') as f:
    f.write('\nmsgid "Epic item"\nmsgstr "Эпик"\n')
code, out = launch()
check('rebuilt', contains(out, 'переведён (копия 1.0.100.1)'))
check('new text', has(out, 'Label:Эпик'))

print('F. orphaned copy: add-on DLLs gone, copy still there')
os.remove(os.path.join(PATCHERS, 'RepoRPG_RU.Patcher.dll'))
os.remove(os.path.join(HOME, 'RepoRPG_RU.dll'))
code, out = launch()
check('still runs', code == 0 and not contains(out, 'PLUGIN FAILED'), out)
check('baked strings stay, display-time terms fall back', has(out, 'Label:Выберите класс') and has(out, 'Btn:Settings'))
check('the mod picks its own font', font_of(out) == system and has(out, 'font-hud=Impact'), [l for l in out if l.startswith('font')])

print('G. Enabled = false')
layout(); launch()
set_cfg('Enabled', 'false')
code, out = launch()
check('copy removed', not os.path.exists(COPY))
check('english again', has(out, 'Label:Choose your class') and contains(out, 'Loading [rpgmod.semibotrpg 1.0.100] from plugins/ExtraCreamyMilk-RepoRPG'))
set_cfg('Enabled', 'true')
code, out = launch()
check('back on', has(out, 'Label:Выберите класс'))

print('H. DisplayHooks = false -> no reference to the runtime at all')
set_cfg('DisplayHooks', 'false')
code, out = launch()
check('sentences translated', has(out, 'Label:Выберите класс'))
check('dual-use words left alone', has(out, 'Btn:Class') and has(out, 'tab-is-class=True'))
check('without hooks: proven-display and scoped entries still baked in', has(out, 'Label:Обзор') and has(out, 'Label:Режим: Вкл') and has(out, 'Label:Быстрые ячейки'))
check('without hooks: id-used name stays english, search intact', has(out, 'Label:Team Vote') and has(out, 'idx=1'))
check('no runtime reference', b'RepoRPG_RU\x00' not in open(COPY, 'rb').read().replace(b'RepoRPG_RU.stamp\x00', b''))
check('without hooks: probed texts stay english, tests intact', has(out, 'Label:Load too big for this door') and has(out, 'probe-prefix-ok')
      and has(out, 'Label:Gift from a friend') and has(out, 'probe-word-ok'))
check('without hooks: unprobed texts baked in', has(out, 'Label:Бонус x2') and has(out, 'Label:Ждём опоздавшего игрока') and has(out, 'Label:Появляется скрытое свойство.'))
check('without hooks: nothing derived at display time', has(out, 'Label:LEGEND') and has(out, 'Toast:Taken by Alice') and has(out, 'measure=Wide') and has(out, 'measure2=Wide'))
set_cfg('DisplayHooks', 'true')

print('I. TranslateStrings = false, TranslateData = false')
set_cfg('TranslateStrings', 'false'); set_cfg('TranslateData', 'false')
code, out = launch()
check('code not rewritten: known beginnings and whole formats are recognised when drawn', has(out, 'Label:Уровень 5') and has(out, 'Label:Золото: 3'), [l for l in out if 'Level' in l or 'Gold' in l or 'Уровень' in l])
check('data english', contains(out, '"name": "Tank"'))
check('terms still translated when drawn', has(out, 'Btn:Настройки'))
set_cfg('TranslateStrings', 'true'); set_cfg('TranslateData', 'true')

print('J. original mod disabled / removed')
launch()
os.rename(os.path.join(ORIG_DIR, 'RepoRPG.dll'), os.path.join(ORIG_DIR, 'RepoRPG.dll.old'))
code, out = launch()
check('copy removed', not os.path.exists(COPY))
check('nothing loaded', contains(out, 'no plugins'))
os.rename(os.path.join(ORIG_DIR, 'RepoRPG.dll.old'), os.path.join(ORIG_DIR, 'RepoRPG.dll'))

print('K. original mod updated')
launch()
shutil.copy(os.path.join(W, 'fake', 'v101', 'RepoRPG.dll'), ORIG_DIR)
code, out = launch()
check('rebuilt for the new version', contains(out, 'Loading [rpgmod.semibotrpg 1.0.101.1]') and has(out, 'handshake=RPG/1.0.101/proto4'))
check('translated', has(out, 'Label:Выберите класс'))

print('L. garbage instead of the original')
with open(os.path.join(ORIG_DIR, 'RepoRPG.dll'), 'wb') as f:
    f.write(b'MZ' + b'\x00' * 4096)
code, out = launch()
check('no crash, copy removed', code == 0 and not os.path.exists(COPY) and not contains(out, 'Failed to run Initializer'), out)

print('M. no dictionary')
layout()
shutil.rmtree(os.path.join(HOME, 'lang'))
code, out = launch()
check('english, no copy', has(out, 'Label:Choose your class') and not os.path.exists(COPY))

print('N. DumpStrings + LogUntranslated')
layout(); launch()
set_cfg('DumpStrings', 'true'); set_cfg('LogUntranslated', 'true')
code, out = launch()
dump = os.path.join(ROOT, 'config', 'RepoRPG_RU', 'dump')
check('dump files', all(os.path.isfile(os.path.join(dump, f)) for f in ('literals.tsv', 'data-strings.tsv', 'RepoRPG.classes.json')))
check('dumped json is the original', open(os.path.join(dump, 'RepoRPG.classes.json'), 'rb').read() == open(os.path.join(REPO, 'tests', 'fake', 'classes.json'), 'rb').read())
lit = open(os.path.join(dump, 'literals.tsv'), encoding='utf-8').read()
check('literal dump has scope + use + consumer', 'RpgRuntime::OnGUI\tDisplay\tRepoRPG.RpgRuntime::Label#0\tChoose your class' in lit
      and 'RpgRuntime::OnGUI\tId\tSystem.String::op_Equality#1\tShop' in lit and '\tKeep\tBepInEx.Logging.ManualLogSource::LogInfo#0\tNot enough gold' in lit, lit[:900])

un = os.path.join(ROOT, 'config', 'RepoRPG_RU', 'untranslated.txt')
check('untranslated log', os.path.isfile(un) and 'Untranslated line' in open(un, encoding='utf-8').read().splitlines(),
      open(un, encoding='utf-8').read() if os.path.isfile(un) else 'no file')
check('untranslated log has no russian', os.path.isfile(un) and not any('\u0400' <= ch <= '\u04ff' for ch in open(un, encoding='utf-8').read()))

print('O. flattened install (everything in one plugins folder, no lang/ subfolder)')
layout()
shutil.move(os.path.join(HOME, 'lang', 'test.po'), os.path.join(HOME, 'test.po'))
os.rmdir(os.path.join(HOME, 'lang'))
for f in ('RepoRPG_RU.ttf', 'RepoRPG_RU.ttf.check'):
    shutil.move(os.path.join(HOME, 'font', f), os.path.join(HOME, f))
os.rmdir(os.path.join(HOME, 'font'))
code, out = launch()
check('translated', has(out, 'Label:Выберите класс') and has(out, 'Btn:Настройки'))
check('font found next to the DLL', font_of(out)[0] == 'RepoRPG_RU', font_of(out))

print('P. the player chose a font in the mod\'s settings: it stays')
layout()
code, out = launch(FAKE_UIFONT='Impact')
check('the mod\'s choice', font_of(out) == ('Impact', system[1]), font_of(out))
check('font.txt says why', 'выбран свой шрифт («Impact»)' in font_txt(), font_txt())
check('the method that did not tell its setting: the mod\'s config file has none, so auto', has(out, 'font-hud=RepoRPG_RU'))
with open(os.path.join(ROOT, 'config', 'rpgmod.semibotrpg.cfg'), 'w', encoding='utf-8') as f:
    f.write('[Controls]\n\n## Menu font\n# Setting type: String\n# Default value: auto\nUiFont = Impact\n\n[HUD]\n\nUiFont = auto\n')
code, out = launch()
check('the setting read from the mod\'s config file', has(out, 'font-hud=Impact') and font_of(out)[0] == 'RepoRPG_RU', [l for l in out if l.startswith('font')])
code, out = launch(FAKE_UIFONT=' AUTO ')
check('auto in any case and with blanks is auto', font_of(out)[0] == 'RepoRPG_RU', font_of(out))
code, out = launch(FAKE_UIFONT='')
check('an empty setting is auto, as in the mod', font_of(out)[0] == 'RepoRPG_RU', font_of(out))

print('Q. MenuFont = false')
layout(); launch()
mtime = os.path.getmtime(COPY)
set_cfg('MenuFont', 'false')
code, out = launch()
check('the mod picks its own font', font_of(out) == system and has(out, 'font-hud=Impact'), [l for l in out if l.startswith('font')])
check('nothing rebuilt for it', os.path.getmtime(COPY) == mtime and contains(out, 'перевод актуален'))
check('font.txt says why', 'MenuFont = false' in font_txt(), font_txt())
check('still translated', has(out, 'Label:Выберите класс'))

print('R. a config file written by an older version gets the new switch, the old ones keep their values')
layout(); launch()
lines = [l for l in open(CFG, encoding='utf-8').read().splitlines()]
start = next(i for i, l in enumerate(lines) if l.startswith('MenuFont ='))
while not lines[start - 1].startswith('DisplayHooks ='):
    start -= 1
end = next(i for i, l in enumerate(lines) if l.startswith('MenuFont =')) + 1
open(CFG, 'w', encoding='utf-8').write('\n'.join(lines[:start] + lines[end:]) + '\n')
set_cfg('LogUntranslated', 'false')
check('(the old file has no MenuFont)', 'MenuFont' not in open(CFG, encoding='utf-8').read())
code, out = launch()
cfg = open(CFG, encoding='utf-8').read()
check('MenuFont added, true', 'MenuFont = true' in cfg, cfg)
check('the player\'s value kept, the default still told', 'LogUntranslated = false' in cfg and '# Default value: true\nLogUntranslated = false' in cfg, cfg)
check('font on', font_of(out)[0] == 'RepoRPG_RU')

print('S. the font file is damaged or gone: the mod keeps its own font')
layout()
with open(os.path.join(HOME, 'font', 'RepoRPG_RU.ttf'), 'wb') as f:
    f.write(b'\x00\x01\x00\x00' + b'not a font at all' * 400)
code, out = launch()
check('damaged: runs, the mod\'s own font', code == 0 and font_of(out) == system and not contains(out, 'PLUGIN FAILED'), [l for l in out if l.startswith('font')])
check('damaged: font.txt shows the widths that gave it away', 'по файлу 49, в игре 70' in font_txt() and 'итог: шрифт не подключён' in font_txt(), font_txt())
check('damaged: what the game made for nothing is destroyed (from the file, then by the name)', has(out, 'destroyed=RepoRPG_RU,RepoRPG RU Menu'), [l for l in out if l.startswith('destroyed')])
check('damaged: asked once, not on every style change', sum(1 for l in out if 'Шрифт меню:' in l) == 1, [l for l in out if 'Шрифт' in l])
check('damaged: still translated', has(out, 'Label:Выберите класс'))
os.remove(os.path.join(HOME, 'font', 'RepoRPG_RU.ttf'))
code, out = launch()
check('gone: the mod\'s own font', code == 0 and font_of(out) == system)
check('gone: font.txt says so', 'не найден' in font_txt(), font_txt())
layout()
os.remove(os.path.join(HOME, 'font', 'RepoRPG_RU.ttf.check'))
code, out = launch()
check('no check file: not trusted blindly', font_of(out) == system and 'нет файла с контрольными ширинами' in font_txt(), font_txt())

print('T. the runtime of an older release next to the new patcher (a half-finished update)')
layout()
shutil.copy(os.path.join(W, 'old', 'RepoRPG_RU.dll'), HOME)
code, out = launch()
check('runs, translated', code == 0 and not contains(out, 'PLUGIN FAILED') and has(out, 'Label:Выберите класс') and has(out, 'Btn:Настройки'), out)
check('the mod picks its own font, both times it asks', font_of(out) == system and has(out, 'font-hud=Impact') and has(out, 'font-again=False'), [l for l in out if l.startswith('font')])

print('U. a game that cannot make a font from a file')
layout()
code, out = launch(FAKE_UNITY_NO_PATHS='1')
check('the mod\'s own font', code == 0 and font_of(out) == system, font_of(out))
check('font.txt says what to do', 'Установить для всех пользователей' in font_txt() and '«RepoRPG RU Menu»' in font_txt() and 'установленный в системе' in font_txt(), font_txt())
code, out = launch(FAKE_UNITY_NO_PATHS='1', FAKE_UNITY_INSTALLED='RepoRPG RU Menu=' + os.path.join(REPO, 'mod', 'font', 'RepoRPG_RU.ttf'))
check('the player installed the file as a system font: found by its name', font_of(out) == ('RepoRPG RU Menu', ours[1]) and 'итог: шрифт подключён' in font_txt(), [font_of(out), font_txt()])
code, out = launch(FAKE_UNITY_NO_PATHS='1', FAKE_UNITY_INSTALLED='RepoRPG RU Menu=' + os.path.join(REPO, 'mod', 'icon.png'))
check('another font under that name is not taken', font_of(out) == system, font_of(out))

print('V. Russian letters in the path of the profile')
layout()
RU = os.path.join(ROOT, 'plugins', 'павел-RepoRPG_RU')
os.rename(HOME, RU)
code, out = launch()
check('a Unity that reads such paths: straight from the file', font_of(out)[0] == 'RepoRPG_RU' and 'попытка 2' not in font_txt() and not os.listdir(GAME), font_txt())
code, out = launch(FAKE_UNITY_ASCII_PATHS='1')
copy = os.path.join(GAME, 'RepoRPG_RU', 'RepoRPG_RU.ttf')
check('a Unity that cannot: a copy at a path without them', font_of(out)[0] == 'RepoRPG_RU' and os.path.isfile(copy), [font_of(out), font_txt()])
check('the copy is the file', os.path.isfile(copy) and open(copy, 'rb').read() == open(os.path.join(REPO, 'mod', 'font', 'RepoRPG_RU.ttf'), 'rb').read())
check('font.txt tells both attempts', 'попытка 1' in font_txt() and 'по файлу 49, в игре 70' in font_txt() and 'попытка 2' in font_txt() and 'прочитан по пути' in font_txt(), font_txt())
mtime = os.path.getmtime(copy)
code, out = launch(FAKE_UNITY_ASCII_PATHS='1')
check('the copy is not written again', os.path.getmtime(copy) == mtime and font_of(out)[0] == 'RepoRPG_RU')
shutil.rmtree(RU)

print()
if failures:
    print('FAILED: %d check(s): %s' % (len(failures), '; '.join(failures)))
    sys.exit(1)
print('all checks passed')
