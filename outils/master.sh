#!/usr/bin/env bash
# Le master du set, en stereo 48 kHz, tire de l'enregistrement multipiste de la table.
#
#   ./outils/master.sh <session>              la paire du Rec Out (STUDIO_MASTER, 5 par defaut)
#   ./outils/master.sh <session> 1            une autre paire (la voie 1 avant fader, par exemple)
#
# LE WAV DE LA SESSION EST UNE MESURE, PAS UN MIX : dix canaux, toutes les paires de la
# table. Ce script en sort une paire, telle quelle — pas de normalisation, pas de limiteur,
# pas de reechantillonnage : 24 bits, 48 kHz, ce que la table a envoye. C'est le second
# master, au cas ou celui du studio a un souci ; c'est aussi ce que run.sh fichier rejoue.
set -euo pipefail
cd "$(dirname "$0")/.."
CACHE="${EMOTION_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator}"
SESSION="${1:?session}"; PAIRE="${2:-${STUDIO_MASTER:-5}}"
[[ "$PAIRE" =~ ^[1-9]$ ]] || { echo "paire : 1 a 9, pas « $PAIRE »" >&2; exit 2; }
DIR="$CACHE/studio/$SESSION"; ENTREE="$DIR/$SESSION-table.wav"
[[ -f "$ENTREE" ]] || { echo "pas de $ENTREE" >&2; exit 1; }
NCH=$(ffprobe -v error -select_streams a:0 -show_entries stream=channels -of csv=p=0 "$ENTREE")
G=$(( 2*PAIRE - 2 )); D=$(( 2*PAIRE - 1 ))
(( D < NCH )) || { echo "le WAV n'a que $NCH canaux, pas de paire $PAIRE" >&2; exit 1; }
SORTIE="$DIR/$SESSION-master${PAIRE:+-paire$PAIRE}.wav"; [[ "$PAIRE" == "${STUDIO_MASTER:-5}" ]] && SORTIE="$DIR/$SESSION-master.wav"
ffmpeg -v error -y -i "$ENTREE" -filter_complex "pan=stereo|c0=c$G|c1=c$D" -c:a pcm_s24le -ar 48000 "$SORTIE"
ffprobe -v error -select_streams a:0 -show_entries stream=channels,sample_rate,sample_fmt:format=duration -of default=nw=1 "$SORTIE" | tr '\n' ' '; echo
echo "→ $SORTIE"
