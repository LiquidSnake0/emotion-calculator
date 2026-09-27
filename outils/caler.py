#!/usr/bin/env python3
# Cale les originaux sur les voies d'une prise, et lit le calage du DJ sur la grille rekordbox.
#
#   python3 outils/caler.py apres-2 --fin=380
#   python3 outils/caler.py set-2 --debut=840 --fin=2310
#
# LA VERITE DES TEMPS, C'EST LA GRILLE REKORDBOX DE L'ORIGINAL. Mesurer l'ecart de deux disques
# sur leurs frappes ne tient pas : le motif de basse biaise la frappe de 100 a 200 ms selon le
# morceau, et l'on « voyait » des decalages sur des transitions que le DJ juge parfaites (27
# septembre 2026). Ici, pour chaque disque de la prise, on retrouve QUEL original il est, a
# quelle vitesse il tourne et ou il commence (correlation de l'onde, au sample), puis la grille
# de temps de l'original — celle que le CDJ affichait au DJ — est ramenee dans le temps de la
# prise. L'ecart entre les grilles des deux disques pendant un recouvrement est le calage du
# DJ, sans biais de frappe ; la difference de leurs periodes est sa derive.
#
# Ecrit <prise>-calage.json : par disque, l'original, la vitesse, l'origine ; par transition,
# l'ecart (ms, > 0 : l'entrant est en retard) et la derive (%). reconstruire.py --calage= les lit.
import json, os, subprocess, sys, unicodedata
from pathlib import Path
import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import reconstruire as R, monter, passages

STUDIO = R.STUDIO
ORIGINAUX = Path(os.environ.get("EMOTION_ORIGINAUX", Path.home() / "Documents/emotion-sources/originaux"))
CACHE = Path(os.environ.get("EMOTION_CACHE_DIR", Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache")) / "emotion-emulator"))
TMP = Path(os.environ.get("TMPDIR", "/tmp"))

def norm(s): return unicodedata.normalize("NFKC", s or "")

def fiches():
    """Les grilles rekordbox, par nom de fichier : tempo, premier temps (ms)."""
    j = CACHE / "rekordbox.json"
    if not j.exists(): return {}
    out = {}
    for x in json.loads(j.read_text()):
        if not isinstance(x, dict): continue
        for cle in (x.get("fichier"), os.path.basename(x.get("chemin") or "")):
            if cle: out[norm(cle)] = x
    return out

def originaux():
    return sorted(p for p in ORIGINAUX.iterdir() if p.suffix.lower() in (".aiff", ".aif", ".wav", ".flac", ".mp3"))

def duree(w):
    return float(subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(w)],
                                capture_output=True, text=True).stdout or 0)

def voie_fichier(prise, voie, debut, fin):
    """La voie en mono 8 kHz dans un fichier temporaire, pour monter.aligner/affiner."""
    f = TMP / f"caler-{prise}-CH{voie}.wav"
    g, d = R.VOIES[voie]
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", f"{debut:.3f}", "-t", f"{fin - debut:.3f}", "-i", str(R.table(prise)),
                    "-filter_complex", f"pan=mono|c0=0.5*c{g}+0.5*c{d}", "-ar", str(monter.SR), str(f)], check=True)
    return f

def identifier(voie_f, disque, debut, candidats, tempo_voie, grilles):
    """Quel original joue sur ce disque, a quelle vitesse, et ou il commence : pour chaque original
    plausible et trois fenetres de la voie (une minute au milieu, le disque entier, la premiere
    minute), un alignement d'enveloppe, puis cinq ancrages fins le long du disque et une droite
    t_orig = a + s * t_voie. On garde le premier qui tient : au moins trois ancrages a 0,4, un
    residu sous 8 ms, une vitesse a 3 % de ce que la fiche predit, et un original qui n'a pas a
    commencer avant le debut du disque.

    UN MORCEAU EN BOUCLE SE RESSEMBLE TOUTES LES SEIZE MESURES : l'alignement peut accrocher une
    repetition. Pour le calage ca ne change rien — les temps et les mesures se superposent aussi —
    et pour poser l'original par-dessus, c'est la meme musique. On ne s'en inquiete donc pas.

    DEUX CONVENTIONS, A NE PAS MELANGER. aligner_energie rend ρ tel que « B (l'original) est A (la
    voie) joue ρ fois plus vite » ; ici r = secondes d'original par seconde de voie = 1/ρ, et un
    disque accelere donne r > 1. Rend un dict (original, s, a, points, q) ou None."""
    d0, d1 = disque.debut - debut, disque.fin - debut
    milieu = (d0 + d1) / 2
    fenetres = [(max(d0 + 5, milieu - 30), min(d1 - 5, milieu + 30)), (d0 + 3, d1 - 3), (d0 + 8, min(d1 - 3, d0 + 70))]
    essais = []
    for orig in candidats:
        g = grilles.get(norm(orig.name))
        # ρ = « l'original est la voie jouee ρ fois plus vite » = tempo natif / tempo joue. Un disque
        # a 97 BPM joue a 90 donne ρ = 1,078. Le 27 septembre 2026 ce rapport etait ecrit a l'envers,
        # et cinq disques sur huit ne se reconnaissaient pas : c'est le DJ qui a pose la question du BPM.
        if g and g.get("bpm") and tempo_voie:
            rho0 = g["bpm"] / tempo_voie
            ratios = rho0 * np.arange(0.97, 1.0301, 0.0025)
        else:
            rho0 = None; ratios = np.arange(0.84, 1.1801, 0.004)
        dur = duree(orig)
        for fen in fenetres:
            if fen[1] - fen[0] < 30: continue
            tA, tB, rho, sc = aligner_energie(voie_f, fen, orig, (0, dur), ratios)
            if tA is None or sc < 0.08: continue
            tA2, tB2, rho2, sc2 = aligner_energie(voie_f, fen, orig, (max(0, tB - 90), min(dur, tB + 90)), rho * np.arange(0.996, 1.0041, 0.0004))
            if tA2 is not None and sc2 >= sc: tA, tB, rho, sc = tA2, tB2, rho2, sc2
            essais.append((sc, orig, tA, tB, 1.0 / rho, rho0, fen))
    essais.sort(key=lambda e: -e[0])
    for sc, orig, tA, tB, r, rho0, fen in essais[:8]:
        aj = ajuster(voie_f, orig, tA, tB, r, d0, d1)
        if aj is None: continue
        s_, a_, residu, points = aj
        q = float(np.mean([p[2] for p in points]))
        debut_orig = a_ + s_ * d0
        ok = len(points) >= 3 and residu <= 8 and debut_orig >= -3 and (rho0 is None or abs(s_ * rho0 - 1) <= 0.04)
        print(f"      {orig.name[:34]:36s} fen {fen[0]:4.0f}-{fen[1]:4.0f} env {sc:.2f} → {len(points)} ancrages q {q:.2f} residu {residu:.0f} ms vitesse {(s_-1)*100:+.2f} % debut orig {debut_orig:+.0f} s {'OK' if ok else '—'}", flush=True)
        if ok: return dict(original=orig, s=s_, a=a_, points=points, q=q)
    return None

def enveloppe_100(x, sr):
    """L'enveloppe d'energie pleine bande a 100 Hz, sans sa tendance : monter.aligner ne regarde que
    le grave, et une voie dont le DJ a coupe les basses n'y laisse rien."""
    e = enveloppe_fine(x, sr)
    nb = len(e) // 10
    e = e[:nb * 10].reshape(nb, 10).mean(axis=1)
    return e - np.convolve(e, np.ones(100) / 100, mode="same")

def aligner_energie(voie_f, fa, orig, fb, ratios):
    """Comme monter.aligner (la plus courte des deux enveloppes glisse sur la longue, pour chaque
    rapport de vitesse), mais sur l'enveloppe d'energie pleine bande. Rend (tA, tB, ρ, score), ρ
    dans la convention de monter : « B est A joue ρ fois plus vite »."""
    a0, a1 = fa; b0, b1 = fb
    A = enveloppe_100(monter.mono(str(voie_f), a0, a1 - a0), monter.SR)
    B = enveloppe_100(monter.mono(str(orig), b0, b1 - b0), monter.SR)
    meilleur = (None, None, 1.0, -1.0)
    for r in ratios:
        nA = int(len(A) / r)
        Ar = np.interp(np.arange(nA) * r, np.arange(len(A)), A)
        court, long_, a_court = (Ar, B, True) if nA <= len(B) else (B, Ar, False)
        if len(court) < 400: continue
        n = 1
        while n < len(long_) + len(court): n *= 2
        c = np.fft.irfft(np.fft.rfft(long_, n) * np.conj(np.fft.rfft(court, n)), n)[:len(long_) - len(court) + 1]
        cs = np.concatenate(([0.0], np.cumsum(long_.astype(np.float64) ** 2)))
        eL = np.sqrt(np.maximum(cs[len(court):] - cs[:len(long_) - len(court) + 1], 0.0)) + 1e-9
        score = c / (eL * np.sqrt(np.sum(court ** 2)) + 1e-9)
        k = int(np.argmax(score))
        if score[k] > meilleur[3]:
            m = len(court) // 2
            tA, tB = (a0 + m * r / 100, b0 + (k + m) / 100) if a_court else (a0 + (k + m) * r / 100, b0 + m / 100)
            meilleur = (tA, tB, float(r), float(score[k]))
    return meilleur

def enveloppe_fine(x, sr):
    """L'enveloppe d'energie a la milliseconde : redressee, lissee sur 5 ms. Elle survit a l'EQ de
    la table et au filtre, que l'onde brute ne supporte pas."""
    k = max(1, int(sr * 0.005))
    cs = np.concatenate(([0.0], np.cumsum(np.abs(x), dtype=np.float64)))
    env = (cs[k:] - cs[:-k]) / k
    par = max(1, int(round(sr / 1000)))
    nb = len(env) // par
    return env[:nb * par].reshape(nb, par).mean(axis=1).astype(np.float32)

def ancrer(voie_f, t_voie, orig, t_orig, r, marge=1.0, duree_s=6.0):
    """Un point d'ancrage : autour de t_voie dans la voie et de t_orig dans l'original, l'instant
    exact ou les deux enveloppes se superposent (l'original ramene a la vitesse de la voie), et la
    correlation normalisee qui le prouve. Rend (t_orig exact, q)."""
    A = enveloppe_fine(monter.mono(str(voie_f), t_voie - duree_s / 2, duree_s), monter.SR)
    B0 = monter.mono(str(orig), t_orig - duree_s / 2 * r - marge, duree_s * r + 2 * marge)
    B = enveloppe_fine(B0, monter.SR)
    # B en secondes de voie : une ms de voie vaut r ms d'original
    Bv = np.interp(np.arange(int(len(B) / r)) * r, np.arange(len(B)), B)
    A = A - A.mean(); Bv = Bv - Bv.mean()
    if A.std() < 1e-9 or Bv.std() < 1e-9 or len(Bv) <= len(A): return t_orig, 0.0
    c = np.correlate(Bv, A, mode="valid")
    cs = np.concatenate(([0.0], np.cumsum(Bv.astype(np.float64) ** 2)))
    e = np.sqrt(np.maximum(cs[len(A):] - cs[:len(Bv) - len(A) + 1], 0.0)) + 1e-9
    q = c / (e * np.sqrt(np.sum(A * A)) + 1e-9)
    k = int(np.argmax(q))
    # l'ancre de A (son milieu) tombe a k + len(A)/2 ms de voie dans Bv, soit (k + len(A)/2) * r ms d'original depuis le debut de B0
    return (t_orig - duree_s / 2 * r - marge) + (k + len(A) / 2) * r / 1000.0, float(q[k])

def ajuster(voie_f, orig, tA, tB, r, seg_debut, seg_fin):
    """Trois points d'ancrage le long du disque, l'onde a chaque fois : la droite t_orig = a + s * t_voie."""
    points = []
    for frac in (0.15, 0.3, 0.5, 0.7, 0.85):
        t = seg_debut + frac * (seg_fin - seg_debut)
        tb = tB + (t - tA) * r
        if tb < 4: continue
        tb2, q = ancrer(voie_f, t, orig, tb, r)
        if q >= 0.4: points.append((t, tb2, q))
    if len(points) < 2: return None
    tv = np.array([p[0] for p in points]); to = np.array([p[1] for p in points])
    s, a = np.polyfit(tv, to, 1)
    residu = float(np.max(np.abs(to - (a + s * tv)))) * 1000
    return float(s), float(a), residu, points

def transitions(disques_infos, grilles, prise):
    """Les ecarts de grille entre disques qui se recouvrent. disques_infos : la liste du .json
    (None pour un disque non identifie), dans l'ordre des debuts."""
    res = []
    infos = [d for d in disques_infos]
    for k, rk in enumerate(infos):
        if not rk: continue
        g_e = grilles.get(norm(rk["original"]))
        if not g_e: continue
        j = next((i for i in range(k - 1, -1, -1) if infos[i] and infos[i]["voie"] != rk["voie"] and infos[i]["fin"] > rk["debut"]), None)
        if j is None: continue
        rj = infos[j]; g_s = grilles.get(norm(rj["original"]))
        if not g_s: continue
        def grille(r_, g):
            per = 60.0 / g["bpm"]; t0 = g["premier_temps_ms"] / 1000.0
            n = int(duree(ORIGINAUX / r_["original"]) / per) + 1
            return (t0 + np.arange(n) * per - r_["a_prise"]) / r_["vitesse"]
        ge, gs = grille(rk, g_e), grille(rj, g_s)
        deb, fin_ = rk["debut"] + 2, min(rj["fin"], rk["fin"]) - 2
        be = ge[(ge >= deb) & (ge <= fin_)]
        if len(be) < 4: continue
        per_s = 60.0 / g_s["bpm"] / rj["vitesse"]; per_e = 60.0 / g_e["bpm"] / rk["vitesse"]
        ecarts = []
        for t in be:
            i = np.searchsorted(gs, t); voisins = gs[max(0, i - 1):i + 1]
            if len(voisins) == 0: continue
            ecarts.append((t - voisins[np.argmin(np.abs(voisins - t))]) * 1000)
        e = np.array(ecarts); med = float(np.median(e)); mad = float(np.median(np.abs(e - med)))
        derive = (per_e / per_s - 1) * 100
        res.append(dict(entrant=k, sortant=j, ecart_ms=med, dispersion_ms=mad, derive_pct=derive, debut_ms=float(e[0]), fin_ms=float(e[-1]),
                        duree_s=float(fin_ - deb), nom=f"{rj['original'][:20]} → {rk['original'][:20]}", t=rk["debut"]))
    return res

def afficher(trs):
    print("\n transition                                      ecart des grilles (ms, > 0 = entrant en retard)   derive")
    for t in trs:
        print(f" {t['nom']:44s} ({t['t']:6.0f} s) : {t['ecart_ms']:+5.0f} ms ±{t['dispersion_ms']:.0f}  ({t['debut_ms']:+.0f} → {t['fin_ms']:+.0f} sur {t['duree_s']:.0f} s)   {t['derive_pct']:+.2f} %")

def main():
    if len(sys.argv) < 2: print(__doc__); return 2
    prise = sys.argv[1]
    args = dict(a.split("=", 1) for a in sys.argv[2:] if "=" in a)
    grilles = fiches()
    if "--recalcule" in sys.argv:
        # les disques sont deja cales : seules les grilles (le cache) ont pu changer
        j = json.loads((STUDIO / prise / f"{prise}-calage.json").read_text())
        trs = transitions(j["disques"], grilles, prise); afficher(trs)
        j["transitions"] = trs
        (STUDIO / prise / f"{prise}-calage.json").write_text(json.dumps(j, indent=1, ensure_ascii=False, default=str))
        return 0
    debut = float(args.get("--debut", 0)); fin = float(args.get("--fin", duree(R.table(prise))))
    cands = originaux()
    if not cands: sys.exit(f"aucun original dans {ORIGINAUX}")
    print(f"{prise} : {debut:.0f} → {fin:.0f} s ; {len(cands)} originaux, {len(grilles)} grilles", flush=True)
    sons = {v: R.mono(R.table(prise), R.VOIES[v], debut, fin - debut) for v in R.VOIES}
    disques = []
    for v in R.VOIES:
        for s0, e0 in R.segments(sons[v]):
            d = R.Disque(v, debut + s0, debut + e0); d.plateau = R.plateau_db(sons[v], s0, e0); disques.append(d)
    disques.sort(key=lambda d: d.debut)
    voies = {v: voie_fichier(prise, v, debut, fin) for v in R.VOIES}
    resultat = []
    for k, d in enumerate(disques):
        if d.fin - d.debut < 40 or d.plateau < -35:
            resultat.append(None); print(f" {k:2d} CH{d.voie} {d.debut:7.1f}-{d.fin:7.1f} : trop court ou trop bas"); continue
        i0, i1 = int((d.debut - debut) * R.SR), int((d.fin - debut) * R.SR)
        tempo_voie, _ = passages.tempo(passages.enveloppe(sons[d.voie][i0:i1])[0], 90.0, 0.25)
        ident = identifier(voies[d.voie], d, debut, cands, tempo_voie, grilles)
        if ident is None:
            resultat.append(None); print(f" {k:2d} CH{d.voie} {d.debut:7.1f}-{d.fin:7.1f} : aucun original ne colle"); continue
        orig, s_, a_, points, q = ident["original"], ident["s"], ident["a"], ident["points"], ident["q"]
        tv = np.array([p[0] for p in points]); to = np.array([p[1] for p in points])
        residu = float(np.max(np.abs(to - (a_ + s_ * tv)))) * 1000
        g = grilles.get(norm(orig.name))
        bpm_joue = g["bpm"] * s_ if g else None
        print(f" {k:2d} CH{d.voie} {d.debut:7.1f}-{d.fin:7.1f}  {orig.name[:44]:46s} q {q:.2f}  vitesse {(s_-1)*100:+.2f} %"
              + (f"  → {bpm_joue:5.1f} BPM (fiche {g['bpm']})" if g else "  (pas de grille)") + f"  residu {residu:.0f} ms sur {len(points)} points", flush=True)
        # a_ est en temps de la voie extraite (depuis `debut`) : t_orig = a_prise + s * t_prise
        resultat.append(dict(voie=d.voie, debut=d.debut, fin=d.fin, original=orig.name, vitesse=s_, a=a_, a_prise=a_ - s_ * debut, q=q,
                             bpm=g["bpm"] if g else None, premier_ms=g.get("premier_temps_ms") if g else None, bpm_joue=bpm_joue, residu_ms=residu))
    trs = transitions(resultat, grilles, prise); afficher(trs)
    (STUDIO / prise / f"{prise}-calage.json").write_text(json.dumps(dict(prise=prise, debut=debut, fin=fin, disques=resultat, transitions=trs),
                                                                     indent=1, ensure_ascii=False, default=str))
    return 0

if __name__ == "__main__":
    sys.exit(main())
