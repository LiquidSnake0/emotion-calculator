#!/usr/bin/env bash
# Un morceau du bac se joue, le moteur l'ecoute, la fenetre le montre.
#
#   ./outils/voir.sh              une piste au hasard de l'album
#   ./outils/voir.sh 5            la piste 5
#   ./outils/voir.sh passepartout une piste par un bout de son titre
#   ./outils/voir.sh --direct     rien ne se joue, le moteur ecoute ce que tu joues toi
#
# UN STEM PLAYER, ET C'EST LUI QUI JOUE. « Tu vois le stem player de Kanye West ? » Six
# pistes, six niveaux qu'on bouge pendant que ca tourne. Le moteur ANALYSE le morceau entier
# depuis le fichier ; la fenetre, elle, JOUE ce qu'on lui demande — le morceau au debut, puis
# les six sources des qu'elles sont extraites.
#
# Le moteur ne peut donc plus ecouter la carte son : elle ne porte plus le morceau mais le
# melange qu'on est en train de tripoter. Il lit le fichier, ce qui est de toute facon le
# seul moyen d'analyser le morceau ENTIER pendant qu'on n'en ecoute qu'un sixieme.
#
# LA FICHE VIENT DU CRATE, qui est la base du bac et la premiere source d'information du
# systeme. Sans elle le moteur cherche son tempo dans le vide : 43 % de justesse au lieu de
# 99, et tout ce qu'on regarde ensuite en depend.
#
# Ctrl-C arrete tout.
set -uo pipefail
cd "$(dirname "$0")/.."

ALBUM="${EMOTION_ALBUM:-$HOME/Downloads/Macroblank & slowerpace 音楽 - The Era of Information}"
CRATE="${EMOTION_CRATE:-$HOME/Documents/crate/src/data/seed.json}"
PORT=5099

for pid in $(ss -lptnH "sport = :$PORT" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
  [[ "$(ps -p "$pid" -o comm= 2>/dev/null)" == *Emotion.Server* ]] && kill "$pid" 2>/dev/null
done

PISTE=""; TITRE="direct"; BPM=""; CLE=""
if [[ "${1:-}" != "--direct" ]]; then
  if [[ ! -d "$ALBUM" ]]; then
    echo "album introuvable : $ALBUM" >&2
    echo "en donner un autre : EMOTION_ALBUM=/chemin/vers/album ./outils/voir.sh" >&2
    exit 1
  fi
  # Le choix, et la fiche qui va avec, se font en Python : il faut lire le crate et
  # apparier sur le numero de piste, ce qui en shell serait illisible.
  LIGNE=$(ALBUM="$ALBUM" CRATE="$CRATE" python3 - "${1:-}" <<'PYEOF'
import glob, json, os, random, re, sys

voulu = (sys.argv[1] if len(sys.argv) > 1 else "").strip().lower()
pistes = sorted(glob.glob(os.path.join(os.environ["ALBUM"], "*.aiff")) +
                glob.glob(os.path.join(os.environ["ALBUM"], "*.wav")) +
                glob.glob(os.path.join(os.environ["ALBUM"], "*.flac")))
if not pistes:
    sys.exit(1)

# Le numero et le titre se lisent dans le nom du fichier : « … - 05 Dead Internet Theory ».
def decoupe(chemin):
    base = os.path.splitext(os.path.basename(chemin))[0]
    m = re.search(r"(\d{2})\s+(.+)$", base)
    return (int(m.group(1)), m.group(2)) if m else (0, base)

choix = None
if voulu.isdigit():
    choix = next((p for p in pistes if decoupe(p)[0] == int(voulu)), None)
elif voulu:
    choix = next((p for p in pistes if voulu in decoupe(p)[1].lower()), None)
    if choix is None and os.path.isfile(voulu):
        choix = voulu
choix = choix or random.choice(pistes)
rang, titre = decoupe(choix)

# LA FICHE SE PREND DANS LE CRATE, jamais devinee. On apparie sur le titre, et sur le rang
# seulement si l'album est identifie — un numero de piste seul ne designe rien.
# ET C'EST LE BPM NATIF, A DESSEIN : ici le fichier se joue tel quel, sans pitch. Sur une
# platine le disque tourne a son BPM joue (`anchorBpm` du crate) et sa cle est transposee
# (le tag) : c'est CES valeurs-la que le crate devra envoyer a /deck, pas les natives.
bpm = ""
cle = ""
try:
    with open(os.environ["CRATE"], encoding="utf-8") as fh:
        faces = json.load(fh)
    exact = [f for f in faces if (f.get("title") or "").lower() == titre.lower()]
    if exact:
        bpm = f"{exact[0]['bpm']}"
        cle = exact[0].get("key") or ""
except (OSError, ValueError, KeyError):
    pass

print(f"{choix}\t{rang:02d} {titre}\t{bpm}\t{cle}")
PYEOF
)
  [[ -z "$LIGNE" ]] && { echo "aucune piste jouable dans $ALBUM" >&2; exit 1; }
  IFS=$'\t' read -r PISTE TITRE BPM CLE <<< "$LIGNE"
fi

if [[ -n "$PISTE" ]]; then
  echo "morceau — $TITRE${BPM:+   fiche $BPM BPM}${CLE:+ · $CLE}"
  [[ -z "$BPM" ]] && echo "         (aucune fiche au crate pour ce titre : le moteur cherchera seul)"
else
  echo "moteur — il ecoute $(pactl get-default-sink).monitor, joue ce que tu veux"
fi

CACHE="${EMOTION_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator}"
mkdir -p "$CACHE"

if [[ -n "$PISTE" ]]; then
  # UN SEUL DECODAGE, ET TOUT LE MONDE PART DE LA. Le moteur ne lit que du RIFF — il refuse
  # l'AIFF du bac — et les six pistes doivent etre taillees sur exactement le meme signal
  # que celui qu'il analyse. Un WAV mono 48 kHz, mis en cache, sert donc aux deux.
  WAV="$CACHE/$(echo "$TITRE" | tr -c '[:alnum:]._-' '_').wav"
  if [[ ! -f "$WAV" ]]; then
    echo "decodage — une fois, puis c'est en cache"
    ffmpeg -v error -y -i "$PISTE" -ac 1 -ar 48000 "$WAV" || exit 1
  fi
  export EMOTION_MORCEAU="$WAV" EMOTION_CACHE="$CACHE/stems"
  Signal__Camelot="$CLE" ./run.sh fichier "$WAV" "$BPM" > /tmp/emotion-moteur.log 2>&1 &
else
  # UNE VARIABLE VIDE N'EST PAS UNE VARIABLE ABSENTE, et le moteur ne demarrait plus.
  # `Signal__Bpm=""` fait echouer la conversion en Single au demarrage : le mode direct et
  # tout morceau absent du crate tombaient sur un serveur qui refusait de s'ouvrir.
  if [[ -n "$BPM" ]]; then
    Signal__Bpm="$BPM" ./run.sh pulse > /tmp/emotion-moteur.log 2>&1 &
  else
    ./run.sh pulse > /tmp/emotion-moteur.log 2>&1 &
  fi
fi
MOTEUR=$!
trap 'kill $MOTEUR 2>/dev/null; kill 0 2>/dev/null' EXIT INT TERM

for _ in $(seq 1 60); do
  ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN && break
  sleep 1
done

echo "fenetre — clic dans une case pour isoler, bord droit pour doser, espace pour marquer"
# La sortie de la fenetre va aussi dans son journal : une erreur Python qui tue la fenetre
# doit pouvoir se relire apres coup, pas seulement s'entrevoir dans le terminal.
python3 outils/fenetre.py "$TITRE" 2>&1 | tee -a "$CACHE/fenetre.log"
