#!/usr/bin/env bash
# Une session au studio : la table sur USB, le moteur en direct, et tout ce qu'il faut
# ramener a la maison.
#
#   ./outils/studio.sh                          ce que la machine voit (table, paires, options)
#   ./outils/studio.sh --verif                  3 s par paire : qui porte du signal
#   ./outils/studio.sh <session> [bpm] [camelot]   la session : enregistre, analyse, montre
#   STUDIO_ALTERNANCE=0 …                       plan B : un cue fixe sur STUDIO_CUE, sans alternance
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
# Ctrl-C arrete tout, dans l'ordre : fenetre, moteur (il cesse d'ecrire), enregistreur de
# l'anneau (il vide son tampon), enregistreur du WAV. Les deux programmes .NET sont
# construits en tete et lances par leur DLL : ce sont leurs vrais pid qu'on tient.
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
[[ "$CUE_PAIRE" =~ ^[1-5]$ ]] || { echo "STUDIO_CUE doit valoir 1 a 5, pas « $CUE_PAIRE »" >&2; exit 2; }
AUTRE_VOIE=$(( CUE_PAIRE == 1 ? 2 : 1 ))
ALTERNANCE="${STUDIO_ALTERNANCE:-1}"
ANNEAU=/dev/shm/emotion-emulator

# --- la table ------------------------------------------------------------------------------

# La carte ALSA et la source PulseAudio de la table. La source multicanal expose une carte de
# canaux (« aux0,aux1,… ») : c'est la qu'on lit les noms des canaux de chaque paire.
CARTE=$(aplay -l 2>/dev/null | grep -i "DJM" | head -1 | sed -E 's/^card ([0-9]+).*/\1/')
SOURCE="${STUDIO_SOURCE:-}"      # STUDIO_SOURCE=<source> pour repeter chez soi sur une autre entree
if [[ -z "$SOURCE" ]]; then
  # La vraie carte, pas les paires djm_k qu'un lancement precedent a laissees.
  SOURCE=$(pactl list sources short 2>/dev/null | awk '{print $2}' | grep -i "djm" | grep -v "^djm_" | grep -v monitor | head -1)
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

# LE NOM DU COMMUTATEUR DEPEND DU NOYAU : « Ch1 Input » sur ce 6.1, « Input 1 Capture
# Switch » dans les noyaux plus recents. On cherche celui qui existe, et on imprime la
# liste reelle pour ne jamais deviner sur place.
ctl() {
  local k="$1"
  amixer -c "$CARTE" scontrols 2>/dev/null | grep -oE "'(Ch$k Input|Input $k Capture Switch)'" | head -1 | tr -d "'"
}
commutateurs() {
  [[ -n "$CARTE" ]] || return 0
  echo "commutateurs ALSA de la carte $CARTE :"; amixer -c "$CARTE" scontrols | sed 's/^/    /'
  for k in 1 2 3 4 5; do
    local c; c=$(ctl "$k"); [[ -n "$c" ]] || { echo "  paire $k : aucun commutateur trouve"; continue; }
    amixer -c "$CARTE" sget "$c" | awk -v k="$k" '
      /Items:/ { sub(/.*Items: /, ""); items=$0 }
      /Item0:/ { sub(/.*Item0: /, ""); print "  paire " k " : " $0 "     (choix : " items ")" }'
  done
}

# Choisit l'option d'un commutateur par son nom exact, tel qu'amixer le liste. Les erreurs
# s'affichent : un reglage qui echoue en silence, c'est une paire muette qu'on decouvre
# apres le set.
regler() {
  local c; c=$(ctl "$1")
  [[ -n "$c" ]] || { echo "paire $1 : pas de commutateur sur la carte $CARTE" >&2; return 1; }
  amixer -c "$CARTE" -q sset "$c" "$2" || echo "paire $1 : « $2 » refuse" >&2
}

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
  # La carte PulseAudio et son profil : sans « multichannel-input », la source n'a pas ses dix canaux.
  local carte_pa; carte_pa=$(pactl list cards short 2>/dev/null | awk '{print $2}' | grep -i "djm\|usb" | head -1)
  if [[ -n "$carte_pa" ]]; then
    echo "carte PulseAudio : $carte_pa · profil actif : $(pactl list cards | awk -v c="$carte_pa" '$1=="Name:"{t=($2==c)} t && /Active Profile:/{print $3}')"
    echo "  profils : $(pactl list cards | awk -v c="$carte_pa" '$1=="Name:"{t=($2==c)} t && /^\tProfiles:/{p=1;next} t && p && /^\t\t/{sub(/^\t\t/,"");sub(/:.*/,"");printf "%s ", $0} t && /Active Profile/{p=0}')"
    echo "  changer : pactl set-card-profile $carte_pa input:multichannel-input"
  fi
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
      done < <(amixer -c "$CARTE" sget "$(ctl "$k")" | awk '/Items:/ { sub(/.*Items: /, ""); gsub(/'"'"' '"'"'/, "\n"); gsub(/'"'"'/, ""); print }')
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

# Construire d'abord : un « dotnet run » compile pendant trente secondes au moment ou tout
# le reste attend, et cache le vrai processus derriere lui.
{ dotnet build -c Release -nologo -v q src/Emotion.Server && dotnet build -c Release -nologo -v q tools/Emotion.Probe; } > "$DIR/$SESSION-build.log" 2>&1 \
  || { echo "la construction a echoue : voir $DIR/$SESSION-build.log" >&2; exit 1; }
SERVEUR_DLL=$(ls src/Emotion.Server/bin/Release/net*/Emotion.Server.dll | head -1)
PROBE_DLL=$(ls tools/Emotion.Probe/bin/Release/net*/Emotion.Probe.dll | head -1)

for pid in $(ss -lptnH "sport = :$PORT" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
  [[ "$(ps -p "$pid" -o comm= 2>/dev/null)" == *Emotion.Server* ]] && kill "$pid" 2>/dev/null
done
pkill -f "Emotion.Probe.*(rejoue|enregistre)" 2>/dev/null
sleep 0.5
# L'ANNEAU D'UNE SESSION PRECEDENTE FAIT UN .pak VIDE : un lecteur qui l'ouvre part de son
# vieux compteur et attend que le nouveau serveur le rattrape. On l'efface, le serveur
# le recree.
rm -f "$ANNEAU"

# Les paires, si --verif ne les a pas deja posees — et les trois qui servent doivent exister.
pactl list sources short | grep -q "djm_$MASTER_PAIRE" || { retirer_remaps; for k in 1 2 3 4 5; do remap "$k" 2>/dev/null || true; done; }
PAIRES_UTILES="$MASTER_PAIRE $CUE_PAIRE"; [[ "$ALTERNANCE" == "1" ]] && PAIRES_UTILES="$PAIRES_UTILES $AUTRE_VOIE"
for p in $PAIRES_UTILES; do
  pactl list sources short | grep -q "djm_$p" || { echo "pas de paire $p sur cette source (canaux : $(canaux_de_la_source))" >&2; exit 1; }
done
[[ -n "$CARTE" ]] && ! [[ "${STUDIO_GARDER_COMMUTATEURS:-}" ]] && regler 5 "Rec Out"

CANAUX=$(canaux_de_la_source)
NCH=$(echo "$CANAUX" | tr ',' '\n' | grep -c .)
IP=$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{ for (i = 1; i <= NF; i++) if ($i == "src") print $(i + 1); exit }')
CUE_DEVICES="djm_$CUE_PAIRE"; [[ "$ALTERNANCE" == "1" ]] && CUE_DEVICES="djm_$CUE_PAIRE,djm_$AUTRE_VOIE"
echo "session $SESSION → $DIR"
echo "table : $SOURCE, $NCH canaux · master djm_$MASTER_PAIRE · cue $CUE_DEVICES${BPM:+ · fiche $BPM BPM}${CLE:+ · $CLE}"

# DEUX HEURES DE SET : NI VEILLE, NI ECRAN QUI SE VERROUILLE, NI CAPOT QUI ENDORT. Le script
# se relance sous systemd-inhibit une fois, s'il existe.
if [[ -z "${STUDIO_INHIBE:-}" ]] && command -v systemd-inhibit > /dev/null; then
  export STUDIO_INHIBE=1
  exec systemd-inhibit --what=idle:sleep:handle-lid-switch --who="studio.sh" --why="session $SESSION" "$0" "$@"
fi

# Le JSON du set pour le crate est servi d'ici, sur l'adresse du moment : l'IP change avec le
# reseau (partage de connexion), un QR d'hier soir ne vaut plus rien.
python3 - "$CACHE/studio" <<'PY' > /dev/null 2>&1 &
import http.server, os, sys
os.chdir(sys.argv[1])
class H(http.server.SimpleHTTPRequestHandler):
    def guess_type(self, path): return 'application/json; charset=utf-8' if path.endswith('.json') else super().guess_type(path)
    def log_message(self, *a): pass
http.server.ThreadingHTTPServer(('0.0.0.0', 8765), H).serve_forever()
PY
JSON_SERVEUR=$!
echo
echo "crate sur le telephone : http://${IP:-<ip>}:5173/crate/   (npm run dev -- --host 0.0.0.0 dans ~/Documents/crate)"
command -v qrencode > /dev/null && [[ -n "$IP" ]] && qrencode -t UTF8 -m 1 "http://$IP:5173/crate/"
echo "moteur, a mettre dans l'onglet Set : http://${IP:-<ip>}:$PORT"
if ls "$CACHE"/studio/*.json > /dev/null 2>&1; then
  for j in "$CACHE"/studio/*.json; do echo "JSON du set a importer : http://${IP:-<ip>}:8765/$(basename "$j")"; done
  command -v qrencode > /dev/null && [[ -n "$IP" ]] && qrencode -t UTF8 -m 1 "http://$IP:8765/$(basename "$(ls "$CACHE"/studio/*.json | head -1)")"
fi
echo

# 1. Toutes les paires, telles quelles, dans l'ordre exact de la source : c'est
#    l'enregistrement qu'on ramene.
parecord --device="$SOURCE" --file-format=wav --channels="$NCH" --channel-map="$CANAUX" --no-remix \
         --format=s24le --rate=48000 "$DIR/$SESSION-table.wav" 2> "$DIR/$SESSION-parecord.log" &
ENREG=$!

# 2. Le moteur, master sur une paire, cue sur l'autre (ou les deux voies en alternance).
#    --no-launch-profile : sinon le profil de lancement impose localhost et le telephone ne
#    joint jamais le moteur.
env ASPNETCORE_URLS="http://0.0.0.0:$PORT" Signal__JournalDeck="$DIR/$SESSION-deck.jsonl" \
    Signal__Source=pulse Signal__Device="djm_$MASTER_PAIRE" Signal__CueDevice="$CUE_DEVICES" \
    ${BPM:+Signal__Bpm="$BPM"} ${CLE:+Signal__Camelot="$CLE"} \
    dotnet "$SERVEUR_DLL" > "$DIR/$SESSION-moteur.log" 2>&1 &
MOTEUR=$!

arreter() {
  echo; echo "arret"
  kill -TERM "$MOTEUR" 2>/dev/null; wait "$MOTEUR" 2>/dev/null       # il cesse d'ecrire dans l'anneau
  [[ -n "${PAK:-}" ]] && { kill -TERM "$PAK" 2>/dev/null; wait "$PAK" 2>/dev/null; }   # il vide son tampon
  kill -INT "$ENREG" 2>/dev/null; wait "$ENREG" 2>/dev/null         # parecord ferme le WAV
  kill "$JSON_SERVEUR" 2>/dev/null
  echo "ramene : $(ls "$DIR")"
}
trap arreter EXIT
trap 'exit 130' INT TERM

for _ in $(seq 1 60); do
  ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN && break
  sleep 1
done
ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN || { echo "le moteur n'a pas demarre : voir $DIR/$SESSION-moteur.log" >&2; exit 1; }

# 3. Ce que le rendu aurait recu, image par image — lance une fois le moteur la, donc sur
#    l'anneau neuf.
dotnet "$PROBE_DLL" enregistre "$DIR/$SESSION.pak" > "$DIR/$SESSION-pak.log" 2>&1 &
PAK=$!

echo "fenetre — autres terminaux : ./outils/terrain.sh $SESSION (notes) · ./outils/cue.sh $SESSION <bpm> [cle] (fiche) · ./outils/cue.sh $SESSION take"
echo "ATTENTION : Q dans la fenetre (ou Ctrl-C ici) termine TOUTE la session — moteur, .pak, WAV."
if [[ "${STUDIO_SANS_FENETRE:-}" == "1" ]]; then
  # Repetition sans ecran : tout tourne, Ctrl-C (ou TERM) arrete dans l'ordre.
  echo "sans fenetre : Ctrl-C pour arreter"; sleep infinity &
  wait $!
else
  python3 outils/fenetre.py "$SESSION" 2>&1 | tee -a "$CACHE/fenetre.log"
fi
