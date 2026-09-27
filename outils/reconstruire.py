#!/usr/bin/env python3
# Reconstruit le master d'une prise a partir de ses deux voies apres fader, en corrigeant ce
# que la table a enregistre tel quel : le niveau de chaque disque, le calage du disque entrant
# (decalage et vitesse), un fader oublie. Le Rec Out n'est garde que la ou on le demande (un
# effet de la table qu'on veut conserver).
#
#   python3 outils/reconstruire.py avant --debut=0 --fin=420 --cible=-20
#   python3 outils/reconstruire.py set-2 --debut=840 --fin=2310 --garder=1416,1524 --sortie=3:2560
#
#   --debut/--fin   la plage de la prise a reconstruire (secondes) ; le reste est ignore
#   --cible=dB      le niveau de plateau (RMS, dBFS) auquel chaque disque est ramene (defaut : la
#                   mediane des disques de la prise) ; --gain-max borne la correction (6 dB)
#   --garder=t0,t1  une fenetre (secondes, temps de la prise) ou le Rec Out remplace la somme
#                   des voies — pour garder un effet de la table ; plusieurs fois possible
#   --sortie=k:t[:d] le disque k (rang dans le tableau) s'eteint a partir de t, en d secondes (8 par
#                   defaut) : le fader qu'on a oublie de baisser, ou un disque a arreter plus tot
#   --gain=k:dB     le disque k recoit ce gain, a la place du calcul (l'oreille a tranche)
#   --brut=k,l      les disques k et l ne sont ni decales ni ralentis (un effet de la table a garder
#                   sur leur transition, ou une mesure qu'on ne croit pas)
#   --plat=k        le disque k garde son niveau
#   --avance=k:ms   le disque k est avance de ms (negatif : retarde), a la place de la mesure
#   --vitesse=k:pct le disque k est accelere de pct % (negatif : ralenti), a la place de la mesure
#   --original=k:t0:t1[:dB]   entre t0 et t1 (secondes de la prise), le disque k est remplace par
#                   son ORIGINAL, cale par caler.py (<prise>-calage.json) : un effet de la table a
#                   effacer, un jog touche. Fondus d'une seconde de chaque cote ; dB ajuste le niveau
#   --prolonger=k:t1[:fondu[:dB]]  le disque k continue au-dela de sa coupure jusqu'a t1 avec son
#                   original, en s'eteignant sur `fondu` secondes (8) ; dB le met en fond (-6 par defaut)
#   --tenir=k:t0:t1 le disque k garde, entre t0 et t1, le niveau qu'il avait juste avant t0 : on
#                   annule une descente de fader (une flute qu'on veut entendre finir sa phrase)
#   --vers=fichier  ecrit la sous ce nom (un extrait a ecouter) au lieu de <prise>-v2.wav
#   --sans-calage   ne corrige ni decalage ni vitesse (pour comparer)
#   --mesure        n'ecrit rien, imprime le tableau ; --detail ajoute l'ecart de chaque fenetre de 12 s
#
# CE QUE LE MULTIPISTE PERMET, ET CE QU'IL NE PERMET PAS. Chaque disque est seul sur sa voie,
# donc on peut le peser, le decaler, le ralentir ou l'accelerer sans toucher a l'autre. Mais la
# voie est prise APRES le fader : elle ne contient que ce que le DJ a laisse passer. Allonger un
# disque ou adoucir une coupure demande l'original, pas cet outil.
#
# LE CALAGE EST MESURE COMME DANS passages.py, puis corrige : sur le recouvrement des deux
# disques, l'ecart des frappes au debut et a la fin. L'ecart du debut devient le decalage du
# disque entrant ; sa derive sur la duree du recouvrement devient sa correction de vitesse. Le
# disque entrant est compare au disque sortant TEL QUE DEJA CORRIGE, donc les corrections
# s'enchainent sans se contredire.
import json, math, os, subprocess, sys
from pathlib import Path
import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import passages

STUDIO = Path(os.environ.get("EMOTION_STUDIO_DIR", Path.home() / "Documents/emotion-sources/studio"))
SR = passages.SR; PAS = passages.PAS
VOIES = {2: (2, 3), 3: (4, 5)}      # les canaux du multipiste : CH2, CH3 apres fader
RECOUT = (8, 9)
SEUIL_ACTIF = -50.0                 # dBFS, en dessous la voie est vide
TROU_MIN = 4.0                      # s de silence qui separent deux disques sur la meme voie
DISQUE_MIN = 15.0                   # s : plus court, ce n'est pas un disque
# QUAND CORRIGER, ET QUAND S'ABSTENIR. L'ecart des frappes est mesure sur l'enveloppe du grave,
# par fenetres de 12 s. On ne deplace un disque que si l'ecart se repete de fenetre en fenetre
# (dispersion <= 40 ms) et s'il s'entend (>= 20 ms). On ne corrige la vitesse que si la derive est
# nette (dispersion autour de la pente <= 40 ms, au moins 30 ms sur le recouvrement) et
# plausible (<= 0,6 % : au-dela c'est la mesure qui a rate, pas le DJ).
DISPERSION_MAX_MS = 40.0
DECALAGE_MIN_MS = 20.0
DERIVE_MIN_MS = 30.0
VITESSE_MAX = 0.006

def table(prise):
    w = sorted((STUDIO / prise).glob("*-table.wav"))
    if not w: sys.exit(f"pas de multipiste dans {STUDIO / prise}")
    return w[0]

def mono(wav, canaux, debut, duree):
    g, d = canaux
    raw = subprocess.run(["ffmpeg", "-v", "error", "-ss", f"{debut:.3f}", "-t", f"{duree:.3f}", "-i", str(wav),
                          "-filter_complex", f"pan=mono|c0=0.5*c{g}+0.5*c{d}", "-ar", str(SR), "-f", "f32le", "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32)

PAS_FIN = 0.001                     # l'enveloppe des frappes, a la milliseconde

def frappes(x):
    """L'enveloppe des attaques du grave (30-250 Hz), a la milliseconde : le grave filtre par FFT,
    redresse, lisse sur 20 ms, derive positive, puis ramene a 1000 Hz. Le flux par trames de
    passages.py (40 ms, pas de 10) change du simple au double selon la phase de la trame — deux
    extractions du meme son a 2 ms d'ecart donnaient des ecarts de frappes sans rapport."""
    n = len(x)
    X = np.fft.rfft(x.astype(np.float32))
    f = np.fft.rfftfreq(n, 1 / SR)
    X[(f < 30) | (f > 250)] = 0
    y = np.abs(np.fft.irfft(X, n)).astype(np.float32)
    k = int(SR * 0.02)
    cs = np.concatenate(([0.0], np.cumsum(y, dtype=np.float64)))
    env = ((cs[k:] - cs[:-k]) / k).astype(np.float32)               # moyenne glissante de 20 ms
    d = np.diff(env, prepend=env[0]); d[d < 0] = 0
    par = int(round(SR * PAS_FIN))                                    # 11 echantillons par milliseconde
    nb = len(d) // par
    return d[:nb * par].reshape(nb, par).mean(axis=1)

def ecart_ms(a, b, periode):
    """Decalage de b par rapport a a, en ms, dans ± une demi-periode, sur des enveloppes a 1000 Hz ;
    NEGATIF = b en retard. Correlation normalisee, pour que la valeur soit comparable d'une fenetre a l'autre."""
    a = a - a.mean(); b = b - b.mean()
    if a.std() == 0 or b.std() == 0: return None
    demi = int(periode * 1000 / 2)
    if len(a) <= 2 * demi + 10: return None
    c = np.correlate(a, b[demi:len(b) - demi], mode="valid")           # c[k] : b decale de (demi - k)
    k = int(np.argmax(c))
    return float(k - demi)

def rms_db(x, pas=1.0):
    n = int(SR * pas); nb = len(x) // n
    e = np.sqrt(np.mean(x[:nb * n].reshape(nb, n) ** 2, axis=1))
    return 20 * np.log10(e + 1e-9)

def segments(x):
    """Les disques d'une voie : plages actives, coupees par des trous de TROU_MIN, d'au moins DISQUE_MIN."""
    db = rms_db(x, 0.5)                                   # une valeur par demi-seconde
    actif = db > SEUIL_ACTIF
    segs, i = [], 0
    while i < len(actif):
        if not actif[i]: i += 1; continue
        j = i
        while j < len(actif):
            if actif[j]: j += 1; continue
            k = j
            while k < len(actif) and not actif[k]: k += 1
            if (k - j) * 0.5 >= TROU_MIN or k >= len(actif): break
            j = k
        if (j - i) * 0.5 >= DISQUE_MIN: segs.append((i * 0.5, j * 0.5))
        i = j
    return segs

def plateau_db(x, debut, fin):
    """Le niveau du disque quand il joue a fond : moyenne des secondes entre le 60e et le 95e centile."""
    d = rms_db(x[int(debut * SR):int(fin * SR)])
    d = d[d > SEUIL_ACTIF]
    if len(d) < 5: return float(np.mean(d)) if len(d) else SEUIL_ACTIF
    lo, hi = np.percentile(d, 60), np.percentile(d, 95)
    return float(np.mean(d[(d >= lo) & (d <= hi)]))

class Disque:
    def __init__(self, voie, debut, fin):
        self.voie = voie; self.debut = debut; self.fin = fin
        self.ancre = debut; self.decalage = 0.0; self.vitesse = 1.0; self.gain_db = 0.0
        self.plateau = None; self.ecarts = None; self.sortie = None; self.contre = None; self.apres = None
    def sortie_temps(self, t):
        """Le temps de sortie d'un instant t de la prise, pour ce disque."""
        return self.ancre - self.decalage + (t - self.ancre) / self.vitesse
    def entree_temps(self, tau):
        return self.ancre + (tau - self.ancre + self.decalage) * self.vitesse

def tempo_fin(fe, ancrage):
    """La periode du disque entrant : l'enveloppe fine ramenee a 10 ms, puis passages.tempo."""
    par = int(round(passages.PAS / PAS_FIN)); nb = len(fe) // par
    grossier = fe[:nb * par].reshape(nb, par).mean(axis=1)
    ta, force = passages.tempo(grossier, ancrage, 0.12)
    return 60 / (ta or ancrage), force

def calage(fr_sortant, sortant, fr_entrant, entrant, t0, ancrage):
    """Sur le recouvrement, l'ecart des frappes de l'entrant par rapport au sortant, tous deux lus
    dans le temps de sortie (un disque pas encore corrige y est identique a lui-meme), par
    fenetres glissantes de 12 s. NEGATIF = l'entrant est en retard. Rend un dict : la mediane et
    sa dispersion (MAD), la pente (ms par s, > 0 : l'entrant prend de l'avance, il est trop
    rapide) et la dispersion autour d'elle, la periode, la force du tempo, le centre — ou None.

    LA DISPERSION VAUT PLUS QUE LA FORCE. Un kick feutre donne une force basse et pourtant un
    ecart identique de fenetre en fenetre (crying → echoes : +140 ms neuf fois de suite) ; un
    kick net peut donner des ecarts qui sautent d'un quart de temps (deux frappes du grave qui
    se disputent). On croit une mesure quand elle se repete, pas quand elle est forte."""
    grille = t0 + np.arange(len(fr_entrant)) * PAS_FIN
    def dans_le_temps_de_sortie(fr, disque):
        src = (disque.entree_temps(grille) - t0) / PAS_FIN
        return np.interp(src, np.arange(len(fr)), fr, left=0.0, right=0.0)
    fs = dans_le_temps_de_sortie(fr_sortant, sortant)
    fe = dans_le_temps_de_sortie(fr_entrant, entrant)
    deb = max(entrant.sortie_temps(entrant.debut), sortant.sortie_temps(sortant.debut))
    fin = min(entrant.sortie_temps(entrant.fin), sortant.sortie_temps(sortant.fin))
    i0, j = int((deb - t0) / PAS_FIN), int((fin - t0) / PAS_FIN)
    if (j - i0) * PAS_FIN < 12: return None
    per, force = tempo_fin(fe[i0:j], ancrage)
    n, pas = int(12 / PAS_FIN), int(6 / PAS_FIN)
    # Seules comptent les fenetres ou les deux disques sonnent : quand l'un s'eteint au fader, la
    # mesure decroche (crying → echoes : +149 ms huit fois, puis +314 et -194 sur la queue de crying).
    fenetres = list(range(i0, j - n + 1, pas))
    if len(fenetres) < 3: return None
    niveau_s = np.array([fs[i:i + n].mean() for i in fenetres]); niveau_e = np.array([fe[i:i + n].mean() for i in fenetres])
    vivant = (niveau_s >= 0.25 * np.median(niveau_s)) & (niveau_e >= 0.25 * np.median(niveau_e))
    ecarts, temps = [], []
    for i, ok in zip(fenetres, vivant):
        if not ok: continue
        e = ecart_ms(fs[i:i + n], fe[i:i + n], per)
        if e is not None: ecarts.append(e); temps.append(t0 + (i + n / 2) * PAS_FIN)
    if len(ecarts) < 3: return None
    e = np.array(ecarts); t = np.array(temps)
    med = float(np.median(e)); mad = float(np.median(np.abs(e - med)))
    pente, ordonnee = np.polyfit(t, e, 1)
    residu = float(np.median(np.abs(e - (pente * t + ordonnee))))
    centre = float(t.mean())
    return dict(mediane=med, mad=mad, pente=float(pente), residu=residu, periode=per, force=force,
                centre=centre, au_centre=float(pente * centre + ordonnee), duree=float(t[-1] - t[0]), n=len(e),
                debut=float(e[0]), fin=float(e[-1]), ecarts=[float(v) for v in e])

def main():
    if len(sys.argv) < 2: print(__doc__); return 2
    prise = sys.argv[1]
    args = dict(a.split("=", 1) for a in sys.argv[2:] if "=" in a)
    flags = {a for a in sys.argv[2:] if "=" not in a}
    wav = table(prise)
    duree_totale = float(subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(wav)],
                                        capture_output=True, text=True).stdout)
    debut = float(args.get("--debut", 0)); fin = min(float(args.get("--fin", duree_totale)), duree_totale)
    garder = [tuple(float(v) for v in g.split(",")) for g in sys.argv[2:] if g.startswith("--garder=") for g in [g[9:]]]
    sorties = {}
    for a in (a[9:] for a in sys.argv[2:] if a.startswith("--sortie=")):
        champs = a.split(":"); sorties[int(champs[0])] = (float(champs[1]), float(champs[2]) if len(champs) > 2 else 8.0)
    gains = {int(a[7:].split(":")[0]): float(a[7:].split(":")[1]) for a in sys.argv[2:] if a.startswith("--gain=")}
    remplacements, prolongations = [], []
    tenues = [(int(c[0]), float(c[1]), float(c[2])) for c in (a[8:].split(":") for a in sys.argv[2:] if a.startswith("--tenir="))]
    for a in sys.argv[2:]:
        if a.startswith("--original="):
            c = a[11:].split(":"); remplacements.append((int(c[0]), float(c[1]), float(c[2]), float(c[3]) if len(c) > 3 else None))
        if a.startswith("--prolonger="):
            c = a[12:].split(":"); prolongations.append((int(c[0]), float(c[1]), float(c[2]) if len(c) > 2 else 8.0, float(c[3]) if len(c) > 3 else -6.0))
    calage = None
    if remplacements or prolongations:
        cj = STUDIO / prise / f"{prise}-calage.json"
        if not cj.exists(): sys.exit(f"pas de {cj} : python3 outils/caler.py {prise} d'abord")
        calage = json.loads(cj.read_text())
    gain_max = float(args.get("--gain-max", 6.0))
    bruts = {int(v) for a in sys.argv[2:] if a.startswith("--brut=") for v in a[7:].split(",") if v}
    plats = {int(v) for a in sys.argv[2:] if a.startswith("--plat=") for v in a[7:].split(",") if v}
    avances = {int(a[9:].split(":")[0]): float(a[9:].split(":")[1]) for a in sys.argv[2:] if a.startswith("--avance=")}
    vitesses = {int(a[10:].split(":")[0]): float(a[10:].split(":")[1]) for a in sys.argv[2:] if a.startswith("--vitesse=")}
    ancrage = passages.ancrage_du_crate(STUDIO / prise) or 90.0

    print(f"{prise} : {debut:.0f} → {fin:.0f} s, ancrage {ancrage:.0f} BPM ; extraction des voies…", flush=True)
    sons = {v: mono(wav, VOIES[v], debut, fin - debut) for v in VOIES}
    flux = {v: frappes(sons[v]) for v in VOIES}

    disques = []
    for v in VOIES:
        for s, e in segments(sons[v]):
            d = Disque(v, debut + s, debut + e); d.plateau = plateau_db(sons[v], s, e); disques.append(d)
    disques.sort(key=lambda d: d.debut)
    if not disques: sys.exit("aucun disque trouve")

    # LE NIVEAU : chaque disque au meme plateau.
    cible = float(args["--cible"]) if "--cible" in args else float(np.median([d.plateau for d in disques]))
    # Un disque reste tres bas (plus de 12 dB sous la cible) : il n'a pas ete joue, juste entrouvert
    # au fader ; on ne le remonte pas.
    for d in disques:
        coupe = d.fin >= fin - 0.6 and d.plateau < cible - 4          # entame par le bout de la plage : son plateau n'est pas le sien
        d.gain_db = max(-gain_max, min(gain_max, cible - d.plateau)) if d.plateau > cible - 12 and not coupe else 0.0
    for k in plats:
        if 0 <= k < len(disques): disques[k].gain_db = 0.0
    for k, g in gains.items():
        if 0 <= k < len(disques): disques[k].gain_db = g

    # LA MAIN DU DJ D'ABORD : un decalage ou une vitesse donnes (--avance, --vitesse) s'appliquent
    # toujours, meme sans recalage automatique. Le 27 septembre 2026, quinze extraits « a ±80 ms »
    # rendus avec --sans-calage etaient le meme fichier, et le DJ a compare cinq fois la v6.
    for k, d in enumerate(disques):
        if k in avances or k in vitesses:
            d.ancre = d.debut; d.decalage = avances.get(k, 0.0) / 1000.0; d.vitesse = 1.0 + vitesses.get(k, 0.0) / 100.0
    # LE CALAGE MESURE, dans l'ordre : chaque disque entrant contre le disque sortant deja corrige.
    if "--sans-calage" not in flags:
        for k, d in enumerate(disques):
            sortant = next((p for p in reversed(disques[:k]) if p.voie != d.voie and p.fin > d.debut), None)
            if sortant is None: continue
            d.contre = disques.index(sortant)
            m = calage(flux[sortant.voie], sortant, flux[d.voie], d, debut, ancrage)
            if m is None: continue
            d.ecarts = m
            if k in bruts or d.plateau < cible - 12: continue          # laisse tel quel, ou a peine entrouvert au fader
            if k in avances or k in vitesses: continue                 # la main du DJ passe avant la mesure
            derive_totale = m["pente"] * m["duree"]
            corriger_vitesse = m["residu"] <= DISPERSION_MAX_MS and abs(derive_totale) >= DERIVE_MIN_MS and abs(m["pente"]) / 1000.0 <= VITESSE_MAX
            ecart_ref = m["au_centre"] if corriger_vitesse else m["mediane"]
            dispersion = m["residu"] if corriger_vitesse else m["mad"]
            deplacer = dispersion <= DISPERSION_MAX_MS and abs(ecart_ref) >= DECALAGE_MIN_MS
            if deplacer or corriger_vitesse:
                d.ancre = m["centre"]
                d.decalage = -ecart_ref / 1000.0 if deplacer else 0.0            # ecart < 0 = en retard : on l'avance
                d.vitesse = 1.0 - m["pente"] / 1000.0 if corriger_vitesse else 1.0  # il prend de l'avance : on le ralentit

    for k, t in sorties.items():
        if 0 <= k < len(disques): disques[k].sortie = t

    # LE CONTROLE : la meme mesure, les deux disques corriges. Un residu pres de zero dit que la
    # correction fait ce qu'elle dit ; il ne dit pas qu'elle sonne juste, ca c'est l'oreille.
    for d in disques:
        d.apres = None
        if d.contre is None or not (d.decalage or d.vitesse != 1.0): continue
        r = calage(flux[disques[d.contre].voie], disques[d.contre], flux[d.voie], d, debut, ancrage)
        if r: d.apres = (r["mediane"], r["mad"], r["pente"] * r["duree"])

    print(f"\n cible {cible:.1f} dBFS\n  k voie   debut     fin   plateau   gain    ecart median ±disp  derive (ms sur le recouvrement)  force   avance   vitesse    apres (median ±disp, derive)")
    for k, d in enumerate(disques):
        m = d.ecarts
        ec = f"{m['mediane']:+5.0f} ±{m['mad']:3.0f}   {m['pente']*m['duree']:+5.0f} sur {m['duree']:3.0f} s (±{m['residu']:.0f})   {m['force']:.2f}" if m else "                    —                          "
        corr = f"{d.decalage*1000:+7.0f} ms  {(d.vitesse-1)*100:+.2f} %" if (d.decalage or d.vitesse != 1.0) else "       —       "
        ap = f"{d.apres[0]:+5.0f} ±{d.apres[1]:3.0f}, {d.apres[2]:+5.0f}" if getattr(d, "apres", None) else ""
        print(f" {k:2d}  CH{d.voie}  {d.debut:7.1f} {d.fin:7.1f}   {d.plateau:6.1f}  {d.gain_db:+5.1f}   {ec}   {corr}   {ap:14s}"
              + (f"   s'eteint a {d.sortie[0]:.0f} s en {d.sortie[1]:.0f} s" if d.sortie else "") + (f"   (contre {d.contre})" if d.contre is not None else ""))
        if m and "--detail" in flags: print("          fenetres :", " ".join(f"{v:+4.0f}" for v in m["ecarts"]))
    if "--mesure" in flags: return 0

    # LE REC OUT, la ou on le garde : son niveau par rapport a la somme des voies, mesure sur la plage.
    ratio_db = 0.0
    if garder:
        rec = mono(wav, RECOUT, debut, fin - debut)
        somme = sons[2][:len(rec)] + sons[3][:len(rec)]
        ratio_db = float(20 * np.log10((np.sqrt(np.mean(somme ** 2)) + 1e-9) / (np.sqrt(np.mean(rec ** 2)) + 1e-9)))
        print(f"\n Rec Out : {-ratio_db:+.1f} dB par rapport a la somme des voies, compense")

    # LE GRAPHE FFMPEG : un disque par chaine, place dans le temps de sortie ; les fenetres du
    # Rec Out creusent les voies et se posent par-dessus, avec deux secondes de fondu.
    def rampe(a, b, t="t"):
        return f"min(max(({t}-({a:.3f}))/({b - a:.3f}),0),1)"
    # LE FICHIER COMMENCE AU PREMIER DISQUE, pas au debut de la prise : une plage qui part a 840 s
    # ne doit pas donner quatorze minutes de silence. Tout se place par rapport a cette origine,
    # que le .json publie pour monter.py.
    origine = min(dd.sortie_temps(dd.debut) for dd in disques)
    chaines, noms = [], []
    for k, d in enumerate(disques):
        g, dr = VOIES[d.voie]
        deb_sortie = d.sortie_temps(d.debut)
        c = (f"[0:a]pan=stereo|c0=c{g}|c1=c{dr},atrim=start={d.debut:.4f}:end={d.fin:.4f},asetpts=PTS-STARTPTS")
        if d.vitesse != 1.0: c += f",asetrate={48000 * d.vitesse:.3f},aresample=48000"
        c += f",volume={d.gain_db:.2f}dB"
        # les bords : vingt millisecondes, contre les clics ; un fader oublie : huit secondes
        exprs = [f"{rampe(0, 0.02)}", f"(1-{rampe((d.fin - d.debut) / d.vitesse - 0.02, (d.fin - d.debut) / d.vitesse)})"]
        if d.sortie is not None:
            s = (d.sortie[0] - d.debut) / d.vitesse
            exprs.append(f"(1-{rampe(s, s + d.sortie[1])})")
        for (w0, w1) in garder:
            a, b = (w0 - 2 - d.debut) / d.vitesse, (w1 + 2 - d.debut) / d.vitesse    # dans le temps de la chaine
            exprs.append(f"(1-{rampe(a, a + 2)}*(1-{rampe(b - 2, b)}))")
        for (kk, w0, w1, _) in remplacements:
            if kk != k: continue
            a, b = (w0 - 1 - d.debut) / d.vitesse, (w1 + 1 - d.debut) / d.vitesse
            exprs.append(f"(1-{rampe(a, a + 1)}*(1-{rampe(b - 1, b)}))")
        for (kk, t0, t1) in tenues:
            if kk != k: continue
            # LE FADER ANNULE : le niveau mesure par demi-seconde entre t0 et t1, ramene a celui des
            # trois secondes d'avant, en dB, par une ligne brisee de rampes (jamais plus de +15 dB).
            x = sons[d.voie]; i = lambda t: int((t - debut) * SR)
            ref = 20 * np.log10(np.sqrt(np.mean(x[i(t0 - 3):i(t0)] ** 2)) + 1e-9)
            pas = 0.5; pts = []
            t = t0
            while t < t1:
                niv = 20 * np.log10(np.sqrt(np.mean(x[i(t):i(min(t + pas, t1))] ** 2)) + 1e-9)
                pts.append((t, float(np.clip(ref - niv, 0.0, 15.0)))); t += pas
            if not pts: continue
            g = f"({pts[0][1]:.2f}*{rampe((t0 - 0.25 - d.debut) / d.vitesse, (t0 + 0.25 - d.debut) / d.vitesse)})"
            for (ta, ga), (tb, gb) in zip(pts, pts[1:]):
                g += f"+({gb - ga:+.2f})*{rampe((ta + pas / 2 - d.debut) / d.vitesse, (tb + pas / 2 - d.debut) / d.vitesse)}"
            exprs.append(f"pow(10,({g})/20)")
            print(f" disque {k} tenu de {t0:.1f} a {t1:.1f} s : jusqu'a +{max(v for _, v in pts):.1f} dB pour rester a {ref:.1f} dBFS")
        c += f",volume=eval=frame:volume='{'*'.join(exprs)}'"
        c += f",adelay={int(round((deb_sortie - origine) * 1000))}|{int(round((deb_sortie - origine) * 1000))}[d{k}]"
        chaines.append(c); noms.append(f"[d{k}]")
    for n, (w0, w1) in enumerate(garder):
        # le Rec Out suit le temps du disque qui entre dans cette fenetre
        dans = [d for d in disques if d.debut <= w1 and d.fin >= w0]
        ref = max(dans, key=lambda d: d.debut) if dans else disques[0]
        autres = [abs(d.sortie_temps(w0) - ref.sortie_temps(w0)) for d in dans if d is not ref]
        if autres and max(autres) > 0.015:
            print(f" ! fenetre {w0:.0f}-{w1:.0f} : les disques presents ne sont pas deplaces pareil ({max(autres)*1000:.0f} ms), le Rec Out suivra le disque entrant")
        a, b = w0 - 2, w1 + 2
        c = (f"[0:a]pan=stereo|c0=c{RECOUT[0]}|c1=c{RECOUT[1]},atrim=start={a:.4f}:end={b:.4f},asetpts=PTS-STARTPTS")
        if ref.vitesse != 1.0: c += f",asetrate={48000 * ref.vitesse:.3f},aresample=48000"
        c += f",volume={ratio_db:.2f}dB,volume=eval=frame:volume='{rampe(0, 2)}*(1-{rampe((b - a) / ref.vitesse - 2, (b - a) / ref.vitesse)})'"
        deb_sortie = ref.sortie_temps(a)
        c += f",adelay={int(round((deb_sortie - origine) * 1000))}|{int(round((deb_sortie - origine) * 1000))}[r{n}]"
        chaines.append(c); noms.append(f"[r{n}]")
    # L'ORIGINAL A LA PLACE DE LA VOIE, OU APRES ELLE. caler.py a dit pour ce disque quel original
    # joue, a quelle vitesse (s secondes d'original par seconde de voie) et ou (t_orig = a_prise + s
    # * t_prise). On rejoue l'original a cette vitesse par reechantillonnage — comme le CDJ sans
    # Master Tempo —, au niveau de la voie, et la voie se creuse la ou il passe.
    entrees_sup = []
    ORIGINAUX = Path(os.environ.get("EMOTION_ORIGINAUX", Path.home() / "Documents/emotion-sources/originaux"))
    def disque_cale(k):
        d = disques[k]
        # le meme disque, meme si la plage rendue ici l'entame (un extrait) : meme voie, et il se recouvrent
        for inf in (calage or {}).get("disques") or []:
            if inf and inf["voie"] == d.voie and inf["debut"] <= d.fin and inf["fin"] >= d.debut: return inf
        sys.exit(f"le disque {k} n'est pas cale dans {prise}-calage.json (relancer caler.py sur la meme plage)")
    def niveau_original(inf, t0, t1):
        """Le niveau RMS (dB) de l'original sur ce qu'il jouera entre t0 et t1 de la prise."""
        o0, o1 = inf["a_prise"] + inf["vitesse"] * t0, inf["a_prise"] + inf["vitesse"] * t1
        raw = subprocess.run(["ffmpeg", "-v", "error", "-ss", f"{o0:.3f}", "-t", f"{o1 - o0:.3f}", "-i", str(ORIGINAUX / inf["original"]),
                              "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"], capture_output=True, check=True).stdout
        y = np.frombuffer(raw, dtype=np.float32)
        return 20 * np.log10(np.sqrt(np.mean(y ** 2)) + 1e-9) if len(y) else -60.0
    def couleur(inf, d, t0):
        """L'EQ du canal de la table, mesuree : le spectre moyen de la voie rapporte a celui de l'original
        aligne, sur les dix secondes avant t0 (la ou la voie est propre), par bande d'octave. Rend un
        filtre firequalizer qui donne a l'original le meme grain — l'EQ du DJ, pas une invention."""
        a, b = max(d.debut + 2, t0 - 12.0), t0 - 2.0
        if b - a < 4: return ""
        g, dr = VOIES[d.voie]
        def spectre(cmd_in):
            raw = subprocess.run(["ffmpeg", "-v", "error"] + cmd_in + ["-ac", "1", "-ar", "44100", "-f", "f32le", "-"], capture_output=True, check=True).stdout
            x = np.frombuffer(raw, dtype=np.float32); n = len(x) // 4096 * 4096
            if n == 0: return None
            X = np.abs(np.fft.rfft(x[:n].reshape(-1, 4096) * np.hanning(4096), axis=1)).mean(axis=0)
            return X, np.fft.rfftfreq(4096, 1 / 44100)
        sv = spectre(["-ss", f"{a:.3f}", "-t", f"{b - a:.3f}", "-i", str(wav), "-filter_complex", f"pan=mono|c0=0.5*c{g}+0.5*c{dr}"])
        o0, o1 = inf["a_prise"] + inf["vitesse"] * a, inf["a_prise"] + inf["vitesse"] * b
        so = spectre(["-ss", f"{o0:.3f}", "-t", f"{o1 - o0:.3f}", "-i", str(ORIGINAUX / inf["original"])])
        if sv is None or so is None: return ""
        (Xv, f), (Xo, _) = sv, so
        bandes = [40, 80, 160, 315, 630, 1250, 2500, 5000, 10000, 16000]
        gains = []
        for lo, hi in zip([0] + bandes[:-1], bandes):
            m = (f >= lo) & (f < hi)
            gv, go = np.sqrt(np.mean(Xv[m] ** 2)), np.sqrt(np.mean(Xo[m] ** 2))
            gains.append(float(np.clip(20 * np.log10((gv + 1e-9) / (go + 1e-9)), -18, 12)))
        moyen = float(np.mean(gains[1:7]))                 # le niveau global est deja regle a part : on ne garde que la forme
        gains = [x - moyen for x in gains]
        print(f"   couleur du canal, par bande (dB) : " + " ".join(f"{x:+.0f}" for x in gains))
        entrees_eq = ";".join(f"entry({int(np.sqrt(max(lo, 20) * hi))},{x:.1f})" for (lo, hi), x in zip(zip([0] + bandes[:-1], bandes), gains))
        return f",firequalizer=gain_entry='{entrees_eq}'"

    def chaine_original(k, inf, t0, t1, entree_ms, sortie_ms, gain_db, tag, eq=""):
        """Une chaine ffmpeg qui joue l'original du disque k entre t0 et t1 (prise), avec ses rampes."""
        d = disques[k]
        orig = ORIGINAUX / inf["original"]
        o0, o1 = inf["a_prise"] + inf["vitesse"] * t0, inf["a_prise"] + inf["vitesse"] * t1
        sr = int(subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate", "-of", "csv=p=0", str(orig)],
                                capture_output=True, text=True).stdout.strip() or 44100)
        idx = len(entrees_sup) // 2 + 1
        entrees_sup.extend(["-i", str(orig)])
        vitesse = inf["vitesse"] * d.vitesse
        duree_chaine = (t1 - t0) / d.vitesse
        c = (f"[{idx}:a]atrim=start={max(0.0, o0):.4f}:end={o1:.4f},asetpts=PTS-STARTPTS,asetrate={sr * vitesse:.3f},aresample=48000,aformat=channel_layouts=stereo"
             f"{eq},volume={gain_db:.2f}dB,volume=eval=frame:volume='{rampe(0, entree_ms / 1000)}*(1-{rampe(duree_chaine - sortie_ms / 1000, duree_chaine)})'")
        deb = d.sortie_temps(t0) - origine
        c += f",adelay={int(round(deb * 1000))}|{int(round(deb * 1000))}[{tag}]"
        chaines.append(c); noms.append(f"[{tag}]")
    for n, (k, t0, t1, gain) in enumerate(remplacements):
        inf = disque_cale(k); d = disques[k]
        g_db = (d.plateau + d.gain_db - niveau_original(inf, t0, t1)) if gain is None else gain
        print(f" original de {inf['original'][:40]} pose sur le disque {k} de {t0:.0f} a {t1:.0f} s ({g_db:+.1f} dB)")
        chaine_original(k, inf, t0 - 1.0, t1 + 1.0, 1000, 1000, g_db, f"o{n}", eq=couleur(inf, d, t0))
    for n, (k, t1, fondu, gain_rel) in enumerate(prolongations):
        inf = disque_cale(k); d = disques[k]
        t0 = d.fin - 1.0
        # le niveau se compare sur la fin du disque tel qu'il a joue, pas sur la queue qu'on ajoute
        # (elle peut etre presque silencieuse : +24 dB de gain mesures sur trois secondes d'outro)
        g_db = d.plateau + d.gain_db - niveau_original(inf, max(d.debut, t0 - 30.0), t0) + gain_rel
        print(f" original de {inf['original'][:40]} prolonge le disque {k} de {t0:.0f} a {t1:.0f} s, fondu {fondu:.0f} s ({g_db:+.1f} dB)")
        chaine_original(k, inf, t0, t1, 1000, int(fondu * 1000), g_db, f"p{n}", eq=couleur(inf, d, t0))
    chaines.append(f"{''.join(noms)}amix=inputs={len(noms)}:normalize=0:duration=longest[out]")
    # --vers=<fichier> : ecrire ailleurs, pour un extrait a faire ecouter (une transition avec un
    # disque avance de 40 ms, par exemple) sans toucher au master reconstruit de la prise
    sortie = Path(args["--vers"]) if "--vers" in args else STUDIO / prise / f"{prise}-v2.wav"
    # EN FLOTTANTS : deux disques remontes qui se recouvrent depassent 0 dBFS, et un fichier
    # intermediaire en 24 bits ecreterait la ou monter.py n'a pas encore pose son gain final.
    cmd = ["ffmpeg", "-v", "error", "-y", "-i", str(wav)] + entrees_sup + ["-filter_complex", ";".join(chaines), "-map", "[out]",
           "-c:a", "pcm_f32le", "-ar", "48000", str(sortie)]
    subprocess.run(cmd, check=True)
    d = float(subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(sortie)],
                             capture_output=True, text=True).stdout)
    print(f"\n→ {sortie} : {d / 60:.1f} min, commence a {origine:.2f} s de la prise")
    if "--vers" in args: return 0
    (STUDIO / prise / f"{prise}-v2.json").write_text(json.dumps({
        "prise": prise, "debut": debut, "fin": fin, "cible": cible, "origine": origine,
        "disques": [{"voie": dd.voie, "debut": dd.debut, "fin": dd.fin, "plateau": dd.plateau, "gain_db": dd.gain_db,
                     "decalage_s": dd.decalage, "vitesse": dd.vitesse, "ancre": dd.ancre,
                     "mesure": dd.ecarts} for dd in disques],
        "garder": garder, "sorties": {str(k): list(v) for k, v in sorties.items()}}, indent=1, ensure_ascii=False))
    return 0

if __name__ == "__main__":
    sys.exit(main())
