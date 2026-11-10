#!/usr/bin/env python3
"""Writes mod/RepoRPG_RU.rules: the hand-written part below plus what tools/flow.py found out about the mod's own methods.

usage: genrules.py <flow.json> <out.rules>
"""
import json, sys, collections

HEAD = r'''# RepoRPG_RU.rules - what the add-on may and may not touch in RepoRPG.dll.
# Written for RepoRPG 1.0.100. Plain text on purpose: after an update of the mod it can be adjusted without rebuilding anything.
# The report (BepInEx/config/RepoRPG_RU/report.txt) lists every rule that no longer finds its target.
#
#   Type::Method(param)   the mod's own method and the name of its text parameter
#   Type::Method#N        argument N (0-based) of a call; "Namespace.Type::Method" also matches other assemblies

[hooks]
# Everything the mod draws or measures passes through these helpers of its own.
# The text is translated right at their entrance: before it is measured, shortened or wrapped.
RpgRuntime::Label(text)
RpgRuntime::ShadowLabel(text)
RpgRuntime::BadgeText(text)
RpgRuntime::CenterText(text)
RpgRuntime::TextW(text)
RpgRuntime::TextH(text)
RpgRuntime::FitStyle(text)
RpgRuntime::Fit(s)
RpgRuntime::FitWidth(text)
RpgRuntime::FitLines(text)
RpgRuntime::LabelW(label)
RpgRuntime::ChipW(label)
RpgRuntime::BtnW(label)
RpgRuntime::UBtn(label)
RpgRuntime::Btn(label)
# notices and dialogs: the text is stored first and drawn later
RpgRuntime::Toast(text)
RpgRuntime::Notify(text)
RpgRuntime::Banner(text)
RpgRuntime::PresetNote(text)
RpgRuntime::AskConfirm(title, body, okLabel)
RpgRuntime::AskPurchase(title, body)
RpgRuntime::SetTip(title, body, foot)
# pages of the in-game menu (MenuLib)
NativeMenu::NewPage(title)
NativeMenu::AddText(text)
NativeMenu::AddButton(text)

[calls]
# The HUD measures some texts directly (GUIContent.text = ...) before drawing them: translated at that very call.
UnityEngine.GUIContent::set_text

[display]
MenuLib.MenuAPI::CreateREPOButton#0
MenuLib.MenuAPI::CreateREPOLabel#0
MenuLib.MenuAPI::CreateREPOPopupPage#0

[skip]
# network, saves, parsers, self-checks: never touched inside the code (their texts are still translated when drawn)
Net
Json
SaveBook
SaveWriter
RunStore
Account
HostSettings
SettingsSync
ClassDatabase
LateJoinHost
NetCodeCheck
GameCheck
MenuLibCheck
Compat
Guard
PatchGuard
Tuning
HudTrace
LoopTrace
NetMeter
VanillaStyle
Game

[keep]
# whatever is sent to other players stays exactly as the mod wrote it
Net::*
# the state of the run travels as one object: its texts are put there by these (the notes "season|The run is complete" are
# cut to 300 characters on arrival, the source of a vote to 60)
RunShape::HostBroadcast#0
RunShape::HostNote#0
RunShape::StartVote#2

[data]
# embedded JSON: only these properties are text; everything else is an id, a number or a colour
*: name, desc, complexityTip, evolveHow, evolveName, evolveDesc, flavor, unlock, bases
*: !id, !key, !stat, !type, !requires, !tags, !icon, !color, !colour

[probes]
# the mod looks for these words in the names of OTHER plugins, never in its own interface texts
ignore "late join"
ignore "more players"
ignore "shared upgrade"
ignore "team upgrade"
# stat lines (StatInfo.Describe): StartsWith("s ") sorts the labels of ability numbers and EndsWith(" kill") the reasons of
# XP gains - a stat line never gets there. Left in English they would come out of the mod's own SplitStat()/StatName()
# as "Mark duration", a text no dictionary entry knows.
free "s ability cooldown per nearby kill"
free "s Momentum hold after you stop"
free "s ability cooldown per dodge"
free "s Mark duration"
'''

HOOKED = set()
for line in HEAD.split('[hooks]')[1].split('[calls]')[0].split('\n'):
    line = line.strip()
    if not line or line.startswith('#'): continue
    tm, ps = line.split('(')
    for p in ps.rstrip(')').split(','): HOOKED.add((tm.split('::')[0], tm.split('::')[1], p.strip()))


def main():
    flow = json.load(open(sys.argv[1], encoding='utf-8'))
    P = flow['params']
    known = {(p['type'].split('.')[-1], p['method'], p['param']) for p in P}
    missing = sorted(h for h in HOOKED if h not in known)
    for h in missing: print('  HOOK NOT IN SOURCE: %s::%s(%s)' % h)
    bad = [p for p in P if (p['type'].split('.')[-1], p['method'], p['param']) in HOOKED and p['anykey']]
    for p in bad: print('  HOOKED BUT USED AS ID: %s::%s(%s)  %s' % (p['type'], p['method'], p['param'], p['whykey']))

    ids, keeps, through, sends = collections.OrderedDict(), collections.OrderedDict(), collections.OrderedDict(), collections.OrderedDict()
    for p in P:
        ref = '%s::%s#%d' % (p['type'], p['method'], p['index'])
        hooked = (p['type'].split('.')[-1], p['method'], p['param']) in HOOKED
        if hooked: continue
        if p['anykey'] or p['label'] == 'KEY': ids.setdefault(ref, p['param'] + ((': ' + p['whykey']) if p['whykey'] else ''))
        elif p.get('sent'): sends.setdefault(ref, p['param'] + ': ' + p['sent'])
        elif (p['anylog'] or p['label'] == 'LOG') and p['label'] != 'DISPLAY': keeps.setdefault(ref, p['param'])
        if p['through'] and not p['anykey'] and p['label'] != 'KEY': through.setdefault(ref, p['param'])
    out = [HEAD.rstrip('\n'), '',
           '# ---------------------------------------------------------------------------------------------------------',
           '# Generated from the source of RepoRPG 1.0.100 (tools/flow.py): how the mod\'s own methods use their text arguments.',
           '',
           '[id]',
           '# the argument is compared, looked up or parsed somewhere down the line: a caption passed here is never replaced',
           '# inside the code (it is still translated when it is drawn)']
    for ref, why in ids.items(): out.append('%-58s # %s' % (ref, why[:70]))
    out += ['', '[keep]', '# the argument ends up in the log']
    for ref, why in keeps.items(): out.append('%-58s # %s' % (ref, why))
    out += ['', '# the argument is sent to the other players through another method of the mod']
    for ref, why in sends.items(): out.append('%-58s # %s' % (ref, why))
    out += ['', '[through]', '# the argument comes back inside the return value: what matters is what the caller does with the result']
    for ref, why in through.items(): out.append('%-58s # %s' % (ref, why))
    open(sys.argv[2], 'w', encoding='utf-8', newline='\n').write('\n'.join(out) + '\n')
    print('rules: %d hooks, %d id, %d keep (+%d sent), %d through' % (len(HOOKED), len(ids), len(keeps), len(sends), len(through)))
    return 1 if missing or bad else 0


if __name__ == '__main__':
    sys.exit(main())
