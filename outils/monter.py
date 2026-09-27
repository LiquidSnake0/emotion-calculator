#!/usr/bin/env python3
# Monte un set a partir de plusieurs prises, raccordees au sample pres sur un morceau
# present dans les deux.
#
#   python3 outils/monter.py <plan.json> <sortie.wav>
#
# LE PLAN dit, dans l'ordre, les prises et, entre deux prises, la fenetre (en secondes de
# chaque prise) ou le meme morceau joue dans les deux : dans la prise A la fin de son
# entree (le disque qui sort finit de s'eteindre), dans la prise B son debut, joue seul.
# L'outil cherche dans ces deux fenetres l'instant ou l'audio est le meme — correlation du
# signal des deux masters, a 8 kHz — et fond A dans B a cet instant-la, sur quelques
# secondes. Si les deux prises sont au meme pitch, le fondu est inaudible ; sinon la
# correlation le dit (score bas) et on le voit avant d'ecouter.
#
#   {"prises": ["avant", "avant-2", ...],
#    "raccords": [{"a": [225, 410], "b": [10, 330], "morceau": "mori"}, ...],
#    "fondu": 4.0,
#    "master": "-v2.wav"}
#
# "master" (facultatif, "-master.wav" par defaut) dit quel fichier de chaque prise on monte : le
# Rec Out tel quel, ou le master reconstruit par reconstruire.py ("-v2.wav"), qui commence a
# l'instant « origine » de son .json — les fenetres du plan restent en secondes de la prise, on
# les decale pour lui.
import json, os, subprocess, sys
from pathlib import Path
import numpy as np

STUDIO = Path(os.environ.get("EMOTION_STUDIO_DIR", Path.home() / "Documents/emotion-sources/studio"))
SR = 8000

SUFFIXE = "-master.wav"

def master(prise):
    m = STUDIO / prise / f"{prise}{SUFFIXE}"
    if not m.exists(): sys.exit(f"pas de {m} : ./outils/master.sh {prise}" if SUFFIXE == "-master.wav" else f"pas de {m} : reconstruire.py {prise}")
    return m

def origine(prise):
    """Ou commence le fichier monte, en secondes de la prise : 0 pour le Rec Out, l'origine du .json pour un master reconstruit."""
    j = STUDIO / prise / f"{prise}{SUFFIXE.replace('.wav', '.json')}"
    if SUFFIXE == "-master.wav" or not j.exists(): return 0.0
    return float(json.loads(j.read_text()).get("origine", 0.0))

def dans_le_fichier(prise, t):
    """Une seconde de la prise, ramenee dans le fichier monte : moins l'origine, moins les coupes que
    reconstruire.py a faites avant elle (un passage saute avance tout ce qui suit)."""
    j = STUDIO / prise / f"{prise}{SUFFIXE.replace('.wav', '.json')}"
    if SUFFIXE == "-master.wav" or not j.exists(): return t
    info = json.loads(j.read_text())
    t2 = t - float(info.get("origine", 0.0))
    for (c0, c1) in info.get("coupes", []):
        if t >= c1: t2 -= (c1 - c0)
        elif t > c0: t2 -= (t - c0)
    return max(0.0, t2)

def mono(wav, debut, duree):
    raw = subprocess.run(["ffmpeg", "-v", "error", "-ss", f"{debut:.3f}", "-t", f"{duree:.3f}", "-i", str(wav),
                          "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"], capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32)

ENV = 100   # enveloppes a 100 Hz

def enveloppe(x):
    """Energie du grave par tranche de 10 ms, racine, sans sa moyenne glissante d'une seconde."""
    n = SR // ENV; nb = len(x) // n
    # passe-bas grossier : moyenne glissante de 2 ms, puis energie par tranche
    k = SR // 500
    y = np.convolve(x, np.ones(k) / k, mode="same")
    e = np.sqrt(np.add.reduceat(y[:nb * n] ** 2, np.arange(0, nb * n, n)) / n)
    tendance = np.convolve(e, np.ones(ENV) / ENV, mode="same")
    return e - tendance

def aligner(wa, fa, wb, fb, ratios=np.arange(0.96, 1.0401, 0.002)):
    """Le meilleur (tA, tB, ratio, score) entre les fenetres de A et de B : correlation des
    enveloppes de frappes, pour chaque rapport de vitesse essaye (B = A joue `ratio` fois
    plus vite). La plus courte des deux glisse sur la plus longue ; le point choisi est au
    milieu de ce qu'elles ont en commun."""
    a0, a1 = fa; b0, b1 = fb
    A = enveloppe(mono(wa, a0, a1 - a0)); B = enveloppe(mono(wb, b0, b1 - b0))
    meilleur = (None, None, 1.0, -1.0)
    for r in ratios:
        nA = int(len(A) / r)
        Ar = np.interp(np.arange(nA) * r, np.arange(len(A)), A)     # A vue a la vitesse de B
        court, long_, a_est_court = (Ar, B, True) if nA <= len(B) else (B, Ar, False)
        if len(court) < ENV * 4: continue
        n = 1
        while n < len(long_) + len(court): n *= 2
        c = np.fft.irfft(np.fft.rfft(long_, n) * np.conj(np.fft.rfft(court, n)), n)[:len(long_) - len(court) + 1]
        cs = np.concatenate(([0.0], np.cumsum(long_.astype(np.float64) ** 2)))
        eL = np.sqrt(np.maximum(cs[len(court):] - cs[:len(long_) - len(court) + 1], 0.0)) + 1e-9
        score = c / (eL * np.sqrt(np.sum(court ** 2)) + 1e-9)
        k = int(np.argmax(score))
        if score[k] > meilleur[3]:
            milieu = len(court) // 2
            if a_est_court:
                tA = a0 + milieu * r / ENV; tB = b0 + (k + milieu) / ENV
            else:
                tA = a0 + (k + milieu) * r / ENV; tB = b0 + milieu / ENV
            meilleur = (tA, tB, float(r), float(score[k]))
    return meilleur

def affiner(wa, tA, wb, tB, r, marge=0.5, duree=1.5):
    """Autour du point trouve, la forme d'onde a 8 kHz : le sample exact, et la correlation
    normalisee de l'onde a cet endroit (1 = le meme audio). A est d'abord mise a la vitesse
    de B : a -0,4 % de pitch, deux secondes d'onde se decorrelent sinon."""
    A = mono(wa, tA - duree / 2, duree); B = mono(wb, tB - duree / 2 - marge, duree + 2 * marge)
    nA = int(len(A) / r)
    A = np.interp(np.arange(nA) * r, np.arange(len(A)), A)
    A = A - A.mean(); B = B - B.mean()
    if A.std() < 1e-5 or B.std() < 1e-5 or len(B) <= len(A): return tB, 0.0
    c = np.correlate(B, A, mode="valid")
    cs = np.concatenate(([0.0], np.cumsum(B.astype(np.float64) ** 2)))
    e = np.sqrt(np.maximum(cs[len(A):] - cs[:len(B) - len(A) + 1], 0.0)) + 1e-9
    q = c / (e * np.sqrt(np.sum(A * A)) + 1e-9)
    k = int(np.argmax(q))
    return tB - marge + k / SR, float(q[k])

def aligner_onde(wa, fa, wb, fb, ratios=(0.994, 0.996, 0.998, 1.0, 1.002, 1.004, 1.006), extrait=3.0, pas=6.0):
    """De secours, quand les frappes se repetent trop pour trancher : des extraits d'onde de A,
    pris tous les `pas` s, cherches dans toute la fenetre de B, a plusieurs vitesses. Rend
    (tA, tB, ratio, score d'onde)."""
    a0, a1 = fa; b0, b1 = fb
    B0 = mono(wb, b0, b1 - b0); B0 = B0 - B0.mean()
    meilleur = (None, None, 1.0, -1.0)
    t = a0
    while t + extrait <= a1:
        A0 = mono(wa, t, extrait); A0 = A0 - A0.mean()
        if A0.std() < 1e-4: t += pas; continue
        for r in ratios:
            nA = int(len(A0) / r)
            A = np.interp(np.arange(nA) * r, np.arange(len(A0)), A0)
            if nA >= len(B0): continue
            n = 1
            while n < len(B0) + nA: n *= 2
            c = np.fft.irfft(np.fft.rfft(B0, n) * np.conj(np.fft.rfft(A, n)), n)[:len(B0) - nA + 1]
            cs = np.concatenate(([0.0], np.cumsum(B0.astype(np.float64) ** 2)))
            e = np.sqrt(np.maximum(cs[nA:] - cs[:len(B0) - nA + 1], 0.0)) + 1e-9
            q = c / (e * np.sqrt(np.sum(A * A)) + 1e-9)
            k = int(np.argmax(q))
            if q[k] > meilleur[3]:
                meilleur = (t + extrait / 2, b0 + (k + nA / 2) / SR, float(r), float(q[k]))
        t += pas
    return meilleur

def main():
    if len(sys.argv) < 3: print(__doc__); return 2
    plan = json.loads(Path(sys.argv[1]).read_text()); sortie = sys.argv[2]
    prises = plan["prises"]; raccords = plan["raccords"]; fondu = float(plan.get("fondu", 4.0))
    global SUFFIXE; SUFFIXE = plan.get("master", "-master.wav")
    assert len(raccords) == len(prises) - 1
    coupes = []   # (tA fin de la prise i, tB debut de la prise i+1)
    for i, r in enumerate(raccords):
        wa, wb = master(prises[i]), master(prises[i + 1])
        fa = (dans_le_fichier(prises[i], r["a"][0]), dans_le_fichier(prises[i], r["a"][1]))
        fb = (dans_le_fichier(prises[i + 1], r["b"][0]), dans_le_fichier(prises[i + 1], r["b"][1]))
        tA, tB, ratio, score = aligner(wa, fa, wb, fb)
        if tA is None: sys.exit(f"raccord {r.get('morceau')} : fenetres trop courtes")
        tB, q = affiner(wa, tA, wb, tB, ratio)
        if q < 0.5:
            # les frappes n'ont pas tranche : l'onde elle-meme, partout dans les deux fenetres
            tA2, tB2, r2, q2 = aligner_onde(wa, fa, wb, fb)
            if tA2 is not None and q2 > q:
                tA, tB, ratio, q = tA2, tB2, r2, q2
                tB, q = affiner(wa, tA, wb, tB, ratio)
        print(f"{prises[i]:8s} → {prises[i+1]:8s} sur {r.get('morceau','?'):12s} : A {tA:8.2f} s ≡ B {tB:8.2f} s   "
              f"vitesse B/A {(ratio-1)*100:+.1f} %   frappes {score:.2f}   onde {q:.2f}"
              + ("" if q >= 0.5 else "   ← FAIBLE, à écouter"), flush=True)
        coupes.append((tA, tB, ratio))
    # Le fondu est centre sur l'instant aligne : A va jusqu'a tA + fondu/2, B part de tB - fondu/2.
    # CHAQUE PRISE EST RAMENEE A LA VITESSE DE LA PREMIERE, comme le ferait le fader de pitch :
    # un reechantillonnage, pas un etirement — le son garde sa nature, la hauteur suit le
    # tempo, exactement ce que la platine fait. Les rapports se cumulent de prise en prise.
    entrees = []; filtres = []; noms = []; cumul = 1.0
    for i, p in enumerate(prises):
        entrees += ["-i", str(master(p))]
        if i > 0: cumul *= coupes[i - 1][2]
        # une seconde de sortie vaut `cumul` secondes de cette prise ralentie/acceleree
        debut = coupes[i - 1][1] - fondu / 2 / cumul if i > 0 else 0.0
        fin = coupes[i][0] + fondu / 2 / cumul if i < len(coupes) else None
        tr = f"[{i}:a]atrim=start={debut:.4f}" + (f":end={fin:.4f}" if fin is not None else "") + ",asetpts=PTS-STARTPTS"
        if abs(cumul - 1.0) > 0.0015:
            tr += f",asetrate={48000 / cumul:.3f},aresample=48000"
        tr += f"[s{i}]"
        filtres.append(tr); noms.append(f"[s{i}]")
    courant = noms[0]
    for i in range(1, len(prises)):
        out = f"[m{i}]"
        filtres.append(f"{courant}{noms[i]}acrossfade=d={fondu}:c1=tri:c2=tri{out}")
        courant = out
    # LA CRETE FINALE : les masters reconstruits sont en flottants et peuvent depasser 0 dBFS ; on
    # mesure la crete du montage et l'on pose le gain qui la met a "crete" (−1 dBFS par defaut),
    # une fois, sur tout le set — jamais de limiteur, un set de DJ garde sa dynamique.
    crete = float(plan.get("crete", -1.0))
    brut = sortie + ".brut.wav"
    cmd = ["ffmpeg", "-v", "error", "-y"] + entrees + ["-filter_complex", ";".join(filtres), "-map", courant,
           "-c:a", "pcm_f32le", "-ar", "48000", brut]
    subprocess.run(cmd, check=True)
    # astats, pas volumedetect : ce dernier mesure en 16 bits et ne voit jamais au-dessus de 0 dB.
    mesure = subprocess.run(["ffmpeg", "-v", "info", "-i", brut, "-af", "astats=measure_perchannel=none:measure_overall=Peak_level",
                             "-f", "null", "-"], capture_output=True, text=True).stderr
    pic = float(next(l for l in mesure.splitlines() if "Peak level dB" in l).split(":")[1])
    gain = crete - pic
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", brut, "-af", f"volume={gain:.2f}dB", "-c:a", "pcm_s24le", sortie], check=True)
    os.remove(brut)
    print(f"crete {pic:+.1f} dB → gain {gain:+.1f} dB, crete finale {crete:+.1f} dB")
    d = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", sortie],
                       capture_output=True, text=True).stdout.strip()
    print(f"→ {sortie} : {float(d)/60:.1f} min")
    return 0

if __name__ == "__main__":
    sys.exit(main())
