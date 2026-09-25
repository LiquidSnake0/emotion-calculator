#!/usr/bin/env python3
# Les fiches d'une clé rekordbox, lues dans ses fichiers d'analyse.
#
#   .venv-rekordbox/bin/python outils/fiches_rekordbox.py /run/media/swave/BF2C-F971            # indexe la clé
#   .venv-rekordbox/bin/python outils/fiches_rekordbox.py /run/media/swave/BF2C-F971 "ti faccio" "glyph"   # cherche
#
# LE DJ JOUE DEMAIN SUR CLÉ USB, PAS SUR VINYLE : ces morceaux ne sont pas dans le crate.
# Mais rekordbox les a déjà analysés, et sa grille de temps (PQTZ) est la meilleure vérité
# terrain qu'on aura jamais : le tempo battement par battement, et l'instant de chaque
# temps, au millième. C'est contre elle qu'on jugera ce que le moteur entend.
#
# Le chemin du fichier est dans PPTH ; le titre et la tonalité (Camelot) viennent
# d'export.pdb, lu par outils/rekordbox_pdb.py et joint sur le chemin.
# Écrit ~/.cache/emotion-emulator/rekordbox.json : un index, pas une œuvre.
import json, os, sys, statistics
from pathlib import Path

def lire(cle: Path):
    sys.path.insert(0, str(Path(__file__).parent))
    from rekordbox_pdb import lire as lire_pdb
    pdb = {}
    try:
        pistes, _ = lire_pdb(str(cle / "PIONEER" / "rekordbox" / "export.pdb"))
        pdb = {p["chemin"]: p for p in pistes}
    except (OSError, KeyError, ValueError) as e:
        print(f"export.pdb illisible ({e}) : sans titres ni tonalités", file=sys.stderr)
    # Importee ici et pas en tete : pyrekordbox vit dans .venv-rekordbox, et le module doit
    # s'importer sans elle (le controle de fumee importe chaque outil avec le python du systeme).
    try:
        from pyrekordbox import AnlzFile
    except ImportError:
        sys.exit("pyrekordbox manque : lancer avec .venv-rekordbox/bin/python")
    fiches = []
    for dat in (cle / "PIONEER" / "USBANLZ").rglob("ANLZ0000.DAT"):
        try:
            a = AnlzFile.parse_file(str(dat))
            chemin = a.get_tag("PPTH").content.path
            grille = a.get_tag("PQTZ").content.entries
        except Exception as e:                       # un fichier d'analyse abîmé n'arrête pas l'index
            print(f"  {dat.parent.name} : {e}", file=sys.stderr); continue
        if not grille: continue
        tempos = [e.tempo / 100.0 for e in grille]
        fiche = pdb.get(chemin, {})
        fiches.append({
            "chemin": chemin,
            "fichier": Path(chemin).name,
            "titre": fiche.get("titre"),
            "bpm": round(statistics.median(tempos), 2),
            "bpm_min": min(tempos), "bpm_max": max(tempos),
            "premier_temps_ms": grille[0].time,
            "temps": len(grille),
            "camelot": fiche.get("cle"),
            "analyse": str(dat.relative_to(cle)),
        })
    return fiches

def main():
    if len(sys.argv) < 2: print(__doc__ or "usage : fiches_rekordbox.py <racine de la clé> [mots...]"); return 2
    cle = Path(sys.argv[1]); mots = [m.lower() for m in sys.argv[2:]]
    cache = Path(os.environ.get("EMOTION_CACHE_DIR", Path.home() / ".cache" / "emotion-emulator")) / "rekordbox.json"
    if cache.exists() and mots:
        fiches = json.loads(cache.read_text())
    else:
        fiches = lire(cle)
        cache.parent.mkdir(parents=True, exist_ok=True)
        cache.write_text(json.dumps(fiches, ensure_ascii=False, indent=1))
        print(f"{len(fiches)} morceaux indexés → {cache}")
    if mots:
        for f in fiches:
            if all(m in (f["chemin"] + " " + (f.get("titre") or "")).lower() for m in mots):
                print(f"{f['bpm']:7.2f} BPM  {f.get('camelot') or '--':>3}  ({f['bpm_min']:.2f}–{f['bpm_max']:.2f}, {f['temps']} temps)  {f.get('titre') or ''}  ·  {f['chemin']}")
    return 0

if __name__ == "__main__": sys.exit(main())
