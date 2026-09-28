#!/usr/bin/env bash
# Essai de captation a la maison : Mixxx + DDJ-FLX4, deux ou trois morceaux.
#
#   ./outils/test-flx4.sh [nom]        (par defaut test-flx4-HHMM)
#
# Verifie que la table est la et que Mixxx sort dessus, lance la session (STUDIO_TABLE=flx4 :
# master et casque en direct, 4 canaux enregistres), et a l'arret (Q dans la fenetre ou Ctrl-C)
# imprime le bilan : niveaux, passages MASTER CUE, BPM capte contre le BPM de Mixxx.
# Condition : HEADPHONES MIX a fond cote CUE ; le master au casque passe par MASTER CUE.
set -u
cd "$(dirname "$0")/.."
SESSION="${1:-test-flx4-$(date +%H%M)}"

# STUDIO_SOURCE donne a la main (essai sur un faux peripherique) : pas de verification de Mixxx.
if [[ -z "${STUDIO_SOURCE:-}" ]]; then
PUITS=$(pactl list sinks short 2>/dev/null | awk '{print $2}' | grep -i "DDJ-FLX4" | head -1)
[[ -n "$PUITS" ]] || { echo "la DDJ-FLX4 n'est pas vue par PulseAudio : branche-la, puis relance" >&2; exit 1; }
ID_PUITS=$(pactl list sinks short | awk -v n="$PUITS" '$2 == n {print $1}')
if ! pactl list sink-inputs | grep -B20 'application.name = "ALSA plug-in \[mixxx\]"' | grep -q "Sink: $ID_PUITS$"; then
  echo "Mixxx ne sort pas sur la FLX4 (pas lance, ou pas depuis le menu). Lance-le, charge un morceau, relance ce test." >&2
  exit 1
fi
fi
echo "FLX4 vue, Mixxx sort dessus. Session : $SESSION"
echo "Joue deux ou trois morceaux avec au moins une transition, un passage au casque et un MASTER CUE."
echo

DEBUT=$(date +%s)
trap ':' INT   # Ctrl-C arrete la session, pas ce script : le bilan suit
STUDIO_TABLE=flx4 ./outils/studio.sh "$SESSION"
trap - INT

python3 outils/bilan_flx4.py "$SESSION" "$DEBUT"
