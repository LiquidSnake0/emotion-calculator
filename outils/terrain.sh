#!/usr/bin/env bash
# Les notes du DJ pendant la session, a l'heure de la machine.
#
#   ./outils/terrain.sh <session>
#   > 1 lance ti faccio ballare        (Entree : la ligne part avec l'heure)
#   > fondu vers 2
#   > pitch +3
#
# LA VERITE TERRAIN, C'EST LUI. Le moteur, le WAV et le .pak ont tous l'horloge de la
# machine ; une ligne tapee ici au moment du geste vaut plus que tout ce qu'on devinera
# apres coup. Une note par geste : disque lance, fondu commence, fondu fini, pitch bouge.
set -uo pipefail
cd "$(dirname "$0")/.."
CACHE="${EMOTION_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator}"
# LES SESSIONS NE VONT PAS DANS LE CACHE : un set enregistre est une oeuvre et une mesure,
# pas un fichier qu'on peut regenerer. Elles vivent hors depot, a cote des pistes.
STUDIO="${EMOTION_STUDIO_DIR:-$HOME/Documents/emotion-sources/studio}"
SESSION="${1:?session}"
FICHIER="$STUDIO/$SESSION/$SESSION-terrain.tsv"
mkdir -p "$(dirname "$FICHIER")"
[[ -s "$FICHIER" ]] || printf 'heure\tepoch_ms\tnote\n' > "$FICHIER"
echo "notes → $FICHIER   (Ctrl-D ou Ctrl-C pour finir)"
while IFS= read -r -p "> " note; do
  [[ -z "$note" ]] && continue
  printf '%s\t%s\t%s\n' "$(date +%H:%M:%S.%3N)" "$(date +%s%3N)" "$note" >> "$FICHIER"
done
