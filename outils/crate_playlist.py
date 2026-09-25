#!/usr/bin/env python3
# La playlist de la clé, prête à entrer dans le crate.
#
#   python3 outils/crate_playlist.py sortie.json "ti faccio" "glyph chamber" ...
#   python3 outils/crate_playlist.py sortie.json @liste.txt        (un morceau par ligne)
#
# LE CRATE EST LA BASE DU BAC VINYLE : les morceaux de la clé n'y sont pas, et l'écran
# « live » n'a alors rien à proposer. Ce script prend l'index rekordbox de la clé
# (fiches_rekordbox.py) et écrit le JSON que l'écran Bibliothèque du crate importe
# (`importJson` : les morceaux s'ajoutent, rien n'est écrasé chez lui). Artiste, album,
# titre viennent du chemin du fichier ; le BPM de la grille rekordbox ; `anchorBpm` = le
# BPM natif, à corriger dans le crate s'il le joue ailleurs ; la famille reste à lui —
# absente, l'axe couleur vaut 0,5 pour tout le monde, ni favorisé ni exclu.
# La clé (Camelot) n'est là que si export.pdb a pu être lu (rekordbox-pdb).
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
    for a in sys.argv[2:]:
        voulus += [l.strip() for l in open(a[1:], encoding="utf-8") if l.strip()] if a.startswith("@") else [a]
    cache = Path(os.environ.get("EMOTION_CACHE_DIR", Path.home() / ".cache" / "emotion-emulator")) / "rekordbox.json"
    fiches = json.loads(cache.read_text())
    tracks, manquants = [], []
    for v in voulus:
        mots = v.lower().split()
        trouves = [f for f in fiches if all(m in f["chemin"].lower() for m in mots)]
        if len(trouves) != 1:
            manquants.append((v, len(trouves))); continue
        f = trouves[0]; artiste, album, numero, titre = decouper(f["chemin"])
        tracks.append({
            "id": slug(artiste, album, numero, titre),
            "artist": artiste, "album": album, "title": titre, "trackNumber": numero,
            "key": f.get("camelot"), "bpm": f["bpm"], "anchorBpm": f["bpm"],
            "side": None, "family": None, "durationSec": None, "artId": None, "bcUrl": None,
            "plIndex": None, "legacyTag": None, "notes": "clé usb, studio 26.09.2026", "audioId": None,
        })
        print(f"  {f['bpm']:7.2f}  {artiste} — {titre}")
    sortie.write_text(json.dumps({"tracks": tracks, "judgements": []}, ensure_ascii=False, indent=1))
    print(f"{len(tracks)} morceaux → {sortie}")
    for v, n in manquants:
        print(f"  ? « {v} » : {n} correspondance(s), à préciser", file=sys.stderr)
    return 1 if manquants else 0

if __name__ == "__main__": sys.exit(main())
