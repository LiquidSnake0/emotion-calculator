#!/usr/bin/env python3
# Dit a la DJM ce que chaque paire USB doit porter — ce que le quirk du noyau fait au
# branchement quand il existe. Sur un noyau sans ce quirk (6.1 : « Controls : 0 »), la table
# stream du silence tant que personne ne lui a parle.
#
#   sudo .venv-rekordbox/bin/python outils/djm_routage.py 1=line 2=line 3=line 4=line 5=recout niveau=-10
#   sudo .venv-rekordbox/bin/python outils/djm_routage.py 1=digital 2=digital
#
# Le message est celui du noyau (sound/usb/mixer_quirks.c, snd_djm_controls_update) :
# une requete vendeur SET_FEATURE, wValue = (paire << 8) | source, wIndex = 0x8002 ;
# le niveau de capture sur wIndex 0x8003. Il faut etre root : le noeud USB est a root.
import sys
try:
    import usb.core, usb.util
except ImportError:
    sys.exit("pyusb manque : .venv-rekordbox/bin/pip install pyusb")

SOURCES = {
    "line": 0x00, "cdline": 0x01, "digital": 0x02, "phono": 0x03,
    "prefader": 0x05, "postfader": 0x06, "xfa": 0x07, "xfb": 0x08,
    "mic": 0x09, "recout": 0x0a, "aux": 0x0d, "none": 0x0f,
}
NIVEAUX = {"-19": 0x0000, "-15": 0x0100, "-10": 0x0200, "-5": 0x0300}
WINDEX_CAP, WINDEX_CAPLVL = 0x8002, 0x8003
REQ_SET_FEATURE, VENDEUR_OUT = 0x03, 0x40

def main():
    if len(sys.argv) < 2:
        print(__doc__); return 2
    d = usb.core.find(idVendor=0x2b73, idProduct=0x001b)
    if d is None:
        sys.exit("DJM-750MK2 introuvable sur l'USB")
    for arg in sys.argv[1:]:
        cle, _, val = arg.partition("=")
        val = val.lower().replace("/", "").replace("dB", "").replace("db", "")
        if cle == "niveau":
            if val not in NIVEAUX: sys.exit(f"niveau : {', '.join(NIVEAUX)}")
            d.ctrl_transfer(VENDEUR_OUT, REQ_SET_FEATURE, NIVEAUX[val], WINDEX_CAPLVL, None)
            print(f"niveau de capture {val} dB")
            continue
        paire = int(cle)
        if not 1 <= paire <= 6 or val not in SOURCES:
            sys.exit(f"paire 1 a 6 = {', '.join(SOURCES)}")
        d.ctrl_transfer(VENDEUR_OUT, REQ_SET_FEATURE, (paire << 8) | SOURCES[val], WINDEX_CAP, None)
        print(f"paire {paire} → {val}")
    return 0

if __name__ == "__main__":
    sys.exit(main())
