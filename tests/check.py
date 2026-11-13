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


def launch(*args):
    r = subprocess.run([os.path.join(RTK, 'run.sh'), os.path.join(W, 'bin', 'Host.exe'), ROOT, *args],
                       capture_output=True, text=True, encoding='utf-8')
    out = r.stdout + r.stderr
    return r.returncode, out.splitlines()


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
check('json with BOM, id/stat kept', data[1] == 'data:{ "perks": [ { "id": "Tank", "name": "Танк", "stat": "Tank" } ] }', data[1])
check('report written', os.path.isfile(os.path.join(ROOT, 'config', 'RepoRPG_RU', 'report.txt')))
check('config created', os.path.isfile(CFG))
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
code, out = launch()
check('translated', has(out, 'Label:Выберите класс') and has(out, 'Btn:Настройки'))

print()
if failures:
    print('FAILED: %d check(s): %s' % (len(failures), '; '.join(failures)))
    sys.exit(1)
print('all checks passed')
