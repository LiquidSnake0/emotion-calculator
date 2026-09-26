#!/usr/bin/env bash
# Rejoue dans la fenetre ce que le rendu a recu pendant une session : le .pak, tel quel.
#
#   ./outils/rejouer_set.sh set-studio            depuis le debut
#   ./outils/rejouer_set.sh set-studio 1200       depuis la vingtieme minute
#   ./outils/rejouer_set.sh set-studio 1200 2     au double de la vitesse
#
# Aucun moteur ne tourne : la sonde ecrit les images enregistrees dans l'anneau a leur
# cadence, la fenetre les lit comme si le moteur etait la. C'est exactement ce que e-r
# recevra. Ctrl-C arrete tout.
set -uo pipefail
cd "$(dirname "$0")/.."
CACHE="${EMOTION_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator}"
# LES SESSIONS NE VONT PAS DANS LE CACHE : un set enregistre est une oeuvre et une mesure,
# pas un fichier qu'on peut regenerer. Elles vivent hors depot, a cote des pistes.
STUDIO="${EMOTION_STUDIO_DIR:-$HOME/Documents/emotion-sources/studio}"
SESSION="${1:?session}"; DEPUIS="${2:-0}"; VITESSE="${3:-1}"
PAK="$STUDIO/$SESSION/$SESSION.pak"
[[ -f "$PAK" ]] || { echo "pas de $PAK" >&2; exit 1; }
for pid in $(ss -lptnH "sport = :5099" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
  [[ "$(ps -p "$pid" -o comm= 2>/dev/null)" == *Emotion.Server* ]] && kill "$pid" 2>/dev/null
done
# Un extrait a partir de DEPUIS secondes : 256 octets par image, 47 images par seconde.
EXTRAIT="$PAK"
if [[ "$DEPUIS" != "0" ]]; then
  EXTRAIT="${TMPDIR:-/tmp}/$SESSION-depuis-$DEPUIS.pak"
  tail -c +$(( DEPUIS * 47 * 256 + 1 )) "$PAK" > "$EXTRAIT"
fi
echo "$(( $(stat -c %s "$EXTRAIT") / 256 )) images a rejouer depuis ${DEPUIS} s"
dotnet build -c Release -nologo -v q tools/Emotion.Probe > /dev/null 2>&1
dotnet tools/Emotion.Probe/bin/Release/net*/Emotion.Probe.dll rejoue "$EXTRAIT" "$VITESSE" > /dev/null &
REJEU=$!
trap 'kill $REJEU 2>/dev/null' EXIT INT TERM
sleep 1.5
python3 outils/fenetre.py "$SESSION (rejeu)"
