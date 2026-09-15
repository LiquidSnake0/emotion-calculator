#!/usr/bin/env python3
"""La mesure du relais : deux disques du bac, un fondu de l'un a l'autre, et le master qui
suit les deux avec les portraits appris au cue. Que retrouve-t-il ?

    python3 outils/relais.py 02_Interactive_WordBank_ 07_Echoes_of_the_Ancients_

Ce que ca fait :
  1. fabrique le melange : A seul pendant AVANT secondes, un fondu lineaire de A vers B sur
     FONDU secondes, puis B seul — cent secondes en tout, chacun a sa propre position dans
     son morceau (B est cale a zero au moment ou il entre, comme un drop) ;
  2. lance la sonde en mode relais : deux cues apprennent A et B sur leur fichier, le master
     suit le melange en recevant les portraits au rythme du fondu ;
  3. juge : pour chaque case publiee, le stem Demucs (de A ou de B, pese par le fader) qui
     lui ressemble le plus, et si ce stem appartient au disque que la case annonce. Puis, par
     disque, si la somme de ses cases suit son fader.

Les stems viennent du cache de `reference.py` (`~/.cache/emotion-emulator/stems/reference/`),
les morceaux du bac (`~/.cache/emotion-emulator/<titre>.wav`).
"""
import os
import subprocess
import sys
import wave

import numpy as np

CACHE = os.path.expanduser(os.environ.get("EMOTION_CACHE_DIR", "~/.cache/emotion-emulator"))
STEMS = os.path.join(CACHE, "stems", "reference")
RACINE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AVANT, FONDU, APRES = 40.0, 20.0, 40.0
DUREE = AVANT + FONDU + APRES
NOMS = ("drums", "bass", "other", "vocals", "guitar", "piano")
EPS = 1e-12


def lire(chemin, secondes=None):
    with wave.open(chemin) as f:
        taux = f.getframerate()
        n = f.getnframes() if secondes is None else min(f.getnframes(), int(secondes * taux))
        x = np.frombuffer(f.readframes(n), "<i2").astype(np.float64)
        if f.getnchannels() == 2:
            x = x.reshape(-1, 2).mean(axis=1)
    return x / 32768.0, taux


def ecrire(chemin, x, taux):
    y = np.clip(x, -1, 1)
    with wave.open(chemin, "wb") as f:
        f.setnchannels(1); f.setsampwidth(2); f.setframerate(taux)
        f.writeframes((y * 32767).astype("<i2").tobytes())


def gains(taux, n):
    t = np.arange(n) / taux
    gB = np.clip((t - AVANT) / FONDU, 0, 1)
    return 1 - gB, gB


def melange(a, b, taux):
    n = int(DUREE * taux)
    xa = np.zeros(n); xb = np.zeros(n)
    xa[:min(n, len(a))] = a[:n]
    # B entre a AVANT secondes, cale a zero : un drop.
    debut = int(AVANT * taux)
    nb = min(n - debut, len(b))
    xb[debut:debut + nb] = b[:nb]
    gA, gB = gains(taux, n)
    return gA * xa + gB * xb, xa, xb


def enveloppe(x, taux, pas_s):
    pas = int(pas_s * taux)
    m = len(x) // pas * pas
    return np.abs(x[:m]).reshape(-1, pas).mean(axis=1)


def correlation(a, b):
    m = min(len(a), len(b)); a = a[:m] - a[:m].mean(); b = b[:m] - b[:m].mean()
    d = np.linalg.norm(a) * np.linalg.norm(b)
    return float(a @ b / d) if d > EPS else 0.0


def juger(tsv, xa_stems, xb_stems, taux, dire=print):
    lignes = [l.rstrip("\n").split("\t") for l in open(tsv) if l.strip()]
    temps = np.array([float(l[0]) for l in lignes])
    ncases = max((len(l) - 2) // 5 for l in lignes)
    niveau = np.zeros((len(lignes), ncases)); disque = np.full((len(lignes), ncases), -1)
    for i, l in enumerate(lignes):
        k = (len(l) - 2) // 5
        for r in range(k):
            niveau[i, r] = float(l[2 + 5 * r]); disque[i, r] = int(l[2 + 5 * r + 4])
    pas_s = float(np.median(np.diff(temps))) if len(temps) > 1 else 0.0213
    n = int(DUREE * taux)
    gA, gB = gains(taux, n)
    env = {}
    for nom, x in xa_stems.items():
        env[("A", nom)] = enveloppe(gA * x, taux, pas_s)
    for nom, x in xb_stems.items():
        env[("B", nom)] = enveloppe(gB * x, taux, pas_s)
    envA = enveloppe(gA, taux, pas_s); envB = enveloppe(gB, taux, pas_s)

    # Les images de la sonde tombent a t ; l'enveloppe des stems au meme pas : on aligne par
    # l'indice temporel.
    idx = np.clip((temps / pas_s).astype(int), 0, len(envA) - 1)

    def serie(e):
        return e[idx]

    # LE JUGEMENT SE FAIT PAR TRANCHE, PAS SUR TOUT LE FICHIER. Avant le fondu, les stems de
    # B sont nuls ; apres, ceux de A. Une correlation prise sur les cent secondes melange
    # trois situations, et c'est pendant le fondu — quand les deux disques sonnent — que la
    # question se pose : la case qui se dit « A » suit-elle un stem de A ou un stem de B ?
    tranches = (("A seul", temps < AVANT), ("fondu", (temps >= AVANT) & (temps <= AVANT + FONDU)),
                ("B seul", temps > AVANT + FONDU))
    ok = 0; juges = 0; fuites = []
    for nom_tranche, dans in tranches:
        dire("")
        dire(f"--- {nom_tranche}")
        dire("case  disque annonce  | stem le plus proche      corr | meilleur de A  meilleur de B | son disque ?")
        for r in range(ncases):
            vivant = dans & (disque[:, r] >= 0)
            if vivant.sum() < 100: continue
            for tag in sorted(set(disque[vivant, r])):
                sel = vivant & (disque[:, r] == tag)
                if sel.sum() < 100: continue
                y = niveau[sel, r]
                if y.std() < 1e-6: continue
                par_disque = {}
                for (d, nom), e in env.items():
                    ref = serie(e)[sel]
                    if ref.std() < EPS: continue
                    c = correlation(y, ref)
                    if d not in par_disque or c > par_disque[d][1]: par_disque[d] = (nom, c)
                if not par_disque: continue
                meilleur_d = max(par_disque, key=lambda d: par_disque[d][1])
                nom_tag = {0: "A (joue)", 1: "B (entre)", 2: "A+B (reste)"}[tag]
                attendu = {0: "A", 1: "B", 2: None}[tag]
                # Le verdict ne vaut que la ou les deux disques sonnent : pendant le fondu.
                if attendu is not None and nom_tranche == "fondu" and len(par_disque) == 2:
                    bon = meilleur_d == attendu
                    juges += 1; ok += bon
                    verdict = "oui" if bon else "NON"
                    autre = "B" if attendu == "A" else "A"
                    fuites.append(par_disque[autre][1] / max(EPS, par_disque[attendu][1]))
                else:
                    verdict = "-"
                a_txt = f"{par_disque['A'][0]:6s} {par_disque['A'][1]:5.2f}" if 'A' in par_disque else "   -        "
                b_txt = f"{par_disque['B'][0]:6s} {par_disque['B'][1]:5.2f}" if 'B' in par_disque else "   -        "
                nom_m, c_m = par_disque[meilleur_d]
                dire(f"  {r + 1}    {nom_tag:12s}   | {meilleur_d} {nom_m:8s}         {c_m:5.2f} | {a_txt}  {b_txt} | {verdict}")
    dire("")
    # Les cases de chaque disque suivent-elles son fader ? Somme des niveaux par tag, pendant
    # le fondu seulement — c'est la ou la question se pose.
    fondu = (temps >= AVANT) & (temps <= AVANT + FONDU)
    for tag, nom, e in ((0, "A", envA), (1, "B", envB)):
        somme = np.array([niveau[i, disque[i] == tag].sum() for i in range(len(lignes))])
        c = correlation(somme[fondu], serie(e)[fondu])
        dire(f"  pendant le fondu, la somme des cases de {nom} suit le fader de {nom} : corr {c:5.2f}")
    if fuites:
        dire(f"  fuite mediane pendant le fondu (corr avec l'autre disque / corr avec le sien) : {np.median(fuites):.2f}")
    if juges:
        dire(f"\n  pendant le fondu : {ok}/{juges} cases dont le stem le plus proche vient du disque annonce")
    return ok, juges


def main():
    if len(sys.argv) != 3:
        print(__doc__); return 1
    a_nom, b_nom = sys.argv[1], sys.argv[2]
    a, taux = lire(os.path.join(CACHE, a_nom + ".wav"))
    b, taux_b = lire(os.path.join(CACHE, b_nom + ".wav"))
    if taux != taux_b:
        print("les deux morceaux n'ont pas le meme taux"); return 1
    mix, xa, xb = melange(a, b, taux)
    dossier = os.path.join(CACHE, "relais"); os.makedirs(dossier, exist_ok=True)
    chemin_mix = os.path.join(dossier, f"{a_nom}--{b_nom}.wav")
    ecrire(chemin_mix, mix, taux)
    print(f"melange ecrit : {chemin_mix} ({DUREE:.0f} s, A seul {AVANT:.0f} s, fondu {FONDU:.0f} s)")

    tsv = os.path.join(dossier, f"{a_nom}--{b_nom}.tsv")
    cmd = ["dotnet", "run", "-c", "Release", "--no-build", "--project", os.path.join(RACINE, "tools", "Emotion.Probe"), "--",
           chemin_mix, "0", str(int(DUREE)), f"relaisA={os.path.join(CACHE, a_nom + '.wav')}",
           f"relaisB={os.path.join(CACHE, b_nom + '.wav')}", f"fondu={AVANT:.0f},{AVANT + FONDU:.0f}", f"sources={tsv}"]
    r = subprocess.run(cmd, capture_output=True, text=True)
    for l in r.stdout.splitlines():
        if l.startswith("cue ") or l.startswith("master") or "accueil de B" in l or "retrait de A" in l:
            print(l)
    if r.returncode != 0:
        print(r.stderr[-800:]); return 1

    def stems(nom, x_ref):
        d = {}
        for s in NOMS:
            p = os.path.join(STEMS, f"{nom}-ref-{s}.wav")
            if not os.path.exists(p): continue
            y, tx = lire(p)
            if tx != taux: continue
            z = np.zeros(len(x_ref)); z[:min(len(z), len(y))] = y[:len(z)]
            d[s] = z
        return d
    n = len(mix)
    sa = stems(a_nom, mix)
    # Les stems de B sont cales comme B dans le melange : a partir de AVANT.
    sb_brut = stems(b_nom, mix)
    sb = {}
    debut = int(AVANT * taux)
    for s, y in sb_brut.items():
        z = np.zeros(n); z[debut:] = y[:n - debut]; sb[s] = z
    if not sa or not sb:
        print("stems Demucs absents pour un des deux morceaux — lancer outils/reference.py d'abord"); return 1
    juger(tsv, sa, sb, taux)
    return 0


if __name__ == "__main__":
    sys.exit(main())
