#!/usr/bin/env python3
# Chaque passage d'un set, juge sur le multipiste : les deux decks etaient-ils cales ?
#
#   python3 outils/passages.py set-2                 (session dans ~/Documents/emotion-sources/studio)
#   python3 outils/passages.py set-2 --deck2=2,3 --deck3=4,5 --ancrage=90
#
# LE DJ VEUT SAVOIR OU IL S'EST FOIRE. On a les deux voies apres fader, separement : pendant
# un passage, chaque deck est seul sur sa piste. On mesure donc, sur le recouvrement (les
# deux pistes vivantes), le tempo de chacun, l'ecart de temps entre leurs frappes au debut
# et a la fin du recouvrement (un ecart qui bouge = pas cale), et le saut de niveau quand
# le disque entrant prend la place. Aucun seuil savant : des millisecondes et des dB, a lire
# avec l'oreille.
#
# Les passages sont detectes : chaque plage ou les deux decks sonnent en meme temps.
#
# LE TEMPO SE REPLIE AUTOUR DE L'ANCRAGE. Cherche seul entre 60 et 120, il rendait 60,6 ou
# 120 sur un deck a 89,6 des que le kick n'etait pas net (27 septembre 2026, la moitie des
# lignes) : des harmoniques, pas des tempos. On pese donc chaque periode par une preference
# gaussienne en logarithme autour de l'ancrage — un quart d'octave, comme le moteur —, et
# l'ancrage vient du journal du crate (la fiche mediane) ou de --ancrage=, sinon 90. Connu, il
# vaut la course du fader (0,12 octave, ±8,7 %) ; par defaut, un quart d'octave.
import json, math, os, subprocess, sys
from pathlib import Path
import numpy as np

STUDIO = Path(os.environ.get("EMOTION_STUDIO_DIR", Path.home() / "Documents/emotion-sources/studio"))
SR = 11025          # assez pour les frappes, dix fois moins de memoire que 48 kHz
PAS = 0.01          # une enveloppe toutes les 10 ms

def extraire(wav, canaux):
    """Une piste mono, moyenne des deux canaux demandes, en float32 a SR."""
    g, d = canaux
    cmd = ["ffmpeg", "-v", "error", "-i", str(wav), "-filter_complex", f"pan=mono|c0=0.5*c{g}+0.5*c{d}",
           "-ar", str(SR), "-f", "f32le", "-"]
    raw = subprocess.run(cmd, capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32)

def enveloppe(x):
    """Flux d'energie du grave (30-250 Hz), redresse : la ou le kick frappe."""
    n = int(SR * PAS); nb = len(x) // n
    fen = 4 * n
    e = np.zeros(nb, dtype=np.float32)
    h = np.hanning(fen).astype(np.float32)
    # passe-bande grossier par FFT sur chaque trame de 40 ms, pas de 10 ms
    freqs = np.fft.rfftfreq(fen, 1 / SR); bande = (freqs >= 30) & (freqs <= 250)
    for i in range(nb):
        s = i * n
        tr = x[s:s + fen]
        if len(tr) < fen: break
        sp = np.abs(np.fft.rfft(tr * h))
        e[i] = float(np.sum(sp[bande]))
    flux = np.diff(e, prepend=e[0]); flux[flux < 0] = 0
    return flux, e

def tempo(flux, ancrage=90.0, largeur=0.25):
    """Autocorrelation de l'enveloppe entre 40 et 180 BPM, pesee autour de l'ancrage (gaussienne en
    octaves, de largeur donnee) ; rend le BPM et sa force brute a cette periode."""
    f = flux - flux.mean()
    if len(f) < 400 or f.std() == 0: return None, 0.0
    ac = np.correlate(f, f, mode="full")[len(f) - 1:]
    ac /= ac[0] + 1e-9
    lo, hi = int(60 / 180 / PAS), int(60 / 40 / PAS)          # 0,33 s a 1,5 s
    if hi >= len(ac): hi = len(ac) - 1
    lags = np.arange(lo, hi)
    bpm = 60 / (lags * PAS)
    poids = np.exp(-0.5 * (np.log2(bpm / ancrage) / largeur) ** 2)
    k = lo + int(np.argmax(ac[lo:hi] * poids))
    return 60 / (k * PAS), float(ac[k])

def ecart(fa, fb, periode):
    """Decalage de fb par rapport a fa, en ms, dans ± une demi-periode."""
    a = fa - fa.mean(); b = fb - fb.mean()
    if a.std() == 0 or b.std() == 0: return None
    demi = int(periode / 2 / PAS)
    meilleur, arg = -1e9, 0
    for d in range(-demi, demi + 1):
        if d >= 0: c = float(np.dot(a[d:], b[:len(b) - d]))
        else: c = float(np.dot(a[:len(a) + d], b[-d:]))
        if c > meilleur: meilleur, arg = c, d
    return arg * PAS * 1000

def ancrage_du_crate(d):
    """La fiche mediane du journal du crate (le BPM d'ancrage envoye a chaque cue), s'il y en a un."""
    bpms = []
    for j in d.glob("*-deck.jsonl"):
        for l in j.read_text().splitlines():
            try: b = (json.loads(l).get("corps") or {}).get("Bpm")
            except ValueError: continue
            if b: bpms.append(float(b))
    return sorted(bpms)[len(bpms) // 2] if bpms else None

def db(x): return 20 * math.log10(max(float(np.sqrt(np.mean(x * x))), 1e-9))

def main():
    if len(sys.argv) < 2: print(__doc__); return 2
    session = sys.argv[1]; d = STUDIO / session
    args = dict(a.split("=") for a in sys.argv[2:] if "=" in a)
    ch2 = tuple(int(v) for v in args.get("--deck2", "2,3").split(","))
    ch3 = tuple(int(v) for v in args.get("--deck3", "4,5").split(","))
    # Un ancrage connu (la fiche du crate, ou donne) vaut la course du fader : 0,12 octave, comme le
    # moteur amorce ; sans rien, un quart d'octave autour de 90, le milieu du bac.
    connu = float(args["--ancrage"]) if "--ancrage" in args else ancrage_du_crate(d)
    ancrage, largeur = (connu, 0.12) if connu else (90.0, 0.25)
    wavs = sorted(d.glob("*-table.wav"))          # une session mise de cote garde son nom d'origine
    if not wavs: sys.exit(f"pas de multipiste dans {d}")
    wav = wavs[0]
    print(f"{session} : extraction des decks…", flush=True)
    a = extraire(wav, ch2); b = extraire(wav, ch3)
    print("enveloppes…", flush=True)
    fa, ea = enveloppe(a); fb, eb = enveloppe(b)
    nb = min(len(fa), len(fb))
    # les plages ou les deux sonnent : energie au-dessus d'un centieme de leur crete, lissee sur 2 s
    def vivant(e):
        s = np.convolve(e[:nb], np.ones(200) / 200, mode="same")
        return s > s.max() * 0.01
    deux = vivant(ea) & vivant(eb)
    # plages continues d'au moins 8 s
    plages, i = [], 0
    while i < nb:
        if deux[i]:
            j = i
            while j < nb and deux[j]: j += 1
            if (j - i) * PAS >= 8: plages.append((i, j))
            i = j
        else: i += 1
    # l'heure du lancement pour dater
    debut = None
    try:
        import datetime
        t0 = os.path.getmtime(sorted(d.glob("*-parecord.log"))[0])
        debut = datetime.datetime.fromtimestamp(t0)
    except OSError: pass
    print(f"{len(plages)} recouvrements (les deux decks ensemble ≥ 8 s), tempo replié autour de {ancrage:.0f} BPM\n")
    print(" n   de      a      durée   tempo CH2   tempo CH3   écart début → fin (ms)   niveau CH2→CH3 (dB)")
    for k, (i, j) in enumerate(plages, 1):
        ta, ca = tempo(fa[i:j], ancrage, largeur); tb, cb = tempo(fb[i:j], ancrage, largeur)
        per = 60 / ((ta or tb or 90))
        tiers = max(1, (j - i) // 3)
        e1 = ecart(fa[i:i + tiers], fb[i:i + tiers], per)
        e2 = ecart(fa[j - tiers:j], fb[j - tiers:j], per)
        # niveau juste avant (CH2 seul, 5 s) et juste apres (CH3 seul, 5 s)
        avant = a[max(0, (i - 500)) * int(SR * PAS): i * int(SR * PAS)]
        apres = b[j * int(SR * PAS): (j + 500) * int(SR * PAS)]
        saut = (db(apres) - db(avant)) if len(avant) and len(apres) else float("nan")
        hh = lambda s: (debut + __import__("datetime").timedelta(seconds=s)).strftime("%H:%M:%S") if debut else f"{s:7.0f}s"
        fmt = lambda v: f"{v:6.1f}" if v is not None else "   -  "
        print(f"{k:2d}  {hh(i*PAS)}  {hh(j*PAS)}  {(j-i)*PAS:5.0f} s   {fmt(ta)} ({ca:.2f})  {fmt(tb)} ({cb:.2f})   "
              f"{(f'{e1:+6.0f} → {e2:+6.0f}' if e1 is not None and e2 is not None else '   -   ')}   {saut:+5.1f}")
    print("\nlire : un écart qui bouge de plus d'une trentaine de ms entre le début et la fin, c'est deux decks qui ne tiennent pas ensemble ;"
          " un écart stable mais loin de zéro, un calage à côté du temps ; un tempo avec une force < 0,3 n'est pas fiable (pas de kick net).")
    return 0

if __name__ == "__main__":
    sys.exit(main())
