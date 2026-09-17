#!/usr/bin/env bash
# Un fondu enregistre, rejoue dans la fenetre avec son melange — sans table, sans serveur.
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

# LE SON AVEC L'IMAGE. Le rejeu n'ecrit que des paquets ; le melange que la sonde a analyse
# est a cote (relais.py l'ecrit en .wav). On le joue a l'instant ou la sonde commence a
# ecrire — elle imprime « N paquets, … » juste avant de lancer son chronometre, c'est le
# signal. Au ralenti, pas de son : un lecteur ne ralentit pas sans changer la hauteur.
WAV="$CACHE/relais/$A--$B.wav"
SON=""
if [[ "$VITESSE" != "1" ]]; then
  echo "au ralenti, pas de son (le melange est dans $WAV)"
elif [[ ! -f "$WAV" ]]; then
  echo "pas de melange a jouer ($WAV absent) : image seule"
fi
SON_PID="$(mktemp)"
{
  dotnet run -c Release --no-build --project tools/Emotion.Probe -- rejoue "$PAK" "$VITESSE" | while IFS= read -r ligne; do
    echo "$ligne"
    if [[ "$ligne" == *paquets* && "$VITESSE" == "1" && -f "$WAV" ]]; then
      paplay "$WAV" & echo $! > "$SON_PID"
    fi
  done
} &
REJEU=$!
trap 'kill $REJEU 2>/dev/null; pkill -f "Emotion.Probe.*rejoue" 2>/dev/null; [[ -s "$SON_PID" ]] && kill "$(cat "$SON_PID")" 2>/dev/null; rm -f "$SON_PID"' EXIT INT TERM
sleep 1
python3 outils/fenetre.py "$A → $B  (fondu rejoue)" 2>&1 | tee -a "$CACHE/fenetre.log"
