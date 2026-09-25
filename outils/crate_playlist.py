#!/usr/bin/env python3
# La playlist de la clé, prête à entrer dans le crate.
#
#   python3 outils/crate_playlist.py sortie.json "ti faccio" "glyph chamber" ...
#   python3 outils/crate_playlist.py sortie.json @liste.txt        (un morceau par ligne)
#   SET="Mix 2" ANCHOR=92 python3 outils/crate_playlist.py ...      (nom du set, tempo vise)
#
# LE CRATE EST LA BASE DU BAC VINYLE : les morceaux de la clé n'y sont pas, et l'écran
# « live » n'a alors rien à proposer. Ce script prend l'index rekordbox de la clé
# (fiches_rekordbox.py) et écrit le JSON que l'écran Bibliothèque du crate importe
# (`importJson` : les morceaux s'ajoutent, rien n'est écrasé chez lui). Artiste, album,
# titre viennent du chemin du fichier ; le BPM de la grille rekordbox ; `anchorBpm` = le
# BPM natif, à corriger dans le crate s'il le joue ailleurs ; la famille reste à lui —
# absente, l'axe couleur vaut 0,5 pour tout le monde, ni favorisé ni exclu.
# Le titre et la clé (Camelot) viennent d'export.pdb (outils/rekordbox_pdb.py).
# `anchorBpm` : le BPM auquel il compte le jouer (ANCHOR=92 par défaut) ; c'est de lui que
# le crate déduit la clé transposée (deriveTag : un demi-ton = sept crans sur la roue).
# L'onglet Set du crate reconnaît un set par ses notes, qui commencent par son nom (SET=),
# et le joue dans l'ordre de `plIndex` : le rang dans la liste donnée ici.
import json, os, re, sys
from pathlib import Path

def decouper(chemin: str):
    # « /Contents/Artiste/Album/Artiste - Album - 02 Titre.aiff » : le dossier fait foi,
    # le nom du fichier donne le numéro et le titre.
    p = Path(chemin)
    artiste = p.parts[2] if len(p.parts) > 3 else ""
    album = p.parts[3] if len(p.parts) > 4 else ""
    base = p.stem
    m = re.search(r"(\d{1,2})\s+(.+)$", base)
    numero, titre = (int(m.group(1)), m.group(2)) if m else (None, base)
    if not artiste and " - " in base: artiste = base.split(" - ")[0]
    return artiste, album, numero, titre

def slug(*parts):
    # La même fonction que le crate (src/db/db.ts) : artiste|album|numéro|titre, minuscules,
    # espaces repliés. Un identifiant différent ferait un doublon au ré-import.
    return re.sub(r"\s+", " ", "|".join(str(p if p is not None else "").strip().lower() for p in parts))

def main():
    if len(sys.argv) < 3: print(__doc__); return 2
    sortie = Path(sys.argv[1]); voulus = []
    nom_set = os.environ.get("SET", "Mix 2")
    for a in sys.argv[2:]:
        voulus += [l.strip() for l in open(a[1:], encoding="utf-8") if l.strip()] if a.startswith("@") else [a]
    cache = Path(os.environ.get("EMOTION_CACHE_DIR", Path.home() / ".cache" / "emotion-emulator")) / "rekordbox.json"
    fiches = json.loads(cache.read_text())
    tracks, manquants = [], []
    for rang, v in enumerate(voulus, 1):
        mots = v.lower().split()
        trouves = [f for f in fiches if all(m in (f["chemin"] + " " + (f.get("titre") or "")).lower() for m in mots)]
        if len(trouves) != 1:
            manquants.append((v, len(trouves))); continue
        f = trouves[0]; artiste, album, numero, titre = decouper(f["chemin"])
        titre = f.get("titre") or titre
        ancre = float(os.environ.get("ANCHOR", "92"))
        tracks.append({
            "id": slug(artiste, album, rang, titre),
            "artist": artiste, "album": album, "title": titre, "trackNumber": rang,
            "key": f.get("camelot"), "bpm": f["bpm"], "anchorBpm": ancre,
            "side": None, "family": None, "durationSec": None, "artId": None, "bcUrl": None,
            "plIndex": rang, "legacyTag": None, "notes": f"{nom_set} · clé usb", "audioId": None,
        })
        print(f"  {f['bpm']:7.2f} {f.get('camelot') or '--':>3}  {artiste} — {titre}")
    sortie.write_text(json.dumps({"tracks": tracks, "judgements": []}, ensure_ascii=False, indent=1))
    print(f"{len(tracks)} morceaux → {sortie}")
    for v, n in manquants:
        print(f"  ? « {v} » : {n} correspondance(s), à préciser", file=sys.stderr)
    return 1 if manquants else 0

if __name__ == "__main__": sys.exit(main())
