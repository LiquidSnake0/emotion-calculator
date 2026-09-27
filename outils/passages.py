#!/usr/bin/env python3
# Chaque passage d'un set, juge sur le multipiste : les deux decks etaient-ils cales ?
#
#   python3 outils/passages.py set-2                 (session dans ~/Documents/emotion-sources/studio)
#   python3 outils/passages.py set-2 --deck2 2,3 --deck3 4,5
#
# LE DJ VEUT SAVOIR OU IL S'EST FOIRE. On a les deux voies apres fader, separement : pendant
# un passage, chaque deck est seul sur sa piste. On mesure donc, sur le recouvrement (les
# deux pistes vivantes), le tempo de chacun, l'ecart de temps entre leurs frappes au debut
# et a la fin du recouvrement (un ecart qui bouge = pas cale), et le saut de niveau quand
# le disque entrant prend la place. Aucun seuil savant : des millisecondes et des dB, a lire
# avec l'oreille.
#
# Les passages sont pris dans le journal du crate (cue → take) quand il existe ; sinon on
# les detecte : chaque plage ou les deux decks sonnent en meme temps.
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

def tempo(flux):
    """Autocorrelation de l'enveloppe entre 60 et 120 BPM ; rend le BPM et sa force."""
    f = flux - flux.mean()
    if len(f) < 400 or f.std() == 0: return None, 0.0
    ac = np.correlate(f, f, mode="full")[len(f) - 1:]
    ac /= ac[0] + 1e-9
    lo, hi = int(60 / 120 / PAS), int(60 / 60 / PAS)          # 0,5 s a 1 s
    k = lo + int(np.argmax(ac[lo:hi]))
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

def db(x): return 20 * math.log10(max(float(np.sqrt(np.mean(x * x))), 1e-9))

def main():
    if len(sys.argv) < 2: print(__doc__); return 2
    session = sys.argv[1]; d = STUDIO / session
    args = dict(a.split("=") for a in sys.argv[2:] if "=" in a)
    ch2 = tuple(int(v) for v in args.get("--deck2", "2,3").split(","))
    ch3 = tuple(int(v) for v in args.get("--deck3", "4,5").split(","))
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
    print(f"{len(plages)} recouvrements (les deux decks ensemble ≥ 8 s)\n")
    print(" n   de      a      durée   tempo CH2   tempo CH3   écart début → fin (ms)   niveau CH2→CH3 (dB)")
    for k, (i, j) in enumerate(plages, 1):
        ta, ca = tempo(fa[i:j]); tb, cb = tempo(fb[i:j])
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
