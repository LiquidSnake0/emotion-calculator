#!/usr/bin/env bash
# La fiche du disque qu'on pose au casque, poussee au moteur — ce que le crate fera un jour.
#
#   ./outils/cue.sh <session> <bpm> [camelot] [titre]      le disque cale au casque
#   ./outils/cue.sh <session> take                         il vient de passer en salle
#
# LE PLAN B DU CRATE. Le crate envoie lui-meme la fiche depuis ses ecrans Set et Live ; si
# le telephone ne joint pas le moteur, elle part d'ici, a la main, au moment ou le disque
# est pose. Le moteur sait alors QUEL disque arrive (tempo, gamme) ; sur QUELLE voie, c'est
# le cue alternant qui le sait.
# Chaque commande est aussi notee dans les notes de la session, a l'heure de la machine :
# la fiche fait partie de la verite terrain.
set -uo pipefail
cd "$(dirname "$0")/.."
CACHE="${EMOTION_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator}"
PORT=5099
SESSION="${1:?session}"; shift
FICHIER="$CACHE/studio/$SESSION/$SESSION-terrain.tsv"
mkdir -p "$(dirname "$FICHIER")"
[[ -s "$FICHIER" ]] || printf 'heure\tepoch_ms\tnote\n' > "$FICHIER"
noter() { printf '%s\t%s\t%s\n' "$(date +%H:%M:%S.%3N)" "$(date +%s%3N)" "$1" >> "$FICHIER"; }

if [[ "${1:-}" == "take" ]]; then
  curl -s -X POST "localhost:$PORT/deck/take" -o /dev/null -w "take → %{http_code}\n"
  noter "take"
  exit 0
fi

BPM="${1:?bpm}"; BPM="${BPM/,/.}"; CLE="${2:-}"; TITRE="${3:-disque}"
JSON=$(printf '{"title":"%s","disc":"cle usb","side":"","camelot":"%s","family":"","colorHex":"#6E6E6E","bpm":%s}' \
       "${TITRE//\"/}" "$CLE" "$BPM")
curl -s -X POST "localhost:$PORT/deck/cue" -H 'Content-Type: application/json' -d "$JSON" \
     -o /dev/null -w "cue $TITRE $BPM BPM${CLE:+ $CLE} → %{http_code}\n"
noter "cue $TITRE $BPM${CLE:+ $CLE}"
