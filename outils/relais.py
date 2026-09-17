#!/usr/bin/env python3
"""Le passage d'un disque a l'autre, tel que le DJ le fait — fabrique, analyse par le
relais, juge case par case.

    python3 outils/relais.py 09_Codex_Sinaiticus_ 06_Passepartout_
    python3 outils/relais.py A B --un-b 4          # le « 1 » de B est son 5e temps detecte
    python3 outils/relais.py A B --nudge-ms 40     # B 40 ms plus tard, a l'oreille
    python3 outils/relais.py A B --lineaire        # l'ancien fondu lineaire de 20 s

LE CRATE DECIDE, PAS CE FICHIER. Le BPM auquel chaque disque se joue (`anchorBpm`) et la
cle transposee viennent de la fiche du crate ; le couple lui-meme doit etre un
enchainement que le crate classe bien (voir docs/mix-vinyle.md). Ici on ne fait que
fabriquer le geste :

  mesures de A   geste
  1-16           A seul
  17 (sur le 1)  B lache sur le 1, basses coupees, fader de canal B monte sur 8 mesures
  25-40          les deux a egalite, B sans basses
  41-48          bass swap : LOW A descend, LOW B monte, sur 8 mesures
  49-56          les deux, basses chez B
  57-64          fader de canal A descend sur 8 mesures
  65-80          B seul

Chaque geste part sur un 1 de phrase. Les temps de A viennent de la grille verite
(periode du crate, phase par l'energie) ; B est reechantillonne au tempo joue de A, cale
temps sur temps, et lache de son propre « 1 » sur le 1 de la mesure 17. Le « 1 » de
chaque disque n'est pas mesurable a coup sur : par defaut c'est le premier temps detecte,
`--un-a` / `--un-b` le decalent quand l'oreille dit autre chose.

Ce que ca produit dans ~/.cache/emotion-emulator/relais/ :
  A--B.wav        le melange (ce que la salle entend)
  A--B-A.wav      A au tempo joue (ce que le cue de A entend)
  A--B-B.wav      B au tempo de A, depuis son 1 (ce que le cue de B entend)
  A--B-cueB.wav   B tel que le casque l'entend, aligne dans le temps, apres EQ, fader a fond
  A--B.tsv        ce que chaque case publie, image par image
  A--B.fader.tsv  le fader tel que BlendEstimator le devine
  A--B.pak        les paquets, a rejouer : ./outils/fondu.sh A B

Puis le juge : les stems Demucs de A et de B, passes par les MEMES faders et la MEME EQ,
et pour chaque case, par tranche du passage, le stem qui lui ressemble le plus.
"""
import argparse
import json
import os
import subprocess
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import verite_terrain  # noqa: E402

CACHE = os.path.expanduser(os.environ.get("EMOTION_CACHE_DIR", "~/.cache/emotion-emulator"))
STEMS = os.path.join(CACHE, "stems", "reference")
RACINE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CRATE = os.path.expanduser(os.environ.get("EMOTION_CRATE", "~/Documents/crate/src/data/seed.json"))
NOMS = ("drums", "bass", "other", "vocals", "guitar", "piano")
EPS = 1e-12

# Le LOW d'une table coupe en plateau sous 100 a 300 Hz selon le modele. Coupure a 120 Hz,
# transition douce de 80 a 160 : lows + highs = le signal, exactement.
CROSSOVER_BAS, CROSSOVER_HAUT = 80.0, 160.0
# Combien de temps chaque cue ecoute son disque avant le passage : au casque, un DJ cale
# une a trois minutes. A 60 s la croissance des sources n'avait pas eu lieu.
CASQUE_S = 180.0
MESURES = 80

# ------------------------------------------------------------------ wav et crate
def lire(chemin):
    with wave.open(chemin) as f:
        taux = f.getframerate()
        x = np.frombuffer(f.readframes(f.getnframes()), "<i2").astype(np.float64)
        if f.getnchannels() == 2:
            x = x.reshape(-1, 2).mean(axis=1)
    return x / 32768.0, taux


def ecrire(chemin, x, taux):
    y = np.clip(x, -1, 1)
    with wave.open(chemin, "wb") as f:
        f.setnchannels(1); f.setsampwidth(2); f.setframerate(taux)
        f.writeframes((y * 32767).astype("<i2").tobytes())


def titre_du_fichier(nom):
    """« 09_Codex_Sinaiticus_ » → « codex sinaiticus »."""
    t = nom.split("_", 1)[1] if nom[:2].isdigit() and "_" in nom else nom
    return " ".join(t.replace("_", " ").split()).lower()


def fiche(nom):
    """La fiche du crate : bpm natif, bpm joue, cle native, cle jouee."""
    titre = titre_du_fichier(nom)
    with open(CRATE, encoding="utf-8") as fh:
        for t in json.load(fh):
            if (t.get("title") or "").lower() == titre:
                bpm = float(t["bpm"]); joue = float(t.get("anchorBpm") or bpm)
                demi_tons = int(round(12 * np.log2(joue / bpm)))
                cle = t.get("key") or ""
                cle_jouee = cle
                if cle and cle[:-1].isdigit():
                    n, lettre = int(cle[:-1]), cle[-1]
                    cle_jouee = f"{((n - 1 + 7 * demi_tons) % 12) + 1}{lettre}"
                return {"titre": t["title"], "bpm": bpm, "joue": joue, "cle": cle, "cle_jouee": cle_jouee,
                        "famille": t.get("family"), "tag": t.get("legacyTag")}
    raise SystemExit(f"{nom} : aucune fiche au crate pour « {titre} » ({CRATE})")


# ------------------------------------------------------------------ signal
def reechantillonner(x, ratio):
    """Joue x `ratio` fois plus vite : une platine dont on pousse le pitch."""
    n = int(len(x) / ratio)
    t = np.arange(n) * ratio
    return np.interp(t, np.arange(len(x)), x)


def couper_bas(x, taux):
    """(lows, highs) par une transition en cosinus, a phase nulle. lows + highs = x."""
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1.0 / taux)
    w = np.ones_like(f)
    z = (f > CROSSOVER_BAS) & (f < CROSSOVER_HAUT)
    w[z] = 0.5 * (1 + np.cos(np.pi * (f[z] - CROSSOVER_BAS) / (CROSSOVER_HAUT - CROSSOVER_BAS)))
    w[f >= CROSSOVER_HAUT] = 0.0
    lows = np.fft.irfft(X * w, n=len(x))
    return lows, x - lows


def rampe(points, n, taux):
    """Une courbe de gain par morceaux lineaires, (seconde, valeur) → un gain par echantillon."""
    t = np.arange(n) / taux
    xs = [p[0] for p in points]; ys = [p[1] for p in points]
    return np.interp(t, xs, ys)


def temps_des_temps(nom, f, ratio):
    """Les instants des temps du disque, au tempo joue ; et si la grille verite est fiable."""
    chemin = os.path.join(CACHE, nom + ".wav")
    instants, relief, periode = verite_terrain.grille(chemin, f["bpm"])
    fiable = relief >= verite_terrain.RELIEF_MINIMUM and len(instants) > 8
    if not fiable:
        x, taux = lire(chemin)
        periode = 60.0 / f["bpm"]
        instants = [k * periode for k in range(int(len(x) / taux / periode))]
    return np.array(instants) / ratio, fiable, relief


# ------------------------------------------------------------------ le geste
class Passage:
    """Les instants du geste, en secondes du melange, et les gains qui en decoulent."""

    def __init__(self, temps_a, un_a):
        # t(n) : le debut de la mesure n (1-based) de A, sur le 1.
        self.temps_a = temps_a; self.un_a = un_a

    def t(self, mesure):
        k = self.un_a + 4 * (mesure - 1)
        if k < len(self.temps_a):
            return float(self.temps_a[k])
        # au-dela des temps detectes : on prolonge a la periode moyenne
        p = float(np.median(np.diff(self.temps_a)))
        return float(self.temps_a[-1]) + p * (k - len(self.temps_a) + 1)

    def gains(self, n, taux):
        t = self.t
        fin = t(MESURES + 1)
        fA = rampe([(0, 1), (t(57), 1), (t(65), 0), (fin, 0)], n, taux)
        fB = rampe([(0, 0), (t(17), 0), (t(25), 1), (fin, 1)], n, taux)
        lowA = rampe([(0, 1), (t(41), 1), (t(49), 0), (fin, 0)], n, taux)
        lowB = rampe([(0, 0), (t(41), 0), (t(49), 1), (fin, 1)], n, taux)
        return fA, fB, lowA, lowB

    def tranches(self):
        t = self.t
        return [("A seul", 1, 17), ("B entre, sans basses", 17, 25), ("les deux, B sans basses", 25, 41),
                ("bass swap", 41, 49), ("les deux, basses chez B", 49, 57), ("A sort", 57, 65), ("B seul", 65, MESURES + 1)]

    def accueil_retrait(self):
        """Ou le relais declare l'accueil (fader B a 15 %) et le retrait (fader A a 10 %)."""
        t = self.t
        return t(17) + 0.15 * (t(25) - t(17)), t(57) + 0.9 * (t(65) - t(57))


def caler_a_la_main(A_m, B_m, taux, periode, t0, t1, pas_s=0.005):
    """Le nudge du DJ : B glisse de moins d'une demi-periode pour que ses kicks tombent sur
    ceux de A. La grille verite se pose a un demi-temps pres (les deux sommets du profil se
    ressemblent) : deux grilles justes chacune peuvent se retrouver a un demi-temps l'une de
    l'autre, et c'est ce qui s'entend comme « un leger decalage ». On juge sur les attaques
    du registre grave, entre t0 et t1, ou les deux jouent a fond. Rend le decalage en
    echantillons a appliquer a B, et la correlation avant / apres."""
    def attaques(x):
        l, _ = couper_bas(x, taux)
        pas = int(pas_s * taux); m = len(l) // pas * pas
        e = np.abs(l[:m]).reshape(-1, pas).mean(axis=1)
        d = np.maximum(np.diff(e), 0); return d - d.mean()
    s0, s1 = int(t0 * taux), int(t1 * taux)
    oa, ob = attaques(A_m[s0:s1]), attaques(B_m[s0:s1])
    nb = np.linalg.norm(oa) * np.linalg.norm(ob)
    if nb < EPS:
        return 0, 0.0, 0.0
    maxlag = int(periode / 2 / pas_s)
    def corr(lag):
        if lag >= 0: return float(oa[lag:] @ ob[:len(ob) - lag]) / nb
        return float(oa[:lag] @ ob[-lag:]) / nb
    lags = range(-maxlag, maxlag + 1)
    best = max(lags, key=corr)
    # un lag positif veut dire que B est en avance sur A : il faut le retarder
    return int(round(best * pas_s * taux)), corr(0), corr(best)


def placer(x, decalage, n):
    """x pose a `decalage` echantillons (negatif : on coupe le debut), sur n echantillons."""
    z = np.zeros(n)
    if decalage >= 0:
        m = min(n - decalage, len(x)); z[decalage:decalage + m] = x[:m]
    else:
        m = min(n, len(x) + decalage); z[:m] = x[-decalage:-decalage + m]
    return z


def fabriquer(a_nom, b_nom, un_a, un_b, dossier, nudge_ms=0.0):
    fa, fb = fiche(a_nom), fiche(b_nom)
    a, taux = lire(os.path.join(CACHE, a_nom + ".wav"))
    b, taux_b = lire(os.path.join(CACHE, b_nom + ".wav"))
    if taux != taux_b:
        raise SystemExit("les deux morceaux n'ont pas le meme taux")
    # A au tempo joue du crate ; B cale sur A : au tempo joue de A, pas au sien.
    rA = fa["joue"] / fa["bpm"]; rB = fa["joue"] / fb["bpm"]
    print(f"A {fa['titre']} : natif {fa['bpm']} → joue {fa['joue']} ({100 * (rA - 1):+.1f} %), {fa['cle']} → {fa['cle_jouee']}, {fa['tag']}")
    print(f"B {fb['titre']} : natif {fb['bpm']} → cale sur A a {fa['joue']} ({100 * (rB - 1):+.1f} %), "
          f"{fb['cle']} → {fb['cle_jouee']} a son tempo joue {fb['joue']}, {fb['tag']}")
    A = reechantillonner(a, rA); B = reechantillonner(b, rB)
    tA, fiableA, reliefA = temps_des_temps(a_nom, fa, rA)
    tB, fiableB, reliefB = temps_des_temps(b_nom, fb, rB)
    print(f"grille A : {'verite' if fiableA else 'PERIODE SEULE'} (relief {reliefA:.2f}), {len(tA)} temps ; "
          f"grille B : {'verite' if fiableB else 'PERIODE SEULE'} (relief {reliefB:.2f}), {len(tB)} temps")
    if un_a + 4 * MESURES > len(tA):
        print(f"A n'a que {len(tA)} temps pour {4 * MESURES} demandes : le passage sera prolonge a la periode moyenne")
    passage = Passage(tA, un_a)
    n = int(passage.t(MESURES + 1) * taux)
    n = min(n, len(A) + int((passage.t(17) - tB[un_b]) * taux) + len(B))
    fA, fB, lowA, lowB = passage.gains(n, taux)

    # B lache de son « 1 » sur le 1 de la mesure 17 de A.
    decalage = int(round((passage.t(17) - tB[un_b]) * taux))
    A_m = placer(A, 0, n); B_m = placer(B, decalage, n)
    nudge, c0, c1 = caler_a_la_main(A_m, B_m, taux, 60.0 / fa["joue"], passage.t(25), passage.t(41))
    if abs(nudge) > int(0.008 * taux):
        decalage += nudge
        print(f"calage a la main : B decale de {1000 * nudge / taux:+.0f} ms pour tomber sur les kicks de A "
              f"(accord des attaques graves {c0:.3f} → {c1:.3f})")
    else:
        print(f"calage : les grilles s'accordent deja (attaques graves {c0:.3f}, meilleur decalage {1000 * nudge / taux:+.0f} ms)")
    # L'OREILLE A LE DERNIER MOT : l'accord des attaques entre deux disques differents reste
    # faible (0,05 a 0,10), la mesure ne tranche pas un demi-temps a coup sur. `--nudge-ms`
    # applique ce que le DJ entend, par-dessus.
    if nudge_ms:
        decalage += int(round(nudge_ms / 1000.0 * taux))
        print(f"nudge de l'oreille : B decale de {nudge_ms:+.0f} ms en plus")
    B_m = placer(B, decalage, n)
    lA, hA = couper_bas(A_m, taux); lB, hB = couper_bas(B_m, taux)
    canalA = fA * (lowA * lA + hA)
    canalB_plein = lowB * lB + hB          # ce que le casque entend de B : apres EQ, fader a fond
    mix = canalA + fB * canalB_plein
    crete = np.abs(mix).max()
    if crete > 0.95:
        mix /= crete / 0.95
    base = os.path.join(dossier, f"{a_nom}--{b_nom}")
    ecrire(base + ".wav", mix, taux)
    ecrire(base + "-A.wav", A, taux)
    ecrire(base + "-B.wav", B[max(0, int(tB[un_b] * taux)):], taux)
    ecrire(base + "-cueB.wav", canalB_plein, taux)
    t0, t1 = passage.accueil_retrait()
    print(f"melange ecrit : {base}.wav ({n / taux:.0f} s, {MESURES} mesures de A a {fa['joue']} BPM ; "
          f"B lache a {passage.t(17):.1f} s, swap {passage.t(41):.1f}-{passage.t(49):.1f} s, A parti a {passage.t(65):.1f} s)")
    return dict(base=base, taux=taux, n=n, passage=passage, fA=fA, fB=fB, lowA=lowA, lowB=lowB,
                rA=rA, rB=rB, decalage=decalage, t0=t0, t1=t1, fiche_a=fa, fiche_b=fb)


# ------------------------------------------------------------------ l'ancien fondu lineaire
AVANT, FONDU, APRES = 40.0, 20.0, 40.0


def fabriquer_lineaire(a_nom, b_nom, dossier):
    a, taux = lire(os.path.join(CACHE, a_nom + ".wav")); b, _ = lire(os.path.join(CACHE, b_nom + ".wav"))
    n = int((AVANT + FONDU + APRES) * taux)
    t = np.arange(n) / taux
    fB = np.clip((t - AVANT) / FONDU, 0, 1); fA = 1 - fB
    A_m = placer(a, 0, n); B_m = placer(b, int(AVANT * taux), n)
    mix = fA * A_m + fB * B_m
    base = os.path.join(dossier, f"{a_nom}--{b_nom}")
    ecrire(base + ".wav", mix, taux); ecrire(base + "-A.wav", a, taux); ecrire(base + "-B.wav", b, taux)
    ecrire(base + "-cueB.wav", B_m, taux)

    class Lineaire:
        def t(self, m): return {1: 0.0, 17: AVANT, 25: AVANT, 41: AVANT, 49: AVANT + FONDU, 57: AVANT + FONDU, 65: AVANT + FONDU, MESURES + 1: AVANT + FONDU + APRES}[m]
        def tranches(self): return [("A seul", 1, 17), ("fondu", 17, 65), ("B seul", 65, MESURES + 1)]
    print(f"melange lineaire ecrit : {base}.wav")
    return dict(base=base, taux=taux, n=n, passage=Lineaire(), fA=fA, fB=fB, lowA=np.ones(n), lowB=np.ones(n),
                rA=1.0, rB=1.0, decalage=int(AVANT * taux), t0=AVANT, t1=AVANT + FONDU, fiche_a=None, fiche_b=None)


# ------------------------------------------------------------------ la sonde
def analyser(p, casque):
    base = p["base"]
    cmd = ["dotnet", "run", "-c", "Release", "--no-build", "--project", os.path.join(RACINE, "tools", "Emotion.Probe"), "--",
           base + ".wav", "0", str(int(p["n"] / p["taux"]) + 1),
           f"relaisA={base}-A.wav", f"relaisB={base}-B.wav", f"fondu={p['t0']:.2f},{p['t1']:.2f}", f"cue={casque:.0f}",
           f"blend={base}-cueB.wav", f"fader={base}.fader.tsv", f"sources={base}.tsv", f"paquets={base}.pak"]
    r = subprocess.run(cmd, capture_output=True, text=True)
    for l in r.stdout.splitlines():
        if l.startswith("cue ") or l.startswith("master") or "accueil de B" in l or "retrait de A" in l:
            print(l)
    if r.returncode != 0:
        print(r.stderr[-1200:]); raise SystemExit(1)
    print(f"paquets enregistres : {base}.pak  (a rejouer : ./outils/fondu.sh {os.path.basename(base).replace('--', ' ')})")


# ------------------------------------------------------------------ le juge
def enveloppe(x, taux, pas_s):
    pas = max(1, int(pas_s * taux)); m = len(x) // pas * pas
    return np.abs(x[:m]).reshape(-1, pas).mean(axis=1)


def correlation(a, b):
    m = min(len(a), len(b)); a = a[:m] - a[:m].mean(); b = b[:m] - b[:m].mean()
    d = np.linalg.norm(a) * np.linalg.norm(b)
    return float(a @ b / d) if d > EPS else 0.0


def stems_places(nom, ratio, decalage, n, taux, low, fader, debut=0):
    """Les stems Demucs du disque, passes par le meme pitch, la meme EQ et le meme fader."""
    d = {}
    for s in NOMS:
        chemin = os.path.join(STEMS, f"{nom}-ref-{s}.wav")
        if not os.path.exists(chemin): continue
        y, tx = lire(chemin)
        if tx != taux: continue
        y = reechantillonner(y, ratio)[debut:]
        y = placer(y, decalage, n)
        l, h = couper_bas(y, taux)
        d[s] = fader * (low * l + h)
    return d


def juger(p, dire=print):
    base, taux, n = p["base"], p["taux"], p["n"]
    a_nom, b_nom = os.path.basename(base).split("--")
    lignes = [l.rstrip("\n").split("\t") for l in open(base + ".tsv") if l.strip()]
    temps = np.array([float(l[0]) for l in lignes])
    ncases = max((len(l) - 2) // 5 for l in lignes)
    niveau = np.zeros((len(lignes), ncases)); disque = np.full((len(lignes), ncases), -1)
    for i, l in enumerate(lignes):
        for r in range((len(l) - 2) // 5):
            niveau[i, r] = float(l[2 + 5 * r]); disque[i, r] = int(l[2 + 5 * r + 4])
    pas_s = float(np.median(np.diff(temps))) if len(temps) > 1 else 0.0213
    idx = lambda e: e[np.clip((temps / pas_s).astype(int), 0, len(e) - 1)]  # noqa: E731

    sa = stems_places(a_nom, p["rA"], 0, n, taux, p["lowA"], p["fA"])
    debut_b = max(0, -p["decalage"]) if p["decalage"] < 0 else 0
    sb = stems_places(b_nom, p["rB"], max(0, p["decalage"]), n, taux, p["lowB"], p["fB"], debut=debut_b)
    if not sa or not sb:
        dire("stems Demucs absents pour un des deux morceaux — lancer outils/reference.py d'abord"); return
    env = {("A", k): idx(enveloppe(v, taux, pas_s)) for k, v in sa.items()}
    env.update({("B", k): idx(enveloppe(v, taux, pas_s)) for k, v in sb.items()})
    fA = idx(enveloppe(p["fA"], taux, pas_s)); fB = idx(enveloppe(p["fB"], taux, pas_s))
    lowA = idx(enveloppe(p["lowA"], taux, pas_s))

    passage = p["passage"]
    ok = juges = 0; fuites = []; basse_a = None
    for nom_tranche, m0, m1 in passage.tranches():
        dans = (temps >= passage.t(m0)) & (temps < passage.t(m1))
        deux = m0 >= 17 and m1 <= 65
        dire(""); dire(f"--- {nom_tranche} (mesures {m0}-{m1 - 1}, {passage.t(m0):.0f}-{passage.t(m1):.0f} s)")
        dire("case  platine   | stem le plus proche   corr | meilleur de A   meilleur de B | son disque ?")
        for r in range(ncases):
            for tag in sorted(set(disque[dans, r])):
                if tag <= 0: continue
                sel = dans & (disque[:, r] == tag)
                if sel.sum() < 100: continue
                y = niveau[sel, r]
                if y.std() < 1e-6: continue
                par_disque = {}
                for (d, s), e in env.items():
                    ref = e[sel]
                    if ref.std() < EPS: continue
                    c = correlation(y, ref)
                    if d not in par_disque or c > par_disque[d][1]: par_disque[d] = (s, c)
                if not par_disque: continue
                meilleur = max(par_disque, key=lambda d: par_disque[d][1])
                attendu = {1: "A", 2: "B"}.get(tag)
                verdict = "-"
                if attendu and deux and len(par_disque) == 2:
                    bon = meilleur == attendu; juges += 1; ok += bon; verdict = "oui" if bon else "NON"
                    autre = "B" if attendu == "A" else "A"
                    fuites.append(par_disque[autre][1] / max(EPS, par_disque[attendu][1]))
                if tag == 1 and nom_tranche.startswith("les deux, B sans") and par_disque.get("A", ("", 0))[0] == "bass":
                    basse_a = r
                a_txt = f"{par_disque['A'][0]:6s} {par_disque['A'][1]:5.2f}" if "A" in par_disque else "   -        "
                b_txt = f"{par_disque['B'][0]:6s} {par_disque['B'][1]:5.2f}" if "B" in par_disque else "   -        "
                s_m, c_m = par_disque[meilleur]
                dire(f"  {r + 1}    {('P1 = A', 'P2 = B', 'reste')[tag - 1]:8s} | {meilleur} {s_m:8s}      {c_m:5.2f} | {a_txt}   {b_txt} | {verdict}")

    dire("")
    chev = (temps >= passage.t(17)) & (temps < passage.t(65))
    for tag, nom, f in ((1, "A", fA), (2, "B", fB)):
        somme = np.array([niveau[i, disque[i] == tag].sum() for i in range(len(lignes))])
        dire(f"  pendant le chevauchement, la somme des cases de {nom} suit le fader de {nom} : corr {correlation(somme[chev], f[chev]):5.2f}")
    if fuites:
        dire(f"  fuite mediane (corr avec l'autre disque / corr avec le sien) : {np.median(fuites):.2f}")
    if juges:
        dire(f"  les deux disques ensemble : {ok}/{juges} cases dont le stem le plus proche vient du disque annonce")

    # LE BASS SWAP, case par case : la case « basse » de A doit descendre avec le bouton LOW
    # de A, pendant que ses autres cases restent.
    swap = (temps >= passage.t(41)) & (temps < passage.t(49))
    avant = (temps >= passage.t(33)) & (temps < passage.t(41)); apres = (temps >= passage.t(49)) & (temps < passage.t(57))
    if basse_a is not None and swap.sum() > 100:
        c = correlation(niveau[swap, basse_a], lowA[swap])
        chute = niveau[apres, basse_a].mean() / max(EPS, niveau[avant, basse_a].mean())
        dire(f"  bass swap : la case {basse_a + 1} (basse de A) suit le bouton LOW de A a {c:5.2f} ; niveau apres / avant = {chute:.2f}")
        for r in range(ncases):
            if r == basse_a or (disque[swap, r] == 1).sum() < 100: continue
            ratio = niveau[apres, r].mean() / max(EPS, niveau[avant, r].mean())
            dire(f"             la case {r + 1} (A, pas la basse) : niveau apres / avant = {ratio:.2f}")
    else:
        dire("  bass swap : aucune case de A n'a ete identifiee comme sa basse avant le swap")

    # LE FADER DEVINE : BlendEstimator ne doit ni retirer A trop tot, ni ignorer B.
    try:
        fl = [l.split("\t") for l in open(base + ".fader.tsv") if l.strip()]
        tf = np.array([float(l[0]) for l in fl]); dev = np.array([float(l[1]) for l in fl])
        def premier(seuil):
            i = np.argmax(dev >= seuil); return tf[i] if dev[i] >= seuil else None
        t15, t90 = premier(0.15), premier(0.90)
        dire("")
        dire(f"  fader devine : accueil (0,15) a {t15 if t15 is None else f'{t15:.1f} s'} pour un fader B a 15 % a {p['t0']:.1f} s ; "
             f"retrait (0,90) a {t90 if t90 is None else f'{t90:.1f} s'} pour un fader A a 10 % a {p['t1']:.1f} s")
        pendant = (tf >= passage.t(41)) & (tf < passage.t(57))
        if pendant.any():
            dire(f"  fader devine pendant le swap et apres (A joue encore a fond) : max {dev[pendant].max():.2f} — "
                 f"{'OK, A n est pas retire' if dev[pendant].max() < 0.9 else 'A SERAIT RETIRE TROP TOT'}")
    except FileNotFoundError:
        pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("a"); ap.add_argument("b")
    ap.add_argument("--un-a", type=int, default=0, help="indice du temps de A qui est un « 1 »")
    ap.add_argument("--un-b", type=int, default=0, help="indice du temps de B qui est un « 1 »")
    ap.add_argument("--casque", type=float, default=CASQUE_S, help="secondes de cue avant le passage")
    ap.add_argument("--nudge-ms", type=float, default=0.0, help="decaler B a l'oreille, en ms (+ = B plus tard)")
    ap.add_argument("--lineaire", action="store_true", help="l'ancien fondu lineaire de 20 s")
    ap.add_argument("--juge-seul", action="store_true", help="ne refabrique ni n'analyse : juge le tsv existant")
    o = ap.parse_args()
    dossier = os.path.join(CACHE, "relais"); os.makedirs(dossier, exist_ok=True)
    p = fabriquer_lineaire(o.a, o.b, dossier) if o.lineaire else fabriquer(o.a, o.b, o.un_a, o.un_b, dossier, o.nudge_ms)
    if not o.juge_seul:
        analyser(p, o.casque)
    juger(p)
    return 0


if __name__ == "__main__":
    sys.exit(main())
