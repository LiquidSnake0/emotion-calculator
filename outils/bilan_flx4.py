#!/usr/bin/env python3
# Bilan d'une session FLX4 : ce que le moteur a capte, contre ce que Mixxx a joue.
#
#   python3 outils/bilan_flx4.py <session> [debut_epoch]
#
# 1. la table : le niveau du master (avant) et du casque (arriere) toutes les 15 s, et la
#    ressemblance casque/master — pres de 1, le casque contient le master (MASTER CUE allume) ;
# 2. les morceaux de l'historique de Mixxx pendant la session, avec leur BPM de bibliotheque et
#    le BPM que le moteur a publie pendant qu'ils etaient au master ;
# 3. le verdict : images perdues, part des images avec un tempo, relais, bascules du cue.
import sqlite3, sys, wave
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import session as S

MIXXX_DB = Path.home() / ".mixxx/mixxxdb.sqlite"
PAS = 15  # secondes par ligne


def db(x):
    return 20 * np.log10(max(x, 1e-9))


def table(chemin):
    """Par tranche de PAS secondes : niveau master, niveau casque (dBFS), ressemblance casque/master."""
    lignes = []
    with wave.open(str(chemin)) as w:
        nc, taux, larg = w.getnchannels(), w.getframerate(), w.getsampwidth()
        while True:
            brut = w.readframes(taux * PAS)
            if not brut:
                break
            b = np.frombuffer(brut, np.uint8).reshape(-1, larg)
            # 24 bits little-endian -> entier signe
            v = (b[:, 0].astype(np.int32) | (b[:, 1].astype(np.int32) << 8) | (b[:, 2].astype(np.int32) << 16))
            v = np.where(v >= 1 << 23, v - (1 << 24), v).astype(np.float32) / (1 << 23)
            v = v.reshape(-1, nc)
            m = v[:, 0:2].mean(axis=1)
            c = v[:, 2:4].mean(axis=1) if nc >= 4 else np.zeros_like(m)
            rm, rc = float(np.sqrt((m ** 2).mean())), float(np.sqrt((c ** 2).mean()))
            r = float(np.dot(m, c) / (np.linalg.norm(m) * np.linalg.norm(c))) if rm > 1e-4 and rc > 1e-4 else 0.0
            lignes.append((db(rm), db(rc), r))
    return lignes


def historique(debut, fin):
    """Les morceaux ajoutes a l'historique de Mixxx entre debut et fin (epoch, secondes)."""
    if not MIXXX_DB.exists():
        return []
    q = ("select p.pl_datetime_added, l.artist, l.title, l.bpm from PlaylistTracks p "
         "join library l on l.id = p.track_id join Playlists pl on pl.id = p.playlist_id "
         "where pl.hidden = 2 order by p.pl_datetime_added")
    with sqlite3.connect(f"file:{MIXXX_DB}?mode=ro", uri=True) as c:
        rangs = c.execute(q).fetchall()
    tous = [(datetime.strptime(q, "%Y-%m-%d %H:%M:%S").replace(tzinfo=timezone.utc).timestamp(),
             a or "", t or "", b or 0.0) for q, a, t, b in rangs]
    out = [x for x in tous if debut - 5 <= x[0] <= fin + 5]
    # le morceau qui jouait deja au lancement de la session : le dernier entre avant
    avant = [x for x in tous if x[0] < debut - 5]
    if avant:
        out.insert(0, (debut,) + avant[-1][1:])
    return out


def main():
    session = sys.argv[1]
    dossier = S.STUDIO / session
    wav = dossier / f"{session}-table.wav"
    if not wav.exists():
        sys.exit(f"pas de {wav}")
    duree = S.duree_wav(wav)
    # parecord.log s'ecrit au lancement et plus apres : son heure est le debut de la session
    debut = float(sys.argv[2]) if len(sys.argv) > 2 else (dossier / f"{session}-parecord.log").stat().st_mtime
    P = S.paquets(session)
    t0 = P[0][0] if P else 0

    def bpm_entre(a, b):
        """BPM publie (mediane) entre a et b secondes depuis le debut de la session."""
        v = sorted(x for tm, x, _ in P if a * 1000 <= tm - t0 <= b * 1000 and x > 0)
        return v[len(v) // 2] if v else 0.0

    print(f"\n== {session} · {duree / 60:.1f} min enregistrees ==\n")
    print(f"{'temps':>6s} {'master':>7s} {'casque':>7s} {'casque=master':>13s} {'bpm e-c':>8s}")
    lignes = table(wav)
    for i, (m, c, r) in enumerate(lignes):
        a = i * PAS
        note = "  <- MASTER CUE ?" if r > 0.8 else ("  casque muet" if c < -60 and m > -40 else "")
        print(f"{a // 60:3d}:{a % 60:02d} {m:6.1f}  {c:6.1f}  {r:12.2f}  {bpm_entre(a, a + PAS):7.1f}{note}")

    morceaux = historique(debut, debut + duree)
    print(f"\n{'arrive a':>8s} {'morceau':42s} {'Mixxx':>6s} {'e-c':>6s} {'écart':>6s}")
    if not morceaux:
        print("  (aucun morceau dans l'historique de Mixxx pendant la session)")
    for i, (t, artiste, titre, bpm) in enumerate(morceaux):
        a = t - debut
        b = morceaux[i + 1][0] - debut if i + 1 < len(morceaux) else duree
        # on laisse 30 s au morceau pour s'installer au master avant de mesurer
        pub = bpm_entre(a + 30, b) if b - a > 40 else bpm_entre(a, b)
        # le moteur peut publier le double ou la moitie : on compare au plus proche
        ecart = min((abs(pub * f - bpm) for f in (0.5, 1, 2)), default=0) if pub and bpm else float("nan")
        nom = f"{artiste} — {titre}"[:42]
        print(f"{int(a) // 60:5d}:{int(a) % 60:02d} {nom:42s} {bpm:6.1f} {pub:6.1f} {ecart:6.1f}")

    r = S.ligne(session)
    trous, tempo, acc, ret, basc = r[4], r[5], r[7], r[8], r[9]
    print(f"\nimages : {r[3]}, perdues (>60 ms) : {trous} · tempo publié sur {tempo:.0f} % des images · "
          f"accueils {acc}, retraits {ret}, bascules du cue {basc}")
    ok = trous == 0 and tempo > 70 and lignes and max(l[1] for l in lignes) > -50
    print("verdict :", "e-c capte le master et le casque" if ok else "à regarder : voir les lignes ci-dessus")


if __name__ == "__main__":
    main()
