#!/usr/bin/env python3
# Ce qu'une session studio a donne, en chiffres : le .pak, le journal du crate et celui du
# moteur, mis cote a cote.
#
#   python3 outils/session.py                      toutes les sessions, une ligne chacune
#   python3 outils/session.py set-2                une session, passage par passage
#   python3 outils/session.py set-2 --pak=set-2/set-2-rejoue.pak   le meme journal, contre un .pak regenere
#
# UNE LIGNE PAR SESSION : la duree du multipiste, les images du .pak et les trous de plus
# de 60 ms (une image manquee), la part du temps ou un tempo est publie et sa mediane,
# les accueils et retraits que le relais a faits, les bascules du cue et celles que les
# filets ont provoquees, les gestes recus du crate.
#
# PASSAGE PAR PASSAGE : pour chaque cue → take du crate, quand le relais a accueilli le
# disque (en secondes apres le cue) et quand il l'a retire (en secondes apres le take), et
# le tempo publie pendant le morceau contre le BPM d'ancrage de la fiche. C'est cette
# lecture qui a montre, le 27 septembre 2026, que les filets du cue alternant se
# declenchaient a contretemps : un relais par passage dans le crate, deux a onze dans le
# moteur.
import json, os, re, struct, sys
from datetime import datetime
from pathlib import Path

STUDIO = Path(os.environ.get("EMOTION_STUDIO_DIR", Path.home() / "Documents/emotion-sources/studio"))
TAILLE = 256           # GpuPacket
OFF_TEMPS, OFF_BPM, OFF_RELAIS = 8, 20, 60

def base(session):
    """Une session mise de cote (set-2-rate-1438) garde le nom de ses fichiers."""
    return re.sub(r"-rate-\d+$", "", session)

PAK = None   # --pak=<chemin> : un .pak regenere par « probe session », a lire a la place de celui de la soiree

def paquets(session):
    pak = Path(PAK) if PAK else STUDIO / session / f"{base(session)}.pak"
    if not pak.exists(): return []
    b = pak.read_bytes(); n = len(b) // TAILLE
    return [(struct.unpack_from("<q", b, i * TAILLE + OFF_TEMPS)[0],
             struct.unpack_from("<f", b, i * TAILLE + OFF_BPM)[0],
             b[i * TAILLE + OFF_RELAIS]) for i in range(n)]

def journal(session):
    j = STUDIO / session / f"{base(session)}-deck.jsonl"
    return [json.loads(l) for l in j.read_text().splitlines() if l.strip()] if j.exists() else []

def moteur(session):
    m = STUDIO / session / f"{base(session)}-moteur.log"
    return m.read_text(errors="replace") if m.exists() else ""

def relais(P):
    """Les instants (ms) des accueils (le fondu s'allume) et des retraits (la platine change)."""
    accueils = [P[i][0] for i in range(1, len(P)) if (P[i][2] >> 2) & 1 and not (P[i - 1][2] >> 2) & 1]
    retraits = [P[i][0] for i in range(1, len(P)) if (P[i][2] & 3) != (P[i - 1][2] & 3)]
    return accueils, retraits

def ligne(session):
    P = paquets(session); n = len(P)
    tbl = STUDIO / session / f"{base(session)}-table.wav"
    wav = (tbl.stat().st_size - 44) / (48000 * 12 * 3) / 60 if tbl.exists() else 0.0
    trous = sum(1 for i in range(1, n) if P[i][0] - P[i - 1][0] > 60)
    bpm = sorted(v for _, v, _ in P if v > 0)
    acc, ret = relais(P)
    log = moteur(session)
    j = journal(session)
    t0 = STUDIO / session / f"{base(session)}-parecord.log"
    debut = datetime.fromtimestamp(t0.stat().st_mtime).strftime("%H:%M") if t0.exists() else "?"
    return (session, debut, wav, n, trous, len(bpm) / n * 100 if n else 0, bpm[len(bpm) // 2] if bpm else 0,
            len(acc), len(ret), log.count("cue → voie"), log.count("filet"), len(j))

def tableau():
    print(f"{'session':16s} {'début':5s} {'wav':>6s} {'images':>7s} {'>60ms':>5s} {'tempo%':>6s} {'bpm':>5s} "
          f"{'acc':>3s} {'ret':>3s} {'basc':>4s} {'filet':>5s} {'gestes':>6s}")
    for s in sorted(p.name for p in STUDIO.iterdir() if p.is_dir()):
        r = ligne(s)
        print(f"{r[0]:16s} {r[1]:5s} {r[2]:6.1f} {r[3]:7d} {r[4]:5d} {r[5]:6.0f} {r[6]:5.1f} {r[7]:3d} {r[8]:3d} "
              f"{r[9]:4d} {r[10]:5d} {r[11]:6d}")

def passages(session):
    P = paquets(session); J = journal(session)
    if not P or not J: sys.exit(f"{session} : il faut le .pak et le journal du crate")
    acc, ret = relais(P)
    cues = [x for x in J if x["verbe"] == "cue"]; takes = [x for x in J if x["verbe"] == "take"]
    print(f"{session} : {len(cues)} cues, {len(takes)} takes dans le crate · {len(acc)} accueils, {len(ret)} retraits dans le moteur")
    print(f"{'disque qui entre':24s} {'cue':>6s} {'accueil':>8s} {'take':>6s} {'retrait':>8s} {'fiche':>6s} {'publié':>6s} {'p10-p90':>12s}")
    for i, (c, t) in enumerate(zip(cues, takes)):
        tc, tt = c["imageMs"], t["imageMs"]
        fin = takes[i + 1]["imageMs"] if i + 1 < len(takes) else P[-1][0]
        fiche = c.get("corps") or {}
        a = [x for x in acc if tc - 5000 <= x <= tt + 60000]
        r = [x for x in ret if tc <= x <= tt + 120000]
        bpm = sorted(v for tm, v, _ in P if tt + 30000 <= tm <= fin and v > 0)
        da = f"{(a[0] - tc) / 1000:+6.0f} s" if a else "    —   "
        dr = f"{(r[0] - tt) / 1000:+6.0f} s" if r else "    —   "
        pub = f"{bpm[len(bpm) // 2]:6.1f}" if bpm else "     —"
        p = f"{bpm[len(bpm) // 10]:5.1f}-{bpm[len(bpm) * 9 // 10]:5.1f}" if bpm else ""
        print(f"{str(fiche.get('Title', ''))[:24]:24s} {tc / 60000:6.1f} {da:>8s} {tt / 60000:6.1f} {dr:>8s} "
              f"{float(fiche.get('Bpm') or 0):6.1f} {pub:>6s} {p:>12s}")
    print("\nlire : cue et take en minutes d'horloge du moteur ; accueil compte depuis le cue, retrait depuis le take ;"
          " plus d'accueils que de takes, ce sont des relais que le crate n'a pas demandes.")

if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    for a in sys.argv[1:]:
        if a.startswith("--pak="): PAK = a[6:]
    if args: passages(args[0])
    else: tableau()
