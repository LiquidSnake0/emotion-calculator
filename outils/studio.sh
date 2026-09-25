#!/usr/bin/env bash
# Une session au studio : la table sur USB, le moteur en direct, et tout ce qu'il faut
# ramener a la maison.
#
#   ./outils/studio.sh                          ce que la machine voit (table, paires, options)
#   ./outils/studio.sh --verif                  3 s par paire : qui porte du signal
#   ./outils/studio.sh <session> [bpm] [camelot]   la session : enregistre, analyse, montre
#
# LA TABLE EST UNE DJM-750MK2, ET LE NOYAU LA CONNAIT (quirk depuis Linux 5.14) : cinq paires
# stereo remontent par USB, choisies depuis l'ordinateur par des commutateurs ALSA. Paire k
# ne peut porter que la voie k (son entree telle quelle — « Control Tone » — ou apres le
# fader) ; la cinquieme porte le Rec Out, un cote du crossfader, ou une voie apres fader.
# Le casque, lui, ne passe pas par l'USB : le cue du moteur est donc l'entree brute d'une
# voie, pas ce que le DJ entend. C'est dit, et c'est la meilleure approximation qu'on ait.
#
# Ce qu'on ramene, dans $CACHE/studio/<session>/ :
#   <session>-table.wav    toutes les paires, 24 bits 48 kHz, telles que la table les donne
#   <session>.pak          chaque image que le moteur a ecrite dans l'anneau (probe enregistre)
#   <session>-moteur.log   le journal du serveur
#   <session>-deck.jsonl   ce que le crate a dit au moteur (cue, take), a l'heure des images
#   <session>-terrain.tsv  les notes du DJ (outils/terrain.sh), a l'heure de la machine
# Le WAV rejoue le moteur a volonte (run.sh fichier), le .pak rejoue le rendu (probe rejoue).
#
# Ctrl-C arrete tout, dans l'ordre : fenetre, moteur, enregistreurs.
set -uo pipefail
cd "$(dirname "$0")/.."

CACHE="${EMOTION_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator}"
PORT=5099
# Quelle paire est le master, quelle paire porte le cue au depart (1 a 5). Le DJ joue sur
# les voies 1 et 2 : le premier disque joue sur la 1, le premier prepare est sur la 2. Le cue
# passe ensuite sur l'autre voie a chaque relais (CueAlternant) ; si le sens de depart est
# faux, le filet permute en trois secondes. STUDIO_CUE=1 si le set commence sur la voie 2.
MASTER_PAIRE="${STUDIO_MASTER:-5}"
CUE_PAIRE="${STUDIO_CUE:-2}"
AUTRE_VOIE=$(( CUE_PAIRE == 1 ? 2 : 1 ))

# --- la table ------------------------------------------------------------------------------

# La carte ALSA et la source PulseAudio de la table. La source multicanal expose une carte de
# canaux (« aux0,aux1,… ») : c'est la qu'on lit les noms des canaux de chaque paire.
CARTE=$(aplay -l 2>/dev/null | grep -i "DJM" | head -1 | sed -E 's/^card ([0-9]+).*/\1/')
SOURCE="${STUDIO_SOURCE:-}"      # STUDIO_SOURCE=<source> pour repeter chez soi sur une autre entree
if [[ -z "$SOURCE" ]]; then
  SOURCE=$(pactl list sources short 2>/dev/null | awk '{print $2}' | grep -i "djm" | grep -v monitor | head -1)
fi
if [[ -z "$SOURCE" ]]; then
  # Sans le nom, on prend la premiere source USB qui n'est pas un monitor.
  SOURCE=$(pactl list sources short 2>/dev/null | awk '{print $2}' | grep -i "usb" | grep -v monitor | head -1)
fi

canaux_de_la_source() {
  pactl list sources 2>/dev/null | awk -v s="$SOURCE" '
    $1=="Name:" { trouve = ($2==s) }
    trouve && $1=="Channel" && $2=="Map:" { print $3; exit }'
}

commutateurs() {
  [[ -n "$CARTE" ]] || return 0
  for k in 1 2 3 4 5; do
    amixer -c "$CARTE" sget "Input $k Capture Switch" 2>/dev/null | awk -v k="$k" '
      /Items:/ { sub(/.*Items: /, ""); items=$0 }
      /Item0:/ { sub(/.*Item0: /, ""); print "  paire " k " : " $0 "     (choix : " items ")" }'
  done
}

# Choisit l'option d'un commutateur par son nom exact, tel qu'amixer le liste.
regler() { amixer -c "$CARTE" -q sset "Input $1 Capture Switch" "$2" 2>/dev/null; }

# Ce que la paire k doit porter : sa voie telle qu'elle entre, sauf la cinquieme, le Rec Out.
# Pour la voie, trois entrees possibles (LINE, CD/LINE, DIGITAL) selon ce qui est branche :
# on n'en sait rien d'avance, --verif ecoute chacune et garde celle qui porte du son.

# La source stereo de la paire k, taillee dans la source multicanal.
remap() {
  local k="$1" nom="djm_$1" carte cm a b
  carte=$(canaux_de_la_source); IFS=',' read -r -a cm <<< "$carte"
  a="${cm[$((2*k-2))]:-}"; b="${cm[$((2*k-1))]:-}"
  [[ -n "$a" && -n "$b" ]] || { echo "la source n'a pas de paire $k (canaux : $carte)" >&2; return 1; }
  pactl load-module module-remap-source source_name="$nom" master="$SOURCE" channels=2 \
        channel_map=front-left,front-right master_channel_map="$a,$b" remix=no > /dev/null 2>&1 \
    || pactl load-module module-remap-source source_name="$nom" master="$SOURCE" channels=2 \
        channel_map=front-left,front-right master_channel_map="$a,$b" > /dev/null
}
retirer_remaps() {
  for id in $(pactl list modules short 2>/dev/null | grep "module-remap-source" | grep "source_name=djm_" | cut -f1); do
    pactl unload-module "$id" 2>/dev/null
  done
}

# Le niveau d'une source pendant n secondes, en dB pleine echelle (mono, 48 kHz).
niveau() {
  local dev="$1" s="${2:-3}"
  timeout "$((s+1))" parec --device="$dev" --format=s16le --rate=48000 --channels=1 --raw 2>/dev/null \
    | python3 -c '
import sys, array, math
b = sys.stdin.buffer.read(); a = array.array("h", b[:len(b)//2*2])
if not a: print("rien"); sys.exit()
rms = math.sqrt(sum(x*x for x in a)/len(a)) / 32768
print(f"{20*math.log10(max(rms,1e-6)):6.1f} dBFS sur {len(a)/48000:.1f} s")'
}

etat() {
  echo "carte ALSA : ${CARTE:-aucune DJM vue}     source PulseAudio : ${SOURCE:-aucune}"
  [[ -n "$SOURCE" ]] && echo "canaux : $(canaux_de_la_source)"
  commutateurs
  echo "master = paire $MASTER_PAIRE, cue = paire $CUE_PAIRE (STUDIO_MASTER / STUDIO_CUE pour changer)"
}

if [[ $# -eq 0 ]]; then etat; exit 0; fi

if [[ "$1" == "--verif" ]]; then
  etat
  [[ -n "$SOURCE" ]] || exit 1
  retirer_remaps
  for k in 1 2 3 4 5; do remap "$k" || true; done
  if [[ -n "$CARTE" ]]; then
    # Pour chaque voie, les trois entrees possibles ; on garde la plus forte.
    for k in 1 2 3 4; do
      meilleur=""; fort=-999
      while IFS= read -r opt; do
        [[ "$opt" == Control\ Tone* ]] || continue
        regler "$k" "$opt"; sleep 0.3
        n=$(niveau "djm_$k" 2); db=$(echo "$n" | awk '{print $1}')
        echo "  paire $k, $opt : $n"
        if [[ "$db" != "rien" ]] && awk -v a="$db" -v b="$fort" 'BEGIN { exit !(a > b) }'; then fort="$db"; meilleur="$opt"; fi
      done < <(amixer -c "$CARTE" sget "Input $k Capture Switch" | awk '/Items:/ { sub(/.*Items: /, ""); gsub(/'"'"' '"'"'/, "\n"); gsub(/'"'"'/, ""); print }')
      [[ -n "$meilleur" ]] && { regler "$k" "$meilleur"; echo "  paire $k → $meilleur"; }
    done
    regler 5 "Rec Out"
  fi
  echo "master (paire $MASTER_PAIRE) : $(niveau "djm_$MASTER_PAIRE" 3)"
  echo "cue    (paire $CUE_PAIRE) : $(niveau "djm_$CUE_PAIRE" 3)"
  echo "les sources djm_1..5 restent chargees ; ./outils/studio.sh <session> pour lancer"
  exit 0
fi

# --- la session ------------------------------------------------------------------------------

SESSION="$1"; BPM="${2:-}"; CLE="${3:-}"
[[ -n "$SOURCE" ]] || { echo "aucune table vue par PulseAudio : brancher l'USB, puis ./outils/studio.sh" >&2; exit 1; }
DIR="$CACHE/studio/$SESSION"; mkdir -p "$DIR"
for pid in $(ss -lptnH "sport = :$PORT" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
  [[ "$(ps -p "$pid" -o comm= 2>/dev/null)" == *Emotion.Server* ]] && kill "$pid" 2>/dev/null
done
pkill -f "Emotion.Probe.*(rejoue|enregistre)" 2>/dev/null

# Les paires, si --verif ne les a pas deja posees.
pactl list sources short | grep -q "djm_$MASTER_PAIRE" || { retirer_remaps; for k in 1 2 3 4 5; do remap "$k" 2>/dev/null || true; done; }
pactl list sources short | grep -q "djm_$MASTER_PAIRE" || { echo "pas de paire $MASTER_PAIRE sur cette source" >&2; exit 1; }
[[ -n "$CARTE" ]] && ! [[ "${STUDIO_GARDER_COMMUTATEURS:-}" ]] && regler 5 "Rec Out"

NCH=$(canaux_de_la_source | tr ',' '\n' | grep -c .)
IP=$(hostname -I | awk '{print $1}')
echo "session $SESSION → $DIR"
echo "crate sur le telephone : http://$IP:5173/crate/ (npm run dev -- --host 0.0.0.0 dans ~/Documents/crate) · moteur = http://$IP:$PORT"
echo "table : $SOURCE, $NCH canaux · master djm_$MASTER_PAIRE · cue djm_$CUE_PAIRE puis alternance avec djm_$AUTRE_VOIE${BPM:+ · fiche $BPM BPM}${CLE:+ · $CLE}"

# 1. Toutes les paires, telles quelles : c'est l'enregistrement qu'on ramene.
parecord --device="$SOURCE" --file-format=wav --channels="$NCH" --format=s24le --rate=48000 \
         "$DIR/$SESSION-table.wav" 2> "$DIR/$SESSION-parecord.log" &
ENREG=$!

# 2. Le moteur, master sur une paire, cue sur l'autre.
env ASPNETCORE_URLS="http://0.0.0.0:$PORT" Signal__JournalDeck="$DIR/$SESSION-deck.jsonl" \
    Signal__Source=pulse Signal__Device="djm_$MASTER_PAIRE" Signal__CueDevice="djm_$CUE_PAIRE,djm_$AUTRE_VOIE" \
    ${BPM:+Signal__Bpm="$BPM"} ${CLE:+Signal__Camelot="$CLE"} \
    dotnet run -c Release --project src/Emotion.Server > "$DIR/$SESSION-moteur.log" 2>&1 &
MOTEUR=$!

# 3. Ce que le rendu aurait recu, image par image.
dotnet run -c Release --no-build --project tools/Emotion.Probe -- enregistre "$DIR/$SESSION.pak" \
    > "$DIR/$SESSION-pak.log" 2>&1 &
PAK=$!

trap 'echo; echo "arret"; kill $MOTEUR 2>/dev/null; sleep 1; kill -INT $PAK 2>/dev/null; kill -INT $ENREG 2>/dev/null; wait $PAK $ENREG 2>/dev/null; echo "ramene : $(ls "$DIR")"' EXIT INT TERM

for _ in $(seq 1 60); do
  ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN && break
  sleep 1
done
ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN || { echo "le moteur n'a pas demarre : voir $DIR/$SESSION-moteur.log" >&2; exit 1; }

echo "fenetre — autres terminaux : ./outils/terrain.sh $SESSION (notes) · ./outils/cue.sh $SESSION <bpm> [cle] (fiche) · ./outils/cue.sh $SESSION take"
python3 outils/fenetre.py "$SESSION" 2>&1 | tee -a "$CACHE/fenetre.log"
