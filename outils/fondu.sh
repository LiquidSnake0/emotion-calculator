#!/usr/bin/env bash
# Un fondu enregistre, rejoue dans la fenetre — sans table, sans serveur, sans carte son.
#
#   ./outils/fondu.sh 05_Dead_Internet_Theory_ 04_Glyph_Chamber_
#   ./outils/fondu.sh 05_Dead_Internet_Theory_ 04_Glyph_Chamber_ 0.5    # au ralenti
#
# LE DJ N'A PAS DE TABLE, ET LE RENDU DOIT POURTANT SE CONSTRUIRE SUR UN VRAI FONDU. La
# sonde sait fabriquer le melange de deux disques du bac et le passer par le relais (voir
# outils/relais.py) ; elle enregistre alors chaque paquet tel que le rendu le recevrait.
# Ici on les rejoue dans l'anneau partage, a leur cadence, et la fenetre les lit comme si
# le moteur tournait. Quarante secondes de A, vingt de fondu, quarante de B : les cases
# P1 s'eteignent pendant que les cases P2 s'allument, et rien ne saute de cote.
#
# Ctrl-C arrete tout.
set -uo pipefail
cd "$(dirname "$0")/.."

A="${1:?disque A (nom de fichier du bac sans .wav)}"
B="${2:?disque B}"
VITESSE="${3:-1}"
CACHE="${EMOTION_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator}"
PAK="$CACHE/relais/$A--$B.pak"

if [[ ! -f "$PAK" ]]; then
  echo "pas de fondu enregistre pour $A → $B : on le fabrique (une minute)"
  python3 outils/relais.py "$A" "$B" || exit 1
fi
[[ -f "$PAK" ]] || { echo "toujours pas de $PAK" >&2; exit 1; }

# Aucun serveur ne doit ecrire dans l'anneau en meme temps.
for pid in $(pgrep -f Emotion.Server); do kill "$pid" 2>/dev/null; done

dotnet run -c Release --no-build --project tools/Emotion.Probe -- rejoue "$PAK" "$VITESSE" &
REJEU=$!
trap 'kill $REJEU 2>/dev/null' EXIT INT TERM
sleep 1
python3 outils/fenetre.py "$A → $B  (fondu rejoue)" 2>&1 | tee -a "$CACHE/fenetre.log"
